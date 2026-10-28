// =====================================================================
// FILE: MerkaiTrial.WebApi/Filters/RequestOwnershipFilter.cs
//
// NEW (104). The caller's identity always wins over what the request says
// about it — on EVERY endpoint, not only the ones that remembered.
//
// WHAT WAS WRONG
//   Most endpoints already did `dto.TenantId = <the signed-in workspace>`
//   before calling their handler. Some did not. POST /api/leads passed the
//   body straight through, so a workspace B admin could send
//   { "tenantId": "<workspace A>", … } and the lead was CREATED IN
//   WORKSPACE A — the query filters guard reads, not inserts. Author
//   fields had the same gap the other way round: a note, reminder or deal
//   could be saved as written "by" anyone the caller named.
//
// WHAT IT DOES — before the action runs, for a signed-in workspace user:
//   • an action argument called tenantId (route, query or body) becomes
//     the caller's workspace;
//   • a TenantId property on a bound object (the body's DTO) becomes the
//     caller's workspace;
//   • an author property on it (CreatedBy, UpdatedBy, ConvertedBy,
//     ImportedBy, ChangedBy, AssignedBy, DeletedBy) that names someone
//     else becomes the caller. Empty stays empty (the handler fills it in);
//     the caller's own id, email or name stays as sent, so every page that
//     already sends the right author behaves exactly as before. A value
//     shaped like an id becomes the caller's id; anything else becomes the
//     caller's name — the same kind of value the field was given.
//
// NOT TOUCHED
//   • The super admin (outside View As): the platform endpoints act on any
//     workspace by design, and those check IsSuperAdmin themselves.
//   • Anonymous endpoints (the customer's quote and invoice links,
//     webhooks): no caller workspace to impose.
//   • Lists of objects inside a body: nothing in the app binds a TenantId
//     there, and each handler checks the records it is given.
// =====================================================================

using System.Collections.Concurrent;
using System.Reflection;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MerkaiTrial.WebApi.Filters;

public sealed class RequestOwnershipFilter : IAsyncActionFilter
{
    private static readonly string[] AuthorProperties =
    {
        "CreatedBy", "UpdatedBy", "ConvertedBy", "ImportedBy", "ChangedBy", "AssignedBy", "DeletedBy",
    };

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> TenantProps = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> AuthorProps = new();

    private readonly ILogger<RequestOwnershipFilter> _logger;

    public RequestOwnershipFilter(ILogger<RequestOwnershipFilter> logger) => _logger = logger;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        var isSuperAdmin = user.HasClaim("IsSuperAdmin", "true") && !user.HasClaim("ViewingAs", "true");

        if (user.Identity?.IsAuthenticated != true || isSuperAdmin
            || !Guid.TryParse(user.FindFirst("TenantId")?.Value, out var tenantId)
            || !Guid.TryParse(user.FindFirst("UserId")?.Value, out var userId))
        {
            await next();
            return;
        }

        CurrentUserContext? me = null;
        async Task<CurrentUserContext?> MeAsync()
        {
            if (me is not null) return me;
            try
            {
                me = await context.HttpContext.RequestServices
                    .GetRequiredService<ICurrentUserService>().GetCurrentUserAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load the caller to check a request's author fields");
            }
            return me;
        }

        foreach (var key in context.ActionArguments.Keys.ToList())
        {
            var value = context.ActionArguments[key];

            // ── tenantId as an argument of its own ─────────────────────
            if (string.Equals(key, "tenantId", StringComparison.OrdinalIgnoreCase))
            {
                var paramType = context.ActionDescriptor.Parameters
                    .FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase))?.ParameterType
                    ?? value?.GetType();
                if (paramType is null) continue;
                var replaced = Replace(value, paramType, tenantId);
                if (replaced.Changed)
                {
                    LogOverride(context, "tenantId argument", value);
                    context.ActionArguments[key] = replaced.Value;
                }
                continue;
            }

            if (value is null || value is string || value.GetType().IsValueType) continue;
            var type = value.GetType();
            if (value is System.Collections.IEnumerable) continue;

            // ── TenantId on the bound object ───────────────────────────
            foreach (var p in TenantProps.GetOrAdd(type, FindTenantProps))
            {
                var current = p.GetValue(value);
                var replaced = Replace(current, p.PropertyType, tenantId);
                if (!replaced.Changed) continue;
                LogOverride(context, $"{type.Name}.TenantId", current);
                p.SetValue(value, replaced.Value);
            }

            // ── author fields on the bound object ──────────────────────
            foreach (var p in AuthorProps.GetOrAdd(type, FindAuthorProps))
            {
                var sent = p.GetValue(value) as string;
                if (string.IsNullOrWhiteSpace(sent)) continue;
                var trimmed = sent.Trim();
                if (string.Equals(trimmed, userId.ToString(), StringComparison.OrdinalIgnoreCase)) continue;

                var caller = await MeAsync();
                if (caller is not null
                    && (string.Equals(trimmed, caller.Email, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(trimmed, caller.FullName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var replacement = Guid.TryParse(trimmed, out _)
                    ? userId.ToString()
                    : caller?.FullName ?? userId.ToString();

                _logger.LogWarning(
                    "{Method} {Path}: {Type}.{Property} named someone other than the caller — set to the caller.",
                    context.HttpContext.Request.Method, context.HttpContext.Request.Path, type.Name, p.Name);
                p.SetValue(value, replacement);
            }
        }

        await next();
    }

    /// <summary>
    /// The caller's workspace in the slot's own type (Guid, Guid? or string);
    /// Changed is false when it already was. Any other type is left alone.
    /// </summary>
    private static (bool Changed, object? Value) Replace(object? current, Type slotType, Guid tenantId)
    {
        if (slotType == typeof(Guid) || slotType == typeof(Guid?))
            return current is Guid g && g == tenantId ? (false, current) : (true, tenantId);

        if (slotType == typeof(string))
            return current is string s && Guid.TryParse(s, out var parsed) && parsed == tenantId
                ? (false, current)
                : (true, tenantId.ToString());

        return (false, current);
    }

    private void LogOverride(ActionExecutingContext context, string what, object? sent)
    {
        // Empty is the ordinary case (the body left it out); only a value
        // naming another workspace is worth a warning.
        if (sent is null || (sent is Guid g && g == Guid.Empty) || (sent is string s && string.IsNullOrWhiteSpace(s)))
            return;
        _logger.LogWarning("{Method} {Path}: {What} named another workspace ({Sent}) — set to the caller's.",
            context.HttpContext.Request.Method, context.HttpContext.Request.Path, what, sent);
    }

    private static PropertyInfo[] FindTenantProps(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
         .Where(p => p.Name == "TenantId" && p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                  && (p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?) || p.PropertyType == typeof(string)))
         .ToArray();

    private static PropertyInfo[] FindAuthorProps(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
         .Where(p => AuthorProperties.Contains(p.Name) && p.CanRead && p.CanWrite
                  && p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
         .ToArray();
}
