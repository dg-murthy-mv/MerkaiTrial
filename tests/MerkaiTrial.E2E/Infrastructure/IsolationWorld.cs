// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/IsolationWorld.cs
//
// NEW (100). The records the tenant-isolation suite tries to reach.
//
// WORKSPACE A — the victim. Built once, the first time the suite runs:
//   • a lead converted into a deal (so a contact and a company too), a
//     quote on the deal, sent and accepted, and an issued invoice with
//     one part-payment recorded
//   • a second lead, left open (a converted lead refuses most changes
//     anyway, which would hide a missing workspace check)
//   • a product
//   • a note on the open lead and a note on the deal
//   • a task on the deal, a logged call on the open lead, and an old-style
//     lead activity
//   • a PDF on the open lead, on the deal and on the quote
// Every one of them carries a marker — a name, a subject, a file content —
// that exists nowhere else. If any answer workspace B gets back contains a
// marker, A's data leaked.
//
// WORKSPACE B — the attacker. Its admin (a workspace ADMIN, so no role
// limit ever explains a refusal — only the workspace boundary can) gets:
//   • a lead and a deal of its own. Most "change the id" attacks use B's
//     OWN record in the address and A's id in the form: "delete note X on
//     my lead", where X is A's note.
//   • a second lead of its own, used for "convert MY lead, attaching it to
//     THEIR company".
//
// Markers are long, odd and stamped with the run, so they cannot turn up
// by chance.
// =====================================================================

using System.Globalization;
using System.Text;

namespace MerkaiTrial.E2E.Infrastructure;

public sealed class IsolationWorld
{
    public required Guid TenantA { get; init; }
    public required Guid TenantB { get; init; }
    public required Guid BAdminUserId { get; init; }

    // ── Workspace A ───────────────────────────────────────────────────
    public required Guid ConvertedLeadId { get; init; }
    public required Guid OpenLeadId { get; init; }
    public required Guid DealId { get; init; }
    public required Guid ContactId { get; init; }
    public required Guid CompanyId { get; init; }
    public required Guid QuoteId { get; init; }
    public required Guid InvoiceId { get; init; }
    public required string InvoiceNumber { get; init; }
    public required Guid PaymentId { get; init; }
    public required Guid ProductId { get; init; }
    public required Guid LeadNoteId { get; init; }
    public required Guid DealNoteId { get; init; }
    public required Guid TaskId { get; init; }
    public required Guid CallId { get; init; }
    public required Guid LeadActivityId { get; init; }
    public required Guid LeadAttachmentId { get; init; }
    public required Guid DealAttachmentId { get; init; }
    public required Guid QuoteAttachmentId { get; init; }

    /// <summary>The open lead's email — searched for by B.</summary>
    public required string OpenLeadEmail { get; init; }

    // ── Workspace B ───────────────────────────────────────────────────
    public required Guid BLeadId { get; init; }
    public required Guid BSpareLeadId { get; init; }
    public required Guid BDealId { get; init; }

    /// <summary>Text that exists only in workspace A's records.</summary>
    public required IReadOnlyList<string> Markers { get; init; }

    // =================================================================

