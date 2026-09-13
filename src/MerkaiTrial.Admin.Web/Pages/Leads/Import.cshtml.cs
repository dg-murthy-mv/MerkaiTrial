// =====================================================================
// LEAD IMPORT WIZARD — Backend
// Location: MerkaiTrial.Admin.Web/Pages/Leads/Import.cshtml.cs
//
// NEW FILE.
//
// Three steps on one page:
//   1  Upload      — choose a .csv or .xlsx
//   2  Map columns — auto-detected, user corrects
//   3  Preview     — every row checked, then Import
//
// The parsed file stays on the API side, referenced by SessionId, so a
// 2,000-row sheet is never round-tripped through the browser.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Leads.Import;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Leads
{
    public class ImportModel : AuthorizedPageModel
    {
        private readonly ILeadImportService _import;
        private readonly ILogger<ImportModel> _logger;

        protected override string ModuleName => Modules.Leads;

        public ImportModel(
            ILeadImportService import,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<ImportModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _import = import;
            _logger = logger;
        }

        // ── Step state ────────────────────────────────────────────────
        public int Step { get; private set; } = 1;

        public ImportUploadResult? Upload { get; private set; }
        public ImportPreview? Preview { get; private set; }
        public ImportResult? Result { get; private set; }

        public IReadOnlyList<ImportField> Fields => ImportFields.All;

        // ── Form ──────────────────────────────────────────────────────
        [BindProperty] public IFormFile? File { get; set; }
        [BindProperty] public Guid SessionId { get; set; }
        [BindProperty] public string FileName { get; set; } = "";
        [BindProperty] public int TotalRows { get; set; }

        /// <summary>Posted back as field key -> column index ("-1" = not mapped).</summary>
        [BindProperty] public Dictionary<string, int> Mapping { get; set; } = new();

        /// <summary>Headers are carried through the form so step 3 can still name columns.</summary>
        [BindProperty] public List<string> Headers { get; set; } = new();

        [BindProperty] public string Duplicates { get; set; } = DuplicateAction.Skip;

        public string? ErrorMessage { get; private set; }
        [TempData] public string? SuccessMessage { get; set; }

        // =================================================================

        public async Task<IActionResult> OnGetAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Create);
            if (check != null) return check;

            await InitializePermissionsAsync();
            Step = 1;
            return Page();
        }

        /// <summary>Step 1 -> 2.</summary>
        public async Task<IActionResult> OnPostUploadAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Create);
            if (check != null) return check;
            await InitializePermissionsAsync();

            if (File is null || File.Length == 0)
            {
                ErrorMessage = "Choose a file to import.";
                Step = 1;
                return Page();
            }

            try
            {
                Upload = await _import.UploadAsync(File);

                SessionId = Upload.SessionId;
                FileName  = Upload.FileName;
                TotalRows = Upload.TotalRows;
                Headers   = Upload.Headers.ToList();

                // Start from the auto-detected mapping; -1 means "don't import".
                Mapping = ImportFields.All.ToDictionary(
                    f => f.Key,
                    f => Upload.SuggestedMapping.TryGetValue(f.Key, out var col) ? col : -1);

                Step = 2;
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Import upload failed");
                ErrorMessage = Explain(ex, "Couldn't read that file.");
                Step = 1;
                return Page();
            }
        }

        /// <summary>Step 2 -> 3.</summary>
        public async Task<IActionResult> OnPostPreviewAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Create);
            if (check != null) return check;
            await InitializePermissionsAsync();

            try
            {
                Preview = await _import.PreviewAsync(BuildRequest());
                Step = 3;
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Import preview failed");
                ErrorMessage = Explain(ex, "Couldn't check that file.");
                Step = 2;
                return Page();
            }
        }

        /// <summary>Step 3 -> done.</summary>
        public async Task<IActionResult> OnPostCommitAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Create);
            if (check != null) return check;
            await InitializePermissionsAsync();

            try
            {
                Result = await _import.CommitAsync(BuildRequest());

                if (Result.Failed == 0)
                {
                    TempData["SuccessMessage"] =
                        $"Imported {Result.Imported} lead(s)" +
                        (Result.Updated > 0 ? $", updated {Result.Updated}" : "") +
                        (Result.Skipped > 0 ? $", skipped {Result.Skipped}" : "") + ".";
                    return RedirectToPage("./Index");
                }

                Step = 4;   // show which rows failed
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Import commit failed");
                ErrorMessage = Explain(ex, "The import didn't complete.");

                // Back to preview so the mapping isn't lost.
                try { Preview = await _import.PreviewAsync(BuildRequest()); Step = 3; }
                catch { Step = 2; }

                return Page();
            }
        }

        // =================================================================

        private ImportRequest BuildRequest() => new(
            SessionId: SessionId,
            TenantId: Guid.Empty,               // set server-side from the signed-in user
            Mapping: Mapping.Where(kv => kv.Value >= 0).ToDictionary(kv => kv.Key, kv => kv.Value),
            Duplicates: Duplicates,
            ImportedBy: null);                  // set server-side

        private static string Explain(Exception ex, string fallback)
            => string.IsNullOrWhiteSpace(ex.Message) || ex is NullReferenceException
                ? $"{fallback} Please try again."
                : ex.Message;

        // ── View helpers ──────────────────────────────────────────────

        public string ColumnName(int index) =>
            index >= 0 && index < Headers.Count ? Headers[index] : "—";

        public string VerdictBadge(string verdict) => verdict switch
        {
            RowVerdict.Ok        => "bg-success",
            RowVerdict.Warning   => "bg-warning text-dark",
            RowVerdict.Duplicate => "bg-info",
            RowVerdict.Error     => "bg-danger",
            _ => "bg-secondary"
        };

        public string VerdictLabel(string verdict) => verdict switch
        {
            RowVerdict.Ok        => "OK",
            RowVerdict.Warning   => "Check",
            RowVerdict.Duplicate => "Duplicate",
            RowVerdict.Error     => "Won't import",
            _ => verdict
        };
    }
}
