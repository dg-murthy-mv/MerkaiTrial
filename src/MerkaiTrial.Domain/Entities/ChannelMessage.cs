using MerkaiTrial.Domain.Common;
using MerkaiTrial.Domain.Enums;
namespace MerkaiTrial.Domain.Entities;
public class ChannelMessage : Entity {
    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = default!;
    public Channel Channel { get; set; } = Channel.Web;
    public bool Outbound { get; set; }
    public string Text { get; set; } = default!;
    public string Status { get; set; } = "Queued"; // Sent/Delivered/Read
    public DateTime? SentAtUtc { get; set; }
}
