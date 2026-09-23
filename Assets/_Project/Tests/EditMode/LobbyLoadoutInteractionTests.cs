using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Game.Account;
using Game.UI;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Gameplay.Tests
{
    public class LobbyInteractionApi : DispatchProxy
    {
        public string FriendQuery;
        public LoadoutRequest LoadoutRequest;
        public Func<LoadoutRequest, Task<ApiResult<LoadoutDto>>> Save;
        protected override object Invoke(MethodInfo method, object[] args)
        {
            switch (method.Name)
            {
                case "SendFriendRequestAsync":
                    FriendQuery = (string)args[0];
                    return Task.FromResult(ApiResult<FriendRequestEntryDto>.Ok(new FriendRequestEntryDto()));
                case "GetFriendsAsync":
                    return Task.FromResult(ApiResult<FriendListDto>.Ok(new FriendListDto()));
                case "UpdateLoadoutAsync":
                    LoadoutRequest = (LoadoutRequest)args[0];
                    return Save(LoadoutRequest);
                default: throw new NotSupportedException(method.Name);
            }
        }
    }

    public sealed class LobbyLoadoutInteractionTests
    {
        private GameObject root;
        private LobbyPresenter presenter;
        private AccountSession session;
        private LobbyInteractionApi api;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private void Set(string name, object value) => typeof(LobbyPresenter).GetField(name, Flags).SetValue(presenter, value);
        private T Get<T>(string name) => (T)typeof(LobbyPresenter).GetField(name, Flags).GetValue(presenter);
        private object Call(string name, params object[] args) => typeof(LobbyPresenter).GetMethod(name, Flags).Invoke(presenter, args);

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("InteractionTest");
            presenter = root.AddComponent<LobbyPresenter>();
            session = new AccountSession();
            session.Apply(new AuthSessionDto { token = "test", expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("o"),
                profile = new PlayerProfileDto { username = "LongPlayerNameForIdentityVisibility", identityTag = "0042", level = 8 },
                loadout = new LoadoutDto { primaryWeaponId = "weapon.m4", secondaryWeaponId = "weapon.service_pistol", version = 3 } });
            Set("session", session);
            Set("weaponAssets", Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog"));
            Call("BuildShell");
            presenter.Navigate(LobbyPage.Lobby);
            var proxy = DispatchProxy.Create<IApiClient, LobbyInteractionApi>();
            api = (LobbyInteractionApi)(object)proxy;
            Set("api", proxy);
            api.Save = request => Task.FromResult(ApiResult<LoadoutDto>.Ok(new LoadoutDto {
                primaryWeaponId = request.primaryWeaponId, secondaryWeaponId = request.secondaryWeaponId, version = request.expectedVersion + 1 }));
        }

        [TearDown]
        public void TearDown() { if (root != null) UnityEngine.Object.DestroyImmediate(root); }

        [Test]
        public void FriendFormComposesFixedHashAndPreservesLeadingZeroTag()
        {
            Call("OpenAddFriendModal");
            var modal = Get<GameObject>("socialModal");
            Assert.That(modal.GetComponentsInChildren<TMP_InputField>().Length, Is.EqualTo(2));
            Assert.That(modal.GetComponentsInChildren<TMP_Text>().Any(t => t.name == "FriendTagSeparator" && t.text == "#"), Is.True);
            Get<TMP_InputField>("friendsSearchInput").text = "  Friend  ";
            Get<TMP_InputField>("friendsTagInput").text = "0042";
            var send = modal.GetComponentsInChildren<Button>().Single(b => b.GetComponentInChildren<TMP_Text>().text == "发送好友申请");
            send.onClick.Invoke();
            Assert.That(api.FriendQuery, Is.EqualTo("Friend#0042"));
            Assert.That(Get<TMP_Text>("modalFeedback").text, Is.EqualTo("好友申请已发送"));
            Assert.That(Get<TMP_Text>("accountTagLabel").text, Is.EqualTo("#0042"));
        }

        [Test]
        public void FriendFormRejectsIncompleteCodeWithoutSending()
        {
            Call("OpenAddFriendModal");
            Get<TMP_InputField>("friendsSearchInput").text = "Friend";
            Get<TMP_InputField>("friendsTagInput").text = "42";
            var send = Get<GameObject>("socialModal").GetComponentsInChildren<Button>().Single(b => b.GetComponentInChildren<TMP_Text>().text == "发送好友申请");
            send.onClick.Invoke();
            Assert.That(api.FriendQuery, Is.Null);
            Assert.That(Get<TMP_Text>("modalFeedback").text, Does.Contain("4位"));
        }

        private CatalogItemDto ShowArmory(string id, string slot)
        {
            var item = new CatalogItemDto { itemId = id, itemType = "Weapon", slotType = slot, displayName = id,
                isOwned = true, isActive = true, isImplemented = true, acquisitionSource = "Shop" };
            Set("cachedCatalog", new ShopCatalogDto { items = new[] { item } });
            Set("currentPage", LobbyPage.Armory);
            Set("armoryFilter", "All");
            Call("RenderTacticalCatalog", false);
            return item;
        }

        [TestCase("weapon.sniper01", "Primary", "装备为主武器")]
        [TestCase("weapon.handgun02", "Secondary", "装备为副武器")]
        public void ArmoryEquipSavesCorrectSlotAndUpdatesLobbyIcon(string id, string slot, string label)
        {
            ShowArmory(id, slot);
            var equip = root.GetComponentsInChildren<Button>().Single(b => b.name == "EquipSelectedWeapon");
            Assert.That(equip.GetComponentInChildren<TMP_Text>().text, Is.EqualTo(label));
            equip.onClick.Invoke();
            Assert.That(api.LoadoutRequest.expectedVersion, Is.EqualTo(3));
            Assert.That(session.Loadout.primaryWeaponId, Is.EqualTo(slot == "Primary" ? id : "weapon.m4"));
            Assert.That(session.Loadout.secondaryWeaponId, Is.EqualTo(slot == "Secondary" ? id : "weapon.service_pistol"));
            Assert.That(root.GetComponentsInChildren<Button>().Single(b => b.name == "EquipSelectedWeapon").interactable, Is.False);
            presenter.Navigate(LobbyPage.Lobby);
            var catalog = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            catalog.TryGet(session.Loadout.primaryWeaponId, out var entry);
            Assert.That(Get<Image>("primaryIcon").sprite, Is.SameAs(entry.icon));
            Assert.That(Get<TMP_Text>("primaryLabel").text, Is.EqualTo(entry.definition.DisplayName));
        }

        [Test]
        public async Task EquipIgnoresLateResponseAfterNavigation()
        {
            var item = ShowArmory("weapon.sniper01", "Primary");
            var pending = new TaskCompletionSource<ApiResult<LoadoutDto>>();
            api.Save = _ => pending.Task;
            var save = (Task)Call("EquipWeaponAsync", item);
            presenter.Navigate(LobbyPage.Login);
            pending.SetResult(ApiResult<LoadoutDto>.Ok(new LoadoutDto { primaryWeaponId = item.itemId, version = 4 }));
            await save;
            Assert.That(session.Loadout.primaryWeaponId, Is.EqualTo("weapon.m4"));
            Assert.That(Get<TMP_Text>("status").text, Is.Empty);
        }
    }
}
