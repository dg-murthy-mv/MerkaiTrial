namespace MerkaiTrial.WebApi.Models;

public record ContactLiteDto(Guid Id, string FirstName, string? Email, string? Phone);

public record ContactMiniDto
{
    public Guid Id { get; init; }
    public string FirstName { get; init; } = "";
}

public record InboxMessageDto
{
    public Guid Id { get; init; }
    public bool Outbound { get; init; }
    public string Text { get; init; } = "";
    public string Status { get; init; } = "Queued";
    public DateTime CreatedAtUtc { get; init; }
    public ContactMiniDto Contact { get; init; } = new();
}

public record SendInboxRequest(Guid ContactId, string Text);
