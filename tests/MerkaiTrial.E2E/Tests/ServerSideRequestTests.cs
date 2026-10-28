// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/ServerSideRequestTests.cs
//
// VUL-29 (round 106) — the servers never fetch an address someone typed.
//
//   The_servers_never_fetch_addresses_saved_in_records
//
// If a server fetched web addresses found in what people save (a website,
// a picture in a quote line, a link in an email message), anyone could
// make it reach inside its own network — the database, the cloud's
// metadata service — for them (server-side request forgery).
//
// The test runs a listener on this machine (127.0.0.1, a free port),
// writes its address into every place people can type one — the
// workspace's website, a lead, a contact, a company, a quote line (as text
// and as an <img>), an invoice's personal message (as an <img> and a
// link) — and then makes the servers do everything that reads those
// places: quote and invoice PDFs, the quote and invoice emails, the lead
// export, a report export, and the website's own pages (fetched without a
// browser, so any request can only come from a server).
//
// Then: the listener must have heard NOTHING.
//
// (The one download the app does make — a received email's attachment,
// from Resend's link — now only connects to public internet addresses;
// that needs Resend itself, so it is checked by reading the code, not
// here.) The workspace's website is put back afterwards.
// =====================================================================

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class ServerSideRequestTests : E2ETest
{
    [Test]
    public async Task The_servers_never_fetch_addresses_saved_in_records()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var apiA = await Api.AsAsync(a.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        using var canary = new Canary();
        string Url(string where) => $"http://127.0.0.1:{canary.Port}/ssrf/{where}-{m}";

        var profile = await apiA.GetAsync("/api/tenants/company-profile");
        Assert.That(profile.IsSuccess, Is.True, $"Reading A's company profile failed: {profile}");
        var original = JsonNode.Parse(profile.Body)!.AsObject();

        try
        {
            await Step("The listener's address goes into every place people can type one", async () =>
            {
                var changed = JsonNode.Parse(profile.Body)!.AsObject();
                changed["website"] = Url("tenant-website");
                var put = await apiA.PutAsync("/api/tenants/company-profile", changed);
                if (!put.IsSuccess) TestContext.Progress.WriteLine($"    (the workspace website couldn't be set: {put.Status})");
            });

            var (quoteId, invoiceId) = await Step<(Guid, Guid)>("…a lead, a contact, a company, a quote line and an invoice message", async () =>
            {
                await apiA.PostAsync("/api/leads", new
                {
                    tenantId = tenantA, fullName = "Ssrf Lead " + m, email = World.EmailFor("A", "ssrf" + m), phone = "+919817" + m,
                    companyName = Url("lead-company"), address = Url("lead-address"), estimatedValue = 1000m, currency = "INR",
                });
                await apiA.PostAsync("/api/companies", new { tenantId = tenantA, name = "Ssrf Co " + m, website = Url("company-website") });

                var contact = await ApiRecords.ContactAsync(apiA, tenantA, "Ssrf Contact " + m, "ssrfc" + m);
                var deal = await ApiRecords.DealAsync(apiA, tenantA, contact, "Ssrf Deal " + m);

                var quote = await apiA.PostAsync("/api/quotes", new
                {
                    dealId = deal, issueDateUtc = DateTime.UtcNow.Date, expiresAtUtc = DateTime.UtcNow.Date.AddDays(30), currency = "INR",
                    items = new[]
                    {
                        new { name = "See " + Url("quote-line"), description = $"<img src=\"{Url("quote-img")}\"> {Url("quote-desc")}",
                              unitPrice = 5000m, quantity = 1m, taxRate = 0m },
                    },
                });
                Assert.That(quote.IsSuccess, Is.True, $"Adding the quote failed: {quote}");
                var qid = ApiIds.IdOf(quote);

                var invoice = await ApiRecords.InvoiceAsync(apiA, deal, "Ssrf line " + Url("invoice-line"));
                return (qid, invoice);
            });

            await Step("The servers do everything that reads those places", async () =>
            {
                // 106-update2: moving it to Sent is what builds and queues the email.
                await apiA.PutAsync($"/api/quotes/{quoteId}/status", new { status = "Sent", baseUrl = E2ESettings.BaseUrl });
                await apiA.GetAsync($"/api/quotes/{quoteId}/pdf");

                await apiA.PostAsync($"/api/invoices/{invoiceId}/issue");
                await apiA.PostAsync($"/api/invoices/{invoiceId}/email", new
                {
                    toAddress = World.EmailFor("A", "ssrfc" + m), toName = "Ssrf",
                    ccAddresses = (string?)null,
                    message = $"<img src=\"{Url("invoice-img")}\"> <a href=\"{Url("invoice-link")}\">pay</a>",
                    baseUrl = E2ESettings.BaseUrl,
                });
                await apiA.GetAsync($"/api/invoices/{invoiceId}/pdf");

                await apiA.GetAsync("/api/leads/export?search=" + Uri.EscapeDataString("Ssrf Lead " + m));
                var from = DateTime.UtcNow.Date.AddDays(-2).ToString("yyyy-MM-dd");
                var to = DateTime.UtcNow.Date.AddDays(2).ToString("yyyy-MM-dd");
                await apiA.GetAsync($"/api/reports/pipeline-summary/export?from={from}&to={to}");

                using var web = await WebSession.SignedInAsync(a.Admin);
                foreach (var path in new[] { "/Dashboard", "/Leads", "/Companies", $"/Quotes/Detail?id={quoteId}",
                                             $"/Invoices/Detail?id={invoiceId}", "/Settings/CompanyProfile" })
                    await web.GetAsync(path);
            });

            await Step("The listener heard nothing", async () =>
            {
                await Task.Delay(5000);    // anything queued in the background has had its chance
                foreach (var heard in canary.Heard.Take(10))
                    problems.Add($"A SERVER fetched an address someone typed: {heard}");
            });
        }
        finally
        {
            await apiA.PutAsync("/api/tenants/company-profile", original);
        }

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    /// <summary>A bare TCP listener on 127.0.0.1 that writes down the first line of anything that connects.</summary>
    private sealed class Canary : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public ConcurrentQueue<string> Heard { get; } = new();
        public int Port { get; }

        public Canary()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var reader = new StreamReader(client.GetStream());
                    var first = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    Heard.Enqueue(first ?? "(connected, said nothing)");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (ex is IOException or SocketException or System.TimeoutException)
                {
                    Heard.Enqueue("(connected: " + ex.GetType().Name + ")");
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
