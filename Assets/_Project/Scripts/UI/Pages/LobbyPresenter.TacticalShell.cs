using System;
using System.Linq;
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
        /// <summary>CF 三背包（Phase D）：大厅装配卡的背包预览页签 + 投掷/配件摘要行。</summary>
        private int lobbyBackpackPreview;
        private readonly Image[] lobbyThrowableIcons = new Image[3];
        private readonly TMP_Text[] lobbyThrowableNames = new TMP_Text[3];
        private TMP_Text throwableLabel, attachmentCountLabel;
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
                new Vector2(0.70f, 0.08f), new Vector2(0.96f, 0.51f));
            StyledText(card.transform, "背包装配", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.07f, 0.915f), new Vector2(0.55f, 0.995f), TextAlignmentOptions.Left);
            // 背包预览页签（只读切换预览；3D 角色始终展示出战背包=活动背包）
            for (int i = 0; i < 3; i++)
            {
                var captured = i;
                var pill = BackpackTab("LobbyBackpack_" + (i + 1), card.transform, "背包 " + (i + 1),
                    new Vector2(0.06f + i * 0.30f, 0.79f), new Vector2(0.34f + i * 0.30f, 0.90f));
                pill.name = "LobbyBackpackTab_" + (i + 1);
                UIComponents.SetNavPillSelected(pill, i == lobbyBackpackPreview);
                pill.onClick.AddListener(() =>
                {
                    if (lobbyBackpackPreview == captured) return;
                    lobbyBackpackPreview = captured;
                    RefreshLobbyLoadout();
                    for (int j = 1; j <= 3; j++)
                        UIComponents.SetNavPillSelected(
                            card.transform.Find("LobbyBackpackTab_" + j)?.GetComponent<Button>(), j - 1 == captured);
                });
            }
            primaryIcon = CreateWeaponIcon(card.transform, null, new Vector2(0.07f, 0.515f), new Vector2(0.93f, 0.765f));
            primaryLabel = StyledText(card.transform, "", UITheme.FontBody, UITheme.TextPrimary,
                new Vector2(0.07f, 0.43f), new Vector2(0.93f, 0.515f), TextAlignmentOptions.Left, FontStyles.Bold);
            primaryLabel.name = "LoadoutPrimaryName";
            secondaryLabel = StyledText(card.transform, "", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.07f, 0.36f), new Vector2(0.93f, 0.425f), TextAlignmentOptions.Left);
            secondaryLabel.name = "LoadoutSecondaryName";
            for (int i=0;i<3;i++)
            {
                var slot=StyledPanel("LobbyThrowableSlot"+i,card.transform,UITheme.CardSurface,new Vector2(.06f+i*.30f,.105f),new Vector2(.34f+i*.30f,.345f));
                lobbyThrowableIcons[i]=CreateWeaponIcon(slot.transform,null,new Vector2(.08f,.35f),new Vector2(.92f,.95f));
                var label=StyledText(slot.transform,"",UITheme.FontCaption,UITheme.TextMuted,new Vector2(.03f,.015f),new Vector2(.97f,.35f),TextAlignmentOptions.Center);
                label.textWrappingMode=TextWrappingModes.NoWrap;
                label.enableAutoSizing=true;label.fontSizeMin=12;label.fontSizeMax=UITheme.FontCaption;
                lobbyThrowableNames[i]=label;
            }
            attachmentCountLabel = StyledText(card.transform, "", UITheme.FontCaption, UITheme.AccentPrimary,
                new Vector2(0.07f, 0.02f), new Vector2(0.93f, 0.08f), TextAlignmentOptions.Left);
            attachmentCountLabel.name = "LoadoutAttachmentSummary";
            var button = card.AddComponent<Button>();
            button.targetGraphic = card.GetComponent<Image>();
            card.GetComponent<Image>().raycastTarget = true;
            button.onClick.AddListener(() => { armoryBackpack = lobbyBackpackPreview; Navigate(LobbyPage.Armory); });
            StyledText(card.transform, "查看仓库  →", UITheme.FontCaption, UITheme.AccentPrimary,
                new Vector2(0.55f, 0.915f), new Vector2(0.94f, 0.995f), TextAlignmentOptions.Right);
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
            // 背包装配详情（Phase D）：页签预览对应背包；3D 角色仍展示出战（活动）背包
            var loadout = session?.LoadoutForBackpack(lobbyBackpackPreview);
            primaryLabel.text = "主  " + WeaponName(loadout?.primaryWeaponId);
            secondaryLabel.text = "副  " + WeaponName(loadout?.secondaryWeaponId);
            var throwableIds=loadout?.throwableIds ?? Game.Gameplay.Combat.ThrowableSlots.Legacy(loadout?.throwableId);
            for(int i=0;i<3;i++)
            {
                var id=throwableIds.Length>i?throwableIds[i]:null;
                ApplyWeaponIcon(lobbyThrowableIcons[i],id);
                if(lobbyThrowableNames[i]!=null) lobbyThrowableNames[i].text=Game.Gameplay.Combat.ThrowableSlots.DisplayName(id);
            }
            if (attachmentCountLabel != null)
            {
                int count = loadout?.attachments?.Length ?? 0;
                attachmentCountLabel.text = count > 0 ? $"已装配件 {count} 项" : "未改装配件";
            }
            ApplyWeaponIcon(primaryIcon, loadout?.primaryWeaponId);
            if (lobbyCharacter != null)
            {
                lobbyCharacter.ApplyLoadout(session?.Loadout, weaponAssets);
                if (previewError != null) previewError.text = lobbyCharacter.Error ?? "";
            }
        }

        /// <summary>投掷物显示名（目录缓存优先——WeaponName 只解析武器条目）。</summary>
        private string ThrowableLabelOrId(Game.Account.LoadoutDto loadout)
        {
            var item = cachedCatalog?.items?.FirstOrDefault(x => x.itemId == loadout?.throwableId);
            return item != null ? item.displayName : loadout.throwableId;
        }

        private Button BackpackTab(string name,Transform parent,string label,Vector2 min,Vector2 max)
        {
            var panel=StyledPanel(name,parent,UITheme.CardSurface,min,max);
            var graphic=panel.GetComponent<Image>();graphic.raycastTarget=true;
            var button=panel.AddComponent<Button>();button.targetGraphic=graphic;
            var text=StyledText(panel.transform,label,UITheme.FontBody,UITheme.TextPrimary,new Vector2(.02f,0),new Vector2(.98f,1),TextAlignmentOptions.Center);
            text.enableAutoSizing=true;text.fontSizeMin=14;text.fontSizeMax=18;text.overflowMode=TextOverflowModes.Overflow;
            return button;
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
            if(string.IsNullOrEmpty(id)){image.sprite=null;image.enabled=false;return;}
            if (id != null && id.StartsWith("throwable.")) { image.sprite=Resources.Load<Sprite>("UI/WeaponIcons/"+id); image.enabled=image.sprite!=null; return; }
            image.enabled=true;
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
