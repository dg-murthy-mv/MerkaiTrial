// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Settings/CustomFields/Index.cshtml.cs
//
// NEW FILE (075 — custom fields, Round A).
//
// 077 — the Deals tab is live (CustomFieldEntityTypes.Enabled). Nothing
//   else on this page changes for it: every label, count and limit is
//   already worked out from the active entity type. ListPage is new, so
//   "Open the list" goes to the pipeline from the Deals tab.
//
// 078 — the Companies tab is live, and ListPage sends "Open the list" to
//   the Companies list from it. The Leads tab shows as "Coming soon".
//
// 079 — the Leads tab is live, and Lead fields get a "When a lead is
//   converted" section: copy this value into a Contact field and/or a Deal
//   field of the same type. The choices are loaded only on the Leads tab
//   (ContactTargets / DealTargets); on the Contacts and Deals tabs each
//   field shows which Lead field fills it (FilledFromLead). The one-click
//   on/off toggle carries the mapping through, for the same reason it
//   carries ShowInList — it rebuilds the whole field, and leaving the
//   mapping out would have quietly removed it every time a Lead field was
//   switched off and on.
//
// 080 — the Products tab is live; "Open the list" goes to the Products
//   list from it.
//
// 081 — the Quotes and Invoices tabs are live.
//   • "Print on quotes & invoices" (PrintOnDocuments) on both tabs — the
//     value appears on the PDF and the customer's quote link.
//   • On the Quotes tab, "When an invoice is raised from this quote": copy
//     the value into an Invoice field of the same type (InvoiceTargets).
//     On the Invoices tab each field shows which Quote field fills it
//     (FilledFromQuote).
//   • The one-click on/off toggle carries both settings through, for the
//     reason it carries ShowInList and the lead mapping: it rebuilds the
//     whole field, and leaving them out would quietly clear them.
//   • "Open the list" goes to the Quotes / Invoices lists.
//
// 076 — "Show on the list": ShowInList on the form, a badge on the row,
//   and the count of list columns used in the side panel. The one-click
//   on/off toggle now carries ShowInList through — it rebuilds the whole
//   field from the row, and leaving the flag out would have quietly taken
//   the field off the list every time it was switched off and on.
//
// Settings → Custom Fields. Define the extra fields a workspace records
// on a kind of record. Contacts in round A; the Companies tab is shown
// as "coming next" so nobody wonders where it is.
//
// THE HOUSE PATTERN, FROM Settings/ProductCategories:
//   • one page, an inline add/edit form in place of the row, not three
//     pages or a modal — the rest of the list stays on screen;
//   • which form is open lives in the QUERY STRING (?edit=, ?add=true),
//     so a validation failure, a refresh and a pasted link all reopen it;
//   • ?add is bound as a STRING, never a bool — round 071b;
//   • up/down buttons, not drag-and-drop;
//   • Settings module. Every POST handler calls ValidatePermissionAsync,
//     never the Can* properties, which are only filled in by OnGet.
//
// NO SEARCH, FILTER OR PAGER, unlike Product Categories. The list is
// capped at 50 per entity type and each row is one line; the order IS
// the order of the inputs on the contact form, so seeing all of it at
// once is the point. The 073 reasoning about filters breaking "move up"
// would apply here too, for nothing gained.
//
// MESSAGES: anything shown after a REDIRECT goes in TempData, which the
// layout renders once. Anything shown on a page that RENDERS (a failed
// save that reopens the form) goes in PageError, an ordinary property —
// [TempData] set on a rendering request turns up on the NEXT page
// instead, which is the trap the handoff calls out.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.CustomFields;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Settings.CustomFields
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly ICustomFieldService _fields;

        public IndexModel(
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<IndexModel> logger,
            ICustomFieldService fields)
            : base(authorizationService, currentUserService, logger)
        {
            _fields = fields;
        }

        protected override string ModuleName => Modules.Settings;

        // ── Which kind of record ──────────────────────────────────────

        /// <summary>?entity=Contact. Anything not enabled falls back to Contact.</summary>
        [BindProperty(SupportsGet = true, Name = "entity")]
        public string? EntityParam { get; set; }

        public string ActiveEntity => CustomFieldEntityTypes.Normalise(EntityParam) ?? CustomFieldEntityTypes.Contact;

        public IReadOnlyList<string> PlannedEntities => CustomFieldEntityTypes.Planned;

        public bool IsEntityEnabled(string entityType) => CustomFieldEntityTypes.IsEnabled(entityType);

        // ── What the page renders ─────────────────────────────────────

        /// <summary>Every field for ActiveEntity, switched-off ones included, in order.</summary>
        public List<CustomFieldDefinitionDto> Fields { get; private set; } = new();

        public bool LoadFailed { get; private set; }

        /// <summary>Shown on a page that RENDERS. Never TempData — see the header.</summary>
        public string? PageError { get; set; }

        public IReadOnlyList<string> FieldTypes => CustomFieldTypes.All;

        public int MaxFields => CustomFieldLimits.MaxFieldsPerEntity;

        /// <summary>076. Active fields currently shown as list columns.</summary>
        public int ListColumnsUsed => Fields.Count(f => f.IsActive && f.ShowInList);
        public int MaxListColumns => CustomFieldLimits.MaxListColumns;

        /// <summary>077. Where this entity type's list lives, for "Open the list".</summary>
        public string ListPage => ActiveEntity switch
        {
            CustomFieldEntityTypes.Deal    => "/Pipeline/Index",
            CustomFieldEntityTypes.Company => "/Companies/Index",   // 078
            CustomFieldEntityTypes.Lead    => "/Leads/Index",       // 079
            CustomFieldEntityTypes.Product => "/Products/Index",    // 080
            CustomFieldEntityTypes.Quote   => "/Quotes/Index",      // 081
            CustomFieldEntityTypes.Invoice => "/Invoices/Index",    // 081
            _                           => "/Contacts/Index"
        };

        /// <summary>
        /// 081. What "Show on the list" means on this tab. Contacts, deals,
        /// companies, leads and products filter and search every field;
        /// the quote and invoice lists show columns only.
        /// </summary>
        public string ListHint => ActiveEntity switch
        {
            CustomFieldEntityTypes.Quote   => $"Up to {CustomFieldLimits.MaxListColumns} fields can be columns in the quotes List view, and the quick search finds their values.",
            CustomFieldEntityTypes.Invoice => $"Up to {CustomFieldLimits.MaxListColumns} fields can be shown on each invoice in the invoices list.",
            _ => $"Up to {CustomFieldLimits.MaxListColumns} fields can be columns. Every field can be searched and filtered on, whether it is a column or not."
        };
        public bool AtLimit => Fields.Count >= MaxFields;

        // ── 079: lead → contact / deal mapping ────────────────────────

        public bool IsLeadTab => ActiveEntity == CustomFieldEntityTypes.Lead;

        /// <summary>Leads tab only: the Contact fields a Lead field can copy into (switched-off ones included, marked).</summary>
        public List<CustomFieldDefinitionDto> ContactTargets { get; private set; } = new();

        /// <summary>Leads tab only: the Deal fields a Lead field can copy into.</summary>
        public List<CustomFieldDefinitionDto> DealTargets { get; private set; } = new();

        /// <summary>Contacts / Deals tab only: target field id → the Lead field that fills it.</summary>
        public Dictionary<Guid, string> FilledFromLead { get; private set; } = new();

        /// <summary>A target's label for the row summary, or null when it is gone.</summary>
        public CustomFieldDefinitionDto? Target(Guid? id, bool contact)
            => id is { } g ? (contact ? ContactTargets : DealTargets).FirstOrDefault(t => t.Id == g) : null;

        // ── 081: printing, and the quote → invoice mapping ────────────

        public bool IsQuoteTab   => ActiveEntity == CustomFieldEntityTypes.Quote;
        public bool IsInvoiceTab => ActiveEntity == CustomFieldEntityTypes.Invoice;

        /// <summary>This tab's fields can be printed on quotes and invoices.</summary>
        public bool SupportsPrint => CustomFieldEntityTypes.SupportsPrint(ActiveEntity);

        /// <summary>How many switched-on fields print, for the side panel.</summary>
        public int PrintedCount => Fields.Count(f => f.IsActive && f.PrintOnDocuments);

        /// <summary>Quotes tab only: the Invoice fields a Quote field can copy into (switched-off ones included, marked).</summary>
        public List<CustomFieldDefinitionDto> InvoiceTargets { get; private set; } = new();

        /// <summary>Invoices tab only: target field id → the Quote field that fills it.</summary>
        public Dictionary<Guid, string> FilledFromQuote { get; private set; } = new();

        /// <summary>The Invoice field a Quote field copies into, or null when none / gone.</summary>
        public CustomFieldDefinitionDto? InvoiceTarget(Guid? id)
            => id is { } g ? InvoiceTargets.FirstOrDefault(t => t.Id == g) : null;

        public string Noun => CustomFieldEntityTypes.SingularNoun(ActiveEntity);
        public string NounPlural => CustomFieldEntityTypes.PluralLabel(ActiveEntity).ToLowerInvariant();

        public bool IsFirst(Guid id) => Fields.Count > 0 && Fields[0].Id == id;
        public bool IsLast(Guid id)  => Fields.Count > 0 && Fields[^1].Id == id;

        /// <summary>The field being edited, for the form's header and warnings. Null when adding.</summary>
        public CustomFieldDefinitionDto? EditingField =>
            EditId is { } id ? Fields.FirstOrDefault(f => f.Id == id) : null;

        // ── Which form is open ────────────────────────────────────────

        [BindProperty(SupportsGet = true, Name = "edit")]
        public Guid? EditId { get; set; }

        /// <summary>?add=… — a STRING, never a bool. Round 071b.</summary>
        [BindProperty(SupportsGet = true, Name = "add")]
        public string? AddFlag { get; set; }

        private bool? _isAdding;

        /// <summary>Settable so a failed POST can keep the form open.</summary>
        public bool IsAdding
        {
            get => _isAdding ?? IsOn(AddFlag);
            set => _isAdding = value;
        }

        private static bool IsOn(string? value)
            => value is not null &&
               (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("1",    StringComparison.Ordinal)           ||
                value.Equals("on",   StringComparison.OrdinalIgnoreCase));

        // ── The inline form, used by both add and edit ────────────────

        [BindProperty] public InputModel Input { get; set; } = new();

        public class InputModel
        {
            public Guid? Id { get; set; }

            [Required(ErrorMessage = "A field needs a name.")]
            [StringLength(CustomFieldLimits.LabelMaxLength,
                ErrorMessage = "A field name cannot be longer than 80 characters.")]
            [Display(Name = "Name")]
            public string Label { get; set; } = string.Empty;

            [Display(Name = "Type")]
            public string FieldType { get; set; } = CustomFieldTypes.Text;

            [StringLength(CustomFieldLimits.HelpTextMaxLength,
                ErrorMessage = "The hint cannot be longer than 200 characters.")]
            [Display(Name = "Hint")]
            public string? HelpText { get; set; }

            [Display(Name = "Required")]
            public bool IsRequired { get; set; }

            [Display(Name = "Show on forms")]
            public bool IsActive { get; set; } = true;

            /// <summary>076. A column on the list page.</summary>
            [Display(Name = "Show on the list")]
            public bool ShowInList { get; set; }

            [Range(0, CustomFieldLimits.MaxDecimalPlaces)]
            [Display(Name = "Decimal places")]
            public int DecimalPlaces { get; set; }

            /// <summary>Dropdown only. Rows with no name and no key are ignored.</summary>
            public List<OptionInput> Options { get; set; } = new();

            /// <summary>079. Lead fields only: copy into this Contact field on conversion. Empty = none.</summary>
            [Display(Name = "Copy to contact field")]
            public Guid? MapToContactFieldId { get; set; }

            /// <summary>079. Lead fields only: copy into this Deal field on conversion. Empty = none.</summary>
            [Display(Name = "Copy to deal field")]
            public Guid? MapToDealFieldId { get; set; }

            /// <summary>081. Quote / Invoice fields: printed on the PDF and the customer's link.</summary>
            [Display(Name = "Print on quotes & invoices")]
            public bool PrintOnDocuments { get; set; }

            /// <summary>081. Quote fields only: copy into this Invoice field when an invoice is raised. Empty = none.</summary>
            [Display(Name = "Copy to invoice field")]
            public Guid? MapToInvoiceFieldId { get; set; }
        }

        public class OptionInput
        {
            public string? Key { get; set; }
            public string? Label { get; set; }
            public bool IsActive { get; set; } = true;
        }

        // ── Routing helpers ───────────────────────────────────────────

        /// <summary>Route values for links that open a form. asp-all-route-data wants IDictionary&lt;string,string&gt;.</summary>
        public Dictionary<string, string> StateRoute(Guid? editId = null, bool adding = false)
        {
            var route = new Dictionary<string, string> { ["entity"] = ActiveEntity };
            if (adding) route["add"] = "true";                   // "true", never "1"
            if (editId is { } id && id != Guid.Empty) route["edit"] = id.ToString();
            return route;
        }

        /// <summary>A RouteValueDictionary, not a Dictionary — RedirectToPage reflects over plain objects.</summary>
        private RouteValueDictionary ReturnRoute() => new() { ["entity"] = ActiveEntity };

        // =============================================================
        // GET
        // =============================================================

        public async Task<IActionResult> OnGetAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Read);
            if (denied is not null) return denied;

            await InitializePermissionsAsync();
            await LoadAsync();

            // Adding wins over editing, as on Product Categories: two forms
            // at once would duplicate the inputs' ids.
            if (IsAdding)
            {
                EditId = null;
                Input = new InputModel
                {
                    FieldType = CustomFieldTypes.Text,
                    IsActive  = true,
                    Options   = new List<OptionInput> { new(), new() }
                };
            }
            else if (EditId is { } id && id != Guid.Empty)
            {
                var row = Fields.FirstOrDefault(f => f.Id == id);
                if (row is null)
                {
                    EditId = null;
                    if (!LoadFailed)
                        PageError = "That field no longer exists. The list has been refreshed.";
                }
                else
                {
                    Input = FromDto(row);
                }
            }

            return Page();
        }

        // =============================================================
        // ADD
        // =============================================================

        public async Task<IActionResult> OnPostAddAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Create);
            if (denied is not null) return denied;

            await InitializePermissionsAsync();
            IsAdding = true;
            EditId   = null;

            if (!CustomFieldEntityTypes.IsEnabled(EntityParam))
            {
                TempData["ErrorMessage"] = "Custom fields are not available for that kind of record yet.";
                return RedirectToPage(ReturnRoute());
            }

            if (!ModelState.IsValid)
            {
                await LoadAsync();
                return Page();
            }

            try
            {
                var created = await _fields.CreateAsync(ToDto(Input, ActiveEntity));
                TempData["SuccessMessage"] =
                    $"\"{created.Label}\" added. It now appears on every {Noun} form" +
                    (created.IsRequired ? " and must be filled in." : ".");
                return RedirectToPage(ReturnRoute());
            }
            catch (InvalidOperationException ex)
            {
                PageError = ex.Message;          // the API's own sentence
                await LoadAsync();
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to add a custom field");
                PageError = "Could not add the field. Please try again.";
                await LoadAsync();
                return Page();
            }
        }

        // =============================================================
        // SAVE AN EXISTING ONE
        // =============================================================

        public async Task<IActionResult> OnPostSaveAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Update);
            if (denied is not null) return denied;

            await InitializePermissionsAsync();
            IsAdding = false;
            EditId   = Input.Id;

            if (Input.Id is null || Input.Id == Guid.Empty)
            {
                TempData["ErrorMessage"] = "Could not tell which field to save. Please try again.";
                return RedirectToPage(ReturnRoute());
            }

            if (!ModelState.IsValid)
            {
                await LoadAsync();
                return Page();
            }

            try
            {
                var saved = await _fields.UpdateAsync(Input.Id.Value, ToDto(Input, ActiveEntity));
                TempData["SuccessMessage"] = saved.IsActive
                    ? $"\"{saved.Label}\" saved."
                    : $"\"{saved.Label}\" saved. It is switched off, so it no longer appears on forms.";
                return RedirectToPage(ReturnRoute());
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That field no longer exists.";
                return RedirectToPage(ReturnRoute());
            }
            catch (InvalidOperationException ex)
            {
                PageError = ex.Message;
                await LoadAsync();
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to save custom field {Id}", Input.Id);
                PageError = "Could not save the field. Please try again.";
                await LoadAsync();
                return Page();
            }
        }

        // =============================================================
        // SWITCH ON / OFF — one click, through the same update endpoint
        // =============================================================

        public async Task<IActionResult> OnPostToggleAsync(Guid id)
        {
            var denied = await ValidatePermissionAsync(Actions.Update);
            if (denied is not null) return denied;

            try
            {
                var list = await _fields.GetDefinitionsAsync(ActiveEntity, includeInactive: true);
                var row  = list.FirstOrDefault(f => f.Id == id);

                if (row is null)
                {
                    TempData["ErrorMessage"] = "That field no longer exists.";
                    return RedirectToPage(ReturnRoute());
                }

                var dto = new SaveCustomFieldDefinitionDto
                {
                    EntityType    = row.EntityType,
                    Label         = row.Label,
                    FieldType     = row.FieldType,
                    HelpText      = row.HelpText,
                    IsRequired    = row.IsRequired,
                    IsActive      = !row.IsActive,
                    ShowInList    = row.ShowInList,       // 076 — see the header
                    DecimalPlaces = row.DecimalPlaces,
                    Options       = row.Options,
                    MapToContactFieldId = row.MapToContactFieldId,   // 079 — see the header
                    MapToDealFieldId    = row.MapToDealFieldId,
                    PrintOnDocuments    = row.PrintOnDocuments,      // 081 — see the header
                    MapToInvoiceFieldId = row.MapToInvoiceFieldId
                };

                await _fields.UpdateAsync(id, dto);

                TempData["SuccessMessage"] = row.IsActive
                    ? $"\"{row.Label}\" switched off. It is hidden from forms; values already recorded are kept."
                    : $"\"{row.Label}\" switched back on.";
                return RedirectToPage(ReturnRoute());
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToPage(ReturnRoute());
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to toggle custom field {Id}", id);
                TempData["ErrorMessage"] = "Could not change the field. Please try again.";
                return RedirectToPage(ReturnRoute());
            }
        }

        // =============================================================
        // DELETE — only a field nobody has filled in
        // =============================================================

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            var denied = await ValidatePermissionAsync(Actions.Delete);
            if (denied is not null) return denied;

            try
            {
                await _fields.DeleteAsync(id);
                TempData["SuccessMessage"] = "Field deleted.";
                return RedirectToPage(ReturnRoute());
            }
            catch (InvalidOperationException ex)
            {
                // "…is filled in on 3 contacts, so it cannot be deleted.
                //  Switch it off instead…" — the API's sentence.
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToPage(ReturnRoute());
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to delete custom field {Id}", id);
                TempData["ErrorMessage"] = "Could not delete the field. Please try again.";
                return RedirectToPage(ReturnRoute());
            }
        }

        // =============================================================
        // REORDER
        // =============================================================

        public async Task<IActionResult> OnPostMoveAsync(Guid id, string? direction)
        {
            var denied = await ValidatePermissionAsync(Actions.Update);
            if (denied is not null) return denied;

            try
            {
                var list    = await _fields.GetDefinitionsAsync(ActiveEntity, includeInactive: true);
                var ordered = list.Select(f => f.Id).ToList();

                var index = ordered.IndexOf(id);
                if (index < 0)
                {
                    TempData["ErrorMessage"] = "That field no longer exists.";
                    return RedirectToPage(ReturnRoute());
                }

                var target = string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase) ? index - 1 : index + 1;
                if (target < 0 || target >= ordered.Count)
                    return RedirectToPage(ReturnRoute());

                (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
                await _fields.ReorderAsync(ActiveEntity, ordered);

                return RedirectToPage(ReturnRoute());
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToPage(ReturnRoute());
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to reorder custom fields");
                TempData["ErrorMessage"] = "Could not change the order. Please try again.";
                return RedirectToPage(ReturnRoute());
            }
        }

        // =============================================================
        // Helpers
        // =============================================================

        private async Task LoadAsync()
        {
            try
            {
                Fields = await _fields.GetDefinitionsAsync(ActiveEntity, includeInactive: true);
                LoadFailed = false;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load custom fields for {Entity}", ActiveEntity);
                Fields = new List<CustomFieldDefinitionDto>();
                LoadFailed = true;
                PageError ??= "Could not load your custom fields. Please try again.";
            }

            await LoadMappingContextAsync();
        }

        /// <summary>
        /// 079. The other side of the lead mapping. Non-fatal. If the lists
        /// cannot be loaded, the form says so and posts the field's CURRENT
        /// mapping back unchanged (hidden inputs in _FieldSettings) — an
        /// empty dropdown would otherwise post "none" and silently clear it.
        /// </summary>
        private async Task LoadMappingContextAsync()
        {
            ContactTargets  = new();
            DealTargets     = new();
            FilledFromLead  = new();
            InvoiceTargets  = new();   // 081
            FilledFromQuote = new();   // 081

            try
            {
                if (IsLeadTab)
                {
                    ContactTargets = await _fields.GetDefinitionsAsync(CustomFieldEntityTypes.Contact, includeInactive: true);
                    DealTargets    = await _fields.GetDefinitionsAsync(CustomFieldEntityTypes.Deal, includeInactive: true);
                }
                else if (ActiveEntity == CustomFieldEntityTypes.Contact || ActiveEntity == CustomFieldEntityTypes.Deal)
                {
                    var leadFields = await _fields.GetDefinitionsAsync(CustomFieldEntityTypes.Lead, includeInactive: true);
                    var contact = ActiveEntity == CustomFieldEntityTypes.Contact;

                    foreach (var lf in leadFields)
                    {
                        var target = contact ? lf.MapToContactFieldId : lf.MapToDealFieldId;
                        if (target is { } t) FilledFromLead[t] = lf.Label;
                    }
                }
                else if (IsQuoteTab)                                                 // 081
                {
                    InvoiceTargets = await _fields.GetDefinitionsAsync(CustomFieldEntityTypes.Invoice, includeInactive: true);
                }
                else if (IsInvoiceTab)                                               // 081
                {
                    var quoteFields = await _fields.GetDefinitionsAsync(CustomFieldEntityTypes.Quote, includeInactive: true);
                    foreach (var qf in quoteFields)
                        if (qf.MapToInvoiceFieldId is { } t) FilledFromQuote[t] = qf.Label;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not load the mapping context for {Entity}", ActiveEntity);
                MappingLoadFailed = true;
            }
        }

        /// <summary>079 / 081. The other side of the mapping could not be loaded.</summary>
        public bool MappingLoadFailed { get; private set; }

        private static InputModel FromDto(CustomFieldDefinitionDto row)
        {
            var input = new InputModel
            {
                Id            = row.Id,
                Label         = row.Label,
                FieldType     = row.FieldType,
                HelpText      = row.HelpText,
                IsRequired    = row.IsRequired,
                IsActive      = row.IsActive,
                ShowInList    = row.ShowInList,
                DecimalPlaces = row.DecimalPlaces,
                MapToContactFieldId = row.MapToContactFieldId,   // 079
                MapToDealFieldId    = row.MapToDealFieldId,
                PrintOnDocuments    = row.PrintOnDocuments,      // 081
                MapToInvoiceFieldId = row.MapToInvoiceFieldId,   // 081
                Options       = row.Options
                    .Select(o => new OptionInput { Key = o.Key, Label = o.Label, IsActive = o.IsActive })
                    .ToList()
            };

            // One spare row to type into. Blank rows are ignored on save.
            if (row.FieldType == CustomFieldTypes.Dropdown)
                input.Options.Add(new OptionInput());

            return input;
        }

        private static SaveCustomFieldDefinitionDto ToDto(InputModel input, string entityType) => new()
        {
            EntityType    = entityType,
            Label         = input.Label?.Trim() ?? string.Empty,
            FieldType     = input.FieldType,
            HelpText      = string.IsNullOrWhiteSpace(input.HelpText) ? null : input.HelpText.Trim(),
            IsRequired    = input.IsRequired,
            IsActive      = input.IsActive,
            ShowInList    = input.ShowInList,
            DecimalPlaces = input.DecimalPlaces,
            // 079 — only meaningful for Lead fields; the API stores null otherwise.
            MapToContactFieldId = entityType == CustomFieldEntityTypes.Lead ? input.MapToContactFieldId : null,
            MapToDealFieldId    = entityType == CustomFieldEntityTypes.Lead ? input.MapToDealFieldId : null,
            // 081 — the API forces both off for types they do not apply to.
            PrintOnDocuments    = CustomFieldEntityTypes.SupportsPrint(entityType) && input.PrintOnDocuments,
            MapToInvoiceFieldId = CustomFieldEntityTypes.SupportsInvoiceMapping(entityType) ? input.MapToInvoiceFieldId : null,
            Options       = (input.Options ?? new List<OptionInput>())
                .Select(o => new CustomFieldOptionDto
                {
                    Key      = o.Key?.Trim() ?? string.Empty,
                    Label    = o.Label?.Trim() ?? string.Empty,
                    IsActive = o.IsActive
                })
                .ToList()
        };
    }
}
