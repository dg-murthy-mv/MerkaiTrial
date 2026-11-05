// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Filters/SuperAdminPageFilter.cs
//
// NEW (119). Two jobs on every /Admin page (the super admin console):
//
// 1. NOT WHILE VIEWING AS. A super admin who is viewing as someone works
//    as that person: their API calls carry that workspace and that
//    person's access. Platform pages opened in that state either fail
//    oddly or act with the wrong identity. Every /Admin page except View
//    As itself (where "Stop viewing as" lives) now sends them there.
//
// 2. EVERY CHANGE IS RECORDED. Every form sent (POST) on an /Admin page is
//    written to the audit log as PlatformChange, AFTER it ran:
//      area      Workspace, Plan, Country, Tax rate, Vertical, …
//      page      /Admin/Tenants/Detail     handler  Delete
//      outcome   done | refused | failed
//      message   the sentence the page showed ("Tenant deleted…",
//                "Type the workspace's name…")
//      target    the id / code it was about; workspaceId when it was a workspace
//      sent      the values in the form (never a password, token or key)
//    with who did it and from where (AuditService adds both). The row goes
//    into MadeeVision's own workspace; Admin → Audit Log reads it.
//
//    Pages that write their OWN, more precise rows into the customer's
//    workspace (People, MadeeVision Team, View As) carry
//    [RecordsOwnAudit] and are skipped here, so nothing is written twice.
//
//    Written in a SEPARATE scope (its own database context): a page whose
//    work failed half-way never has its half-made changes saved by the
//    audit write. And an audit failure never breaks the page.
// =====================================================================

using System.Reflection;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Filters;

/// <summary>119. This /Admin page writes its own audit rows; SuperAdminPageFilter skips it.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RecordsOwnAuditAttribute : Attribute { }

