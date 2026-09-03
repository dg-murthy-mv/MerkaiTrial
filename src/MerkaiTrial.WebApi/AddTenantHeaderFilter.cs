using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

public class AddTenantHeaderFilter : IOperationFilter
{
    public void Apply(OpenApiOperation op, OperationFilterContext ctx)
    {
        op.Parameters ??= new List<OpenApiParameter>();
        // Only add for the quotes route (optional filter)
        // if (!ctx.ApiDescription.RelativePath?.Contains("quotes", StringComparison.OrdinalIgnoreCase) ?? true) return;

        op.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Tenant-Id",
            In = ParameterLocation.Header,
            Required = true,
            Schema = new OpenApiSchema { Type = "string", Format = "uuid" },
            Description = "Tenant id GUID"
        });
    }
}
