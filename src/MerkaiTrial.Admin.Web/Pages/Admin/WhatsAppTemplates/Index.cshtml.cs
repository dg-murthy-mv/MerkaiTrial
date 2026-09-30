// =====================================================================
// IndexModel.cs
// Location: MerkaiTrial.Admin.Web/Pages/Admin/WhatsAppTemplates/Index.cshtml.cs
//
// NEW FILE (045).
//
// SUPER ADMIN ONLY, and not because of anything in this file:
// AddAdminWebPages() has
//
//     options.Conventions.AuthorizeFolder("/Admin", "SuperAdmin");
//
// so every page under Pages/Admin is gated by the folder. That is why the
// Plans page carries no attribute either, and it is a better arrangement
// than per-page attributes — a new page cannot forget.
//
// Handlers are injected DIRECTLY rather than called over the API, the
// same as Pages/Admin/Plans. They are registered in
// AddAdminWebDirectHandlers.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Notifications;      // 046
using MerkaiTrial.Application.Commands.Notifications;
using MerkaiTrial.Application.Services.Notifications;    // 046: MetaTemplate
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Admin.WhatsAppTemplates;

public class IndexModel : PageModel
{
    private readonly GetWhatsAppTemplatesHandler _get;
    private readonly SaveWhatsAppTemplatesHandler _save;
    private readonly SendWhatsAppTestHandler _test;
    private readonly IWhatsAppStatusService _status;          // 046
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        GetWhatsAppTemplatesHandler get,
        SaveWhatsAppTemplatesHandler save,
        SendWhatsAppTestHandler test,
        IWhatsAppStatusService status,                        // 046
        ILogger<IndexModel> logger)
    {
        _get = get;
        _save = save;
        _test = test;
        _status = status;
        _logger = logger;
    }

    public WhatsAppTemplatesDto? Data { get; private set; }

    /// <summary>
    /// 046. The API's answer about its OWN configuration, and Meta's about
    /// our templates.
    ///
    /// Until 046 this page reported whether ADMIN.WEB had a WhatsApp token,
    /// which it never does and never should — the token lives in the API,
    /// which is the process that sends. The badge said "Off" while sending
    /// worked perfectly.
    /// </summary>
    public WhatsAppStatusView? Status { get; private set; }

    /// <summary>Meta's record for a row, or null when it has no opinion yet.</summary>
    public MetaTemplate? MetaFor(WhatsAppTemplateRow row)
        => Status?.Find(row.TemplateName, row.LanguageCode);

    /// <summary>
    /// True when Meta is reachable AND says this template is not approved.
    /// The switch is disabled in that case — switching it on would queue
    /// messages that die at Meta hours later with a numeric code.
    ///
    /// Only when Meta is REACHABLE. An unreachable directory must not lock
    /// the page: a stale or missing answer is not evidence that a template
    /// is unapproved, and being unable to switch something on because a
    /// status call timed out would be its own kind of broken.
    /// </summary>
    public bool BlockedByMeta(WhatsAppTemplateRow row)
    {
        if (Status is not { DirectoryReachable: true }) return false;

        var meta = MetaFor(row);

        // Not in Meta's list at all: the name is wrong, or it was never
        // submitted. Either way it cannot send.
        return meta is null || !meta.IsApproved;
    }

    /// <summary>
    /// 049. Whether to actually DISABLE the switch — which is not the same
    /// question as BlockedByMeta, and conflating the two was a bug.
    ///
    /// A disabled checkbox cannot be clicked, in either direction. So a row
    /// that was already ON when Meta started saying no became impossible to
    /// turn OFF: the admin could see "Meta has no template by that name",
    /// see "Live" underneath it, and have no way to act on either. Every
    /// notification for that event kept queueing and dying at Meta.
    ///
    /// The guard's job is to stop you switching something ON before it is
    /// approved. Turning something OFF is always allowed, and is exactly
    /// what someone looking at that warning wants to do.
    /// </summary>
    public bool LockSwitch(WhatsAppTemplateRow row)
        => !row.IsActive && BlockedByMeta(row);

    /// <summary>
    /// 049. On, but Meta will refuse it. Worth saying loudly: this is the
    /// state where messages queue and die, and nothing on the screen used
    /// to connect those two facts.
    /// </summary>
    public bool LiveButBroken(WhatsAppTemplateRow row)
        => row.IsActive && BlockedByMeta(row);

    public IEnumerable<IGrouping<string, WhatsAppTemplateRow>> Groups
        => (Data?.Items ?? new List<WhatsAppTemplateRow>()).GroupBy(i => i.Group);

    /// <summary>
    /// Posted as parallel arrays keyed by the row's id, matched BY VALUE.
    ///
    /// The same trap as the notification settings page: an unchecked
    /// checkbox posts nothing, so a bound List&lt;T&gt; loses its indexes
    /// the moment someone unticks one in the middle and every row after it
    /// takes the previous row's values. A hidden id per row plus
    /// value-matched arrays cannot do that.
    /// </summary>
    [BindProperty] public List<string> Ids { get; set; } = new();
    [BindProperty] public List<string> Names { get; set; } = new();
    [BindProperty] public List<string> Languages { get; set; } = new();
    [BindProperty] public List<string> Categories { get; set; } = new();
    [BindProperty] public List<string> Previews { get; set; } = new();
    [BindProperty] public List<string> VariableCounts { get; set; } = new();
    [BindProperty] public List<string> ActiveIds { get; set; } = new();

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var items = new List<SaveWhatsAppTemplateDto>();

        for (var i = 0; i < Ids.Count; i++)
        {
            if (!Guid.TryParse(Ids[i], out var id) || id == Guid.Empty) continue;

            items.Add(new SaveWhatsAppTemplateDto(
                Id: id,
                TemplateName: At(Names, i),
                LanguageCode: At(Languages, i),
                Category: At(Categories, i),
                BodyPreview: At(Previews, i),
                VariableCount: int.TryParse(At(VariableCounts, i), out var count) ? count : 2,

                // Matched by value, not by position — see the note above.
                IsActive: ActiveIds.Contains(Ids[i], StringComparer.OrdinalIgnoreCase)));
        }

        if (items.Count == 0)
        {
            TempData["Error"] = "Nothing was submitted — please try again.";
            return RedirectToPage();
        }

        try
        {
            await _save.Handle(new SaveWhatsAppTemplatesDto(items));
            TempData["Success"] = "Templates saved.";
        }
        catch (InvalidOperationException ex)
        {
            // The handler's refusals are written for a person to read —
            // "expects 3 variables, Merkai sends 2" — so they go straight
            // through rather than being replaced with a generic message.
            TempData["Error"] = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save WhatsApp templates");
            TempData["Error"] = "The templates could not be saved just now.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(Guid templateId)
    {
        try
        {
            var name = await _test.Handle(templateId);

            TempData["Success"] =
                $"A test using \"{name}\" is queued. It goes to your own mobile, " +
                "usually within a minute — check the Email log if it doesn't arrive.";
        }
        catch (KeyNotFoundException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not queue a WhatsApp test for {TemplateId}", templateId);
            TempData["Error"] = "The test could not be queued just now.";
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        try
        {
            Data = await _get.Handle();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the WhatsApp templates");
            TempData["Error"] = "The templates could not be loaded just now.";
        }

        // 046. Separate try/catch, and the service never throws anyway: a
        // status panel must not be able to take the page down with it.
        Status = await _status.GetAsync();
    }

    private static string At(List<string> list, int index)
        => index >= 0 && index < list.Count ? list[index] ?? string.Empty : string.Empty;

    /// <summary>The Meta categories, cheapest first — which is also the order of preference.</summary>
    public static readonly string[] CategoryOptions = { "utility", "authentication", "marketing" };
}
