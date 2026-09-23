using System;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// Docs/20 Step 6: restyled demo HUD / Pause / Results / Error / SessionExpired overlays.
    /// The real combat crosshair (Presentation/HUD) is intentionally untouched (Day4 accepted).
    /// </summary>
    public sealed partial class LobbyPresenter
    {
        private void RenderHud()
        {
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("HudPage");

            // HP + ammo cards bottom-left
            var hpCard = StyledPanel("HpCard", root, UITheme.CardSurface, new Vector2(0.03f, 0.05f), new Vector2(0.22f, 0.14f));
            StyledText(hpCard.transform, "HP  100", UITheme.FontCardTitle, UITheme.AccentSecondary,
                new Vector2(0.10f, 0.15f), new Vector2(0.90f, 0.85f), TextAlignmentOptions.Left, FontStyles.Bold);
            var ammoCard = StyledPanel("AmmoCard", root, UITheme.CardSurface, new Vector2(0.03f, 0.16f), new Vector2(0.22f, 0.25f));
            StyledText(ammoCard.transform, "30 / 90", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.10f, 0.15f), new Vector2(0.90f, 0.85f), TextAlignmentOptions.Left, FontStyles.Bold);

            // Center crosshair (procedural four-bar + dot)
            var cross = new GameObject("Crosshair", typeof(RectTransform));
            cross.transform.SetParent(root, false);
            UIComponents.Place(cross.GetComponent<RectTransform>(), new Vector2(0.485f, 0.47f), new Vector2(0.515f, 0.53f));
            void Bar(string n, Vector2 min, Vector2 max)
            {
                var b = new GameObject(n, typeof(RectTransform), typeof(Image));
                b.transform.SetParent(cross.transform, false);
                var img = b.GetComponent<Image>();
                img.color = UITheme.AccentPrimary;
                img.raycastTarget = false;
                UIComponents.Place(b.GetComponent<RectTransform>(), min, max);
            }
            Bar("Up", new Vector2(0.47f, 0.62f), new Vector2(0.53f, 0.94f));
            Bar("Down", new Vector2(0.47f, 0.06f), new Vector2(0.53f, 0.38f));
            Bar("Left", new Vector2(0.06f, 0.44f), new Vector2(0.40f, 0.56f));
            Bar("Right", new Vector2(0.60f, 0.44f), new Vector2(0.94f, 0.56f));
            var dot = new GameObject("Dot", typeof(RectTransform), typeof(Image));
            dot.transform.SetParent(cross.transform, false);
            var dotImg = dot.GetComponent<Image>();
            dotImg.sprite = UISprites.Circle();
            dotImg.color = UITheme.AccentPrimary;
            dotImg.raycastTarget = false;
            UIComponents.Place(dot.GetComponent<RectTransform>(), new Vector2(0.44f, 0.44f), new Vector2(0.56f, 0.56f));

            StyledButton(root, "暂停", UIComponents.ButtonKind.Secondary,
                new Vector2(0.88f, 0.90f), new Vector2(0.97f, 0.96f), () => Navigate(LobbyPage.Pause));
        }

        private void RenderPause()
        {
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("PausePage");
            // Dim full-screen veil behind the menu card.
            var veil = UIComponents.Panel("Veil", root, new Color(0.02f, 0.04f, 0.07f, 0.72f), Vector2.zero, Vector2.one, 0f, false);
            veil.GetComponent<Image>().raycastTarget = false;

            var card = StyledPanel("PausePanel", root, UITheme.CardSurface, new Vector2(0.28f, 0.14f), new Vector2(0.72f, 0.86f));
            StyledText(card.transform, "暂停", UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.1f, 0.76f), new Vector2(0.9f, 0.92f), TextAlignmentOptions.Center, FontStyles.Bold);
            StyledButton(card.transform, "继续", UIComponents.ButtonKind.Primary,
                new Vector2(0.18f, 0.52f), new Vector2(0.82f, 0.66f), () => Navigate(LobbyPage.Hud));
            StyledButton(card.transform, "设置", UIComponents.ButtonKind.Info,
                new Vector2(0.18f, 0.33f), new Vector2(0.82f, 0.47f), () => Navigate(LobbyPage.Settings));
            StyledButton(card.transform, "返回大厅", UIComponents.ButtonKind.Danger,
                new Vector2(0.18f, 0.14f), new Vector2(0.82f, 0.28f), () => SceneManager.LoadScene("Lobby"));
            PlayEnter(card);
        }

        private void RenderResults()
        {
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("ResultsPage");
            var card = StyledPanel("ResultsPanel", root, UITheme.BackgroundPanel, new Vector2(0.18f, 0.12f), new Vector2(0.82f, 0.88f));
            var result = MatchSettlementFlow.LastRoomResult;
            StyledText(card.transform, result == null ? "暂无对局结算数据" : MatchSettlementFlow.BuildRoomResultVerdict(result, session.Room?.Mode),
                UITheme.FontPageTitle, UITheme.TextPrimary, new Vector2(0.07f, 0.81f), new Vector2(0.93f, 0.95f), TextAlignmentOptions.Left, FontStyles.Bold);
            StyledText(card.transform, result == null ? "完成联网对战后可查看战绩。" : $"对局时长 {result.durationSeconds / 60:00}:{result.durationSeconds % 60:00}  ·  {(result.status == "Final" ? "结算完成" : "结算中…")}",
                UITheme.FontBody, UITheme.TextMuted, new Vector2(0.07f, 0.71f), new Vector2(0.93f, 0.81f), TextAlignmentOptions.Left);
            var rows = TacticalScroll(card.transform, "ResultsList", new Vector2(0.07f, 0.22f), new Vector2(0.93f, 0.69f));
            foreach (var player in result?.players ?? System.Array.Empty<RoomMatchResultPlayerViewDto>())
            {
                var row = StyledPanel("Result_" + player.userId, rows, UITheme.CardSurface, Vector2.zero, Vector2.one);
                row.AddComponent<LayoutElement>().preferredHeight = 56;
                StyledText(row.transform, player.username, UITheme.FontBody, UITheme.TextPrimary, new Vector2(0.04f, 0), new Vector2(0.50f, 1), TextAlignmentOptions.Left);
                StyledText(row.transform, $"{player.kills} / {player.deaths} / {player.assists}", UITheme.FontBody, UITheme.TextMuted, new Vector2(0.52f, 0), new Vector2(0.78f, 1));
                StyledText(row.transform, player.isWin ? "胜利" : "", UITheme.FontBody, UITheme.AccentPrimary, new Vector2(0.81f, 0), new Vector2(0.97f, 1));
            }
            StyledButton(card.transform, session.Room != null ? "返回房间" : "返回大厅", UIComponents.ButtonKind.Primary,
                new Vector2(0.63f, 0.07f), new Vector2(0.93f, 0.17f), () => _ = ReturnToLobbyFromResultsAsync());
            PlayEnter(card);
        }

        /// <summary>Results 返回大厅：刷新档案/钱包（照抄 PurchaseAsync 刷新惯例）后 Navigate。
        /// 说明：结算流回大厅后本页已在 Lobby 场景，无需再 LoadScene("Lobby")（与票据原文的偏差，见执行报告）。</summary>
        private async System.Threading.Tasks.Task ReturnToLobbyFromResultsAsync()
        {
            var profile = await api.GetProfileAsync();
            if (profile.Success && profile.Data != null)
            {
                session.ApplyProfile(profile.Data);
                status.text = "档案已刷新";
            }
            Navigate(session.Room != null ? LobbyPage.WaitingRoom : LobbyPage.Lobby);
        }

        private void RenderError(string message, Action retry)
        {
            ClearBody();
            retryAction = retry;
            SetBackground(UIArt.KeyBackgroundLogin);
            var card = StyledPanel("ErrorPanel", body, UITheme.CardSurface, new Vector2(0.24f, 0.22f), new Vector2(0.76f, 0.78f));
            UIComponents.Badge("State", card.transform, "ERROR", UITheme.AccentDanger,
                new Vector2(0.42f, 0.82f), new Vector2(0.58f, 0.92f));
            StyledText(card.transform, "连接或服务错误", UITheme.FontCardTitle + 2, UITheme.TextPrimary,
                new Vector2(0.1f, 0.62f), new Vector2(0.9f, 0.78f), TextAlignmentOptions.Center, FontStyles.Bold);
            StyledText(card.transform, message, UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.1f, 0.38f), new Vector2(0.9f, 0.60f));
            if (retry != null)
                StyledButton(card.transform, "重试", UIComponents.ButtonKind.Primary,
                    new Vector2(0.16f, 0.14f), new Vector2(0.48f, 0.27f), () => retry());
            StyledButton(card.transform, apiAvailable ? "取消 / 登录页" : "返回连接检查", UIComponents.ButtonKind.Info,
                new Vector2(0.52f, 0.14f), new Vector2(0.84f, 0.27f), () => Navigate(apiAvailable ? LobbyPage.Login : LobbyPage.Boot));
            PlayEnter(card);
        }

        private void RenderSessionExpired()
        {
            AppRoot.Instance?.AcknowledgeSessionExpired();
            Game.Account.RememberedSession.Clear();
            api.ClearToken(); session.Clear(); SetNavigationVisible(false);
            SetBackground(UIArt.KeyBackgroundLogin);
            var card = StyledPanel("SessionExpired", body, UITheme.CardSurface, new Vector2(0.26f, 0.26f), new Vector2(0.74f, 0.74f));
            UIComponents.Badge("State", card.transform, "AUTH", UITheme.AccentWarning,
                new Vector2(0.42f, 0.78f), new Vector2(0.58f, 0.88f));
            StyledText(card.transform, "登录状态已失效", UITheme.FontCardTitle + 2, UITheme.TextPrimary,
                new Vector2(0.1f, 0.56f), new Vector2(0.9f, 0.74f), TextAlignmentOptions.Center, FontStyles.Bold);
            StyledText(card.transform, "账号已在其他客户端登录，或登录凭证已过期。当前连接已退出，请重新登录。", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.1f, 0.36f), new Vector2(0.9f, 0.54f));
            StyledButton(card.transform, "返回登录", UIComponents.ButtonKind.Primary,
                new Vector2(0.26f, 0.12f), new Vector2(0.74f, 0.26f), () => Navigate(LobbyPage.Login));
            PlayEnter(card);
        }
    }
}
