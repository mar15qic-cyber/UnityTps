using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed partial class LobbyPresenter
    {
        private GameObject socialRail, socialDrawer, socialModal;
        private Transform socialRows;
        private TMP_Text socialCount, socialFeedback, modalFeedback;
        private TMP_InputField friendsSearchInput, friendsTagInput;
        private bool socialExpanded, socialBusy;
        private int socialTab, socialEpoch;
        private CancellationTokenSource modalCts;

        private void BuildSocialShell()
        {
            socialRail = StyledPanel("SocialRail", canvas.transform, UITheme.BackgroundPanel,
                new Vector2(0.96f, 0.02f), new Vector2(0.999f, 0.92f));
            StyledButton(socialRail.transform, "好友", UIComponents.ButtonKind.Secondary,
                new Vector2(0.04f, 0.89f), new Vector2(0.96f, 0.97f), ToggleSocial);
            socialCount = StyledText(socialRail.transform, "0", UITheme.FontCaption, UITheme.AccentPrimary,
                new Vector2(0.05f, 0.70f), new Vector2(0.95f, 0.88f), TextAlignmentOptions.Center);
            StyledButton(socialRail.transform, "+", UIComponents.ButtonKind.Secondary,
                new Vector2(0.08f, 0.03f), new Vector2(0.92f, 0.10f), OpenAddFriendModal);
            socialDrawer = StyledPanel("SocialDrawer", canvas.transform, UITheme.BackgroundPanel,
                new Vector2(0.72f, 0.02f), new Vector2(0.956f, 0.92f));
            socialDrawer.GetComponent<Image>().raycastTarget = true;
            StyledText(socialDrawer.transform, "好友", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.06f, 0.91f), new Vector2(0.61f, 0.98f));
            StyledButton(socialDrawer.transform, "+ 添加", UIComponents.ButtonKind.Secondary,
                new Vector2(0.64f, 0.925f), new Vector2(0.94f, 0.98f), OpenAddFriendModal);
            var tabs = new[] { "列表", "收到", "已发送" };
            for (int i = 0; i < tabs.Length; i++)
            {
                var index = i;
                StyledButton(socialDrawer.transform, tabs[i], UIComponents.ButtonKind.Secondary,
                    new Vector2(0.05f + i * 0.30f, 0.84f), new Vector2(0.33f + i * 0.30f, 0.90f),
                    () => { socialTab = index; RefreshSocialViews(); });
            }
            socialRows = TacticalScroll(socialDrawer.transform, "SocialList", new Vector2(0.04f, 0.10f), new Vector2(0.96f, 0.82f));
            socialFeedback = StyledText(socialDrawer.transform, "", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.06f, 0.025f), new Vector2(0.94f, 0.09f));
            StyledButton(socialRail.transform, "消息", UIComponents.ButtonKind.Secondary,
                new Vector2(.04f,.48f),new Vector2(.96f,.60f),()=>SocialWindow.Open(canvas.GetComponent<Canvas>()));
            socialDrawer.SetActive(false);
        }

        internal static RectTransform TacticalScroll(Transform parent, string name, Vector2 min, Vector2 max)
        {
            var root = UIComponents.Panel(name, parent, Color.clear, min, max, 0, false);
            root.GetComponent<Image>().raycastTarget = true;
            var scroll = root.AddComponent<ScrollRect>();
            var viewport = UIComponents.Panel("Viewport", root.transform, Color.clear, Vector2.zero, Vector2.one, 0, false);
            viewport.AddComponent<RectMask2D>();
            var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            var rect = (RectTransform)content.transform;
            rect.anchorMin = new Vector2(0, 1); rect.anchorMax = Vector2.one; rect.pivot = new Vector2(0.5f, 1);
            rect.sizeDelta = Vector2.zero;
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 8; layout.childControlHeight = true; layout.childForceExpandHeight = false;
            layout.childControlWidth = true; layout.childForceExpandWidth = true;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = (RectTransform)viewport.transform; scroll.content = rect;
            scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 28;
            return rect;
        }

        private void ToggleSocial()
        {
            socialExpanded = !socialExpanded;
            socialDrawer.SetActive(socialExpanded);
            if (bodyRect != null) bodyRect.anchorMax = socialExpanded ? new Vector2(0.71f, BodyAnchorMaxVisible.y) : BodyAnchorMaxVisible;
            if (socialExpanded) { socialDrawer.transform.SetAsLastSibling(); RefreshSocialViews(); _ = RefreshSocialAsync(); }
        }
        private void RenderFriends() { Navigate(LobbyPage.Lobby); if (!socialExpanded) ToggleSocial(); }

        private void RefreshSocialViews()
        {
            if (socialCount == null) return;
            var friends = cachedFriends?.friends ?? Array.Empty<FriendEntryDto>();
            socialCount.text = friends.Count(f => f.presence != FriendPresence.Offline) + "\n在线";
            var incoming = cachedFriends?.incoming ?? Array.Empty<FriendRequestEntryDto>();
            var unread = SocialSession.Instance?.Unread ?? 0;
            var invites = SocialSession.Instance?.Inbox?.invitations?.Length ?? 0;
            if (unread + invites > 0) socialCount.text += "\n" + unread + " 消息\n" + invites + " 邀请";
            if (incoming.Length > 0) socialCount.text += "\n\n" + incoming.Length + "\n申请";
            if (!socialExpanded || socialRows == null) return;
            var scroll = socialRows.GetComponentInParent<ScrollRect>();
            var position = scroll.verticalNormalizedPosition;
            ClearChildren(socialRows);
            if (cachedFriends == null) { SocialHeading("同步好友中…"); return; }
            if (socialTab == 0)
            {
                if (friends.Length == 0) SocialHeading("暂无好友，点击右上角添加");
                foreach (bool online in new[] { true, false })
                {
                    var group = friends.Where(f => (f.presence != FriendPresence.Offline) == online).OrderBy(f => f.username).ToArray();
                    if (group.Length == 0) continue;
                    SocialHeading((online ? "在线" : "离线") + "  " + group.Length);
                    foreach (var friend in group)
                    {
                        var row = SocialRow("Friend_" + friend.userId, 116);
                        SocialName(row, friend.username + "#" + friend.identityTag);
                        StyledText(row, PresenceLabel(friend.presence), UITheme.FontCaption, PresenceColor(friend.presence), new Vector2(0.04f, 0.33f), new Vector2(0.65f, 0.49f));
                        StyledButton(row, "私信", UIComponents.ButtonKind.Secondary, new Vector2(.04f,.02f),new Vector2(.34f,.31f),
                            () => SocialWindow.Open(canvas.GetComponent<Canvas>(), friend.userId, friend.username+"#"+friend.identityTag));
                        var invite = StyledButton(row, "邀请", UIComponents.ButtonKind.Secondary, new Vector2(.37f,.02f),new Vector2(.67f,.31f),
                            () => _ = InviteSocialFriend(friend.userId));
                        invite.interactable = session.Room?.Status == "Waiting" && friend.presence != FriendPresence.Offline;
                        StyledButton(row, "删除", UIComponents.ButtonKind.Secondary, new Vector2(0.73f, 0.18f), new Vector2(0.98f, 0.70f),
                            () => _ = SocialAction(t => api.RemoveFriendAsync(friend.userId, t), "已删除好友"));
                    }
                }
            }
            else
            {
                var requests = socialTab == 1 ? incoming : cachedFriends.outgoing ?? Array.Empty<FriendRequestEntryDto>();
                if (requests.Length == 0) SocialHeading(socialTab == 1 ? "暂无收到的申请" : "暂无已发送的申请");
                foreach (var request in requests)
                {
                    var row = SocialRow("Request_" + request.requestId, 90);
                    SocialName(row, request.username + "#" + request.identityTag);
                    if (socialTab == 1)
                        StyledButton(row, "同意", UIComponents.ButtonKind.Primary, new Vector2(0.04f, 0.08f), new Vector2(0.46f, 0.42f),
                            () => _ = SocialAction(t => api.AcceptFriendRequestAsync(request.requestId, t), "已同意申请"));
                    StyledButton(row, socialTab == 1 ? "拒绝" : "撤销", UIComponents.ButtonKind.Secondary,
                        new Vector2(0.52f, 0.08f), new Vector2(0.96f, 0.42f),
                        () => _ = SocialAction(t => api.RemoveFriendRequestAsync(request.requestId, t), "申请已处理"));
                }
            }
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = position;
        }
        private async Task InviteSocialFriend(long friendId)
        {
            if (session.Room == null || !long.TryParse(session.Room.RoomId, out var roomId)) return;
            await SocialAction(t => api.InviteFriendAsync(roomId, friendId, t), "邀请已发送，有效期 5 分钟");
        }
        private Transform SocialRow(string name, float height)
        {
            var go = StyledPanel(name, socialRows, UITheme.CardSurface, Vector2.zero, Vector2.one);
            go.AddComponent<LayoutElement>().preferredHeight = height;
            return go.transform;
        }
        private void SocialHeading(string text)
        {
            var row = SocialRow("Section", 38);
            StyledText(row, text, UITheme.FontCaption, UITheme.TextMuted, new Vector2(0.04f, 0), new Vector2(0.96f, 1));
        }
        private void SocialName(Transform row, string name)
        {
            var label = StyledText(row, name, UITheme.FontBody, UITheme.TextPrimary, new Vector2(0.04f, 0.49f), new Vector2(0.71f, 0.96f));
            label.richText = false; label.overflowMode = TextOverflowModes.Ellipsis; label.textWrappingMode = TextWrappingModes.NoWrap;
        }

        private Task RefreshSocialAsync()
        {
            SocialSession.Instance?.RefreshSoon();
            OnSocialChanged();
            return Task.CompletedTask;
        }
        private async Task SocialAction<T>(Func<CancellationToken, Task<ApiResult<T>>> action, string success)
        {
            if (socialBusy || api == null || pageCts == null) return;
            var token = pageCts.Token;
            socialBusy = true;
            try
            {
                var result = await action(token);
                if (token.IsCancellationRequested) return;
                if (result.Code == "AUTH_UNAUTHORIZED") { Navigate(LobbyPage.SessionExpired); return; }
                if (socialFeedback != null) socialFeedback.text = result.Success ? success : ApiErrorMessages.ToUserMessage(result);
                if (result.Success) await RefreshSocialAsync();
            }
            catch (OperationCanceledException) { }
            finally { socialBusy = false; }
        }

        private void OpenAddFriendModal()
        {
            if (socialModal != null || session == null || !session.IsAuthenticated) return;
            CloseAccountPanel();
            modalCts = CancellationTokenSource.CreateLinkedTokenSource(pageCts.Token);
            socialModal = UIComponents.Panel("AddFriendModal", canvas.transform, new Color(0, 0, 0, 0.72f), Vector2.zero, Vector2.one, 0, false);
            socialModal.GetComponent<Image>().raycastTarget = true;
            var panel = StyledPanel("Dialog", socialModal.transform, UITheme.BackgroundPanel, new Vector2(0.32f, 0.32f), new Vector2(0.68f, 0.68f));
            panel.GetComponent<Image>().raycastTarget = true;
            StyledText(panel.transform, "添加好友", UITheme.FontCardTitle, UITheme.TextPrimary, new Vector2(0.07f, 0.75f), new Vector2(0.83f, 0.93f));
            StyledButton(panel.transform, "×", UIComponents.ButtonKind.Secondary, new Vector2(0.85f, 0.78f), new Vector2(0.96f, 0.94f), CloseSocialModal);
            friendsSearchInput = StyledInput("FriendSearchInput", panel.transform, "用户名", new Vector2(0.07f, 0.43f), new Vector2(0.63f, 0.64f));
            StyledText(panel.transform, "#", UITheme.FontCardTitle, UITheme.AccentPrimary,
                new Vector2(0.64f, 0.43f), new Vector2(0.70f, 0.64f), TextAlignmentOptions.Center).name = "FriendTagSeparator";
            friendsTagInput = StyledInput("FriendTagInput", panel.transform, "4位编码", new Vector2(0.71f, 0.43f), new Vector2(0.93f, 0.64f));
            friendsTagInput.characterValidation = TMP_InputField.CharacterValidation.Digit;
            friendsTagInput.characterLimit = 4;
            modalFeedback = StyledText(panel.transform, "分别输入用户名和4位编码，无需输入 #", UITheme.FontCaption, UITheme.TextMuted, new Vector2(0.07f, 0.27f), new Vector2(0.93f, 0.42f));
            var send = StyledButton(panel.transform, "发送好友申请", UIComponents.ButtonKind.Primary, new Vector2(0.48f, 0.07f), new Vector2(0.93f, 0.24f), () => { });
            send.onClick.AddListener(() => _ = SendFriendRequestAsync(send));
            friendsSearchInput.ActivateInputField();
        }
        private async Task SendFriendRequestAsync(Button button)
        {
            var username = friendsSearchInput.text.Trim();
            var tag = friendsTagInput.text.Trim();
            if (string.IsNullOrEmpty(username) || username.Contains("#") || username.Contains("＃"))
            { modalFeedback.text = "请输入用户名，无需输入 # 或编码"; return; }
            if (tag.Length != 4 || tag.Any(c => c < '0' || c > '9'))
            { modalFeedback.text = "请输入4位数字编码（保留开头的0）"; return; }
            var query = username + "#" + tag;
            if (!button.interactable || api == null) return;
            button.interactable = false;
            friendsSearchInput.interactable = friendsTagInput.interactable = false;
            modalFeedback.text = "正在发送好友申请…";
            var epoch = socialEpoch;
            var token = modalCts.Token;
            try
            {
                var result = await api.SendFriendRequestAsync(query, token);
                if (token.IsCancellationRequested || epoch != socialEpoch) return;
                modalFeedback.text = result.Success ? "好友申请已发送" : ApiErrorMessages.ToUserMessage(result);
                if (result.Success) { friendsSearchInput.text = friendsTagInput.text = ""; await RefreshSocialAsync(); }
                else if (result.Code == "AUTH_UNAUTHORIZED") Navigate(LobbyPage.SessionExpired);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (button != null && epoch == socialEpoch)
                {
                    button.interactable = true;
                    if (friendsSearchInput != null) friendsSearchInput.interactable = true;
                    if (friendsTagInput != null) friendsTagInput.interactable = true;
                }
            }
        }
        private void CloseSocialModal()
        {
            socialEpoch++;
            modalCts?.Cancel(); modalCts?.Dispose(); modalCts = null;
            if (socialModal != null) { socialModal.SetActive(false); if (Application.isPlaying) Destroy(socialModal); else DestroyImmediate(socialModal); }
            socialModal = null; friendsSearchInput = friendsTagInput = null; modalFeedback = null;
        }
        private void LateUpdate()
        {
            if (SocialWindow.Instance != null || SocialWindow.EscapeConsumedFrame == Time.frameCount) return;
            if (Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame) return;
            if (socialModal != null) CloseSocialModal();
            else if (accountPanel != null) CloseAccountPanel();
            else if (socialExpanded) ToggleSocial();
        }
        private static Color PresenceColor(string value) => value == FriendPresence.Offline ? UITheme.TextMuted : UITheme.AccentPrimary;
        private static string PresenceLabel(string value) => value switch
        {
            FriendPresence.InMatch => "对局中", FriendPresence.InRoom => "房间中", FriendPresence.Online => "在线", _ => "离线"
        };
    }
}
