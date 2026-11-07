// =====================================================================
// PlanFeatureKeys.cs
// Location: MerkaiTrial.Application/Configuration/PlanFeatureKeys.cs
//
// NEW (114). The plan features the app ENFORCES, and the one attribute
// that says "this page / this endpoint needs that feature".
//
// Before 114 the Features list on a plan was mostly a label: only the
// report menu and the lead score looked at it, so a Starter workspace could
// use everything Professional and Enterprise sell. Now:
//
//   advanced_reports      the six Advanced reports (pages and API)
//   basic_reports         every report needs basic_reports OR advanced_reports
//   email_integration     the Shared Inbox, received emails, Inbound Email
//                         settings. Sending quotes and invoices by email is
//                         core and stays in every plan.
//   custom_fields         adding, changing, removing custom fields
//   deal_pipeline_custom  changing pipeline stages, the sales process and
//                         lead statuses (reading them stays: every deal and
//                         lead form needs the lists)
//   custom_roles          CREATING a new role. Editing the roles a workspace
//                         already has is in every plan.
//   audit_logs            Settings → Activity Log
//   ai_assistant          (122) the AI assistant: its panel, page, settings and API
//
// Not enforced, because nothing in the product does them yet: api_access,
// sso, webhooks, white_label, dedicated_support. leads, deals, contacts,
// companies, tasks and notes are in every plan.
//
// HOW IT IS USED
//   [RequiresPlanFeature(PlanFeatureKeys.CustomFields)] on a page model, a
//   controller or an action. Admin.Web's PlanFeaturePageFilter sends a page
//   to /Upgrade; WebApi's PlanFeatureFilter answers 403 "Not in your plan".
//   Several attributes = all must pass; several keys in one = any of them.
// =====================================================================

namespace MerkaiTrial.Application.Configuration;

public static class PlanFeatureKeys
{
    public const string BasicReports       = "basic_reports";
    public const string AdvancedReports    = "advanced_reports";
    public const string EmailIntegration   = "email_integration";
    public const string CustomFields       = "custom_fields";
    public const string CustomPipeline     = "deal_pipeline_custom";
    public const string CustomRoles        = "custom_roles";
    public const string AuditLogs          = "audit_logs";
    public const string LeadScoring        = "lead_scoring";
    /// <summary>122 — the AI assistant (Professional, Enterprise and trial plans).</summary>
    public const string AiAssistant        = "ai_assistant";
}

/// <summary>
/// 114. The workspace's plan must include one of these features. Read by
/// PlanFeaturePageFilter (Admin.Web) and PlanFeatureFilter (WebApi).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequiresPlanFeatureAttribute : Attribute
{
    public RequiresPlanFeatureAttribute(params string[] anyOf) => AnyOf = anyOf;

    /// <summary>The plan needs at least one of these.</summary>
    public IReadOnlyList<string> AnyOf { get; }
}
