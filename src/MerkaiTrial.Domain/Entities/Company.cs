using MerkaiTrial.Domain.Common;
namespace MerkaiTrial.Domain.Entities;
public class Company
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    // Basic Info
    public string Name { get; set; } = string.Empty;
    public string Country { get; set; } = "TH"; // Default Thailand
    public string? TaxId { get; set; }
    public string Vertical { get; set; } = "Generic";

    // Audit
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>
    /// 102. SQL Server rowversion — changes by itself on every write to the
    /// row. An edit form carries the value it loaded; a save made from an
    /// older value is refused instead of silently overwriting someone
    /// else's change (RecordVersion, FlowDbContext). Never set by code.
    /// </summary>
    public byte[]? RowVersion { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<Contact> Contacts { get; set; } = new List<Contact>();
    // Note: Deals are accessed through Contacts (Deal -> Contact -> Company)
}
