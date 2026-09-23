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
        private string catalogSelection;
        private void RenderTacticalCatalog(bool shop)
        {
            ClearBody();
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot(shop ? "ShopPage" : "ArmoryPage");
            StyledText(root, shop ? "军备商城" : "武器仓库", UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.03f, 0.90f), new Vector2(0.55f, 0.99f), TextAlignmentOptions.Left, FontStyles.Bold);
            var filter = shop ? catalogFilter : armoryFilter;
            float y = 0.83f;
            foreach (var (key, label) in ArmoryTabs)
            {
                var captured = key;
                var tab = UIComponents.NavPill("Category_" + key, root, label, new Vector2(0.025f, y - 0.057f), new Vector2(0.15f, y));
                UIComponents.SetNavPillSelected(tab, key == filter);
                tab.onClick.AddListener(() => { if (shop) catalogFilter = captured; else armoryFilter = captured; catalogSelection = null; RenderTacticalCatalog(shop); });
                y -= 0.076f;
            }
            var items = (cachedCatalog?.items ?? Array.Empty<CatalogItemDto>()).Where(IsLpfpWeaponItem)
                .Where(x => shop ? x.acquisitionSource == "Shop" : x.isOwned)
                .Where(x => filter == "All" || GetCategory(x.itemId) == filter).ToArray();
            var content = TacticalScroll(root, "WeaponGrid", new Vector2(0.17f, 0.05f), new Vector2(0.70f, 0.87f));
            if (items.Length == 0)
            {
                StyledText(root, "该分类下暂无武器", UITheme.FontBody, UITheme.TextMuted, new Vector2(0.20f, 0.43f), new Vector2(0.66f, 0.57f));
                return;
            }
            var selected = items.FirstOrDefault(x => x.itemId == catalogSelection) ?? items[0];
            catalogSelection = selected.itemId;
            for (int i = 0; i < items.Length; i += 2)
            {
                var row = new GameObject("WeaponRow", typeof(RectTransform), typeof(LayoutElement));
                row.transform.SetParent(content, false);
                row.GetComponent<LayoutElement>().preferredHeight = 218;
                for (int col = 0; col < 2 && i + col < items.Length; col++)
                {
                    var item = items[i + col];
                    var card = StyledPanel("WeaponCard_" + item.itemId, row.transform,
                        item.itemId == selected.itemId ? UITheme.CardSurfaceAlt : UITheme.CardSurface,
                        new Vector2(col * 0.51f, 0), new Vector2(col * 0.51f + 0.49f, 1));
                    CreateWeaponIcon(card.transform, item.itemId, new Vector2(0.07f, 0.24f), new Vector2(0.93f, 0.77f));
                    StyledText(card.transform, item.displayName, UITheme.FontBody, UITheme.TextPrimary,
                        new Vector2(0.06f, 0.78f), new Vector2(0.94f, 0.95f), TextAlignmentOptions.Left, FontStyles.Bold).name = "CardName";
                    var equipped = session.Loadout != null && (session.Loadout.primaryWeaponId == item.itemId || session.Loadout.secondaryWeaponId == item.itemId);
                    StyledText(card.transform, equipped ? "已装备" : item.isOwned ? "已拥有" : $"{item.priceCoins:N0} 金币", UITheme.FontCaption,
                        equipped ? UITheme.AccentPrimary : UITheme.TextMuted, new Vector2(0.06f, 0.05f), new Vector2(0.94f, 0.23f));
                    card.GetComponent<Image>().raycastTarget = true;
                    var button = card.AddComponent<Button>(); button.targetGraphic = card.GetComponent<Image>();
                    button.onClick.AddListener(() => { catalogSelection = item.itemId; RenderTacticalCatalog(shop); });
                }
            }
            var detail = StyledPanel("SelectionDetails", root, UITheme.BackgroundPanel, new Vector2(0.73f, 0.05f), new Vector2(0.98f, 0.87f));
            StyledText(detail.transform, CategoryLabel(GetCategory(selected.itemId)), UITheme.FontCaption, UITheme.AccentPrimary,
                new Vector2(0.07f, 0.90f), new Vector2(0.93f, 0.96f));
            StyledText(detail.transform, selected.displayName, UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.07f, 0.80f), new Vector2(0.93f, 0.90f), TextAlignmentOptions.Left, FontStyles.Bold);
            CreateWeaponIcon(detail.transform, selected.itemId, new Vector2(0.07f, 0.56f), new Vector2(0.93f, 0.79f));
            var stats = weaponAssets.FindStats(selected.itemId);
            StyledText(detail.transform, $"伤害   {stats.damage:0}\n射速   {stats.roundsPerMinute:0} RPM\n弹容量   {stats.magazineSize:0}\n后坐力   {stats.recoil:0.##}",
                UITheme.FontBody, UITheme.TextMuted, new Vector2(0.07f, 0.30f), new Vector2(0.93f, 0.54f));
            StyledButton(detail.transform, "检视武器", UIComponents.ButtonKind.Secondary, new Vector2(0.07f, 0.17f), new Vector2(selected.isOwned ? 0.48f : 0.93f, 0.25f),
                () => { selectedWeapon = selected; detailsFromShop = shop; Navigate(LobbyPage.WeaponDetails); });
            if (shop && !selected.isOwned)
            {
                var level = cachedCatalog?.level ?? session.Profile?.level ?? 0;
                var coins = cachedCatalog?.coins ?? session.Profile?.coins ?? 0;
                var label = selected.unlockLevel > level ? $"等级 {selected.unlockLevel} 解锁" : selected.priceCoins > coins ? "金币不足" : $"购买 · {selected.priceCoins:N0}";
                var buy = StyledButton(detail.transform, label, UIComponents.ButtonKind.Primary, new Vector2(0.07f, 0.05f), new Vector2(0.93f, 0.14f), () => _ = PurchaseAsync(selected));
                buy.interactable = selected.unlockLevel <= level && selected.priceCoins <= coins;
            }
            else
            {
                StyledButton(detail.transform, "改装配件", UIComponents.ButtonKind.Secondary,
                    new Vector2(0.52f, 0.17f), new Vector2(0.93f, 0.25f), () => OpenGunsmithFromArmory(selected));
                var secondary = selected.slotType == "Secondary";
                var equipped = (secondary ? session.Loadout?.secondaryWeaponId : session.Loadout?.primaryWeaponId) == selected.itemId;
                var equip = StyledButton(detail.transform, equipped ? "当前已装备" : secondary ? "装备为副武器" : "装备为主武器", UIComponents.ButtonKind.Primary,
                    new Vector2(0.07f, 0.05f), new Vector2(0.93f, 0.14f), () => _ = EquipWeaponAsync(selected));
                equip.name = "EquipSelectedWeapon";
                equip.interactable = !equipped && !equipBusy;
            }
            if (session.Loadout == null && api != null && pageCts != null) _ = EnsureLoadoutCachedAsync(pageCts.Token);
        }
    }
}
