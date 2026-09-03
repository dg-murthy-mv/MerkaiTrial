# MadeeFlow — Starter Plan (v1)
Multi-tenant-ready skeleton focused on **Starter** features:
- Widget + Unified Inbox (storage only; no external gateways wired)
- Basic CRM: Contacts & Leads
- Pipelines (limit = 1 per tenant)
- Journeys/Automations (limit = 1 per tenant)
- Consent ledger (PDPA/DPA basics)

> This is a compile-ready skeleton for .NET 8 with EF Core. Add your connection string and run migrations.

## Projects
- `MadeeFlow.Domain` — entities & enums
- `MadeeFlow.Infrastructure` — EF Core DbContext
- `MadeeFlow.WebApi` — minimal APIs (widget intake, CRM, inbox, pipelines, journeys, consent)
- `MadeeFlow.Admin.Web` — Razor Pages for Starter admin
- `seed/sql` — SQL to create a **Starter** demo tenant and sample data

## Quickstart
1. Set your DB string in `src/MadeeFlow.WebApi/appsettings.json` and `src/MadeeFlow.Admin.Web/appsettings.json`.
2. From solution root, run migrations from WebApi (after installing EF tools):
   ```bash
   dotnet restore
   dotnet tool install --global dotnet-ef
   dotnet ef migrations add InitialCreate -p src/MadeeFlow.Infrastructure -s src/MadeeFlow.WebApi
   dotnet ef database update -p src/MadeeFlow.Infrastructure -s src/MadeeFlow.WebApi
   ```
3. Seed Starter tenant: run `seed/sql/seed-starter.sql` against your DB.
4. Run WebApi then Admin.Web:
   ```bash
   dotnet run --project src/MadeeFlow.WebApi
   dotnet run --project src/MadeeFlow.Admin.Web
   ```
5. Open `http://localhost:5099/swagger` (WebApi) and `http://localhost:5199` (Admin).

## Tenancy
- Send `X-Tenant-Id` header. Default (if missing): `00000000-0000-0000-0000-000000000001` (Starter demo).
- Pipeline/Journey limits enforced server-side (Starter: 1 each).

## Widget
- Static demo under `src/MadeeFlow.Admin.Web/wwwroot/widget/widget.html`.
- Posts to `/api/widget/lead` on WebApi.
