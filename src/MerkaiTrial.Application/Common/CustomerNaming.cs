// =====================================================================
// CustomerNaming.cs
// Location: MerkaiTrial.Application/Common/CustomerNaming.cs
//
// NEW FILE (053). Who a quote or an invoice is ADDRESSED TO.
//
// ── WHAT WAS WRONG ───────────────────────────────────────────────────
//
// Six places worked this out, and every one of them got it wrong in a
// slightly different way:
//
//   InvoicesQueries (list)     CompanyName = Deal.Contact.FirstName
//   InvoicesQueries (detail)   CompanyName = Deal.Contact.FirstName
//                              ContactName = FirstName + " " + LastName
//   GenerateInvoicePdfHandler  CompanyName = Deal.Contact.FirstName
//   GetQuoteByIdHandler        CompanyName = "FirstName LastName"
//   GetQuoteByTokenHandler     CompanyName = "FirstName LastName"
//   GetQuotesHandler (list)    CompanyName = "FirstName LastName"
//
// None of them ever looked at a Company. So an invoice to Ramesh Kumar
// of Acme Interiors was headed "BILL TO: Ramesh", and the quote PDF put
// a person's name under a heading that reads QUOTE TO — while
// Deal.CompanyId sat there unread, and DealsCommandHandlers had been
// joining dbo.Companies on that very column all along.
//
// The ContactName concatenation had a second bug of its own: with no
// deal, `Deal?.Contact?.FirstName + " " + Deal?.Contact?.LastName`
// evaluates to a single SPACE, not null — so "is there a contact?" was
// always true and the screen printed an empty line with an icon.
//
// ── THE RULE, IN ONE PLACE ───────────────────────────────────────────
//
//   Company:  the deal's own Company, else the contact's Company
//   Person:   the contact's first + last name
//   Display:  the company when there is one, otherwise the person
//
// The deal's company comes FIRST on purpose. Deal.CompanyId is set when
// the deal is created (and on lead conversion); Contact.CompanyId is
// where the contact happens to work now. If somebody changes jobs, the
// deal stays with the company it was actually sold to — and an invoice
// already sent must not silently re-address itself.
//
// ── WHY THE STRING OVERLOADS EXIST ───────────────────────────────────
//
// Five of the six sites project from entities loaded into memory and can
// call CompanyOf(deal) directly. The SIXTH — the invoice LIST — is a
// server-side EF projection, and EF cannot translate a C# method call
// into SQL. Rather than write the rule out a second time there (which is
// how these six drifted apart in the first place), that query selects
// the four raw strings and calls Display(...) on them afterwards. One
// rule, one implementation, two ways in.
// =====================================================================

using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Application.Common;

public static class CustomerNaming
{
    /// <summary>What to show when there is neither a company nor a person.</summary>
    public const string Unknown = "Unknown";

    // ── the rule, on plain strings ───────────────────────────────────

    /// <summary>
    /// The company a document is addressed to. Null when neither the deal
    /// nor the contact has one — a genuinely person-to-person sale, which
    /// happens and is not an error.
    /// </summary>
    public static string? Company(string? dealCompanyName, string? contactCompanyName)
        => Clean(dealCompanyName) ?? Clean(contactCompanyName);

    /// <summary>
    /// The person. Null rather than a stray space when there is no contact
    /// — see the note above about `first + " " + last`.
    /// </summary>
    public static string? Person(string? firstName, string? lastName)
        => Clean(string.Join(" ", new[] { Clean(firstName), Clean(lastName) }
                                  .Where(x => x is not null)));

    /// <summary>
    /// One line for a list column or a heading: the company when there is
    /// one, otherwise the person, otherwise "Unknown".
    /// </summary>
    public static string Display(
        string? dealCompanyName, string? contactCompanyName,
        string? firstName, string? lastName)
        => Company(dealCompanyName, contactCompanyName)
           ?? Person(firstName, lastName)
           ?? Unknown;

    // ── the same rule, for entities already loaded ───────────────────
    //
    // These need Deal.Company and Deal.Contact.Company to have been
    // INCLUDED. A missing Include gives null, and null falls through the
    // rule quietly to the person's name — which is exactly the old wrong
    // behaviour, arrived at by a different route. Every call site in 053
    // includes both; if you add a seventh, include them there too.

    public static string? CompanyOf(Deal? deal)
        => Company(deal?.Company?.Name, deal?.Contact?.Company?.Name);

    public static string? PersonOf(Contact? contact)
        => Person(contact?.FirstName, contact?.LastName);

    public static string? PersonOf(Deal? deal)
        => PersonOf(deal?.Contact);

    public static string DisplayFor(Deal? deal)
        => CompanyOf(deal) ?? PersonOf(deal) ?? Unknown;

    // ── helpers ──────────────────────────────────────────────────────

    /// <summary>Whitespace-only is the same as absent. "  " is not a name.</summary>
    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
