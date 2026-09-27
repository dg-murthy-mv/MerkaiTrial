// =====================================================================
// OutboundMessageConfiguration.cs + UserNotificationPreferenceConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// NEW FILE (038). Both configurations live here because they are two
// halves of the same feature and neither is long enough to justify its
// own file.
//
// THE INDEX THAT MATTERS
//   IX_OutboundMessages_Claim is filtered to Pending and Failed only. The
//   worker asks "what is due?" every few seconds, for ever. Without the
//   filter that query walks every message ever sent; with it, the index
//   contains only the handful still owed a send, and it stays that size
//   permanently no matter how much history builds up behind it.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class OutboundMessageConfiguration : IEntityTypeConfiguration<OutboundMessage>
    {
        public void Configure(EntityTypeBuilder<OutboundMessage> builder)
        {
            builder.ToTable("OutboundMessages");

            builder.HasKey(m => m.Id);

            builder.Property(m => m.TenantId).IsRequired();

            builder.Property(m => m.Channel).HasConversion<int>().IsRequired();
            builder.Property(m => m.Status).HasConversion<int>().IsRequired();
            builder.Property(m => m.EventType).HasConversion<int>().IsRequired();

            // 320 = the practical maximum for an email address (64 local +
            // @ + 255 domain).
            builder.Property(m => m.ToAddress).IsRequired().HasMaxLength(320);
            builder.Property(m => m.ToName).HasMaxLength(200);
            builder.Property(m => m.ReplyToAddress).HasMaxLength(320);

            builder.Property(m => m.Subject).IsRequired().HasMaxLength(300);

            // nvarchar(max): an HTML email body has no sensible bound, and
            // these are never filtered or indexed on.
            builder.Property(m => m.BodyHtml).IsRequired();
            builder.Property(m => m.BodyText).IsRequired();

            builder.Property(m => m.EntityType).HasMaxLength(50);
            builder.Property(m => m.ProviderMessageId).HasMaxLength(200);

            // Bounded so a provider returning a wall of text cannot fail the
            // save that is trying to RECORD the failure. The sender truncates
            // to match.
            builder.Property(m => m.LastError).HasMaxLength(2000);

            builder.Property(m => m.UpdatedBy).HasMaxLength(200);

            // ── Indexes ───────────────────────────────────────────────

            // The worker's claim query. Filtered: see the header.
            // 0 = Pending, 3 = Failed.
            builder.HasIndex(m => new { m.Status, m.NextAttemptAtUtc })
                   .HasDatabaseName("IX_OutboundMessages_Claim")
                   .HasFilter("[Status] IN (0, 3)");

            // The log page: this tenant's messages, newest first.
            builder.HasIndex(m => new { m.TenantId, m.CreatedAtUtc })
                   .HasDatabaseName("IX_OutboundMessages_Log")
                   .IsDescending(false, true);

            // "What did we send about this quote?"
            builder.HasIndex(m => new { m.TenantId, m.EntityType, m.EntityId })
                   .HasDatabaseName("IX_OutboundMessages_Entity");
        }
    }

    public class UserNotificationPreferenceConfiguration
        : IEntityTypeConfiguration<UserNotificationPreference>
    {
        public void Configure(EntityTypeBuilder<UserNotificationPreference> builder)
        {
            builder.ToTable("UserNotificationPreferences");

            builder.HasKey(p => p.Id);

            builder.Property(p => p.TenantId).IsRequired();
            builder.Property(p => p.UserId).IsRequired();
            builder.Property(p => p.EventType).HasConversion<int>().IsRequired();

            builder.Property(p => p.UpdatedBy).HasMaxLength(200);

            builder.HasOne(p => p.User)
                   .WithMany()
                   .HasForeignKey(p => p.UserId)
                   .OnDelete(DeleteBehavior.Cascade);

            // One row per user per event type, enforced rather than trusted:
            // two rows for the same pair would make "what does this person
            // want" ambiguous, and the settings page would save a duplicate
            // every time it was used.
            builder.HasIndex(p => new { p.TenantId, p.UserId, p.EventType })
                   .IsUnique()
                   .HasDatabaseName("UX_UserNotificationPreferences_User_Event");
        }
    }
}
