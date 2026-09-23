using System;
namespace Game.Account
{
    [Serializable] public sealed class DirectMessageDto { public long id, senderId, recipientId; public string clientMessageId, body, createdAtUtc; }
    [Serializable] public sealed class ConversationDto { public long peerId, lastMessageId; public string username, identityTag, preview; public int unread; public bool canSend; }
    [Serializable] public sealed class DirectMessagePageDto { public DirectMessageDto[] messages; public bool hasMore; }
    [Serializable] public sealed class RoomInvitationDto { public long id, roomId, senderId; public string senderName, leaderUsername, mapId, mode, expiresAtUtc, state; public int joinedPlayers, maxPlayers; }
    [Serializable] public sealed class SocialInboxDto { public ConversationDto[] conversations; public RoomInvitationDto[] invitations; }
    [Serializable] public sealed class SendDirectMessageRequest { public string clientMessageId, body; }
    [Serializable] public sealed class ReadDirectMessageRequest { public long lastReadId; }
    [Serializable] public sealed class SendRoomInvitationRequest { public long roomId, friendId; }
    [Serializable] public sealed class AcceptRoomInvitationRequest { public string clientProtocolId; }
}
