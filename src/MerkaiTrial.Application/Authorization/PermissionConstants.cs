// FILE: MerkaiTrial.Application/Authorization/PermissionConstants.cs

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
        // Products
        public const string ProductsCreate = "products.create";
        public const string ProductsRead = "products.read";
        public const string ProductsUpdate = "products.update";
        public const string ProductsDelete = "products.delete";

        // Contacts
        public const string ContactsCreate = "contacts.create";
        public const string ContactsRead = "contacts.read";
        public const string ContactsUpdate = "contacts.update";
        public const string ContactsDelete = "contacts.delete";

        // Companies
        public const string CompaniesCreate = "companies.create";
        public const string CompaniesRead = "companies.read";
        public const string CompaniesUpdate = "companies.update";
        public const string CompaniesDelete = "companies.delete";

        // Leads
        public const string LeadsCreate = "leads.create";
        public const string LeadsRead = "leads.read";
        public const string LeadsUpdate = "leads.update";
        public const string LeadsDelete = "leads.delete";

        // Deals
        public const string DealsCreate = "deals.create";
        public const string DealsRead = "deals.read";
        public const string DealsUpdate = "deals.update";
        public const string DealsDelete = "deals.delete";

        // Quotes
        public const string QuotesCreate = "quotes.create";
        public const string QuotesRead = "quotes.read";
        public const string QuotesUpdate = "quotes.update";
        public const string QuotesDelete = "quotes.delete";

        // Invoices
        public const string InvoicesCreate = "invoices.create";
        public const string InvoicesRead = "invoices.read";
        public const string InvoicesUpdate = "invoices.update";
        public const string InvoicesDelete = "invoices.delete";

        // Users
        public const string UsersCreate = "users.create";
        public const string UsersRead = "users.read";
        public const string UsersUpdate = "users.update";
        public const string UsersDelete = "users.delete";

        // Tenants
        public const string TenantsCreate = "tenants.create";
        public const string TenantsRead = "tenants.read";
        public const string TenantsUpdate = "tenants.update";
        public const string TenantsDelete = "tenants.delete";

        // Reports
        public const string ReportsCreate = "reports.create";
        public const string ReportsDelete = "reports.delete";
    }
}
