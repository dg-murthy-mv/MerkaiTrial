// =====================================================================
// NotificationConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/NotificationConfiguration.cs
//
// NEW FILE (037).
//
// TWO INDEXES, FOR TWO DIFFERENT QUESTIONS
//
//   IX_Notifications_Feed    (TenantId, RecipientUserId, CreatedAtUtc DESC)
//       The notifications page: this person's list, newest first.
//
//   IX_Notifications_Unread  filtered on ReadAtUtc IS NULL
//                                    AND DismissedAtUtc IS NULL
//       The bell count, which every signed-in browser asks for every 45
//       seconds. A filtered index means that query reads only the handful
//       of unread rows rather than walking a person's whole history, and it
//       stays small no matter how many notifications accumulate.
//
// RECIPIENT FK IS RESTRICT, NOT CASCADE
//   Deleting a user must not silently delete their notification history.
//   Users are soft-deleted here anyway (IsDeleted), so this is about a
//   hard delete during cleanup — and in that case the right answer is to
//   notice, not to lose rows.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
    {
        public void Configure(EntityTypeBuilder<Notification> builder)
        {
            builder.ToTable("Notifications");

            builder.HasKey(n => n.Id);

            builder.Property(n => n.TenantId).IsRequired();
            builder.Property(n => n.RecipientUserId).IsRequired();

            // Stored as an int. The enum's numbers are in the database, so
            // members are appended and never renumbered.
            builder.Property(n => n.EventType)
                   .HasConversion<int>()
                   .IsRequired();

            builder.Property(n => n.Title)
                   .IsRequired()
                   .HasMaxLength(200);

            builder.Property(n => n.Body)
                   .HasMaxLength(500);

            builder.Property(n => n.EntityType)
                   .HasMaxLength(50);

            builder.Property(n => n.ActorName)
                   .HasMaxLength(200);

            builder.Property(n => n.CreatedAtUtc).IsRequired();

            // ── Relationships ─────────────────────────────────────────
            builder.HasOne(n => n.Recipient)
                   .WithMany()
                   .HasForeignKey(n => n.RecipientUserId)
                   .OnDelete(DeleteBehavior.Restrict);

            // ── Indexes ───────────────────────────────────────────────

            // The page's list.
            builder.HasIndex(n => new { n.TenantId, n.RecipientUserId, n.CreatedAtUtc })
                   .HasDatabaseName("IX_Notifications_Feed")
                   .IsDescending(false, false, true);

            // The bell count, asked by every open browser every 45 seconds.
            builder.HasIndex(n => new { n.TenantId, n.RecipientUserId })
                   .HasDatabaseName("IX_Notifications_Unread")
                   .HasFilter("[ReadAtUtc] IS NULL AND [DismissedAtUtc] IS NULL");

            // Finding everything raised about one record — used when a page
            // wants to mark "all notifications about this deal" as read.
            builder.HasIndex(n => new { n.TenantId, n.EntityType, n.EntityId })
                   .HasDatabaseName("IX_Notifications_Entity");
        }
    }
}