    /// <summary>
    /// Builds both sides. <paramref name="a"/> is workspace A's admin,
    /// <paramref name="customer"/> a signed-out session (to accept the
    /// quote), <paramref name="b"/> workspace B's admin.
    /// </summary>
    public static async Task<IsolationWorld> BuildAsync(IPage a, IPage customer, IPage b)
    {
        var db = GlobalSetup.Db;
        var tag = $"{World.Stamp.Replace("-", "")}{Random.Shared.Next(100, 999)}";
        string M(string what) => $"Qzv{what}{tag}";          // e.g. QzvLead1022130501417

        var markers = new List<string>();
        string Mark(string what) { var m = M(what); markers.Add(m); return m; }

        var tenantA = await db.TenantIdAsync(World.A.Name);
        var tenantB = await db.TenantIdAsync(World.B.Name);
        var apiA = await Api.AsAsync(World.A.Admin);
        await Api.EnsureTokensWorkAsync(apiA);

        // ── A: lead → deal → quote → invoice ──────────────────────────
        var convertedName = Mark("Converted");
        var company = Mark("Company");
        var dealTitle = Mark("Deal");
        var convertedEmail = $"{M("mail").ToLowerInvariant()}@example.com";
        markers.Add(convertedEmail);

        var (convertedLeadId, dealId) = await Flows.LeadToDealAsync(
            a, World.A, convertedName, convertedEmail, dealTitle, 90000m, company);
        var (contactId, companyId) = await db.DealContactAsync(dealId);
        if (contactId is null || companyId is null)
            throw new AssertionException("The converted deal has no contact or company to protect.");

        var quoteItem = Mark("QuoteItem");
        var quoteId = await Flows.SendQuoteAsync(a, dealTitle, new QuoteLine(quoteItem, 1, 90000m, TaxRate: 18));
        var invoiceId = await Flows.IssuedInvoiceAsync(a, customer, quoteId);

        var invoice = await apiA.GetAsync($"/api/invoices/{invoiceId}");
        if (!invoice.IsSuccess) throw new AssertionException($"Reading A's invoice back failed: {invoice}");
        var invoiceNumber = invoice.Json().GetProperty("number").GetString() ?? "";

        var paid = await apiA.PostAsync($"/api/invoices/{invoiceId}/payments", new
        {
            amount = 1000m,
            method = "BankTransfer",
            paidAtUtc = DateTime.UtcNow,
            notes = Mark("PaymentNote"),
        });
        if (!paid.IsSuccess) throw new AssertionException($"Recording A's part-payment failed: {paid}");
        var paymentId = paid.Json().GetProperty("id").GetGuid();

        // ── A: an open lead, a product ────────────────────────────────
        var openName = Mark("OpenLead");
        var openEmail = $"{M("open").ToLowerInvariant()}@example.com";
        markers.Add(openEmail);
        var openLeadId = await Flows.CreateLeadAsync(a, openName, openEmail, 45000m, World.A, Mark("OpenCo"));

        var productName = Mark("Product");
        await a.GotoAsync("/Products/Create");
        await a.Locator("#Input_Name").FillAsync(productName);
        await Ui.SelectFirstIfEmptyAsync(a.Locator("#Input_Category"));
        await a.Locator("#Input_Sku").FillAsync(M("SKU"));
        await a.Locator("#Input_ListPrice").FillAsync("1500");
        await a.Locator("#Input_TaxRate").FillAsync("18");
        await a.GetByRole(AriaRole.Button, new() { Name = "Create Product" }).ClickAsync();
        await Ui.WaitForPageAsync(a, u => u.Contains("/Products/Detail", StringComparison.OrdinalIgnoreCase), "A's new product");
        var productId = Ui.IdFromUrl(a.Url);

        // ── A: notes, a task, a call, an old-style activity ──────────
        var leadNote = Mark("LeadNote");
        await Must(apiA.PostAsync($"/api/leads/{openLeadId}/notes",
            new { tenantId = tenantA, leadId = openLeadId, note = leadNote, createdBy = World.A.Admin.FullName }), "A's lead note");

        var dealNote = Mark("DealNote");
        await Must(apiA.PostAsync($"/api/deals/{dealId}/notes",
            new { tenantId = tenantA, dealId, note = dealNote, createdBy = World.A.Admin.FullName }), "A's deal note");

        var taskSubject = Mark("Task");
        await Must(apiA.PostAsync("/api/activities", new
        {
            tenantId = tenantA, entityType = "Deal", entityId = dealId, activityType = "Task",
            subject = taskSubject, description = Mark("TaskBody"), isTask = true,
            dueDate = DateTime.UtcNow.Date.AddDays(3),
        }), "A's task");

        var callSubject = Mark("Call");
        await Must(apiA.PostAsync("/api/activities", new
        {
            tenantId = tenantA, entityType = "Lead", entityId = openLeadId, activityType = "Call",
            subject = callSubject, description = Mark("CallBody"), duration = 5,
        }), "A's logged call");

        var leadActivity = Mark("LeadActivity");
        await Must(apiA.PostAsync($"/api/leads/{openLeadId}/activities", new
        {
            tenantId = tenantA, leadId = openLeadId, activityType = "Meeting", subject = leadActivity,
            description = Mark("LeadActivityBody"), duration = 30, activityDate = DateTime.UtcNow,
        }), "A's lead activity");

        // ── A: files ──────────────────────────────────────────────────
        var leadFile = $"{M("leadfile")}.pdf";
        var dealFile = $"{M("dealfile")}.pdf";
        var quoteFile = $"{M("quotefile")}.pdf";
        markers.Add(leadFile); markers.Add(dealFile); markers.Add(quoteFile);
        await Must(apiA.UploadAsync($"/api/leads/{openLeadId}/attachments", leadFile, Files.SmallPdf(Mark("LeadPdf")), "application/pdf"), "A's lead file");
        await Must(apiA.UploadAsync($"/api/deals/{dealId}/attachments", dealFile, Files.SmallPdf(Mark("DealPdf")), "application/pdf"), "A's deal file");
        await Must(apiA.UploadAsync($"/api/quotes/{quoteId}/attachments", quoteFile, Files.SmallPdf(Mark("QuotePdf")), "application/pdf"), "A's quote file");

        // ── B: its own records ────────────────────────────────────────
        var bTag = $"B {tag}";
        var bLeadId = await Flows.CreateLeadAsync(b, $"Bram Own {bTag}", World.EmailFor("B", "own" + Random.Shared.Next(1000, 9999)), 1000m, World.B);
        var bSpareLeadId = await Flows.CreateLeadAsync(b, $"Bram Spare {bTag}", World.EmailFor("B", "spare" + Random.Shared.Next(1000, 9999)), 1000m, World.B);
        var (_, bDealId) = await Flows.LeadToDealAsync(b, World.B, $"Bram Deal Lead {bTag}",
            World.EmailFor("B", "deal" + Random.Shared.Next(1000, 9999)), $"Bram Deal {bTag}", 2000m);

        return new IsolationWorld
        {
            TenantA = tenantA,
            TenantB = tenantB,
            BAdminUserId = await db.UserIdAsync(World.B.Admin.Email),
            ConvertedLeadId = convertedLeadId,
            OpenLeadId = openLeadId,
            DealId = dealId,
            ContactId = contactId.Value,
            CompanyId = companyId.Value,
            QuoteId = quoteId,
            InvoiceId = invoiceId,
            InvoiceNumber = invoiceNumber,
            PaymentId = paymentId,
            ProductId = productId,
            LeadNoteId = await Find(db.ChildIdAsync("LeadNotes", "LeadId", openLeadId, "Note", leadNote), "A's lead note"),
            DealNoteId = await Find(db.ChildIdAsync("DealNotes", "DealId", dealId, "Note", dealNote), "A's deal note"),
            TaskId = await Find(db.ChildIdAsync("Activities", "EntityId", dealId, "Subject", taskSubject), "A's task"),
            CallId = await Find(db.ChildIdAsync("Activities", "EntityId", openLeadId, "Subject", callSubject), "A's call"),
            LeadActivityId = await Find(db.ChildIdAsync("LeadActivities", "LeadId", openLeadId, "Subject", leadActivity), "A's lead activity"),
            LeadAttachmentId = await Find(db.AttachmentIdAsync(openLeadId, leadFile), "A's lead file"),
            DealAttachmentId = await Find(db.AttachmentIdAsync(dealId, dealFile), "A's deal file"),
            QuoteAttachmentId = await Find(db.AttachmentIdAsync(quoteId, quoteFile), "A's quote file"),
            OpenLeadEmail = openEmail,
            BLeadId = bLeadId,
            BSpareLeadId = bSpareLeadId,
            BDealId = bDealId,
            Markers = markers,
        };
    }

    private static async Task Must(Task<ApiResponse> call, string what)
    {
        var r = await call;
        if (!r.IsSuccess) throw new AssertionException($"Setting up {what} failed: {r}");
    }

    private static async Task<Guid> Find(Task<Guid?> lookup, string what) =>
        await lookup ?? throw new AssertionException($"{what} was not found in the test database after it was added.");
}

/// <summary>Small files for upload tests.</summary>
public static class Files
{
    /// <summary>A tiny, valid one-page PDF whose text (uncompressed) is <paramref name="label"/>.</summary>
    public static byte[] SmallPdf(string label)
    {
        var content = $"BT /F1 18 Tf 72 720 Td ({label}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
