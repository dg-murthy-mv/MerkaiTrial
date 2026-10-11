// =====================================================================
// InboundEmailConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/InboundEmailConfiguration.cs
//
// NEW FILE (083). Maps the three tables 083_InboundEmail.sql creates.
//
// THE LENGTHS BELOW MUST MATCH THE SQL. EF does not create these tables —
// the script does — so a mismatch is not caught at startup. It shows up as
// "String or binary data would be truncated" on the first long subject.
// The handlers truncate to these same numbers before saving.
//
// Picked up by ApplyConfigurationsFromAssembly in FlowDbContext; nothing
// to register.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class InboundEmailConfiguration : IEntityTypeConfiguration<InboundEmail>
    {
        public void Configure(EntityTypeBuilder<InboundEmail> b)
        {
            b.ToTable("InboundEmails");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();

            b.Property(e => e.ProviderEmailId).IsRequired().HasMaxLength(100);
            b.HasIndex(e => e.ProviderEmailId).IsUnique();

            b.Property(e => e.MessageId).HasMaxLength(500);
            b.Property(e => e.InReplyTo).HasMaxLength(500);

            b.Property(e => e.Direction).HasConversion<int>();
            b.Property(e => e.Status).HasConversion<int>();
            b.Property(e => e.MatchMethod).HasConversion<int>();
            b.Property(e => e.ProcessingState).HasConversion<int>();

            b.Property(e => e.FromAddress).IsRequired().HasMaxLength(320);
            b.Property(e => e.FromName).HasMaxLength(200);
            b.Property(e => e.ToAddresses).HasMaxLength(2000);
            b.Property(e => e.CcAddresses).HasMaxLength(2000);
            b.Property(e => e.CounterpartAddress).HasMaxLength(320);

            b.Property(e => e.Subject).IsRequired().HasMaxLength(500);
            b.Property(e => e.Snippet).HasMaxLength(300);
            // BodyText, BodyHtml, MetadataJson: NVARCHAR(MAX) — EF's default.

            b.Property(e => e.AuthSummary).HasMaxLength(100);
            b.Property(e => e.MatchNote).HasMaxLength(300);
            b.Property(e => e.AttachmentNote).HasMaxLength(1000);
            b.Property(e => e.ProcessingError).HasMaxLength(1000);
            b.Property(e => e.LinkedBy).HasMaxLength(200);
            b.Property(e => e.DeletedBy).HasMaxLength(200);

            b.HasMany(e => e.Attachments)
             .WithOne(a => a.Email!)
             .HasForeignKey(a => a.InboundEmailId)
             .OnDelete(DeleteBehavior.Cascade);
        }
    }

    public class InboundEmailAttachmentConfiguration : IEntityTypeConfiguration<InboundEmailAttachment>
    {
        public void Configure(EntityTypeBuilder<InboundEmailAttachment> b)
        {
            b.ToTable("InboundEmailAttachments");
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).ValueGeneratedNever();

            b.Property(a => a.FileName).IsRequired().HasMaxLength(255);
            b.Property(a => a.FileUrl).IsRequired().HasMaxLength(1000);
            b.Property(a => a.MimeType).HasMaxLength(200);
        }
    }

    public class EmailReplyTokenConfiguration : IEntityTypeConfiguration<EmailReplyToken>
    {
        public void Configure(EntityTypeBuilder<EmailReplyToken> b)
        {
            b.ToTable("EmailReplyTokens");
            b.HasKey(t => t.Token);

            b.Property(t => t.Token).HasMaxLength(40);
            b.Property(t => t.EntityType).IsRequired().HasMaxLength(50);
        }
    }
}