public class SuperAdminPageFilter : IAsyncPageFilter
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SuperAdminPageFilter> _logger;

    public SuperAdminPageFilter(IServiceScopeFactory scopes, ILogger<SuperAdminPageFilter> logger)
    {
        _scopes = scopes; _logger = logger;
    }

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var path = context.ActionDescriptor.ViewEnginePath ?? "";
        var user = context.HttpContext.User;
        var isAdminPage = path.Equals("/Admin/Index", StringComparison.OrdinalIgnoreCase)
                       || path.StartsWith("/Admin/", StringComparison.OrdinalIgnoreCase);

        if (!isAdminPage || !user.HasClaim(SignInService.ClaimIsSuperAdmin, "true"))
        {
            await next();
            return;
        }

        // ── 1. not while viewing as ────────────────────────────────────
        if (user.HasClaim(SignInService.ClaimViewingAs, "true")
            && !path.StartsWith("/Admin/ViewAs", StringComparison.OrdinalIgnoreCase))
        {
            if (context.HandlerInstance is PageModel pm)
                pm.TempData["ErrorMessage"] = "You are viewing as someone else. Stop viewing as first, then open the admin console.";
            context.Result = new RedirectToPageResult("/Admin/ViewAs/ViewAsIndex");
            return;
        }

        // ── 2. record what was sent ────────────────────────────────────
        var isPost = HttpMethods.IsPost(context.HttpContext.Request.Method);
        var recordsItself = context.HandlerInstance?.GetType().IsDefined(typeof(RecordsOwnAuditAttribute), inherit: true) == true;
        if (!isPost || recordsItself)
        {
            await next();
            return;
        }

        var sent = ReadForm(context.HttpContext.Request);
        var handler = context.HandlerMethod?.Name;
        var routeId = context.RouteData.Values.TryGetValue("id", out var rid) ? rid?.ToString() : null;
        var routeCode = context.RouteData.Values.TryGetValue("code", out var rc) ? rc?.ToString() : null;

        var executed = await next();

        try
        {
            var (outcome, message) = Outcome(context, executed);
            var area = AreaOf(path);
            var entityId = FirstGuid(routeId, Get(sent, "id"), Get(sent, "tenantId"), Get(sent, "planId"),
                                     Get(sent, "rateId"), Get(sent, "templateId"), Get(sent, "userId"));
            var workspaceId = area == "Workspace" ? FirstGuid(routeId, Get(sent, "id"), Get(sent, "tenantId")) : null;

            var actorTenant = user.FindFirst(SignInService.ClaimRealTenantId)?.Value
                              ?? user.FindFirst(SignInService.ClaimTenantId)?.Value;
            Guid? tenantId = Guid.TryParse(actorTenant, out var t) ? t : null;

            using var scope = _scopes.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            await audit.WriteAsync(AuditAction.PlatformChange, area, entityId, tenantId, new
            {
                area,
                page = path,
                handler = string.IsNullOrEmpty(handler) ? "Save" : handler,
                outcome,
                message = Cap(message, 400),
                target = routeCode ?? routeId ?? Get(sent, "code") ?? entityId?.ToString(),
                workspaceId,
                sent,
            });
        }
        catch (Exception ex)
        {
            // Never break the page over the record of it.
            _logger.LogError(ex, "Recording a super admin change on {Page} failed", path);
        }
    }

    // ── what happened ──────────────────────────────────────────────────

    private static (string Outcome, string? Message) Outcome(PageHandlerExecutingContext before, PageHandlerExecutedContext after)
    {
        if (after.Exception is not null && !after.ExceptionHandled)
            return ("failed", after.Exception.GetType().Name);

        var td = (after.HandlerInstance as PageModel)?.TempData;
        string? Peek(string key) => td?.Peek(key) as string;

        var error = Peek("ErrorMessage") ?? Peek("Error") ?? PropertyText(after.HandlerInstance, "ErrorMessage", "FormError");
        if (!string.IsNullOrWhiteSpace(error)) return ("refused", error);

        if (!before.ModelState.IsValid)
        {
            var first = before.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
            return ("refused", first ?? "The form had mistakes.");
        }

        return ("done", Peek("SuccessMessage") ?? Peek("Success"));
    }

    private static string? PropertyText(object? model, params string[] names)
    {
        if (model is null) return null;
        foreach (var n in names)
        {
            var p = model.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
            if (p?.PropertyType == typeof(string) && p.GetValue(model) is string s && !string.IsNullOrWhiteSpace(s))
                return s;
        }
        return null;
    }

    // ── what was sent ──────────────────────────────────────────────────

    private static readonly string[] Secret = { "password", "token", "secret", "apikey", "api_key", "key", "verification" };

    /// <summary>The form's values, minus anything secret; each cut short, at most 60 of them.</summary>
    private static Dictionary<string, string> ReadForm(HttpRequest request)
    {
        var sent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!request.HasFormContentType) return sent;
        foreach (var (name, values) in request.Form)
        {
            if (sent.Count >= 60) break;
            var lower = name.ToLowerInvariant();
            if (Secret.Any(s => lower.Contains(s))) continue;
            sent[name] = Cap(string.Join(", ", values.Where(v => v is not null)), 120) ?? "";
        }
        return sent;
    }

    private static string? Get(Dictionary<string, string> sent, string key) =>
        sent.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static Guid? FirstGuid(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (Guid.TryParse(c, out var g) && g != Guid.Empty) return g;
        return null;
    }

    private static string? Cap(string? s, int max) =>
        s is null ? null : (s.Length <= max ? s : s[..max] + "…");

    /// <summary>"/Admin/TaxRates/Edit" → "TaxRate". Also the audit row's EntityType.</summary>
    public static string AreaOf(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var folder = parts.Length > 1 ? parts[1] : "Console";
        return folder switch
        {
            "Tenants" => "Workspace",
            "Plans" => "Plan",
            "Countries" => "Country",
            "TaxRates" => "TaxRate",
            "Verticals" => "Vertical",
            "StartingRoles" => "StartingRole",
            "WhatsAppTemplates" => "WhatsAppTemplate",
            "Team" => "Team",
            _ => folder.Length > 60 ? folder[..60] : folder,
        };
    }
}
