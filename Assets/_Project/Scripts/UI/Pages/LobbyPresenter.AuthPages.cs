using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>Docs/20 restyle: Boot splash, Login/Register card and Identity welcome page.</summary>
    public sealed partial class LobbyPresenter
    {
        private void RenderBoot(CancellationToken token) => _ = BootAsync(token);

        private async Task BootAsync(CancellationToken token)
        {
            apiAvailable = false;
            SetNavigationInteractable(false);
            SetBackground(UIArt.KeyBackgroundLogin);

            var card = StyledPanel("BootPanel", body, UITheme.CardSurface, new Vector2(0.30f, 0.18f), new Vector2(0.70f, 0.82f));
            var logo = CreateLogo(card.transform, new Vector2(0.38f, 0.56f), new Vector2(0.62f, 0.88f));
            StyledText(card.transform, "UNITY FPS", UITheme.FontHero, UITheme.TextPrimary,
                new Vector2(0.1f, 0.44f), new Vector2(0.9f, 0.56f), TextAlignmentOptions.Center, FontStyles.Bold);
            StyledText(card.transform, "正在连接作战服务…", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.1f, 0.35f), new Vector2(0.9f, 0.44f));
            var fill = UIComponents.ProgressBar(card.transform, new Vector2(0.18f, 0.24f), new Vector2(0.82f, 0.31f), UITheme.AccentPrimary);
            PlayEnter(card);
            UIMotion.Tween(1.2f, UIMotion.Ease.OutQuad, t => { if (fill != null) fill.fillAmount = t * 0.7f; });

            var result = await api.GetHealthAsync(token);
            if (token.IsCancellationRequested || currentPage != LobbyPage.Boot) return;
            if (result.Success)
            {
                if (fill != null) fill.fillAmount = 1f;
                apiAvailable = true;
                SetNavigationVisible(session.IsAuthenticated);
                status.text = "服务在线 · " + (result.Data?.database ?? "database");
                Navigate(session.IsAuthenticated ? LobbyPage.Lobby : LobbyPage.Login);
            }
            else
            {
                apiAvailable = false;
                SetNavigationInteractable(false);
                RenderError(ApiErrorMessages.ToUserMessage(result), () => Navigate(LobbyPage.Boot));
            }
        }

        private void RenderLoginPage() => RenderAuthPage(false);

        private void RenderRegisterPage() => RenderAuthPage(true);

        private void RenderAuthPage(bool register)
        {
            SetBackground(UIArt.KeyBackgroundLogin);

            var card = StyledPanel("AuthCard", body, UITheme.CardSurface, new Vector2(0.30f, 0.04f), new Vector2(0.70f, 0.96f));
            var cardRect = card.GetComponent<RectTransform>();
            CreateLogo(card.transform, new Vector2(0.38f, 0.80f), new Vector2(0.62f, 0.97f));
            StyledText(card.transform, register ? "建立身份" : "登录作战网络", UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.08f, 0.68f), new Vector2(0.92f, 0.79f), TextAlignmentOptions.Center, FontStyles.Bold);
            StyledText(card.transform, register ? "注册后将获得 M4、AK 与 Service Pistol" : "使用服务器账号继续", UITheme.FontCaption + 2, UITheme.TextMuted,
                new Vector2(0.08f, 0.61f), new Vector2(0.92f, 0.68f));

            var username = StyledInput("Username", card.transform, "用户名", new Vector2(0.10f, 0.47f), new Vector2(0.90f, 0.57f));
            var password = StyledInput("Password", card.transform, "密码（至少 8 位）", new Vector2(0.10f, 0.35f), new Vector2(0.90f, 0.45f));
            password.contentType = TMP_InputField.ContentType.Password;

            Button submit = null;
            submit = StyledButton(card.transform, register ? "注册并继续" : "登录", UIComponents.ButtonKind.Primary,
                new Vector2(0.10f, 0.20f), new Vector2(0.90f, 0.31f),
                () => _ = SubmitAuthAsync(register, username, password, submit, cardRect));
            StyledButton(card.transform, register ? "返回登录" : "创建账号", UIComponents.ButtonKind.Secondary,
                new Vector2(0.10f, 0.08f), new Vector2(0.52f, 0.16f),
                () => Navigate(register ? LobbyPage.Login : LobbyPage.Register));
            StyledText(card.transform, "请求期间可安全取消", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.55f, 0.08f), new Vector2(0.90f, 0.16f), TextAlignmentOptions.Right);

            PlayEnter(card);
        }

        private void RenderIdentity()
        {
            SetBackground(UIArt.KeyBackgroundLogin);
            var name = session.Profile?.username ?? "Operator";
            var card = StyledPanel("IdentityPanel", body, UITheme.CardSurface, new Vector2(0.26f, 0.14f), new Vector2(0.74f, 0.86f));
            CreateLogo(card.transform, new Vector2(0.42f, 0.66f), new Vector2(0.58f, 0.94f));
            StyledText(card.transform, "身份确认", UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.1f, 0.52f), new Vector2(0.9f, 0.66f), TextAlignmentOptions.Center, FontStyles.Bold);
            StyledText(card.transform, "欢迎，" + name + "。服务器已创建你的账户、钱包和初始库存。", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.10f, 0.40f), new Vector2(0.90f, 0.52f));

            StyledText(card.transform, "初始解锁", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.10f, 0.32f), new Vector2(0.90f, 0.38f));
            var unlocks = new[] { "M4", "AK", "Service Pistol" };
            for (var i = 0; i < unlocks.Length; i++)
            {
                var x0 = 0.20f + i * 0.21f;
                UIComponents.Badge("Unlock_" + unlocks[i], card.transform, unlocks[i], UITheme.AccentSecondary,
                    new Vector2(x0, 0.22f), new Vector2(x0 + 0.19f, 0.31f));
            }

            StyledButton(card.transform, "进入大厅", UIComponents.ButtonKind.Primary,
                new Vector2(0.25f, 0.06f), new Vector2(0.75f, 0.17f), EnterAuthenticatedLobby);
            PlayEnter(card);
        }

        private Image CreateLogo(Transform parent, Vector2 anchorMin, Vector2 anchorMax)
        {
            var go = new GameObject("Logo", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.sprite = UIArt.Get(UIArt.KeyLogo);
            image.preserveAspect = true;
            image.raycastTarget = false;
            UIComponents.Place(go.GetComponent<RectTransform>(), anchorMin, anchorMax);
            return image;
        }
    }
}
