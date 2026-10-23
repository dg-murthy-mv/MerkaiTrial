// =====================================================================
// LeadsWorkflowController.cs — REMOVED IN ROUND 100. Delete this file.
//
// This file is intentionally empty so that copying the round's files
// over the solution removes the controller even if the file itself was
// not deleted. It is safe to delete it now.
//
// Why it went: it trusted an X-Tenant-Id header and checked no
// permission. It also used the SAME route as
// LeadsController's POST api/leads/{id}/convert, so every call to that
// address failed with AmbiguousMatchException (HTTP 500).
// Admin.Web calls neither of them.
// =====================================================================
