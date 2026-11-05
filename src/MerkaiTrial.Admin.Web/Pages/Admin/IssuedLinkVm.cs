// =====================================================================
// FILE: Pages/Admin/IssuedLinkVm.cs
//
// NEW (118). A sign-in link shown ONCE on a super admin page (People,
// MadeeVision Team) — only its hash is stored, so the response that
// issued it is the only place it exists. Rendered by
// Pages/Admin/_IssuedLinkCard.cshtml.
// =====================================================================

namespace MerkaiTrial.Admin.Web.Pages.Admin
{
    /// <param name="Kind">"invite" or "reset".</param>
    public sealed record IssuedLinkVm(string Kind, string Url, string Email, bool Emailed, string Lifetime);
}
