using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public class MapFlowApi : DispatchProxy
    {
        public int CatalogCalls, CreateCalls;
        public Func<Task<ApiResult<MapCatalogDto[]>>> Catalog;
        public CancellationTokenSource Page;
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == "ListMapsAsync") { CatalogCalls++; return Catalog(); }
            if (method.Name == "CreateRoomAsync")
            {
                CreateCalls++; Page.Cancel();
                return Task.FromResult(ApiResult<RoomSnapshotDto>.Ok(null));
            }
            throw new NotSupportedException(method.Name);
        }
    }

    public sealed class MapContentFlowTests
    {
        private GameObject root;
        private LobbyPresenter presenter;
        private MapFlowApi api;
        private AccountSession session;
        private CancellationTokenSource page;
        private MapCatalogDto[] previous;
        private string oldRoot, temp;
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
        private void Set(string name, object value) => typeof(LobbyPresenter).GetField(name, Flags).SetValue(presenter, value);
        private Task Call(string name, params object[] args) => (Task)typeof(LobbyPresenter).GetMethod(name, Flags).Invoke(presenter, args);

        [SetUp] public void SetUp()
        {
            previous = System.Linq.Enumerable.ToArray(HotMapCatalog.Cached);
            HotMapCatalog.Store(null);
            oldRoot = HotUpdateRuntime.HotFilesRoot;
            temp = Path.Combine(Path.GetTempPath(), "map-flow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(temp, "maps"));
            File.WriteAllText(Path.Combine(temp, "maps/map_nightrelay.bundle"), "test-content");
            HotUpdateRuntime.HotFilesRoot = temp;
            root = new GameObject("MapFlowTest"); presenter = root.AddComponent<LobbyPresenter>();
            var label = new GameObject("Status", typeof(RectTransform), typeof(TextMeshProUGUI));
            label.transform.SetParent(root.transform);
            Set("status", label.GetComponent<TMP_Text>());
            session = new AccountSession();
            session.Apply(new AuthSessionDto { token = "map-flow", expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("o") });
            Set("session", session); Set("apiAvailable", true);
            page = new CancellationTokenSource(); Set("pageCts", page);
            var proxy = DispatchProxy.Create<IApiClient, MapFlowApi>(); api = (MapFlowApi)(object)proxy;
            api.Page = page;
            api.Catalog = () => Task.FromResult(ApiResult<MapCatalogDto[]>.Ok(new[] {
                new MapCatalogDto { mapId = "map_05", sceneName = "Map_NightRelay", contentHash = new string('a',64), availability = "ready" } }));
            Set("api", proxy);
        }
        [TearDown] public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(root);
            HotMapCatalog.Store(previous); HotUpdateRuntime.HotFilesRoot = oldRoot;
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
        [Test] public async Task ColdCatalog_CreateNightMap_RefreshesBeforeAdmission()
        {
            await Call("StartOnlineCreateAsync", new CreateRoomRequest { mapId = "map_05", mode = "KillRace" });
            Assert.That(api.CatalogCalls, Is.EqualTo(1)); Assert.That(api.CreateCalls, Is.EqualTo(1));
            Assert.That(typeof(LobbyPresenter).GetField("roomEntryPending", Flags).GetValue(presenter), Is.False);
        }
        [Test] public async Task BrowserRefresh_DoesNotRequireContentDownload()
        {
            Set("currentPage", LobbyPage.OnlineJoin);
            await Call("CheckMapUpdatesAsync");
            Assert.That(api.CatalogCalls, Is.EqualTo(1)); Assert.That(HotMapCatalog.TryGet("map_05", out _), Is.True);
        }
        [Test] public async Task FailedCatalog_DoesNotCreateOrClaimContentMismatch()
        {
            api.Catalog = () => Task.FromException<ApiResult<MapCatalogDto[]>>(new IOException("offline"));
            await Call("StartOnlineCreateAsync", new CreateRoomRequest { mapId = "map_05" });
            Assert.That(api.CreateCalls, Is.Zero);
            Assert.That(root.GetComponentInChildren<TMP_Text>().text, Does.Contain("地图目录获取失败"));
        }
        [Test] public async Task AccountChangedDuringFetch_DiscardsCatalog()
        {
            var pending = new TaskCompletionSource<ApiResult<MapCatalogDto[]>>(); api.Catalog = () => pending.Task;
            var task = Call("RefreshMapCatalogAsync");
            session.Apply(new AuthSessionDto { token = "different", expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("o") });
            pending.SetResult(ApiResult<MapCatalogDto[]>.Ok(new[] { new MapCatalogDto { mapId = "map_05" } }));
            await task; Assert.That(HotMapCatalog.Cached, Is.Empty);
        }
        [Test] public async Task ConcurrentCatalogRefresh_SharesRequestWithCreation()
        {
            var pending = new TaskCompletionSource<ApiResult<MapCatalogDto[]>>(); api.Catalog = () => pending.Task;
            var refresh = Call("RefreshMapCatalogAsync");
            var create = Call("StartOnlineCreateAsync", new CreateRoomRequest { mapId = "map_05" });
            Assert.That(api.CatalogCalls, Is.EqualTo(1));
            pending.SetResult(ApiResult<MapCatalogDto[]>.Ok(new[] { new MapCatalogDto {
                mapId = "map_05", sceneName = "Map_NightRelay", contentHash = new string('a',64), availability = "ready" } }));
            await refresh; await create;
            Assert.That(api.CreateCalls, Is.EqualTo(1));
        }
        [Test] public void LocalEnvironment_UsesDefaultEndpoint()
            => Assert.That(MapContentUpdater.ResolveBaseUrl(null, null), Is.EqualTo(HotUpdateBootstrap.DefaultBaseUrl));
        [Test] public void DisabledLocalUpdates_StayDisabled()
            => Assert.That(MapContentUpdater.ResolveBaseUrl(null, "off"), Is.Null);
        [Test] public void PrivateEnvironment_IgnoresEndpointOverride()
            => Assert.That(MapContentUpdater.ResolveBaseUrl(new ClientReleaseEnvironment { networkMode = "private-overlay", hotUpdateBaseUrl = "http://10.1.1.1/hotupdate/" }, "off"), Is.EqualTo("http://10.1.1.1/hotupdate"));
        [TestCase("", "fps-net-v23", "maps/map_nightrelay.bundle", true)]
        [TestCase("other-release", "fps-net-v23", "maps/map_nightrelay.bundle", false)]
        [TestCase("", "fps-net-v22", "maps/map_nightrelay.bundle", false)]
        [TestCase("", "fps-net-v23", "bootstrap.lua", false)]
        public void LocalChannel_StillChecksIdentity(string release, string protocol, string path, bool expected)
        {
            var manifest = new HotUpdateManifest { version = "10", releaseId = release, protocolId = protocol,
                files = new[] { new HotUpdateManifest.HotUpdateFileEntry { path = path, hash = new string('a',64), size = 1 } } };
            Assert.That(MapContentUpdater.IsCompatible(manifest, null), Is.EqualTo(expected));
        }
    }
}
