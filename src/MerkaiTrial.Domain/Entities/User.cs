// =====================================================================
// FILE: MerkaiTrial.Domain/Entities/User.cs
// CHANGED: credential + lockout fields added (migration 001).
// CHANGED: TeamId added (migration 014) — drives "Team" record scope.
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
        /// <summary>
        /// Whatever was typed. Kept exactly as entered, because it is what
        /// someone dials and it may carry an extension, a second number, or
        /// a note. Never used for messaging — see MobileE164.
        /// </summary>
        public string? Phone { get; set; }

        // ── Messaging (044) ───────────────────────────────────────────

        /// <summary>
        /// 044. The user's mobile in E.164 — "+919876543210". A SEPARATE
        /// column from Phone, deliberately.
        ///
        /// WhatsApp (and later SMS) will not accept anything else: no
        /// spaces, no dashes, no leading zero, no local format. Phone above
        /// holds "098-765 4321 (ext 12)" and always will, because that is
        /// what people type and we should not fight them over it. This one
        /// is normalised on save, by PhoneNumbers.ToE164, using the
        /// workspace's country dial code when the user omits it.
        ///
        /// Null means "no mobile on file". That is not an error — it is why
        /// the workspace defaults page can tell an admin how many people
        /// have WhatsApp switched on but nowhere to send it, which is
        /// otherwise invisible and looks exactly like a broken feature.
        /// </summary>
        public string? MobileE164 { get; set; }

        /// <summary>
        /// 044. When this person agreed to receive WhatsApp messages from
        /// us. Null = they have not.
        ///
        /// A TIMESTAMP rather than a bool, because that is what consent is:
        /// Meta's policy requires opt-in before a business-initiated
        /// message, and if it is ever questioned the answer has to include
        /// WHEN. A bool would let us say yes and nothing more.
        ///
        /// Clearing the mobile number clears this too — consent is given
        /// for a number, not in the abstract.
        /// </summary>
        public DateTime? WhatsAppOptInAtUtc { get; set; }

        /// <summary>
        /// Ready to receive a WhatsApp message: a number to send to, and
        /// permission to use it. Both halves, always together.
        /// </summary>
        public bool CanReceiveWhatsApp =>
            !string.IsNullOrWhiteSpace(MobileE164) && WhatsAppOptInAtUtc.HasValue;
        public string? Department { get; set; }
        public string? JobTitle { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsTenantAdmin { get; set; }

        /// <summary>
        /// The user's team, if any. A user whose role has "Team" scope sees
        /// records owned by anyone with the same TeamId (plus unassigned
        /// ones). Null = no team: Team scope then behaves like Own + unassigned.
        /// Must belong to the same tenant — enforced in SetUserTeamHandler.
        /// </summary>
        public Guid? TeamId { get; set; }

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
