#nullable enable

namespace lycanthrope.Models;

public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? SenderPlayerId { get; set; }

    public string SenderName { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public bool IsSystem { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
