// =====================================================================
// FILE: MerkaiTrial.Application/Authorization/PermissionConstants.cs
//
// STEP 3 CHANGE: a "roles" module is added.
//
// WHY: RolesController had no [Authorize] attributes at all. Under the new
// fallback policy it is no longer anonymous, but ANY authenticated user
// could still reach it — a Viewer could edit permissions.
//
// Reusing users.* was the alternative. Rejected: "can manage people" and
// "can change what people are allowed to do" are different powers, and the
// second is the one that matters. A sales manager who can add a colleague
// should not thereby be able to grant that colleague delete rights on
// invoices.
//
// PermissionPolicyProvider builds "module.action" policies at runtime, so
// nothing needs registering — adding the constants below is enough.
//
// NOTE: PermissionHandler grants a blanket bypass on IsTenantAdmin == true,
// so a tenant admin passes these checks regardless. That is intended: the
// client's admin is meant to manage their own tenant's roles.
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
        public const string Roles = "roles";        // ← STEP 3: added
        public const string Tenants = "tenants";
        public const string Reports = "reports";
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
    }
}
