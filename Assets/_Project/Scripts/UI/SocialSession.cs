using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using UnityEngine;

namespace Game.UI
{
    /// <summary>One polling owner per authenticated account; views never own social polling.</summary>
    public sealed class SocialSession : MonoBehaviour
    {
        public static SocialSession Instance { get; private set; }
        public FriendListDto Friends { get; private set; }
        public SocialInboxDto Inbox { get; private set; }
        public event Action Changed;
        public CancellationToken Token => lifetime?.Token ?? new CancellationToken(true);
        private CancellationTokenSource lifetime;
        private string accountToken;
        private bool polling;
        private float nextPoll, nextFriends;
        public int Unread => (Inbox?.conversations ?? Array.Empty<ConversationDto>()).Sum(c=>c.unread);
        private void Awake() { Instance=this; }
        private void Update()
        {
            var session=AppRoot.Instance?.Session;
            var token=session?.IsAuthenticated==true?session.Token:null;
            if(token!=accountToken)
            {
                lifetime?.Cancel(); lifetime?.Dispose(); lifetime=null;
                accountToken=token; Friends=null; Inbox=null; polling=false; nextPoll=nextFriends=0;
                if(token!=null)lifetime=new CancellationTokenSource();
                Changed?.Invoke();
            }
            if(lifetime!=null && !polling && Time.unscaledTime>=nextPoll) _=Poll();
        }
        public void RefreshSoon() { nextPoll=nextFriends=0; }
        private async Task Poll()
        {
            var ct=Token; polling=true; nextPoll=Time.unscaledTime+3;
            try
            {
                var api=AppRoot.Instance.ApiClient;
                if(Time.unscaledTime>=nextFriends)
                {
                    nextFriends=Time.unscaledTime+15;
                    var friends=await api.GetFriendsAsync(ct);
                    if(ct.IsCancellationRequested)return;
                    if(friends.Success)Friends=friends.Data;
                }
                var inbox=await api.GetSocialInboxAsync(ct);
                if(ct.IsCancellationRequested)return;
                if(inbox.Success)Inbox=inbox.Data;
                Changed?.Invoke();
            }
            catch(OperationCanceledException) { }
            catch(Exception exception) { if(!ct.IsCancellationRequested)Debug.LogWarning("Social sync: "+exception.Message); }
            finally { if(!ct.IsCancellationRequested)polling=false; }
        }
        private void OnDestroy() { lifetime?.Cancel(); lifetime?.Dispose(); if(Instance==this)Instance=null; }
    }
}
