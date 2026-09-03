// =====================================================================
// FILE: MerkaiTrial.Domain/Entities/User.cs
// CHANGED: credential + lockout fields added (migration 001).
//
// IsSuperAdmin moves from appsettings (DemoAuthenticationOptions) onto the
// user row. That is the whole point: super-admin status must be a property
// of an authenticated identity, never of a request-scoped flag that anyone
// can influence.
// =====================================================================

namespace MerkaiTrial.Domain.Entities
{
    public class User
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public string? Department { get; set; }
        public string? JobTitle { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsTenantAdmin { get; set; }

        // ── Credentials ───────────────────────────────────────────────
        /// <summary>Null means "no password set" — user cannot log in and must
        /// be sent an invite link. Never treat null as "any password works".</summary>
        public string? PasswordHash { get; set; }

        /// <summary>Rotated on password change and on forced sign-out. Carried
        /// in the auth cookie; a mismatch invalidates the session.</summary>
        public string? SecurityStamp { get; set; }

        public bool MustChangePassword { get; set; }
        public DateTime? EmailConfirmedAtUtc { get; set; }
        public DateTime? LastPasswordChangeUtc { get; set; }

        // ── Lockout ───────────────────────────────────────────────────
        public int FailedLoginCount { get; set; }
        public DateTime? LockoutEndUtc { get; set; }

        /// <summary>MadeeVision staff. Gates /Admin/* and ViewAs.</summary>
        public bool IsSuperAdmin { get; set; }

        public DateTime? LastLoginUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation
        public Tenant? Tenant { get; set; }
        public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

        public string FullName => $"{FirstName} {LastName}".Trim();

        public bool IsLockedOut() => LockoutEndUtc.HasValue && LockoutEndUtc.Value > DateTime.UtcNow;

        public bool CanSignIn() => IsActive && !IsDeleted && !IsLockedOut()
                                   && !string.IsNullOrEmpty(PasswordHash);
    }
}
