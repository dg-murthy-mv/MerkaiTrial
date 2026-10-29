// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/PerfWorld.cs
//
// NEW (107). The performance workspace, made ready once per run by
// whichever performance test runs first:
//
//   1. WorldBuilder.EnsurePerfWorkspaceAsync — the workspace and its ten
//      people exist and can sign in (created through the screens on the
//      very first run only).
//   2. PerfSeeder — if it has not been filled yet, one of each record is
//      made through the API and copied into the big set (PerfData.SeedAsync).
//      Seeding happens ONCE; later runs reuse the same records, so their
//      timings compare like with like.
//   3. Sample records in the middle of the set, for the detail pages.
//
// A failure is remembered and reported to every later performance test,
// rather than tried again (the same rule as workspaces C and D, 095).
// =====================================================================

namespace MerkaiTrial.E2E.Infrastructure;

public static class PerfWorld
{
    public const string WorkspaceName = "E2E Performance";
    public const int RepCount = 9;

    public static string EmailOf(string local) => $"{local}@perf-e2e.example.com";

    /// <summary>Puts the workspace and its people aside, so the next run makes a fresh one.</summary>
    public const string StartAgainSql =
        "UPDATE Users SET Email = REPLACE(Email, '@perf-e2e.example.com', '@perf-old.example.com') " +
        "WHERE Email LIKE '%@perf-e2e.example.com'; " +
        "UPDATE Tenants SET Name = Name + ' (old)' WHERE Name = '" + WorkspaceName + "';";

    public static PerfData.Sizes Size => PerfData.Sizes.Big;

    public static PerfData Data { get; } = new(E2ESettings.ConnectionString);

    public static TestTenant P { get; private set; } = null!;
    public static Guid TenantId { get; private set; }
    public static PerfData.Samples Samples { get; private set; } = null!;

    /// <summary>Everyone in the workspace: the admin first, then rep01…rep09.</summary>
    public static IReadOnlyList<TestUser> People =>
        new[] { P.Admin }.Concat(P.People.OrderBy(p => p.Key).Select(p => p.Value)).ToList();

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _ready;
    private static Exception? _failure;

    public static async Task EnsureAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_ready) return;
            if (_failure is not null)
                throw new InvalidOperationException(
                    $"The performance workspace could not be made ready earlier in this run. The first failure: {_failure.Message}", _failure);

            try
            {
                (P, TenantId) = await WorldBuilder.EnsurePerfWorkspaceAsync(Data);
                await SeedIfNeededAsync();
                Samples = await Data.SamplesAsync(TenantId, Size);

                var rows = await Data.RowCountsAsync(TenantId);
                Log("Records: " + string.Join(", ", rows.Select(r => $"{r.Table} {r.Rows:N0}")));
                Log($"Test database size: {await Data.DatabaseSizeMbAsync():N0} MB");
                _ready = true;
            }
            catch (Exception ex)
            {
                _failure = ex;
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task SeedIfNeededAsync()
    {
        var have = await Data.SeededLeadsAsync(TenantId);
        if (have >= Size.Leads)
        {
            Log($"Already filled ({have:N0} copied leads) — reusing it.");
            return;
        }
        if (have > 0)
            throw new InvalidOperationException(
                $"The performance workspace has {have:N0} copied leads, fewer than the {Size.Leads:N0} it should — " +
                "a different size from an earlier round? Put it aside so a fresh one is made: " + StartAgainSql);

        Log($"Filling the workspace (first run only): {Size.Companies:N0} companies, {Size.Contacts:N0} contacts, " +
            $"{Size.Leads:N0} leads, {Size.Deals:N0} deals, {Size.Quotes:N0} quotes, {Size.Invoices:N0} invoices, " +
            $"{Size.AuditRows:N0} audit rows. This takes a few minutes, once.");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var templates = await MakeTemplatesAsync();
        await Data.SeedAsync(TenantId, templates, Size, m => Log("  " + m));

        Log($"Filled in {watch.Elapsed.TotalMinutes:0.0} minutes.");
    }

    /// <summary>One of each record, through the API, as the admin — exactly what the app writes.</summary>
    private static async Task<PerfData.Templates> MakeTemplatesAsync()
    {
        var api = await Api.AsAsync(P.Admin);
        await Api.EnsureTokensWorkAsync(api);

        var company = await api.PostAsync("/api/companies", new
        {
            tenantId = TenantId, name = "Perf Template Company", country = "IN", vertical = "Generic", createdBy = "e2e-perf",
        });
        Assert.That(company.IsSuccess, Is.True, $"Making the template company failed: {company}");
        var companyId = ApiIds.IdOf(company);

        var contact = await api.PostAsync("/api/contacts", new
        {
            tenantId = TenantId, firstName = "Perf", lastName = "Template Contact", companyId,
            email = EmailOf("perf.template.contact"),
        });
        Assert.That(contact.IsSuccess, Is.True, $"Making the template contact failed: {contact}");
        var contactId = ApiIds.IdOf(contact);

        var lead = await api.PostAsync("/api/leads", new
        {
            tenantId = TenantId, fullName = "Perf Template Lead", email = EmailOf("perf.template.lead"),
            phone = "+919000000001", companyName = "Perf Template Lead Co", estimatedValue = 1000m, currency = "INR",
        });
        Assert.That(lead.IsSuccess, Is.True, $"Making the template lead failed: {lead}");
        var leadId = ApiIds.IdOf(lead);

        var dealId = await ApiRecords.DealAsync(api, TenantId, contactId, "Perf Template Deal");
        var quoteId = await ApiRecords.QuoteAsync(api, dealId, "Perf template quote line");

        var invoiceId = await ApiRecords.InvoiceAsync(api, dealId, "Perf template invoice line");
        var issued = await api.PostAsync($"/api/invoices/{invoiceId}/issue");
        Assert.That(issued.IsSuccess, Is.True, $"Issuing the template invoice failed: {issued}");

        return new PerfData.Templates(companyId, contactId, leadId, dealId, quoteId, invoiceId);
    }

    private static void Log(string message) => TestContext.Progress.WriteLine($"[e2e perf] {message}");
}
