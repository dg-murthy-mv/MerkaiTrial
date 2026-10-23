// =====================================================================
// PermissionOperationFilter.cs
// Location: MerkaiTrial.WebApi/SwaggerGen/PermissionOperationFilter.cs
//
// NEW FILE (100). Writes WHO MAY CALL each endpoint into the Swagger
// document, as two extensions on every operation:
//
//   "x-merkai-anonymous": true | false     [AllowAnonymous] on it
//   "x-merkai-policies":  ["Leads.Update"]  every [Authorize(Policy = …)]
//                                            on the action AND its
//                                            controller (all must pass)
//
// WHY. The end-to-end test "An unauthorized user cannot call protected API
// endpoints" (round 100) reads /swagger/v1/swagger.json and calls EVERY
// endpoint as a signed-out visitor, a Viewer and a Sales Rep, expecting
// 401 / 403 wherever the person lacks the permission. Reading the
// permissions from here means a controller added next month is covered
// the day it exists, with nothing to remember to update in the tests.
//
// SAFE TO HAVE. Swagger is served in Development only (ApiPipeline.
// UseApiSwaggerInDevelopment), so production never publishes this. The
// permission names are the same strings already on the attributes.
// =====================================================================

using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MerkaiTrial.WebApi.SwaggerGen;

public sealed class PermissionOperationFilter : IOperationFilter
{
    public const string AnonymousKey = "x-merkai-anonymous";
    public const string PoliciesKey  = "x-merkai-policies";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        // Controller-level and action-level attributes both end up here.
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata ?? new List<object>();

        var anonymous = metadata.OfType<IAllowAnonymous>().Any();

        var policies = metadata.OfType<IAuthorizeData>()
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var list = new OpenApiArray();
        foreach (var p in policies) list.Add(new OpenApiString(p));

        operation.Extensions[AnonymousKey] = new OpenApiBoolean(anonymous);
        operation.Extensions[PoliciesKey]  = list;
    }
}
