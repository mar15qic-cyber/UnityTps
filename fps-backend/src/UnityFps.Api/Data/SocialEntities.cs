namespace UnityFps.Api.Data;

public sealed class DirectMessage
{
    public long Id { get; set; }
    public long SenderId { get; set; }
    public long RecipientId { get; set; }
    public string ClientMessageId { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class DirectMessageRead
{
    public long UserId { get; set; }
    public long PeerId { get; set; }
    public long LastReadId { get; set; }
}

public sealed class RoomInvitation
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public long SenderId { get; set; }
    public long RecipientId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string State { get; set; } = "Pending";
}
