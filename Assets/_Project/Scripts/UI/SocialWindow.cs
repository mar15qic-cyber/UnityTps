using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using Game.Gameplay.Menu;
using Game.Gameplay.Network;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class SocialWindow : MonoBehaviour
    {
        public static SocialWindow Instance { get; private set; }
        private RectTransform contacts, messages;
        private TMP_Text title, feedback;
        private TMP_InputField input;
        private Button send, older;
        private long peer;
        private string peerName;
        private int epoch;
        private bool busy, loading, combat;
        private long generation;
        private GameObject confirmationRoot;
        public static int EscapeConsumedFrame { get; private set; } = -1;
        private CancellationTokenSource lifetime;
        private SendDirectMessageRequest pending;
        private readonly List<DirectMessageDto> history = new();
        private IApiClient Api => AppRoot.Instance.ApiClient;
        private AccountSession Session => AppRoot.Instance.Session;

        public static void Open(Canvas canvas, long peerId = 0, string name = null)
        {
            if(canvas==null || AppRoot.Instance?.Session.IsAuthenticated!=true)return;
            if(Instance!=null) { if(peerId!=0)Instance.Select(peerId,name); return; }
            foreach(var chat in canvas.GetComponentsInChildren<Chat.ChatHudView>())chat.ForceCloseAndRelease();
            var root=UIComponents.Panel("SocialWindow",canvas.transform,new Color(0,0,0,.65f),Vector2.zero,Vector2.one,0,false);
            root.GetComponent<Image>().raycastTarget=true;
            Instance=root.AddComponent<SocialWindow>();
            Instance.Build();
            if(peerId!=0)Instance.Select(peerId,name);
        }
        private void Build()
        {
            combat=GameplayMenuController.Instance!=null;
            generation=NetworkLaunchContext.CurrentGeneration;
            lifetime=CancellationTokenSource.CreateLinkedTokenSource(SocialSession.Instance.Token);
            var panel=UIComponents.Panel("Dialog",transform,UITheme.BackgroundPanel,new Vector2(.18f,.18f),new Vector2(.82f,.82f),4,true).transform;
            panel.GetComponent<Image>().raycastTarget=true;
            title=Text(panel,"好友消息与邀请",24,new Vector2(.04f,.9f),new Vector2(.86f,.98f));
            Button(panel,"×",new Vector2(.90f,.9f),new Vector2(.98f,.98f),Close);
            contacts=LobbyPresenter.TacticalScroll(panel,"Contacts",new Vector2(.02f,.04f),new Vector2(.31f,.87f));
            messages=LobbyPresenter.TacticalScroll(panel,"Messages",new Vector2(.34f,.26f),new Vector2(.98f,.80f));
            older=Button(panel,"更早的消息",new Vector2(.35f,.81f),new Vector2(.62f,.87f),()=>_=Load(true));
            input=UIComponents.Input("DirectMessageInput",panel,"输入私信，最多 100 字",new Vector2(.35f,.11f),new Vector2(.78f,.23f));
            input.characterLimit=200;
            input.onSubmit.AddListener(value=>{ _=Send(); });
            send=Button(panel,"发送",new Vector2(.80f,.11f),new Vector2(.98f,.23f),()=>_=Send());
            feedback=Text(panel,"选择好友或历史会话",16,new Vector2(.35f,.02f),new Vector2(.98f,.09f));
            SocialSession.Instance.Changed+=Refresh;
            Refresh();
            if(combat) { GameplayInputGate.SetChatFocused(true); Cursor.lockState=CursorLockMode.None; Cursor.visible=true; }
        }
        private void Update()
        {
            if(lifetime==null || lifetime.IsCancellationRequested) { Close(); return; }
            if(combat && (GameplayInputGate.MenuOpen || GameplayInputGate.HardLocked || NetworkLaunchContext.CurrentGeneration!=generation)) { Close(); return; }
            if(Keyboard.current!=null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                EscapeConsumedFrame=Time.frameCount;
                if(confirmationRoot!=null){Destroy(confirmationRoot);confirmationRoot=null;}else Close();
            }
        }
        private void Refresh()
        {
            if(contacts==null)return;
            foreach(Transform child in contacts) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
            var social=SocialSession.Instance;
            var people=new Dictionary<long,string>();
            foreach(var f in social.Friends?.friends??Array.Empty<FriendEntryDto>())people[f.userId]=f.username+"#"+f.identityTag;
            foreach(var c in social.Inbox?.conversations??Array.Empty<ConversationDto>())people[c.peerId]=c.username+"#"+c.identityTag;
            foreach(var person in people)
            {
                var row=Row(contacts,96);var captured=person;
                var c=social.Inbox?.conversations?.FirstOrDefault(x=>x.peerId==person.Key);
                Button(row,person.Value+(c?.unread>0?" ("+c.unread+")":""),new Vector2(0,.42f),Vector2.one,()=>Select(captured.Key,captured.Value));
                var inviteButton=Button(row,"邀请进入房间",Vector2.zero,new Vector2(1,.39f),()=>_=Invite(captured.Key));
                var friend=social.Friends?.friends?.FirstOrDefault(f=>f.userId==person.Key);
                inviteButton.interactable=!combat && Session.Room?.Status=="Waiting" && friend!=null && friend.presence!=FriendPresence.Offline;
            }
            foreach(var invite in social.Inbox?.invitations??Array.Empty<RoomInvitationDto>())
            {
                var row=Row(contacts,155);
                Text(row,$"{invite.senderName} 邀请你\n{invite.mapId} · {invite.mode}\n房主 {invite.leaderUsername} · {invite.joinedPlayers}/{invite.maxPlayers}",16,new Vector2(.02f,.36f),new Vector2(.98f,1));
                var accept=Button(row,combat?"战斗后处理":"接受",new Vector2(.02f,.03f),new Vector2(.48f,.30f),()=>ConfirmAccept(invite));
                accept.interactable=!combat && !busy;
                Button(row,"拒绝",new Vector2(.52f,.03f),new Vector2(.98f,.30f),()=>_=Reject(invite.id));
            }
            if(peer>0)
            {
                send.interactable=!busy && social.Friends?.friends?.Any(f=>f.userId==peer)==true;
                input.interactable=send.interactable;
                if(!input.interactable && !busy)feedback.text="对方已不是好友，历史消息仍可查看";
                var latest=social.Inbox?.conversations?.FirstOrDefault(c=>c.peerId==peer)?.lastMessageId??0;
                if(!loading && latest>(history.LastOrDefault()?.id??0))_=Load(false);
            }
        }
        private void Select(long id,string name)
        {
            peer=id;peerName=name;epoch++;history.Clear();pending=null;loading=false;busy=false;
            input.text="";title.text="私信 · "+name;feedback.text="正在加载…";
            DrawHistory();Refresh();_=Load(false);
        }
        private async Task Load(bool previous)
        {
            if(peer==0 || loading)return;
            var version=epoch; var ct=lifetime.Token;loading=true;
            try
            {
                var result=await Api.GetDirectMessagesAsync(peer,previous?(history.FirstOrDefault()?.id??0):0,previous?0:(history.LastOrDefault()?.id??0),ct);
                if(ct.IsCancellationRequested || version!=epoch)return;
                if(!result.Success) { feedback.text=result.Message;return; }
                foreach(var m in result.Data.messages??Array.Empty<DirectMessageDto>())if(!history.Any(x=>x.id==m.id))history.Add(m);
                history.Sort((a,b)=>a.id.CompareTo(b.id));
                if(previous || history.Count<=50)older.interactable=result.Data.hasMore;
                DrawHistory();if(!previous)messages.GetComponentInParent<ScrollRect>().verticalNormalizedPosition=0;
                feedback.text="";
                if(history.Count>0)await Api.ReadDirectMessagesAsync(peer,history[history.Count-1].id,ct);
            }
            catch(OperationCanceledException) { }
            finally { if(!ct.IsCancellationRequested && version==epoch)loading=false; }
        }
        private void DrawHistory()
        {
            foreach(Transform child in messages) { child.gameObject.SetActive(false);Destroy(child.gameObject); }
            foreach(var message in history)
            {
                var row=Row(messages,76);
                var label=Text(row,(message.senderId==peer?peerName:"我")+"  "+message.createdAtUtc+"\n"+message.body,18, new Vector2(.02f,0),new Vector2(.98f,1));
                row.GetComponent<LayoutElement>().preferredHeight=Mathf.Max(76, label.GetPreferredValues(label.text, Mathf.Max(200,messages.rect.width*.96f), float.PositiveInfinity).y+18);
            }
            Canvas.ForceUpdateCanvases();
        }
        private async Task Send()
        {
            if(busy || peer==0 || !input.interactable || string.IsNullOrWhiteSpace(input.text))return;
            if(pending==null || pending.body!=input.text)pending=new SendDirectMessageRequest{clientMessageId=Guid.NewGuid().ToString("D"),body=input.text};
            var version=epoch;var ct=lifetime.Token;busy=true;send.interactable=false;input.interactable=false;feedback.text="正在发送…";
            try
            {
                var result=await Api.SendDirectMessageAsync(peer,pending,ct);
                if(ct.IsCancellationRequested || version!=epoch)return;
                feedback.text=result.Success?"已发送":result.Message+" · 点击重试";
                send.GetComponentInChildren<TMP_Text>().text=result.Success?"发送":"重试";
                if(result.Success){ input.text="";pending=null;if(!history.Any(m=>m.id==result.Data.id))history.Add(result.Data);DrawHistory();messages.GetComponentInParent<ScrollRect>().verticalNormalizedPosition=0;SocialSession.Instance.RefreshSoon(); }
            }
            catch(OperationCanceledException) { }
            finally { if(!ct.IsCancellationRequested && version==epoch){busy=false;send.interactable=input.interactable=SocialSession.Instance?.Friends?.friends?.Any(f=>f.userId==peer)==true;} }
        }
        private async Task Invite(long friend)
        {
            var ct=lifetime.Token;
            if(!long.TryParse(Session.Room?.RoomId,out var roomId))return;
            var result=await Api.InviteFriendAsync(roomId,friend,ct);
            if(!ct.IsCancellationRequested)feedback.text=result.Success?"邀请已发送，有效期 5 分钟":result.Message;
        }
        private void ConfirmAccept(RoomInvitationDto invite)
        {
            if(busy || combat)return;
            if(Session.Room!=null && Session.Room.RoomId!=invite.roomId.ToString())
            {
                if(Session.Room.Status!="Waiting"){feedback.text="请在战斗结束并返回后接受邀请";return;}
                if(confirmationRoot!=null)return;
                confirmationRoot=UIComponents.Panel("ConfirmBackdrop",transform,new Color(0,0,0,.6f),Vector2.zero,Vector2.one,0,false);
                confirmationRoot.GetComponent<Image>().raycastTarget=true;
                var confirmation=UIComponents.Panel("ConfirmSwitch",confirmationRoot.transform,UITheme.BackgroundPanel,new Vector2(.34f,.4f),new Vector2(.66f,.6f),4,true);
                confirmation.GetComponent<Image>().raycastTarget=true;
                Text(confirmation.transform,"退出当前房间并接受邀请？",22,new Vector2(.05f,.5f),new Vector2(.95f,.95f));
                Button(confirmation.transform,"取消",new Vector2(.05f,.08f),new Vector2(.45f,.4f),()=>{Destroy(confirmationRoot);confirmationRoot=null;});
                Button(confirmation.transform,"退出并加入",new Vector2(.55f,.08f),new Vector2(.95f,.4f),()=>{Destroy(confirmationRoot);confirmationRoot=null;_=Accept(invite,true);});
            }
            else _=Accept(invite,false);
        }
        private async Task Accept(RoomInvitationDto invite,bool leave)
        {
            if(busy)return;busy=true;var ct=lifetime.Token;
            try
            {
                if(leave)
                {
                    var left=await Api.LeaveRoomAsync(ct);if(ct.IsCancellationRequested)return;
                    if(!left.Success){feedback.text=left.Message;return;}
                    Session.ClearRoom(); Chat.ChatController.StopAndClear();
                }
                var result=await Api.AcceptRoomInvitationAsync(invite.id,GameProtocolIdentity.ProtocolId,ct);
                if(ct.IsCancellationRequested)return;
                var lobby=FindFirstObjectByType<LobbyPresenter>();
                if(result.Success){Session.ApplyRoomSnapshot(result.Data);Close();lobby?.Navigate(LobbyPage.WaitingRoom);}
                else {feedback.text=result.Message;if(leave){Close();Session.SetGameplayError(result.Message);lobby?.Navigate(LobbyPage.OnlineJoin);}}
                SocialSession.Instance.RefreshSoon();
            }
            catch(OperationCanceledException) { }
            finally { if(!ct.IsCancellationRequested)busy=false; }
        }
        private async Task Reject(long id) { var ct=lifetime.Token;var result=await Api.RejectRoomInvitationAsync(id,ct);if(!ct.IsCancellationRequested){feedback.text=result.Success?"已拒绝":result.Message;SocialSession.Instance.RefreshSoon();} }
        public static void CloseActive() { if(Instance!=null)Instance.Close(); }
        private void Close()
        {
            if(Instance!=this)return;
            Instance=null;lifetime?.Cancel();
            if(SocialSession.Instance!=null)SocialSession.Instance.Changed-=Refresh;
            if(combat && generation==NetworkLaunchContext.CurrentGeneration){GameplayInputGate.SetChatFocused(false);GameplayInputGate.GrantResumeGrace(2);}
            gameObject.SetActive(false);Destroy(gameObject);
        }
        private void OnDestroy(){if(Instance==this)Close();lifetime?.Cancel();lifetime?.Dispose();if(SocialSession.Instance!=null)SocialSession.Instance.Changed-=Refresh;}
        private static Transform Row(Transform parent,float height){var go=UIComponents.Panel("Row",parent,UITheme.CardSurface,Vector2.zero,Vector2.one,2,false);go.AddComponent<LayoutElement>().preferredHeight=height;return go.transform;}
        private static Button Button(Transform parent,string label,Vector2 min,Vector2 max,Action action){var b=UIComponents.Button(label,parent,label,UIComponents.ButtonKind.Secondary,min,max);b.onClick.AddListener(()=>action());var t=b.GetComponentInChildren<TMP_Text>();if(t!=null)t.richText=false;return b;}
        private static TMP_Text Text(Transform parent,string value,int size,Vector2 min,Vector2 max){var go=new GameObject("Text",typeof(RectTransform));go.transform.SetParent(parent,false);var r=(RectTransform)go.transform;r.anchorMin=min;r.anchorMax=max;r.offsetMin=r.offsetMax=Vector2.zero;var t=go.AddComponent<TextMeshProUGUI>();t.text=value;t.fontSize=size;t.color=UITheme.TextPrimary;t.richText=false;t.raycastTarget=false;t.overflowMode=TextOverflowModes.Ellipsis;return t;}
    }
}
