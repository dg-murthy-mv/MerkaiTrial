// =====================================================================
// SpreadsheetText.cs
// Location: MerkaiTrial.Application/Common/SpreadsheetText.cs
//
// NEW FILE (105 — VUL-15). Text from people that could be read as a formula.
//
// A lead named =HYPERLINK("https://evil.example","Click") or a company
// called +cmd|' /C calc'!A0 is just text to us. In an .xlsx we write it as
// text, so Excel shows it and runs nothing — but the moment someone saves
// that sheet as CSV, or edits the cell and presses Enter, Excel reads the
// leading = + - @ as the start of a formula.
//
// So every exported text cell that starts like a formula is also marked
// "quote prefix" — the same as typing an apostrophe before it in Excel.
// The value is unchanged (it still reads =HYPERLINK(…) and imports back
// exactly), but Excel treats it as text even after an edit.
//
// THE AGREED LIMIT (105): a copy someone re-saves as CSV themselves loses
// the mark, because CSV has no styles. The other choice — a visible
// apostrophe in front of the value — was turned down: people would see
// it, and the importer would have to take it off again.
//
// Used by the lead export (ClosedXML) and the report exports (EPPlus).
// =====================================================================

namespace MerkaiTrial.Application.Common;

public static class SpreadsheetText
{
    /// <summary>
    /// True when a spreadsheet could take this text for a formula: it starts
    /// with = + - @, or with a tab or carriage return before one of them.
    /// </summary>
    public static bool LooksLikeFormula(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var first = text[0];
        if (first is '=' or '+' or '-' or '@') return true;
        if (first is '\t' or '\r' or '\n') return true;
        return false;
    }
}
