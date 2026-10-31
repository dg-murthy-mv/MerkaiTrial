// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/PdfText.cs
//
// NEW (109). Downloads a PDF the way a signed-in person would (the page's
// own cookies) and reads its text, for MarketTests: the amounts, the tax
// wording and the currency printed on a quote and an invoice.
//
// Reading uses UglyToad.PdfPig (test project only). The text is
// normalised before comparing:
//   • NFKC — Arabic letters in a PDF are often stored as "presentation
//     forms" (ﺩ instead of د); NFKC turns them back into ordinary letters;
//   • no whitespace — a PDF places words, not sentences, so "VAT 7%" may
//     come out as "VAT7%" or "VAT 7 %". Compare with Contains(Squash(x)).
// =====================================================================

using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace MerkaiTrial.E2E.Infrastructure;

public static class PdfText
{
    /// <summary>
    /// GETs <paramref name="path"/> with <paramref name="page"/>'s session and
    /// returns the PDF's text, squashed (see the header). Fails with what the
    /// server said when the answer is not a PDF.
    /// </summary>
    public static async Task<string> DownloadAsync(IPage page, string path, string what)
    {
        var response = await page.APIRequest.GetAsync(path);
        var bytes = await response.BodyAsync();

        var isPdf = bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F';
        if (!response.Ok || !isPdf)
        {
            var start = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 300));
            throw new AssertionException(
                $"The {what} did not download as a PDF (HTTP {response.Status}). It began: \"{Regex.Replace(start, @"\s+", " ")}\". " +
                "See e2e-logs/adminweb.out.log and webapi.out.log at this time — a PDF that cannot be drawn (for example a " +
                "currency sign the PDF's font has no letter for) shows up there.");
        }

        using var doc = PdfDocument.Open(bytes);
        var text = string.Join("\n", doc.GetPages().Select(p => p.Text));
        return Squash(text);
    }

    /// <summary>NFKC, then every kind of space removed.</summary>
    public static string Squash(string text) =>
        Regex.Replace(text.Normalize(NormalizationForm.FormKC), @"[\s ​-‏‪-‮⁦-⁩]+", "");
}
