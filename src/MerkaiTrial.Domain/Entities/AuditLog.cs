using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    public static class AuditAction
    {
        public const string ViewAsStart = "ViewAsStart";
        public const string ViewAsEnd = "ViewAsEnd";
        public const string SignIn = "SignIn";
        public const string SignInFailed = "SignInFailed";
        public const string PasswordChanged = "PasswordChanged";
        public const string InviteIssued = "InviteIssued";
        public const string ResetIssued = "ResetIssued";
        public const string TrialProvisioned = "TrialProvisioned";
        public const string TrialExtended = "TrialExtended";
        public const string TrialSuspended = "TrialSuspended";
        public const string PermissionChanged = "PermissionChanged";
        public const string TenantPlanChanged = "TenantPlanChanged";
        public const string TrialConverted = "TrialConverted";
    }

    public class AuditLog
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Which tenant's data this concerns. NULL for system-level
        /// events (a failed login before any tenant is known, a plan edit).</summary>
        public Guid? TenantId { get; set; }

        public string EntityType { get; set; } = string.Empty;
        public Guid? EntityId { get; set; }
        public string Action { get; set; } = string.Empty;

        /// <summary>Identity the action was performed AS — during ViewAs this
        /// is the impersonated user.</summary>
        public string By { get; set; } = "system";

        /// <summary>Identity that actually performed it. During ViewAs this is
        /// the real super admin, and the difference between this and By is the
        /// entire value of the record.</summary>
        public Guid? ActorUserId { get; set; }

        public string? IpAddress { get; set; }

        /// <summary>JSON detail. Never put credentials, tokens or raw
        /// personal data here — an audit log is widely readable by design.</summary>
        public string? Data { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }

        // Present on the table, deliberately unused — see migration 002.
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }
    }
}
