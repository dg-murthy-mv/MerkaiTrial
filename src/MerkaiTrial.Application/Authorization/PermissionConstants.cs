// =====================================================================
// FILE: MerkaiTrial.Application/Authorization/PermissionConstants.cs
//
// COMPLETE FILE — replaces the existing one.
//
// ROUND 024 CHANGE: a "settings" module is added.
//
// WHY
//   Settings/Pipeline, Settings/PipelineRules and Settings/LeadStatuses
//   were gated on deals.* and leads.* — the same permissions a rep needs
//   to do their job. So "may edit a deal" and "may redesign the pipeline
//   every deal moves through" were one permission, and a Sales Rep could
//   rename stages, reorder them, retire them, and change the lead scoring
//   that drives every rep's queue.
//
//   Reusing deals.update was never a decision — it was the default that
//   came with copying the pattern from a business-module page. Reusing
//   users.* or roles.* was the alternative once the bug was found, and
//   rejected for the reason already written above the roles module: "can
//   manage people" and "can change what people are allowed to do" are
//   different powers. "Can shape the pipeline" is a third.
//
// WHAT IT COVERS
//   How the pipeline is SHAPED: pipeline stages, the sales process
//   (transitions, requirements, actions), and lead statuses. NOT the
//   deals and leads that move through it — those stay on deals.* and
//   leads.*, and a rep needs those to work at all.
//
// WHY THERE IS NO MIGRATION
//   PermissionPolicyProvider builds "module.action" policies at runtime,
//   so adding the constants below is enough — nothing to register.
//   PermissionHandler grants a blanket bypass on IsTenantAdmin == true,
//   so a workspace admin passes these the moment they exist, while nobody
//   else holds them. That is exactly the default we want: admins keep
//   working, reps lose access, and delegating it to a sales manager is a
//   checkbox in the role editor rather than a future round.
//
// ALSO ADDED: Modules.Audit. ModuleCatalog has enforced an "audit" module
// since it was written, and this file never had the constant — so every
// call site had to spell the string by hand, which is precisely how the
// casing bug in _Layout happened.
// =====================================================================

namespace MerkaiTrial.Application.Authorization
{
    public static class Modules
    {
        public const string Products = "products";
        public const string Contacts = "contacts";
        public const string Companies = "companies";
        public const string Leads = "leads";
        public const string Deals = "deals";
        public const string Quotes = "quotes";
        public const string Invoices = "invoices";
        public const string Users = "users";
        public const string Roles = "roles";
        public const string Audit = "audit";        // ← 024: existed in ModuleCatalog, not here
        public const string Tenants = "tenants";
        public const string Reports = "reports";

        /// <summary>
        /// How the pipeline is shaped — stages, the sales process, lead
        /// statuses. Held by nobody until a tenant admin grants it; admins
        /// bypass it like every other permission.
        /// </summary>
        public const string Settings = "settings";  // ← 024
    }

    public static class Actions
    {
        public const string Create = "create";
        public const string Read = "read";
        public const string Update = "update";
        public const string Delete = "delete";
    }

    public static class Policies
    {
        public const string CompaniesRead = "companies.read";
        public const string CompaniesCreate = "companies.create";
        public const string CompaniesUpdate = "companies.update";
        public const string CompaniesDelete = "companies.delete";

        public const string ContactsRead = "contacts.read";
        public const string ContactsCreate = "contacts.create";
        public const string ContactsUpdate = "contacts.update";
        public const string ContactsDelete = "contacts.delete";

        public const string ProductsRead = "products.read";
        public const string ProductsCreate = "products.create";
        public const string ProductsUpdate = "products.update";
        public const string ProductsDelete = "products.delete";

        public const string LeadsRead = "leads.read";
        public const string LeadsCreate = "leads.create";
        public const string LeadsUpdate = "leads.update";
        public const string LeadsDelete = "leads.delete";

        public const string DealsRead = "deals.read";
        public const string DealsCreate = "deals.create";
        public const string DealsUpdate = "deals.update";
        public const string DealsDelete = "deals.delete";

        public const string QuotesRead = "quotes.read";
        public const string QuotesCreate = "quotes.create";
        public const string QuotesUpdate = "quotes.update";
        public const string QuotesDelete = "quotes.delete";

        public const string InvoicesRead = "invoices.read";
        public const string InvoicesCreate = "invoices.create";
        public const string InvoicesUpdate = "invoices.update";
        public const string InvoicesDelete = "invoices.delete";

        // READ ONLY — was missing entirely, while ReportsCreate and
        // ReportsDelete existed and were never used.
        public const string ReportsRead = "reports.read";

        public const string UsersRead = "users.read";
        public const string UsersCreate = "users.create";
        public const string UsersUpdate = "users.update";
        public const string UsersDelete = "users.delete";

        public const string RolesRead = "roles.read";
        public const string RolesCreate = "roles.create";
        public const string RolesUpdate = "roles.update";
        public const string RolesDelete = "roles.delete";

        // READ ONLY — the Activity Log.
        public const string AuditRead = "audit.read";

        // ── 024: how the pipeline is shaped ──────────────────────────
        // Used as [Authorize(Policy = ...)] on every write to pipeline
        // stages, the sales process and lead statuses. Reading those stays
        // on deals.read / leads.read, because every deal page, board and
        // lead list needs the stage and status lists to render a picker.
        public const string SettingsRead = "settings.read";
        public const string SettingsCreate = "settings.create";
        public const string SettingsUpdate = "settings.update";
        public const string SettingsDelete = "settings.delete";
    }
}
