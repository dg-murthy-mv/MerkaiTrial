using MerkaiTrial.Domain.Common;
namespace MerkaiTrial.Domain.Entities;
public class Consent : Entity {
    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = default!;
    public string Purpose { get; set; } = "Marketing";
    public bool Granted { get; set; } = true;
    public string LawfulBasis { get; set; } = "consent";
    public string Source { get; set; } = "web";
    public DateTime GrantedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; set; }
}
