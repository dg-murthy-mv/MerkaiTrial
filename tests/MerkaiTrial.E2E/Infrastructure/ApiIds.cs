// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/ApiIds.cs
//
// NEW (104). The id of what a create call made, whatever shape the answer
// takes: { "id": … }, { "invoiceId": … }, { "data": { "id": … } } or the
// bare id as a JSON string.
// =====================================================================

using System.Text.Json;

namespace MerkaiTrial.E2E.Infrastructure;

public static class ApiIds
{
    public static Guid IdOf(ApiResponse r)
    {
        var j = r.Json();
        if (j.ValueKind == JsonValueKind.String && Guid.TryParse(j.GetString(), out var bare)) return bare;
        var roots = new List<JsonElement> { j };
        if (j.ValueKind == JsonValueKind.Object)
            roots.AddRange(j.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).Select(p => p.Value));
        foreach (var root in roots)
        {
            if (root.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in new[] { "id", "invoiceId", "quoteId", "dealId", "leadId", "userId", "roleId" })
                if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g))
                    return g;
        }
        throw new AssertionException("The answer carries no id: " + ApiResponse.Short(r.Body, 200));
    }
}
