// =====================================================================
// WhatsAppTemplateConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/WhatsAppTemplateConfiguration.cs
//
// NEW FILE (044). Its own file, so the 038 and 040 configurations stay
// exactly as you applied them.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class WhatsAppTemplateConfiguration : IEntityTypeConfiguration<WhatsAppTemplate>
    {
        public void Configure(EntityTypeBuilder<WhatsAppTemplate> builder)
        {
            builder.ToTable("WhatsAppTemplates");

            builder.HasKey(t => t.Id);

            builder.Property(t => t.EventType).HasConversion<int>().IsRequired();

            // Meta's own limits. TemplateName is lower case, digits and
            // underscores, up to 512; language codes are short.
            builder.Property(t => t.TemplateName).HasMaxLength(512).IsRequired();
            builder.Property(t => t.LanguageCode).HasMaxLength(10).IsRequired();
            builder.Property(t => t.Category).HasMaxLength(30).IsRequired();
            builder.Property(t => t.BodyPreview).HasMaxLength(2000);
            builder.Property(t => t.VariableHints).HasMaxLength(1000);
            builder.Property(t => t.UpdatedBy).HasMaxLength(200);

            // One template per event per language.
            //
            // Not per event alone: the same notification in English and Hindi
            // is two approved templates with two names, and both are valid at
            // once. Not per name either — a name is unique within a language,
            // and the pair is what Meta is actually keyed on.
            builder.HasIndex(t => new { t.EventType, t.LanguageCode })
                   .IsUnique()
                   .HasDatabaseName("UX_WhatsAppTemplates_Event_Language");
        }
    }
}
