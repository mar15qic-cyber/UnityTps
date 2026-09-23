namespace UnityFps.Api.Features;

public sealed record DirectMessageDto(long Id, long SenderId, long RecipientId, string ClientMessageId, string Body, DateTime CreatedAtUtc);
public sealed record ConversationDto(long PeerId, string Username, string IdentityTag, long LastMessageId, string Preview, int Unread, bool CanSend);
public sealed record DirectMessagePageDto(DirectMessageDto[] Messages, bool HasMore);
public sealed record RoomInvitationDto(long Id, long RoomId, long SenderId, string SenderName, string LeaderUsername, string MapId, string Mode, int JoinedPlayers, int MaxPlayers, DateTime ExpiresAtUtc, string State);
public sealed record SocialInboxDto(ConversationDto[] Conversations, RoomInvitationDto[] Invitations);
public sealed class SendDirectMessageRequest { public string ClientMessageId { get; set; } = ""; public string Body { get; set; } = ""; }
public sealed class ReadDirectMessageRequest { public long LastReadId { get; set; } }
public sealed class SendRoomInvitationRequest { public long RoomId { get; set; } public long FriendId { get; set; } }
public sealed class AcceptRoomInvitationRequest { public string? ClientProtocolId { get; set; } }
