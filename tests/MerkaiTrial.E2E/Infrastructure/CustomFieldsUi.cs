// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/CustomFieldsUi.cs
//
// NEW (092). Settings → Custom fields, and reading a value back off a
// record's "Additional details" card — for the tests that need custom
// fields without being about the Settings page. The same steps
// CustomFieldTests has used since 089 (it keeps its own copy and is left
// alone), shared here so the import / export tests do not copy them a
// third time.
//
// 095: a field is found by the label that STARTS WITH its name. "has-text"
// matches anywhere and ignores case, so "Tier …" also found "Licence
// tier …", a field another test adds to the same workspace, and read the
// wrong one's value. LabelStarting is the one rule, used here and by the
// import / export tests' form boxes.
// =====================================================================

using System.Text.RegularExpressions;

namespace MerkaiTrial.E2E.Infrastructure;

public static class CustomFieldsUi
{
    /// <summary>What to fill in on Add field. Type is the radio's value:
    /// text, number, date, dropdown, checkbox.</summary>
    public sealed record FieldSpec(string Label, string Type)
    {
        public bool Required { get; init; }
        public string[] Choices { get; init; } = Array.Empty<string>();
    }

    /// <summary>Settings → Custom fields → (entity tab) → Add field.
    /// Ends on the tab's list with the green "added" message.</summary>
    public static async Task AddFieldAsync(IPage admin, string entity, FieldSpec f)
    {
        await admin.GotoAsync($"/Settings/CustomFields?entity={entity}&add=true");
        var form = admin.Locator("#cfForm");
        await Assertions.Expect(form).ToBeVisibleAsync();

        // The radios sit behind their cards; the card is what a person clicks.
        await form.Locator($"label[for='cfType_{f.Type}']").ClickAsync();
        await Assertions.Expect(form.Locator($"#cfType_{f.Type}")).ToBeCheckedAsync();

        await form.Locator("#Input_Label").FillAsync(f.Label);
        if (f.Required) await form.Locator("#Input_IsRequired").CheckAsync();

        if (f.Choices.Length > 0)
        {
            var boxes = form.Locator("[data-cf-options] input[data-cf-name=Label]");
            for (var i = 0; i < f.Choices.Length; i++)
            {
                if (await boxes.CountAsync() <= i)
                    await form.Locator("[data-cf-add-option]").ClickAsync();
                await boxes.Nth(i).FillAsync(f.Choices[i]);
            }
        }

        await form.Locator("button[type=submit]").ClickAsync();
        await Ui.ExpectSuccessAsync(admin, $"\"{f.Label}\" added");
    }

    /// <summary>The one-click switch on a field's row.</summary>
    public static async Task SwitchOffAsync(IPage admin, string entity, string label)
    {
        await admin.GotoAsync($"/Settings/CustomFields?entity={entity}");
        await admin.Locator($"button[aria-label='Switch off {label}']").ClickAsync();
        await Ui.ExpectSuccessAsync(admin, "switched off");
    }

    /// <summary>
    /// Switches each field off, logging rather than throwing — for finally
    /// blocks, so a field left on cannot get in the way of later tests and
    /// a clean-up problem never hides the real failure.
    /// </summary>
    public static async Task SwitchOffQuietlyAsync(IPage admin, string entity, IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            try { await SwitchOffAsync(admin, entity, label); }
            catch (Exception ex)
            {
                TestContext.Progress.WriteLine(
                    $"[e2e] Could not switch \"{label}\" off again: {ex.Message.Split('\n')[0]}");
            }
        }
    }

    /// <summary>
    /// On a Detail page's "Additional details" card: the value printed
    /// under the field's name contains <paramref name="expected"/>.
    /// </summary>
    public static async Task ExpectDetailValueAsync(IPage page, string label, string expected)
    {
        var value = DetailValues(page, label).First;
        try
        {
            await Assertions.Expect(value).ToContainTextAsync(expected);
        }
        catch (Exception ex) when (ex is not AssertionException)
        {
            var shown = await DetailValues(page, label).AllInnerTextsAsync();
            throw new AssertionException(
                $"On {Ui.PathOf(page.Url)}, \"{label}\" should show \"{expected}\" but " +
                (shown.Count == 0
                    ? "the page has no such field in its Additional details card."
                    : $"it shows \"{string.Join(" | ", shown.Select(s => s.Trim()))}\"."));
        }
    }

    /// <summary>
    /// 095. Text that begins with <paramref name="name"/> (after any
    /// spaces), case and all — so "Tier 1018" never matches "Licence tier
    /// 1018", and a required field's trailing * still matches.
    /// </summary>
    public static Regex LabelStarting(string name) => new("^\\s*" + Regex.Escape(name));

    /// <summary>095. The value printed under each label starting with the name.</summary>
    private static ILocator DetailValues(IPage page, string label) =>
        page.Locator("label", new() { HasTextRegex = LabelStarting(label) })
            .Locator("xpath=following-sibling::*[1][self::div and contains(concat(' ', normalize-space(@class), ' '), ' fw-semibold ')]");
}
