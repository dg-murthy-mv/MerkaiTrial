// =====================================================================
// FILE: MerkaiTrial.WebApi/Filters/PlanFeatureFilter.cs
//
// NEW (114). Two plan rules, checked for every endpoint, BEFORE the
// request body is read:
//
//   1. [RequiresPlanFeature(...)] on the controller or the action — the
//      workspace's plan must include the feature. If not: 403
//        { "title": "Not in your plan",
//          "detail": "Custom Fields is not included in the Starter plan…",
//          "feature": "custom_fields" }
//      Every attribute must pass; any one key in an attribute is enough.
//
//   2. [CountsTowardStorage] on an upload — the workspace's files plus this
//      upload must fit in its storage limit (TenantSettings.StorageLimit,
//      set from the plan). If not: 400 { "error": "Your workspace's file
//      storage is full …" }, which the pages show as the upload's error.
//
// A RESOURCE filter, not an action filter: it runs after authorization
// (so a signed-out caller still gets 401 and a missing permission still
// gets 403 for that reason) but before model binding, so a half-filled
// body is refused for the plan, not reported as a validation error — and a
// file that does not fit is never buffered.
//
// Registered globally in ApiServiceRegistration (options.Filters).
//
// 114-update1 — ONE small query. The first version asked
// ICurrentTenantService, which loads the whole workspace (tenant, country,
// settings, plan: 4 queries) on every gated call — the performance test
// caught the Inbox at +4 and the Activity Log at +8. Now it reads just the
// two columns it needs from TenantSettings (FeatureFlags, StorageLimit) in
// one query, and the plan's name only when it refuses.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.WebApi.Filters;

/// <summary>114. Marks an upload endpoint whose file counts toward the workspace's storage limit.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CountsTowardStorageAttribute : Attribute { }

public class PlanFeatureFilter : IAsyncResourceFilter
{
    private readonly ILogger<PlanFeatureFilter> _logger;

    public PlanFeatureFilter(ILogger<PlanFeatureFilter> logger) => _logger = logger;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var metadata = context.ActionDescriptor.EndpointMetadata;
        var required = metadata.OfType<RequiresPlanFeatureAttribute>().ToList();
        var storage = metadata.OfType<CountsTowardStorageAttribute>().Any();

        if (required.Count == 0 && !storage)
        {
            await next();
            return;
        }

        var http = context.HttpContext;
        var tenantClaim = http.User?.FindFirst("TenantId")?.Value;
        if (string.IsNullOrEmpty(tenantClaim) || !Guid.TryParse(tenantClaim, out var tenantId))
        {
            // No workspace to check (authorization already let it through).
            await next();
            return;
        }

        var db = http.RequestServices.GetRequiredService<FlowDbContext>();
        var settings = await db.TenantSettings.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .Select(s => new { s.FeatureFlags, s.StorageLimit })
            .FirstOrDefaultAsync(http.RequestAborted);
        var features = ParseFeatures(settings?.FeatureFlags);

        // ── 1. The plan's features ───────────────────────────────────
        foreach (var rule in required)
        {
            if (rule.AnyOf.Any(features.Contains)) continue;

            var key = rule.AnyOf.FirstOrDefault() ?? "";
            var label = FeatureCatalog.Resolve(key).Label;
            var plan = SafePlanName(http.RequestServices.GetRequiredService<ICurrentTenantService>());

            _logger.LogInformation("Refused {Method} {Path}: plan {Plan} of tenant {TenantId} has no {Feature}.",
                http.Request.Method, http.Request.Path, plan, tenantId, key);

            var problem = new ProblemDetails
            {
                Title = "Not in your plan",
                Detail = $"{label} is not included in the {plan} plan. Contact MadeeVision to change your plan.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://merkai/errors/plan-feature",
            };
            problem.Extensions["feature"] = key;
            context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        // ── 2. Storage ───────────────────────────────────────────────
        if (storage)
        {
            var limit = settings?.StorageLimit ?? 0L;
            if (limit > 0)
            {
                var used = await db.Attachments.AsNoTracking()
                    .Where(a => a.TenantId == tenantId && !a.IsDeleted)
                    .SumAsync(a => (long?)a.FileSize, http.RequestAborted) ?? 0L;

                // The whole request, not just the file: the form's few
                // hundred bytes of overhead never matter at a real limit,
                // and the file is not read until the check has passed.
                var incoming = http.Request.ContentLength ?? 0L;

                if (used + incoming > limit)
                {
                    _logger.LogInformation("Refused upload {Path}: tenant {TenantId} uses {Used} of {Limit} bytes.",
                        http.Request.Path, tenantId, used, limit);

                    context.Result = new BadRequestObjectResult(new
                    {
                        error = $"Your workspace's file storage is full ({Size(used)} of {Size(limit)} used). " +
                                "Delete files you no longer need, or contact MadeeVision to change your plan.",
                        storageUsedBytes = used,
                        storageLimitBytes = limit,
                    });
                    return;
                }
            }
        }

        await next();
    }

    /// <summary>TenantSettings.FeatureFlags (a JSON array of keys) as a set; unreadable = none.</summary>
    private static HashSet<string> ParseFeatures(string? json)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return set;
        try
        {
            foreach (var k in JsonSerializer.Deserialize<List<string>>(json) ?? new())
                if (!string.IsNullOrWhiteSpace(k)) set.Add(k.Trim());
        }
        catch (JsonException) { }
        return set;
    }

    private static string SafePlanName(ICurrentTenantService tenant)
    {
        try { return tenant.GetPlanDisplayName(); }
        catch { return "current"; }
    }

    /// <summary>1.2 GB, 350 MB, 12 KB — for the sentence, not for sums.</summary>
    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" :
        bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" :
        bytes >= 1L << 10 ? $"{bytes / (double)(1L << 10):0} KB" :
                            $"{bytes} bytes";
}
