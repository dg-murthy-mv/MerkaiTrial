using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class TaxRateConfiguration : IEntityTypeConfiguration<TaxRate>
    {
        public void Configure(EntityTypeBuilder<TaxRate> builder)
        {
            builder.ToTable("TaxRates");
            builder.HasKey(c => c.Id);
            builder.HasIndex(c => c.Name).IsUnique();           
            builder.Property(c => c.Name).IsRequired().HasMaxLength(100);
        }
    }
}
