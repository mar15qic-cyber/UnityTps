using System;
using System.Linq;
using System.Threading.Tasks;
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
            // CF 三背包（2026-09-30 Phase D）：仓库页背包页签 + 该背包装配条（商城页不渲染）
            float gridTop = 0.87f;
            if (!shop) gridTop = BuildArmoryBackpackStrip(root);
            var items = (cachedCatalog?.items ?? Array.Empty<CatalogItemDto>()).Where(IsCatalogEquipment)
                .Where(x => shop ? x.acquisitionSource == "Shop" : x.isOwned)
                .Where(x => filter == "All" || EquipmentCategory(x) == filter).ToArray();
            var content = TacticalScroll(root, "WeaponGrid", new Vector2(0.17f, 0.05f), new Vector2(0.70f, gridTop));
            if (items.Length == 0)
            {
                StyledText(root, "该分类下暂无武器", UITheme.FontBody, UITheme.TextMuted, new Vector2(0.20f, 0.43f), new Vector2(0.66f, 0.57f));
                return;
            }
            var selected = items.FirstOrDefault(x => x.itemId == catalogSelection) ?? items[0];
            catalogSelection = selected.itemId;
            var pageLoadout = shop ? session.Loadout : session.LoadoutForBackpack(armoryBackpack);
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
                    var equipped = pageLoadout != null && (pageLoadout.primaryWeaponId == item.itemId || pageLoadout.secondaryWeaponId == item.itemId);
                    StyledText(card.transform, equipped ? "已装备" : item.isOwned ? "已拥有" : $"{item.priceCoins:N0} 金币", UITheme.FontCaption,
                        equipped ? UITheme.AccentPrimary : UITheme.TextMuted, new Vector2(0.06f, 0.05f), new Vector2(0.94f, 0.23f));
                    card.GetComponent<Image>().raycastTarget = true;
                    var button = card.AddComponent<Button>(); button.targetGraphic = card.GetComponent<Image>();
                    button.onClick.AddListener(() => { catalogSelection = item.itemId; RenderTacticalCatalog(shop); });
                }
            }
            var detail = StyledPanel("SelectionDetails", root, UITheme.BackgroundPanel, new Vector2(0.73f, 0.05f), new Vector2(0.98f, 0.87f));
            StyledText(detail.transform, CategoryLabel(EquipmentCategory(selected)), UITheme.FontCaption, UITheme.AccentPrimary,
                new Vector2(0.07f, 0.90f), new Vector2(0.93f, 0.96f));
            StyledText(detail.transform, selected.displayName, UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.07f, 0.80f), new Vector2(0.93f, 0.90f), TextAlignmentOptions.Left, FontStyles.Bold);
            CreateWeaponIcon(detail.transform, selected.itemId, new Vector2(0.07f, 0.56f), new Vector2(0.93f, 0.79f));
            if(selected.itemType=="Throwable")
            {
                StyledText(detail.transform,"永久解锁 · 每槽每生命一枚\n可重复装配，复活后补齐",UITheme.FontBody,UITheme.TextMuted,new Vector2(.07f,.38f),new Vector2(.93f,.53f));
                if(shop && !selected.isOwned)
                {
                    var buy=StyledButton(detail.transform,$"购买 · {selected.priceCoins:N0} 金币",UIComponents.ButtonKind.Primary,new Vector2(.07f,.05f),new Vector2(.93f,.14f),()=>_=PurchaseAsync(selected));
                    buy.interactable=selected.priceCoins <= (cachedCatalog?.coins??0) && selected.unlockLevel <= (cachedCatalog?.level??0);
                }
                else if(!shop)
                    StyledButton(detail.transform,$"装入投掷槽 {throwableSlotSelection+1}",UIComponents.ButtonKind.Primary,new Vector2(.07f,.05f),new Vector2(.93f,.14f),()=>_=SaveThrowableSlotAsync(throwableSlotSelection,selected.itemId));
                return;
            }
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
                var equipped = (secondary ? pageLoadout?.secondaryWeaponId : pageLoadout?.primaryWeaponId) == selected.itemId;
                var equip = StyledButton(detail.transform, equipped ? "当前已装备" : secondary ? "装备为副武器" : "装备为主武器", UIComponents.ButtonKind.Primary,
                    new Vector2(0.07f, 0.05f), new Vector2(0.93f, 0.14f), () => _ = EquipWeaponAsync(selected));
                equip.name = "EquipSelectedWeapon";
                equip.interactable = !equipped && !equipBusy;
            }
            if (session.Loadout == null && api != null && pageCts != null) _ = EnsureLoadoutCachedAsync(pageCts.Token);
        }

        // ---- CF 三背包（2026-09-30 Phase D）：仓库页背包页签 + 装配条 ----

        /// <summary>构建仓库页背包条（页签 1/2/3 + 该背包主/副/投掷摘要 + 投掷物循环切换）。
        /// 返回网格上沿（装配条占位后网格收窄）。</summary>
        private float BuildArmoryBackpackStrip(Transform root)
        {
            const float stripMinY = 0.845f, stripMaxY = 0.905f;
            for (int i = 0; i < 3; i++)
            {
                var captured = i;
                var pill = BackpackTab("Backpack_" + (i + 1), root, "背包" + (i + 1),
                    new Vector2(0.17f + i * 0.085f, stripMinY), new Vector2(0.17f + (i + 1) * 0.085f - 0.006f, stripMaxY));
                pill.name = "BackpackTab_" + (i + 1);
                UIComponents.SetNavPillSelected(pill, i == armoryBackpack);
                pill.onClick.AddListener(() => { armoryBackpack = captured; RenderTacticalCatalog(false); });
            }
            var loadout = session.LoadoutForBackpack(armoryBackpack);
            StyledText(root, $"主  {WeaponName(loadout?.primaryWeaponId)}    副  {WeaponName(loadout?.secondaryWeaponId)}",
                UITheme.FontCaption, UITheme.TextPrimary, new Vector2(0.435f, stripMinY), new Vector2(0.70f, stripMaxY),
                TextAlignmentOptions.Left);
            for(int i=0;i<3;i++)
            {
                int slotIndex=i;
                var ids=loadout?.throwableIds??Game.Gameplay.Combat.ThrowableSlots.Legacy(loadout?.throwableId);
                var id=ids.Length>i?ids[i]:null;
                var slot=StyledPanel("ThrowableSlot"+i,root,i==throwableSlotSelection?UITheme.CardSurfaceAlt:UITheme.CardSurface,new Vector2(.17f+i*.17f,.735f),new Vector2(.33f+i*.17f,.835f));
                CreateWeaponIcon(slot.transform,id,new Vector2(.04f,.22f),new Vector2(.55f,.95f));
                StyledText(slot.transform,Game.Gameplay.Combat.ThrowableSlots.DisplayName(id),UITheme.FontCaption,UITheme.TextPrimary,new Vector2(.42f,.2f),new Vector2(.98f,.96f),TextAlignmentOptions.Center);
                var select=slot.AddComponent<Button>();slot.GetComponent<Image>().raycastTarget=true;
                select.onClick.AddListener(()=>{throwableSlotSelection=slotIndex;armoryFilter="Throwable";RenderTacticalCatalog(false);});
                var clear=StyledButton(slot.transform,"清空",UIComponents.ButtonKind.Secondary,new Vector2(.55f,0),new Vector2(.98f,.25f),()=>_=SaveThrowableSlotAsync(slotIndex,null));
                clear.interactable=!equipBusy&&!string.IsNullOrEmpty(id);
            }
            return .725f;
        }

        private int throwableSlotSelection;
        private bool IsCatalogEquipment(CatalogItemDto item) => IsLpfpWeaponItem(item) || item != null && item.isActive && item.itemType=="Throwable" && item.itemId!="throwable.standard" && item.itemId!="throwable.frag_assault";
        private string EquipmentCategory(CatalogItemDto item) => item.itemType=="Throwable" ? "Throwable" : GetCategory(item.itemId);
        private async Task SaveThrowableSlotAsync(int slot,string id)
        {
            if(equipBusy||api==null||pageCts==null) return;
            var target=session.LoadoutForBackpack(armoryBackpack);if(target==null)return;
            var slots=(string[])(target.throwableIds??Game.Gameplay.Combat.ThrowableSlots.Legacy(target.throwableId)).Clone();
            slots[slot]=id;
            equipBusy=true;
            try
            {
                var token=pageCts.Token;
                var result=await api.UpdateLoadoutAsync(new LoadoutRequest{primaryWeaponId=target.primaryWeaponId,secondaryWeaponId=target.secondaryWeaponId,throwableIds=slots,expectedVersion=target.version},armoryBackpack+1,token);
                if(token.IsCancellationRequested)return;
                if(!result.Success){status.text=ApiErrorMessages.ToUserMessage(result);return;}
                session.ApplyLoadout(result.Data,armoryBackpack);
                equipBusy=false;RenderTacticalCatalog(false);status.text=$"背包 {armoryBackpack+1} · 投掷槽 {slot+1} 已保存";
            }
            catch(OperationCanceledException){}
            finally{equipBusy=false;}
        }

        private string ThrowableUiLabel(string throwableId)
        {
            if (string.IsNullOrEmpty(throwableId)) return "不带";
            var item = cachedCatalog?.items?.FirstOrDefault(x => x.itemId == throwableId);
            return item != null ? item.displayName : throwableId;
        }
    }
}
