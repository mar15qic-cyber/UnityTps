using System;
using Game.Account;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed partial class LobbyPresenter
    {
        private LobbyCharacterPreview lobbyCharacter;
        private TMP_Text primaryLabel, secondaryLabel, previewError, accountLabel, accountTagLabel, accountStatsLabel;
        private Image primaryIcon;
        private GameObject accountPanel;

        private void RenderTacticalLobby()
        {
            if (!session.IsAuthenticated) { Navigate(LobbyPage.Login); return; }
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("LobbyPage");
            var display = new GameObject("CharacterDisplay", typeof(RectTransform), typeof(RawImage), typeof(LobbyCharacterPreview));
            display.transform.SetParent(root, false);
            UIComponents.Place((RectTransform)display.transform, Vector2.zero, Vector2.one);
            lobbyCharacter = display.GetComponent<LobbyCharacterPreview>();
            display.GetComponent<RawImage>().color = Color.clear;
            var shade = UIComponents.Panel("EdgeShade", root, Color.white, Vector2.zero, Vector2.one, 0, false).GetComponent<Image>();
            shade.sprite = UISprites.EdgeShade(); shade.raycastTarget = false;
            StyledText(root, "作战大厅", UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.035f, 0.84f), new Vector2(0.32f, 0.94f), TextAlignmentOptions.Left, FontStyles.Bold);
            StyledText(root, "LOWPOLY OPS  /  作战准备", UITheme.FontCaption, UITheme.AccentPrimary,
                new Vector2(0.036f, 0.80f), new Vector2(0.34f, 0.84f), TextAlignmentOptions.Left);
            StyledText(root, "拖动角色以旋转视角", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.40f, 0.03f), new Vector2(0.63f, 0.07f), TextAlignmentOptions.Center);
            previewError = StyledText(root, "", UITheme.FontCaption, UITheme.AccentWarning,
                new Vector2(0.37f, 0.70f), new Vector2(0.67f, 0.76f), TextAlignmentOptions.Center);
            StyledText(root, "多人行动", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.035f, 0.26f), new Vector2(0.29f, 0.32f), TextAlignmentOptions.Left, FontStyles.Bold);
            StyledText(root, "团队竞技 · 击杀竞赛", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.035f, 0.21f), new Vector2(0.30f, 0.26f), TextAlignmentOptions.Left);
            StyledButton(root, "联机对战   →", UIComponents.ButtonKind.Primary,
                new Vector2(0.035f, 0.10f), new Vector2(0.28f, 0.19f), () => Navigate(LobbyPage.OnlineJoin));
            var quitButton = StyledButton(root, "退出游戏", UIComponents.ButtonKind.Secondary,
                new Vector2(0.035f, 0.025f), new Vector2(0.16f, 0.085f), QuitGame);
            quitButton.transform.parent.name = "QuitGameButton";

            var card = StyledPanel("LoadoutCard", root, new Color(0.10f, 0.15f, 0.16f, 0.94f),
                new Vector2(0.72f, 0.08f), new Vector2(0.94f, 0.37f));
            StyledText(card.transform, "当前配装", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.07f, 0.83f), new Vector2(0.93f, 0.96f), TextAlignmentOptions.Left);
            primaryIcon = CreateWeaponIcon(card.transform, null, new Vector2(0.07f, 0.37f), new Vector2(0.93f, 0.81f));
            primaryLabel = StyledText(card.transform, "", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.07f, 0.26f), new Vector2(0.93f, 0.40f), TextAlignmentOptions.Left, FontStyles.Bold);
            primaryLabel.name = "LoadoutPrimaryName";
            secondaryLabel = StyledText(card.transform, "", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.07f, 0.13f), new Vector2(0.93f, 0.26f), TextAlignmentOptions.Left);
            secondaryLabel.name = "LoadoutSecondaryName";
            var button = card.AddComponent<Button>();
            button.targetGraphic = card.GetComponent<Image>();
            card.GetComponent<Image>().raycastTarget = true;
            button.onClick.AddListener(() => Navigate(LobbyPage.Armory));
            StyledText(card.transform, "查看仓库  →", UITheme.FontCaption, UITheme.AccentPrimary,
                new Vector2(0.07f, 0.02f), new Vector2(0.93f, 0.13f));
            // Build all controls before starting optional 3D presentation.
            StyledButton(root, "检查地图更新", UIComponents.ButtonKind.Secondary,
                new Vector2(.75f, .01f), new Vector2(.96f, .065f), () => _ = CheckMapUpdatesAsync());
            if (Application.isPlaying) lobbyCharacter.Initialize();
            RefreshLobbyLoadout();
            var error = session.ConsumeGameplayError();
            if (!string.IsNullOrWhiteSpace(error)) status.text = error;
            if (session.Loadout == null && api != null && pageCts != null) _ = EnsureLoadoutCachedAsync(pageCts.Token);
        }

        private string WeaponName(string id)
            => !string.IsNullOrEmpty(id) && weaponAssets != null && weaponAssets.TryGet(id, out var row)
                ? row.definition != null ? row.definition.DisplayName : id : "未配置";

        private void RefreshLobbyLoadout()
        {
            var profile = session?.Profile;
            if (accountLabel != null) accountLabel.text = profile?.username ?? "账号";
            if (accountTagLabel != null) accountTagLabel.text = string.IsNullOrEmpty(profile?.identityTag) ? "" : "#" + profile.identityTag;
            if (accountStatsLabel != null) accountStatsLabel.text = $"Lv.{profile?.level ?? 1}  ·  {profile?.coins ?? 0:N0} 金币";
            if (currentPage != LobbyPage.Lobby || primaryLabel == null) return;
            var loadout = session?.Loadout;
            primaryLabel.text = WeaponName(loadout?.primaryWeaponId);
            secondaryLabel.text = "副武器  " + WeaponName(loadout?.secondaryWeaponId);
            ApplyWeaponIcon(primaryIcon, loadout?.primaryWeaponId);
            if (lobbyCharacter != null)
            {
                lobbyCharacter.ApplyLoadout(loadout, weaponAssets);
                if (previewError != null) previewError.text = lobbyCharacter.Error ?? "";
            }
        }

        private Image CreateWeaponIcon(Transform parent, string id, Vector2 min, Vector2 max)
        {
            var go = new GameObject("WeaponIcon", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            UIComponents.Place((RectTransform)go.transform, min, max);
            var image = go.GetComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            ApplyWeaponIcon(image, id);
            return image;
        }

        private void ApplyWeaponIcon(Image image, string id)
        {
            if (image == null) return;
            image.sprite = !string.IsNullOrEmpty(id) && weaponAssets != null && weaponAssets.TryGet(id, out var entry) && entry.icon != null
                ? entry.icon : WeaponIconFallback.Sprite;
            image.color = Color.white;
        }

        private void ToggleAccountPanel()
        {
            if (accountPanel != null) { CloseAccountPanel(); return; }
            accountPanel = StyledPanel("AccountPopover", canvas.transform, UITheme.BackgroundPanel,
                new Vector2(0.70f, 0.68f), new Vector2(0.955f, 0.92f));
            accountPanel.GetComponent<Image>().raycastTarget = true;
            var p = session.Profile;
            StyledText(accountPanel.transform, (p?.username ?? "") + "#" + p?.identityTag, UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.07f, 0.70f), new Vector2(0.93f, 0.91f));
            StyledText(accountPanel.transform, $"等级 {p?.level ?? 1}     XP {p?.xp ?? 0}/{p?.xpToNextLevel ?? 100}", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.07f, 0.48f), new Vector2(0.93f, 0.65f));
            var fill = UIComponents.ProgressBar(accountPanel.transform, new Vector2(0.07f, 0.37f), new Vector2(0.93f, 0.40f), UITheme.AccentPrimary);
            fill.fillAmount = Mathf.Clamp01((p?.xp ?? 0) / (float)Mathf.Max(1, p?.xpToNextLevel ?? 100));
            StyledButton(accountPanel.transform, "退出账号", UIComponents.ButtonKind.Secondary,
                new Vector2(0.07f, 0.08f), new Vector2(0.93f, 0.28f), Logout);
        }
        private void CloseAccountPanel()
        {
            if (accountPanel != null) { accountPanel.SetActive(false); if (Application.isPlaying) Destroy(accountPanel); else DestroyImmediate(accountPanel); }
            accountPanel = null;
        }

        private static void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }

    internal static class WeaponIconFallback
    {
        private static Sprite sprite;
        internal static Sprite Sprite
        {
            get
            {
                if (sprite != null) return sprite;
                var texture = new Texture2D(128, 48, TextureFormat.RGBA32, false);
                var pixels = new Color[128 * 48];
                for (int y = 0; y < 48; y++) for (int x = 0; x < 128; x++)
                    if ((x > 25 && x < 94 && y > 23 && y < 34) || (x >= 94 && x < 121 && y > 27 && y < 31)
                        || (x > 7 && x <= 25 && y > 18 && y < 32) || (x > 44 && x < 53 && y > 10 && y <= 23))
                        pixels[y * 128 + x] = UITheme.TextMuted;
                texture.SetPixels(pixels); texture.Apply();
                sprite = UnityEngine.Sprite.Create(texture, new Rect(0, 0, 128, 48), new Vector2(0.5f, 0.5f));
                return sprite;
            }
        }
    }
}
