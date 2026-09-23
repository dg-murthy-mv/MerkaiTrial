// =====================================================================
// ProcessTemplates.cs
// Location: MerkaiTrial.Application/Commands/PipelineStages/ProcessTemplates.cs
//
// NEW FILE (021). Replaces SuggestedProcess from 020.
//
// THREE SHAPES A SALES PROCESS ACTUALLY TAKES
//   Rather than one "suggested" process, a tenant picks the shape that
//   matches how they sell. All three are computed over the stages THAT
//   TENANT ALREADY HAS — no template ever renames, adds or removes a
//   stage, so applying one is always safe and never touches a deal.
//
//   That constraint is the whole design. A template that shipped its own
//   stages could only be applied to an empty workspace, which is exactly
//   when nobody knows which one they want. Expressed over the tenant's
//   own stages, a template is something they can try on a Tuesday
//   afternoon with three years of deals in the system.
//
// THEY ARE CODE, NOT DATA
//   Same reasoning as DefaultPipelineStages. A template is our product
//   opinion about how selling works, not the client's configuration. It
//   needs no table, no migration and no settings screen — and when we
//   improve one, every tenant gets the better version the next time they
//   open the page.
//
// APPLIED IN THE BROWSER, SAVED BY THE PERSON
//   The page serialises all three and a click fills the form. Nothing is
//   written until Save, so a curious admin can look at Strict, decide
//   against it, and press Cancel. 020's version saved immediately, which
//   made "what does this button do?" an expensive question.
// =====================================================================

using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Application.Commands.PipelineStages;

/// <summary>One cell of a template: what this move should look like.</summary>
public sealed record TemplateMove(
    bool IsActive,
    TransitionActor Actor = TransitionActor.Anyone,
    bool RequiresQuote = false,
    bool RequiresAcceptedQuote = false,
    bool RequiresValue = false,
    bool RequiresCloseDate = false,
    bool RequiresNote = false,
    string? NotePrompt = null);

public sealed record ProcessTemplate(
    string Key,
    string Name,
    string Tagline,
    string WhoItSuits);

public static class ProcessTemplates
{
    public const string Linear = "linear";
    public const string Flexible = "flexible";
    public const string Strict = "strict";

    /// <summary>What the picker shows. Order is the order on screen.</summary>
    public static readonly IReadOnlyList<ProcessTemplate> All = new List<ProcessTemplate>
    {
        new(Flexible,
            "Flexible",
            "Forward, back one step, and close from anywhere.",
            "Most teams. Deals move at their own pace and can be lost at any point."),

        new(Linear,
            "Step by step",
            "Forward only. A deal is won or lost at the end.",
            "A defined delivery process where every deal goes through the same gates."),

        new(Strict,
            "Quote-driven",
            "Forward only, a quote before the last stage, an accepted quote before Won.",
            "Selling against written quotes, where nothing is won without one."),
    };

