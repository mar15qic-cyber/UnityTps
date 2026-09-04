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
            var card = StyledPanel("ResultsPanel", root, UITheme.CardSurface, new Vector2(0.22f, 0.14f), new Vector2(0.78f, 0.88f));
            CreateLogo(card.transform, new Vector2(0.44f, 0.80f), new Vector2(0.56f, 0.96f));

            // Docs/23 P2（G5）：实数据结算页——数据源 = MatchSettlementFlow（服务器权威值）。
            // 无对局数据（直接打开/演示路径）时诚实降级展示。
            var result = MatchSettlementFlow.LastResult;
            var request = MatchSettlementFlow.LastRequest;
            var pending = MatchSettlementFlow.LastPendingRequest;

            if (result == null && request == null)
            {
                StyledText(card.transform, "暂无对局结算数据", UITheme.FontPageTitle, UITheme.TextPrimary,
                    new Vector2(0.1f, 0.52f), new Vector2(0.9f, 0.68f), TextAlignmentOptions.Center, FontStyles.Bold);
                StyledText(card.transform, "完成一局联网对战后，这里将展示三币、通行证与成就结算。",
                    UITheme.FontBody, UITheme.TextMuted, new Vector2(0.1f, 0.38f), new Vector2(0.9f, 0.50f));
                StyledButton(card.transform, "返回大厅", UIComponents.ButtonKind.Primary,
                    new Vector2(0.24f, 0.06f), new Vector2(0.76f, 0.17f), () => _ = ReturnToLobbyFromResultsAsync());
                PlayEnter(card);
                return;
            }

            // 胜负标题
            StyledText(card.transform, MatchSettlementFlow.LastVerdictText ?? "对局结束", UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.1f, 0.66f), new Vector2(0.9f, 0.80f), TextAlignmentOptions.Center, FontStyles.Bold);

            // K/D（服务器权威收集值）
            string kd = request != null ? $"K {request.kills}  /  D {request.deaths}   ·   {request.durationSeconds}s" : "K/D 数据缺失";
            StyledText(card.transform, kd, UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.1f, 0.56f), new Vector2(0.9f, 0.65f), TextAlignmentOptions.Center, FontStyles.Bold);

            if (result != null)
            {
                // 三币 + 通行证进度
                string replayTag = result.replayed ? "幂等重放 · " : string.Empty;
                UIComponents.Badge("Currencies", card.transform,
                    $"{replayTag}+{result.xpEarned} XP · +{result.coinsEarned} COINS · +{result.passXpEarned} PASS XP",
                    UITheme.AccentPrimary, new Vector2(0.14f, 0.44f), new Vector2(0.86f, 0.55f));
                StyledText(card.transform, $"通行证 Lv.{result.passLevel}   {result.passXp} / {result.passXpToNextLevel} XP",
                    UITheme.FontBody, UITheme.TextPrimary,
                    new Vector2(0.1f, 0.375f), new Vector2(0.9f, 0.44f), TextAlignmentOptions.Center);

                // 通行证升级 / 新配件 / 新成就（各取前 3 条，诚实省略超出部分）
                var lines = new System.Collections.Generic.List<string>();
                if (result.passLevelUps != null)
                    foreach (var up in result.passLevelUps)
                    {
                        if (lines.Count >= 3) break;
                        lines.Add($"通行证升级 Lv.{up.level} · {(string.IsNullOrEmpty(up.itemId) ? (up.coinsAmount + " 金币") : up.itemId)}");
                    }
                if (result.newAttachments != null)
                    foreach (var item in result.newAttachments)
                    {
                        if (lines.Count >= 3) break;
                        lines.Add($"新配件获得：{item}");
                    }
                if (result.unlockedAchievements != null)
                    foreach (var ach in result.unlockedAchievements)
                    {
                        if (lines.Count >= 3) break;
                        lines.Add($"成就解锁：{ach.displayName}");
                    }
                if (lines.Count > 0)
                    StyledText(card.transform, string.Join("\n", lines), UITheme.FontBody, UITheme.TextMuted,
                        new Vector2(0.1f, 0.20f), new Vector2(0.9f, 0.37f), TextAlignmentOptions.Center);
            }
            else if (!string.IsNullOrEmpty(MatchSettlementFlow.LastError))
            {
                StyledText(card.transform, "结算提交失败：" + MatchSettlementFlow.LastError, UITheme.FontBody, UITheme.AccentDanger,
                    new Vector2(0.1f, 0.20f), new Vector2(0.9f, 0.43f), TextAlignmentOptions.Center);
            }

            // 提交失败暂存 → 重试按钮（重试成功后自动刷新为实数据）
            if (pending != null)
                StyledButton(card.transform, "重试结算", UIComponents.ButtonKind.Danger,
                    new Vector2(0.36f, 0.06f), new Vector2(0.64f, 0.16f),
                    () => _ = RetrySettlementAndRerenderAsync());

            StyledButton(card.transform, "返回大厅", UIComponents.ButtonKind.Primary,
                new Vector2(0.24f, 0.17f), new Vector2(0.76f, 0.28f), () => _ = ReturnToLobbyFromResultsAsync());
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
            Navigate(LobbyPage.Lobby);
        }

        private async System.Threading.Tasks.Task RetrySettlementAndRerenderAsync()
        {
            await MatchSettlementFlow.RetryPendingAsync();
            Navigate(LobbyPage.Results);
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
            api.ClearToken(); session.Clear(); SetNavigationVisible(false);
            SetBackground(UIArt.KeyBackgroundLogin);
            var card = StyledPanel("SessionExpired", body, UITheme.CardSurface, new Vector2(0.26f, 0.26f), new Vector2(0.74f, 0.74f));
            UIComponents.Badge("State", card.transform, "AUTH", UITheme.AccentWarning,
                new Vector2(0.42f, 0.78f), new Vector2(0.58f, 0.88f));
            StyledText(card.transform, "会话已过期", UITheme.FontCardTitle + 2, UITheme.TextPrimary,
                new Vector2(0.1f, 0.56f), new Vector2(0.9f, 0.74f), TextAlignmentOptions.Center, FontStyles.Bold);
            StyledText(card.transform, "请重新登录。持久化数据仍由服务器保存。", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.1f, 0.36f), new Vector2(0.9f, 0.54f));
            StyledButton(card.transform, "返回登录", UIComponents.ButtonKind.Primary,
                new Vector2(0.26f, 0.12f), new Vector2(0.74f, 0.26f), () => Navigate(LobbyPage.Login));
            PlayEnter(card);
        }
    }
}
