namespace MerkaiTrial.Domain.Entities;

public class InboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LeadId { get; set; }           // contact = Lead
    public bool Outbound { get; set; } = true; // → outbound, ← inbound if false
    public string Text { get; set; } = string.Empty;
    public string Status { get; set; } = "Sent"; // Sent/Delivered/Failed
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