    /// <summary>
    /// The template as a map of "from|to" → what that move should be.
    /// Pairs the template does not mention are switched OFF; nothing is
    /// ever deleted, so a tenant's own labels and prompts survive a change
    /// of mind.
    /// </summary>
    public static Dictionary<string, TemplateMove> Build(
        string templateKey, IReadOnlyList<PipelineStage> stages)
    {
        var live = stages.Where(s => s.IsActive).OrderBy(s => s.SortOrder).ToList();

        var open = live.Where(s => s.Category == StageCategory.Open).ToList();
        var won = live.Where(s => s.Category == StageCategory.Won).ToList();
        var lost = live.Where(s => s.Category == StageCategory.Lost).ToList();
        var closed = won.Concat(lost).ToList();

        var map = new Dictionary<string, TemplateMove>();

        // Everything starts off. The template switches on what it wants.
        foreach (var f in live)
            foreach (var t in live)
                if (f.Key != t.Key)
                    map[Key(f.Key, t.Key)] = new TemplateMove(IsActive: false);

        if (open.Count == 0) return map;

        var last = open[^1];
        var start = live.FirstOrDefault(s => s.IsDefault && s.Category == StageCategory.Open) ?? open[0];

        for (var i = 0; i < open.Count; i++)
        {
            var here = open[i];

            // Forward one — every template has this; it is the pipeline.
            if (i + 1 < open.Count)
                map[Key(here.Key, open[i + 1].Key)] = new TemplateMove(
                    IsActive: true,
                    RequiresQuote: templateKey == Strict && open[i + 1].Key == last.Key,
                    RequiresValue: templateKey != Flexible);

            // Back one — only Flexible. Reps genuinely do move a deal back
            // when a client goes quiet, and a CRM that refuses just teaches
            // them to leave the stage wrong.
            if (templateKey == Flexible && i - 1 >= 0)
                map[Key(here.Key, open[i - 1].Key)] = new TemplateMove(IsActive: true);

            // Closing.
            var canCloseFromHere = templateKey == Flexible || here.Key == last.Key;

            if (canCloseFromHere)
            {
                foreach (var w in won)
                    map[Key(here.Key, w.Key)] = new TemplateMove(
                        IsActive: true,
                        RequiresAcceptedQuote: templateKey == Strict,
                        RequiresValue: true,
                        RequiresCloseDate: templateKey != Flexible);

                foreach (var l in lost)
                    map[Key(here.Key, l.Key)] = new TemplateMove(
                        IsActive: true,
                        RequiresNote: true,
                        NotePrompt: TransitionLabels.LostPrompt);
            }
            else
            {
                // Even a strict process has to let a deal die early. A firm
                // that can only record a loss at the final stage is a firm
                // whose pipeline report is fiction.
                foreach (var l in lost)
                    map[Key(here.Key, l.Key)] = new TemplateMove(
                        IsActive: true,
                        RequiresNote: true,
                        NotePrompt: TransitionLabels.LostPrompt);
            }
        }

        // Reopening: back to where deals start, and nowhere else. One door,
        // with a manager's name on it.
        foreach (var c in closed)
        {
            map[Key(c.Key, start.Key)] = new TemplateMove(
                IsActive: true,
                Actor: templateKey == Flexible
                    ? TransitionActor.TeamManagers
                    : TransitionActor.Admins,
                RequiresNote: true,
                NotePrompt: TransitionLabels.ReopenPrompt);
        }

        return map;
    }

    /// <summary>
    /// The process a brand-new workspace gets. Flexible: a tenant who has
    /// never sold anything through the product should not meet a refusal
    /// in their first hour, and tightening later is a two-minute job on the
    /// settings page.
    /// </summary>
    public const string ForNewTenants = Flexible;

    public static string Key(string from, string to) => $"{from}|{to}";

    /// <summary>
    /// The template as ready-to-insert rows. Used when a workspace has no
    /// transitions at all — provisioning a new tenant, or repairing one
    /// whose process was never seeded.
    ///
    /// Only the moves the template switches ON become rows. The rest are
    /// created by the settings page the first time an admin saves, which
    /// keeps a new workspace's table small and readable.
    /// </summary>
    public static List<ProcessTransition> BuildRows(
        Guid tenantId,
        IReadOnlyList<PipelineStage> stages,
        string templateKey,
        DateTime nowUtc,
        string? createdBy)
    {
        var map = Build(templateKey, stages);
        var byKey = stages.ToDictionary(s => s.Key);
        var rows = new List<ProcessTransition>();

        foreach (var (pair, move) in map)
        {
            if (!move.IsActive) continue;

            var parts = pair.Split('|');
            if (parts.Length != 2) continue;
            if (!byKey.TryGetValue(parts[0], out var from)) continue;
            if (!byKey.TryGetValue(parts[1], out var to)) continue;

            rows.Add(new ProcessTransition
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                FromStageKey = from.Key,
                ToStageKey = to.Key,
                Label = TransitionLabels.For(from.Category, to.Category, to.Name),
                SortOrder = to.SortOrder,
                IsActive = true,
                Actor = move.Actor,
                RequiresQuote = move.RequiresQuote && !move.RequiresAcceptedQuote,
                RequiresAcceptedQuote = move.RequiresAcceptedQuote,
                RequiresValue = move.RequiresValue,
                RequiresCloseDate = move.RequiresCloseDate,
                RequiresNote = move.RequiresNote,
                NotePrompt = move.NotePrompt,
                RequiresAttachment = false,
                CreatedAtUtc = nowUtc,
                CreatedBy = createdBy
            });
        }

        return rows;
    }
}
