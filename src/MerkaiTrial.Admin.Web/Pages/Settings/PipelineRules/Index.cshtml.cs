// =====================================================================
// Settings/PipelineRules/Index.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/Settings/PipelineRules/Index.cshtml.cs
//
// COMPLETE FILE — replaces the 021 version.
//
// CHANGES (022)
//   ✅ Each step carries its ACTIONS — what happens after a deal takes it.
//   ✅ They travel as JSON in one hidden field per cell rather than as a
//      nested Cells[i].Actions[j] binding. A variable-length list inside a
//      variable-length list is where Razor Pages model binding stops being
//      worth the fight: indexes have to stay contiguous, adding a row
//      client-side means renumbering every input after it, and one gap
//      silently truncates the post. One string per cell is read and
//      written by the same panel that edits it, and the server parses it
//      once — far less to go wrong, and the form's shape stays flat.
//
// CHANGES (021)
//   ✅ A from × to GRID replaces thirty stacked rows. The whole process
//      fits on one screen and you can see its shape; clicking a cell opens
//      one step in a side panel. The binding underneath is unchanged — the
//      grid and the panel both read and write the same hidden inputs, so
//      the server side of this page is the same code it was.
//   ✅ Three named TEMPLATES replace "Apply suggested process", and they
//      are applied in the BROWSER. Clicking one fills the form; nothing is
//      written until Save, and Cancel undoes it. 020 saved on click, which
//      made looking at a template an expensive thing to do.
//   ✅ A read-only flow diagram above the grid.
//
// WHAT THIS PAGE IS NOW
//   The sales process. For each stage, the moves a deal can make out of
//   it: what the button says, who may press it, and what the deal needs
//   first. Plus the one rule that belongs to no single move — an invoiced
//   deal cannot be reopened.
//
// WHAT WENT
//   019's five per-stage requirement checkboxes and its three whole-
//   pipeline switches. Requirements moved onto the transition, where the
//   same stage can ask different things depending on where the deal came
//   from. ForwardOnly is now expressed by which moves exist; the two
//   reopen rules by the Actor and the note on the moves out of a closed
//   stage.
//
// THE GRID IS ALWAYS COMPLETE
//   Every from-to pair is rendered whether a row exists or not. A tenant
//   who ran the migration with ForwardOnly on has no backward rows at
//   all, and a grid with holes in it would be impossible to reason about.
//   Saving creates whatever is missing.
//
// Admin-only to change; readable by anyone who can read deals, because a
// manager who cannot work out why a deal will not move should be able to
// look the rule up rather than ask.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.Admin.Web.Services.Pipeline;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.PipelineRules
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly IPipelineRuleService _ruleService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<IndexModel> _logger;

        protected override string ModuleName => Modules.Deals;

        public IndexModel(
            IPipelineRuleService ruleService,
            ICurrentUserService currentUserService,
            IAuthorizationService authorizationService,
            ILogger<IndexModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _ruleService = ruleService;
            _currentUserService = currentUserService;
            _logger = logger;
        }

        // ── View state ─────────────────────────────────────────────────

        /// <summary>
        /// Never null once a handler has run. A failed load leaves an empty
        /// process here rather than null, so the view renders the page and
        /// its error message instead of bailing out halfway through its own
        /// markup.
        /// </summary>
        public PipelineRulesDto Rules { get; private set; } = Empty();

        public bool LoadFailed { get; private set; }

        /// <summary>
        /// Admin-only to change, and the page says so rather than hiding
        /// itself: a sales manager who wonders why a deal will not move
        /// should be able to read the rule that stopped it.
        /// </summary>
        public bool CanEditRules { get; private set; }

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        // ── Posted form ────────────────────────────────────────────────

        [BindProperty] public bool BlockReopenWithIssuedInvoice { get; set; }

        /// <summary>
        /// One entry per from-to pair, in grid order. A list of a flat
        /// input model rather than the DTO: Razor Pages needs settable
        /// properties and an index to bind a collection, and a positional
        /// record gives it neither.
        /// </summary>
        [BindProperty] public List<CellInput> Cells { get; set; } = new();

        public class CellInput
        {
            // Identity — hidden fields, so the server knows which cell this
            // row of checkboxes belongs to.
            public string FromStageKey { get; set; } = string.Empty;
            public string ToStageKey { get; set; } = string.Empty;

            // Display only; re-resolved on the server after a failed post.
            public string FromStageName { get; set; } = string.Empty;
            public string ToStageName { get; set; } = string.Empty;
            public StageCategory ToCategory { get; set; }
            public bool ToIsActive { get; set; } = true;

            public bool IsActive { get; set; }
            public string Label { get; set; } = string.Empty;
            public TransitionActor Actor { get; set; }

            public bool RequiresQuote { get; set; }
            public bool RequiresAcceptedQuote { get; set; }
            public bool RequiresValue { get; set; }
            public bool RequiresCloseDate { get; set; }
            public bool RequiresAttachment { get; set; }
            public bool RequiresNote { get; set; }
            public string? NotePrompt { get; set; }

            /// <summary>
            /// This step's actions, as a JSON array. Written by the side
            /// panel, parsed on save. See the note at the top of the file
            /// for why this is not a nested binding.
            /// </summary>
            public string ActionsJson { get; set; } = "[]";
        }

        /// <summary>The shape carried in ActionsJson. Mirrored in the page's script.</summary>
        public class ActionInput
        {
            public int Kind { get; set; }              // 0 task, 1 log
            public bool IsActive { get; set; } = true;
            public string Subject { get; set; } = string.Empty;
            public string? Description { get; set; }
            public string ActivityType { get; set; } = "Task";
            public int AssignTo { get; set; }          // 0 owner, 1 mover, 2 specific
            public Guid? AssignToUserId { get; set; }
            public int DueInDays { get; set; } = 1;
        }

        // ── GET ────────────────────────────────────────────────────────

        public async Task<IActionResult> OnGetAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;

            await InitializePermissionsAsync();

            var me = await _currentUserService.GetCurrentUserAsync();
            CanEditRules = me.IsTenantAdmin;

            try
            {
                Rules = await _ruleService.GetAsync();
                BuildGrid();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load the pipeline process");
                LoadFailed = true;
                ErrorMessage = "Failed to load the sales process. Please try again.";
                return Page();
            }
        }

        // ── POST: save ─────────────────────────────────────────────────

        public async Task<IActionResult> OnPostAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            await InitializePermissionsAsync();

            var me = await _currentUserService.GetCurrentUserAsync();
            CanEditRules = me.IsTenantAdmin;

            if (!CanEditRules)
            {
                // The API refuses this too. Checked here as well so the
                // message is the page's rather than a bare 403.
                ErrorMessage = "Only a workspace admin can change the sales process.";
                await ReloadAsync();
                return Page();
            }

            try
            {
                var dto = new SaveAllPipelineRulesDto(
                    BlockReopenWithIssuedInvoice,
                    Cells.Select(c => new SaveTransitionDto(
                        c.FromStageKey,
                        c.ToStageKey,
                        c.Label,
                        c.IsActive,
                        c.Actor,
                        c.RequiresQuote,
                        c.RequiresAcceptedQuote,
                        c.RequiresValue,
                        c.RequiresCloseDate,
                        c.RequiresNote,
                        c.NotePrompt,
                        c.RequiresAttachment,
                        ParseActions(c.ActionsJson))).ToList());

                await _ruleService.SaveAsync(dto);

                SuccessMessage = "Sales process saved.";
                return RedirectToPage();
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                await ReloadAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save the pipeline process");
                ErrorMessage = "Failed to save the sales process. Please try again.";
                await ReloadAsync();
                return Page();
            }
        }

        /* OnPostSuggestAsync was removed in 021. A template is applied in
           the browser now — the click fills the form and Save writes it,
           so there is nothing to post and Cancel still means cancel. */

        // ── building the grid ──────────────────────────────────────────

        /// <summary>
        /// Every from-to pair, whether a transition row exists or not. The
        /// migration seeds a full matrix, but a tenant who ran it with
        /// ForwardOnly on has no backward rows, and a stage added later has
        /// none at all until it is saved — a grid with holes would be
        /// impossible to reason about.
        /// </summary>
        private void BuildGrid()
        {
            BlockReopenWithIssuedInvoice = Rules.BlockReopenWithIssuedInvoice;

            var byPair = Rules.Transitions
                .GroupBy(t => (t.FromStageKey, t.ToStageKey))
                .ToDictionary(g => g.Key, g => g.First());

            var cells = new List<CellInput>();

            // Out of EVERY stage, including retired ones: a deal parked in a
            // stage the tenant has withdrawn still needs a way forward.
            foreach (var from in Rules.Stages.OrderBy(s => s.SortOrder))
            {
                // Into ACTIVE stages only. Offering a retired stage as a
                // destination is exactly what retiring it was meant to stop.
                foreach (var to in Rules.Stages.Where(s => s.IsActive).OrderBy(s => s.SortOrder))
                {
                    if (from.Key == to.Key) continue;

                    byPair.TryGetValue((from.Key, to.Key), out var t);

                    cells.Add(new CellInput
                    {
                        FromStageKey = from.Key,
                        ToStageKey = to.Key,
                        FromStageName = from.Name,
                        ToStageName = to.Name,
                        ToCategory = to.Category,
                        ToIsActive = to.IsActive,

                        IsActive = t?.IsActive ?? false,
                        Label = t?.Label ?? DefaultLabel(from, to),
                        Actor = t?.Actor ?? TransitionActor.Anyone,

                        RequiresQuote = t?.RequiresQuote ?? false,
                        RequiresAcceptedQuote = t?.RequiresAcceptedQuote ?? false,
                        RequiresValue = t?.RequiresValue ?? false,
                        RequiresCloseDate = t?.RequiresCloseDate ?? false,
                        RequiresAttachment = t?.RequiresAttachment ?? false,
                        RequiresNote = t?.RequiresNote ?? false,
                        NotePrompt = t?.NotePrompt,

                        ActionsJson = JsonSerializer.Serialize(
                            (t?.Actions ?? new List<TransitionActionDto>())
                                .Select(a => new ActionInput
                                {
                                    Kind = (int)a.Kind,
                                    IsActive = a.IsActive,
                                    Subject = a.Subject,
                                    Description = a.Description,
                                    ActivityType = a.ActivityType,
                                    AssignTo = (int)a.AssignTo,
                                    AssignToUserId = a.AssignToUserId,
                                    DueInDays = a.DueInDays
                                }))
                    });
                }
            }

            Cells = cells;
        }

        /// <summary>
        /// Reads a cell's actions back out of its hidden field.
        ///
        /// Malformed JSON yields no actions rather than failing the save.
        /// The only thing that writes this field is our own panel, so a
        /// bad value means a bug on our side — and losing the whole
        /// process because one cell's actions would not parse is a far
        /// worse outcome than losing that cell's actions.
        /// </summary>
        private List<SaveTransitionActionDto> ParseActions(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<SaveTransitionActionDto>();

            try
            {
                var rows = JsonSerializer.Deserialize<List<ActionInput>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                return (rows ?? new List<ActionInput>())
                    .Where(a => !string.IsNullOrWhiteSpace(a.Subject))
                    .Select(a => new SaveTransitionActionDto(
                        (TransitionActionKind)a.Kind,
                        a.IsActive,
                        a.Subject,
                        a.Description,
                        a.ActivityType,
                        (TransitionAssignee)a.AssignTo,
                        a.AssignToUserId,
                        a.DueInDays))
                    .ToList();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "A step's actions could not be read and were skipped");
                return new List<SaveTransitionActionDto>();
            }
        }

        private static string DefaultLabel(StageLiteDto from, StageLiteDto to)
            => TransitionLabels.For(from.Category, to.Category, to.Name);

        /// <summary>
        /// After a failed save, reload what the page needs to RENDER — the
        /// stage names and categories — and leave the posted checkbox values
        /// alone, so an admin who ticked twenty boxes and hit a failure does
        /// not have to tick them again.
        /// </summary>
        private async Task ReloadAsync()
        {
            try
            {
                Rules = await _ruleService.GetAsync();

                var stages = Rules.Stages.ToDictionary(s => s.Key);

                foreach (var c in Cells)
                {
                    if (stages.TryGetValue(c.FromStageKey, out var f))
                        c.FromStageName = f.Name;

                    if (stages.TryGetValue(c.ToStageKey, out var t))
                    {
                        c.ToStageName = t.Name;
                        c.ToCategory = t.Category;
                        c.ToIsActive = t.IsActive;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload the pipeline process after a failed save");
                LoadFailed = true;
            }
        }

        // ── 021: the grid ──────────────────────────────────────────────

        /// <summary>Rows of the matrix — every stage a deal can leave.</summary>
        public List<StageLiteDto> GridRows =>
            Rules.Stages.OrderBy(s => s.SortOrder).ToList();

        /// <summary>Columns — only stages a deal can still be moved into.</summary>
        public List<StageLiteDto> GridColumns =>
            Rules.Stages.Where(s => s.IsActive).OrderBy(s => s.SortOrder).ToList();

        private Dictionary<string, int>? _cellIndex;

        /// <summary>
        /// Where a given from-to pair sits in Cells. The grid and the side
        /// panel both address the hidden inputs by index, so this is the
        /// one lookup that ties the visual layer to the binding.
        /// </summary>
        public int? IndexOf(string fromKey, string toKey)
        {
            _cellIndex ??= Cells
                .Select((c, i) => (c, i))
                .GroupBy(x => $"{x.c.FromStageKey}|{x.c.ToStageKey}")
                .ToDictionary(g => g.Key, g => g.First().i);

            return _cellIndex.TryGetValue($"{fromKey}|{toKey}", out var idx) ? idx : null;
        }

        public CellInput? CellAt(string fromKey, string toKey)
        {
            var i = IndexOf(fromKey, toKey);
            return i is null ? null : Cells[i.Value];
        }

        /// <summary>
        /// A one-glance summary for a grid cell: the initials of what it
        /// requires. "AQ" accepted quote, "Q" quote, "V" value, "D" date,
        /// "F" file, "N" note. Long enough to be a hint, short enough to
        /// fit in a cell — the full wording is in the side panel.
        /// </summary>
        public string CellMarks(CellInput c)
        {
            var marks = new List<string>();
            if (c.RequiresAcceptedQuote) marks.Add("AQ");
            else if (c.RequiresQuote) marks.Add("Q");
            if (c.RequiresValue) marks.Add("V");
            if (c.RequiresCloseDate) marks.Add("D");
            if (c.RequiresAttachment) marks.Add("F");
            if (c.RequiresNote) marks.Add("N");
            return string.Join(" ", marks);
        }

        // ── 021: templates, applied in the browser ─────────────────────

        public IReadOnlyList<ProcessTemplate> Templates => ProcessTemplates.All;

        /// <summary>
        /// All three templates, computed over THIS tenant's stages and
        /// serialised for the page. Clicking one rewrites the form's hidden
        /// inputs; nothing is saved until the person presses Save.
        ///
        /// Computed server-side because the rules about which stage is
        /// "last open" and which is the starting one belong with the rest
        /// of the process logic, not in a script tag.
        /// </summary>
        public string TemplatesJson
        {
            get
            {
                // The DTOs the page holds are enough to drive the
                // templates: they only need each stage's key, order,
                // category and whether it is the default.
                var stages = Rules.Stages.Select(s => new PipelineStage
                {
                    Id = s.Id,
                    Key = s.Key,
                    Name = s.Name,
                    SortOrder = s.SortOrder,
                    Category = s.Category,
                    IsActive = s.IsActive,
                    IsDefault = s.IsDefault
                }).ToList();

                var payload = ProcessTemplates.All.ToDictionary(
                    t => t.Key,
                    t => ProcessTemplates.Build(t.Key, stages)
                        .ToDictionary(
                            kv => kv.Key,
                            kv => new
                            {
                                active = kv.Value.IsActive,
                                actor = (int)kv.Value.Actor,
                                q = kv.Value.RequiresQuote,
                                aq = kv.Value.RequiresAcceptedQuote,
                                v = kv.Value.RequiresValue,
                                d = kv.Value.RequiresCloseDate,
                                n = kv.Value.RequiresNote,
                                prompt = kv.Value.NotePrompt
                            }));

                return System.Text.Json.JsonSerializer.Serialize(payload,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        // Lands inside a <script> block.
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
                    });
            }
        }

        // ── 021: the diagram ───────────────────────────────────────────

        public record DiagramBox(string Key, string Name, StageCategory Category,
                                 double X, double Y, double W, double H);

        public record DiagramArrow(string Path, string Kind, string Title);

        public record DiagramLayout(double Width, double Height,
                                    List<DiagramBox> Boxes, List<DiagramArrow> Arrows);

        /// <summary>
        /// The process as a picture. Open stages run left to right; Won sits
        /// above the end of the line and Lost below it, which is how anyone
        /// draws a pipeline on a whiteboard.
        ///
        /// Read-only on purpose. A drag-and-drop canvas is a week of
        /// JavaScript and its own class of bugs; a diagram you can look at
        /// answers "is my process right?" for a fraction of that, and the
        /// grid below is where it gets changed.
        /// </summary>
        public DiagramLayout Diagram
        {
            get
            {
                const double boxW = 132, boxH = 46, gapX = 58;
                const double openY = 132, wonY = 24, lostY = 240;
                const double padL = 20;

                var open = GridColumns.Where(s => s.Category == StageCategory.Open).ToList();
                var won  = GridColumns.Where(s => s.Category == StageCategory.Won).ToList();
                var lost = GridColumns.Where(s => s.Category == StageCategory.Lost).ToList();

                var boxes = new List<DiagramBox>();
                var at = new Dictionary<string, DiagramBox>();

                for (var i = 0; i < open.Count; i++)
                {
                    var b = new DiagramBox(open[i].Key, open[i].Name, StageCategory.Open,
                                           padL + i * (boxW + gapX), openY, boxW, boxH);
                    boxes.Add(b); at[b.Key] = b;
                }

                // The closed column sits one slot past the last open stage.
                var closedX = padL + open.Count * (boxW + gapX);

                for (var i = 0; i < won.Count; i++)
                {
                    var b = new DiagramBox(won[i].Key, won[i].Name, StageCategory.Won,
                                           closedX, wonY + i * (boxH + 10), boxW, boxH);
                    boxes.Add(b); at[b.Key] = b;
                }

                for (var i = 0; i < lost.Count; i++)
                {
                    var b = new DiagramBox(lost[i].Key, lost[i].Name, StageCategory.Lost,
                                           closedX, lostY + i * (boxH + 10), boxW, boxH);
                    boxes.Add(b); at[b.Key] = b;
                }

                var arrows = new List<DiagramArrow>();

                foreach (var c in Cells.Where(x => x.IsActive))
                {
                    if (!at.TryGetValue(c.FromStageKey, out var f)) continue;
                    if (!at.TryGetValue(c.ToStageKey, out var t)) continue;

                    var title = $"{f.Name} → {t.Name}: {c.Label}";

                    // Leaving a closed stage — the long way back.
                    if (f.Category != StageCategory.Open)
                    {
                        var y = f.Category == StageCategory.Won ? f.Y + f.H / 2 : f.Y + f.H / 2;
                        var sweep = f.Category == StageCategory.Won ? -70 : 70;
                        arrows.Add(new DiagramArrow(
                            $"M {f.X} {y} C {f.X - 120} {y + sweep}, {t.X + t.W + 120} {t.Y + t.H / 2 + sweep}, {t.X + t.W} {t.Y + t.H / 2}",
                            "reopen", title));
                        continue;
                    }

                    // Closing.
                    if (t.Category != StageCategory.Open)
                    {
                        arrows.Add(new DiagramArrow(
                            $"M {f.X + f.W} {f.Y + f.H / 2} C {f.X + f.W + 40} {f.Y + f.H / 2}, {t.X - 40} {t.Y + t.H / 2}, {t.X} {t.Y + t.H / 2}",
                            t.Category == StageCategory.Won ? "won" : "lost", title));
                        continue;
                    }

                    // Forward along the line.
                    if (t.X > f.X)
                    {
                        arrows.Add(new DiagramArrow(
                            $"M {f.X + f.W} {f.Y + f.H / 2} L {t.X} {t.Y + t.H / 2}",
                            "forward", title));
                        continue;
                    }

                    // Back one — drawn beneath so it never overlaps the line.
                    var mid = (f.X + t.X + t.W) / 2;
                    arrows.Add(new DiagramArrow(
                        $"M {f.X} {f.Y + f.H - 8} C {mid} {f.Y + f.H + 46}, {mid} {t.Y + t.H + 46}, {t.X + t.W} {t.Y + t.H - 8}",
                        "back", title));
                }

                var width = closedX + boxW + padL + 20;
                var height = lostY + (Math.Max(1, lost.Count) * (boxH + 10)) + 30;

                return new DiagramLayout(width, height, boxes, arrows);
            }
        }

        // ── view helpers ───────────────────────────────────────────────

        public StageLiteDto? StageByKey(string key) =>
            Rules.Stages.FirstOrDefault(s => s.Key == key);

        /// <summary>
        /// A stage nothing can leave. Deals there are stuck unless an admin
        /// overrides on the deal page, so the page says so loudly.
        /// </summary>
        public bool IsDeadEnd(string stageKey) =>
            Cells.Where(c => c.FromStageKey == stageKey).All(c => !c.IsActive);

        public int LiveCount => Cells.Count(c => c.IsActive);

        // ── 022: what the panel's action editor needs ──────────────────

        /// <summary>Active users, for "a specific person".</summary>
        public string AssigneesJson => JsonSerializer.Serialize(
            Rules.Assignees.Select(a => new { id = a.UserId, name = a.Name }),
            new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
            });

        /// <summary>
        /// Which activity types may be scheduled and which may only be
        /// logged. Taken from ActivityType rather than retyped here, so the
        /// panel can never offer something CreateActivityHandler will
        /// refuse.
        /// </summary>
        public string ActivityTypesJson => JsonSerializer.Serialize(new
        {
            task = MerkaiTrial.Application.Commands.Activities.ActivityType.Schedulable,
            log = MerkaiTrial.Application.Commands.Activities.ActivityType.Loggable
        });

        /// <summary>How many actions a cell has, for the grid badge.</summary>
        public int ActionCount(CellInput c)
        {
            if (string.IsNullOrWhiteSpace(c.ActionsJson)) return 0;
            try
            {
                return JsonSerializer.Deserialize<List<ActionInput>>(c.ActionsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?
                    .Count(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Subject)) ?? 0;
            }
            catch (JsonException) { return 0; }
        }

        public string CategoryLabel(StageCategory c) => c switch
        {
            StageCategory.Won => "Won",
            StageCategory.Lost => "Lost",
            _ => "Open"
        };

        public string CategoryBadge(StageCategory c) => c switch
        {
            StageCategory.Won => "bg-success",
            StageCategory.Lost => "bg-danger",
            _ => "bg-secondary"
        };

        public string ActorLabel(TransitionActor a) => a switch
        {
            TransitionActor.DealOwner => "Deal owner",
            TransitionActor.TeamManagers => "Managers + admins",
            TransitionActor.Admins => "Admins only",
            _ => "Anyone"
        };

        private static PipelineRulesDto Empty() => new(
            PipelineRuleDefaults.BlockReopenWithIssuedInvoice,
            IsDefault: true,
            UpdatedAtUtc: null,
            UpdatedBy: null,
            Stages: new List<StageLiteDto>(),
            Transitions: new List<TransitionDto>(),
            DeadEndStageKeys: new List<string>(),
            Assignees: new List<AssigneeDto>());
    }
}
