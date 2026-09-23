using System.Threading;
using System.Threading.Tasks;
namespace Game.Account
{
    public sealed partial class ApiClient
    {
        public Task<ApiResult<SocialInboxDto>> GetSocialInboxAsync(CancellationToken ct = default) => SendAsync<SocialInboxDto>("social-inbox", "GET", "/api/social/inbox", null, ct);
        public Task<ApiResult<DirectMessagePageDto>> GetDirectMessagesAsync(long peer, long before = 0, long after = 0, CancellationToken ct = default) =>
            SendAsync<DirectMessagePageDto>("dm-history-"+peer+"-"+before+"-"+after,"GET","/api/social/messages/"+peer+"?before="+before+"&after="+after,null,ct);
        public Task<ApiResult<DirectMessageDto>> SendDirectMessageAsync(long peer, SendDirectMessageRequest request, CancellationToken ct = default) =>
            SendAsync<DirectMessageDto>("dm-send-"+request.clientMessageId,"POST","/api/social/messages/"+peer,request,ct);
        public Task<ApiResult<object>> ReadDirectMessagesAsync(long peer,long last,CancellationToken ct=default) =>
            SendAsync<object>("dm-read-"+peer,"POST","/api/social/messages/"+peer+"/read",new ReadDirectMessageRequest{lastReadId=last},ct);
        public Task<ApiResult<object>> InviteFriendAsync(long room,long peer,CancellationToken ct=default) =>
            SendAsync<object>("invite-"+room+"-"+peer,"POST","/api/social/invitations",new SendRoomInvitationRequest{roomId=room,friendId=peer},ct);
        public Task<ApiResult<RoomSnapshotDto>> AcceptRoomInvitationAsync(long id,string protocol,CancellationToken ct=default) =>
            SendAsync<RoomSnapshotDto>("invite-accept-"+id,"POST","/api/social/invitations/"+id+"/accept",new AcceptRoomInvitationRequest{clientProtocolId=protocol},ct);
        public Task<ApiResult<object>> RejectRoomInvitationAsync(long id,CancellationToken ct=default) =>
            SendAsync<object>("invite-reject-"+id,"POST","/api/social/invitations/"+id+"/reject",null,ct);
    }
    public partial interface IApiClient
    {
        Task<ApiResult<SocialInboxDto>> GetSocialInboxAsync(CancellationToken ct=default);
        Task<ApiResult<DirectMessagePageDto>> GetDirectMessagesAsync(long peer,long before=0,long after=0,CancellationToken ct=default);
        Task<ApiResult<DirectMessageDto>> SendDirectMessageAsync(long peer,SendDirectMessageRequest request,CancellationToken ct=default);
        Task<ApiResult<object>> ReadDirectMessagesAsync(long peer,long last,CancellationToken ct=default);
        Task<ApiResult<object>> InviteFriendAsync(long room,long peer,CancellationToken ct=default);
        Task<ApiResult<RoomSnapshotDto>> AcceptRoomInvitationAsync(long id,string protocol,CancellationToken ct=default);
        Task<ApiResult<object>> RejectRoomInvitationAsync(long id,CancellationToken ct=default);
    }
}
