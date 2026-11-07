// =====================================================================
// FILE: Pages/Shared/AiRecordHelperVm.cs
//
// NEW (124). What the record helpers card (_AiRecordHelper) needs: which
// kind of record ("lead" or "deal") and its id.
// =====================================================================

namespace MerkaiTrial.Admin.Web.Pages.Shared
{
    public sealed record AiRecordHelperVm(string Type, Guid Id);
}
