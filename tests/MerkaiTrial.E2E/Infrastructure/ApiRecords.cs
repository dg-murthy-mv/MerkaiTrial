// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/ApiRecords.cs
//
// NEW (105). Quick records made straight through the API, for tests whose
// subject is something else (uploads, downloads, exports, webhooks): a
// lead, a contact, a deal on it, a quote on the deal, an invoice on the
// deal. Each insists it worked and returns the new id.
// =====================================================================

using System.Text.Json;

namespace MerkaiTrial.E2E.Infrastructure;

public static class ApiRecords
{
    public static async Task<Guid> LeadAsync(ApiCaller api, Guid tenantId, string fullName, string emailKey, string? ownerUserId = null)
    {
        var digits = Random.Shared.Next(10000000, 99999999).ToString();
        var r = await api.PostAsync("/api/leads", new
        {
            tenantId, fullName, email = World.EmailFor("A", emailKey), phone = "+9198" + digits,
            companyName = fullName + " Co", estimatedValue = 1000m, currency = "INR", ownerUserId,
        });
        Assert.That(r.IsSuccess, Is.True, $"Adding the lead '{fullName}' failed: {r}");
        return ApiIds.IdOf(r);
    }

    public static async Task<Guid> ContactAsync(ApiCaller api, Guid tenantId, string lastName, string emailKey)
    {
        var r = await api.PostAsync("/api/contacts", new { tenantId, firstName = "E2E", lastName, email = World.EmailFor("A", emailKey) });
        Assert.That(r.IsSuccess, Is.True, $"Adding the contact '{lastName}' failed: {r}");
        return ApiIds.IdOf(r);
    }

    public static async Task<Guid> DealAsync(ApiCaller api, Guid tenantId, Guid contactId, string title, string? ownerUserId = null)
    {
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["tenantId"] = tenantId.ToString(),
            ["contactId"] = contactId.ToString(),
            ["title"] = title,
            ["expectedValue"] = 10000m,
            ["currency"] = "INR",
            ["expectedCloseDateUtc"] = DateTime.UtcNow.Date.AddDays(30),
        };
        if (ownerUserId is not null) body["ownerUserId"] = ownerUserId;
        var r = await api.PostAsync("/api/deals", body);
        Assert.That(r.IsSuccess, Is.True, $"Adding the deal '{title}' failed: {r}");
        return ApiIds.IdOf(r);
    }

    public static async Task<Guid> QuoteAsync(ApiCaller api, Guid dealId, string lineName)
    {
        var r = await api.PostAsync("/api/quotes", new
        {
            dealId, issueDateUtc = DateTime.UtcNow.Date, expiresAtUtc = DateTime.UtcNow.Date.AddDays(30), currency = "INR",
            items = new[] { new { name = lineName, unitPrice = 5000m, quantity = 1m, taxRate = 0m } },
        });
        Assert.That(r.IsSuccess, Is.True, $"Adding the quote failed: {r}");
        return ApiIds.IdOf(r);
    }

    public static async Task<Guid> InvoiceAsync(ApiCaller api, Guid dealId, string lineName)
    {
        var r = await api.PostAsync("/api/invoices", new
        {
            dealId, issueDateUtc = DateTime.UtcNow.Date, dueDateUtc = DateTime.UtcNow.Date.AddDays(14), currency = "INR",
            lines = new[] { new { name = lineName, unitPrice = 3000m, quantity = 1m, taxRate = 0m } },
            sendImmediately = false,
        });
        Assert.That(r.IsSuccess, Is.True, $"Adding the invoice failed: {r}");
        return ApiIds.IdOf(r);
    }

    /// <summary>A text property of a JSON object, or null.</summary>
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
