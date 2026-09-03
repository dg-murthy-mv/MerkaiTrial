namespace MerkaiTrial.WebApi.Services;

public static class Templates
{
    public const string QuoteEmailSubject = "Your MerkaiTrial Quote {{QuoteNumber}} (View / Accept / Pay)";

    public const string QuoteEmailHtml = @"
<p>Hi {{BuyerName}},</p>
<p>Here is your quote <strong>{{QuoteNumber}}</strong> from {{SellerName}}.</p>
<p>Total: <strong>{{GrandTotal}}</strong></p>
<p><a href=""{{PublicLink}}"">View / Accept</a></p>
<p>Thank you,<br/>{{SellerName}}</p>";

    public const string PublicQuoteHtml = @"
<html><head><meta charset=""utf-8"" /><title>Quote {{QuoteNumber}}</title>
<style>
body{font-family:system-ui,-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;margin:24px}
.btn{padding:10px 16px;border-radius:6px;border:1px solid #ccc;background:#0d6efd;color:#fff;text-decoration:none}
.table{border-collapse:collapse;width:100%} .table th,.table td{border:1px solid #ddd;padding:8px}
</style></head>
<body>
  <h2>Quote {{QuoteNumber}}</h2>
  <div>Buyer: {{BuyerName}}</div>
  <div>Date: {{IssueDate}}</div>
  <br/>
  <table class=""table"">
    <thead><tr><th>Description</th><th>Qty</th><th>Unit</th><th>Tax</th><th>Total</th></tr></thead>
    <tbody>
      {{Lines}}
    </tbody>
  </table>
  <p><strong>Subtotal:</strong> {{Subtotal}}<br/>
     <strong>VAT:</strong> {{VatTotal}}<br/>
     <strong>Grand Total:</strong> {{GrandTotal}}</p>

  <form method=""post"" action=""{{AcceptUrl}}"">
    <button class=""btn"">Accept Quote</button>
  </form>
</body></html>";
}
