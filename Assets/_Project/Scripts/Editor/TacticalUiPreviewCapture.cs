#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using Game.Account;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Editor
{
    /// <summary>Static EditMode visual proof, never enters PlayMode or contacts a backend.</summary>
    public static class TacticalUiPreviewCapture
    {
        private static Action pendingCapture;
        public static void CompletePending()
        {
            var capture = pendingCapture; pendingCapture = null; capture?.Invoke();
        }
        public static string Capture(int width, int height, string variant = "lobby")
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("StaticMenuProof");
            SceneManager.MoveGameObjectToScene(root, scene);
            var rt = new RenderTexture(width, height, 24);
            var previous = RenderTexture.active;
            Texture2D image = null;
            Camera camera = null;
            Action cleanup = () =>
            {
                RenderTexture.active = previous;
                if (camera != null) camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(root);
                rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                EditorSceneManager.ClosePreviewScene(scene);
            };
            try
            {
                var presenter = root.AddComponent<LobbyPresenter>();
                var session = new AccountSession();
                session.Apply(new AuthSessionDto { token = "static-preview", expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("o"),
                    profile = new PlayerProfileDto { username = "OPERATOR", identityTag = "0774", level = 12, xp = 420, xpToNextLevel = 1200, coins = 12850 },
                    loadout = new LoadoutDto { primaryWeaponId = "weapon.m4", secondaryWeaponId = "weapon.service_pistol", version = 1 } });
                Set(presenter, "session", session);
                var assets = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
                Set(presenter, "weaponAssets", assets);
                Set(presenter, "apiAvailable", true);
                var catalogItems = new System.Collections.Generic.List<CatalogItemDto>();
                foreach (var entry in assets.Entries)
                    if (entry.IsLpfp && entry.definition != null) catalogItems.Add(new CatalogItemDto {
                        itemId = entry.itemId, itemType = "Weapon", displayName = entry.definition.DisplayName,
                        slotType = entry.slotType.ToString(), acquisitionSource = "Shop", isOwned = variant != "shop",
                        priceCoins = 1200, unlockLevel = 1, isActive = true, isImplemented = true });
                Set(presenter, "cachedCatalog", new ShopCatalogDto { coins = 12850, level = 12, items = catalogItems.ToArray() });
                Call(presenter, "BuildShell");
                // The static fixture does not boot AppRoot/Lua. Represent the production career slot.
                var bar = root.transform.Find("LobbyCanvas/ShellTopBar");
                if (bar.Find("NavHot_career") == null)
                    typeof(LobbyPresenter).GetMethod("CreateHotNavPill", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(presenter, new object[] { bar, new HotPageRegistry.HotPage { Id = "career", Label = "战绩" }, 0.38f });
                presenter.Navigate(variant == "login" ? LobbyPage.Login : LobbyPage.Lobby);
                if (variant.StartsWith("rooms"))
                {
                    var rooms = new GameRoomDto[30];
                    for (int i = 0; i < rooms.Length; i++) rooms[i] = new GameRoomDto {
                        roomId=i+1, leaderUsername=i==0 ? "很长的房主名字用于验证列表省略显示" : "OPERATOR_"+i,
                        mapId=i%2==0?"map_01":"arena", mode=i%3==0?"KillRace":"TDM", status=i%4==0?"InMatch":"Waiting",
                        joinedPlayers=i%4+1,maxPlayers=8,killTarget=100,timeLimitMinutes=10 };
                    Set(presenter,"cachedRoomRows",rooms);Set(presenter,"selectedRoomId",2L);
                    presenter.Navigate(LobbyPage.OnlineJoin);
                    if(variant=="rooms-create")Call(presenter,"OpenCreateRoomDialog");
                    if(variant=="rooms-code")Call(presenter,"OpenRoomCodeDialog");
                }
                if (variant == "armory") Call(presenter, "RenderArmoryPage");
                if (variant == "shop") typeof(LobbyPresenter).GetMethod("RenderCatalog", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(presenter, new object[] { true });
                if (variant == "settings") presenter.Navigate(LobbyPage.Settings);
                if (variant == "armory" || variant == "shop")
                {
                    Set(presenter, "currentPage", variant == "armory" ? LobbyPage.Armory : LobbyPage.Shop);
                    Call(presenter, "UpdateNavSelection");
                }
                var canvas = root.GetComponentInChildren<Canvas>();
                var cameraGo = new GameObject("StaticUiCamera", typeof(Camera));
                cameraGo.transform.SetParent(root.transform, false);
                camera = cameraGo.GetComponent<Camera>();
                // Game camera classification renders uGUI; this remains an EditMode preview scene.
                camera.cameraType = CameraType.Game;
                camera.scene = scene;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = UITheme.BackgroundDeep;
                camera.cullingMask = 1 << 5;
                camera.targetTexture = rt;
                // World-space canvas gives deterministic camera-only rendering in an Editor preview scene.
                // ScreenSpaceCamera is batched by GameView and can be omitted from Camera.Render in URP.
                var scale = Mathf.Min(width / 1920f, height / 1080f);
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = camera;
                var canvasRect = (RectTransform)canvas.transform;
                canvasRect.sizeDelta = new Vector2(width / scale, height / scale);
                canvasRect.localPosition = new Vector3(0, 0, 5);
                canvasRect.localRotation = Quaternion.identity;
                canvasRect.localScale = Vector3.one * scale;
                camera.orthographic = true;
                camera.orthographicSize = height * 0.5f;
                camera.nearClipPlane = 0.1f; camera.farClipPlane = 10;
                foreach (var t in canvas.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 5;
                Canvas.ForceUpdateCanvases();
                var character = root.GetComponentInChildren<LobbyCharacterPreview>();
                if (character != null)
                {
                    character.Initialize(true); character.ApplyLoadout(session.Loadout, assets); character.RenderEditorPreview();
                }
                if (variant == "friends")
                {
                    Set(presenter, "cachedFriends", new FriendListDto { friends = new[] {
                        new FriendEntryDto { userId = 2, username = "RAVEN", identityTag = "1024", presence = FriendPresence.Online },
                        new FriendEntryDto { userId = 3, username = "NOMAD", identityTag = "2048", presence = FriendPresence.InMatch } } });
                    Call(presenter, "ToggleSocial"); Call(presenter, "OpenAddFriendModal");
                }
                Directory.CreateDirectory("screenshots/tactical-ui");
                var path = Path.GetFullPath($"screenshots/tactical-ui/{variant}-{width}x{height}.png");
                // Let uGUI register newly enabled/rebuilt graphics before reading the camera.
                pendingCapture = () =>
                {
                    try
                    {
                        foreach (var group in canvas.GetComponentsInChildren<CanvasGroup>(true)) group.alpha = 1;
                        foreach (var t in canvas.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 5;
                        Canvas.ForceUpdateCanvases();
                        if (character != null) character.RenderEditorPreview();
                        foreach (var graphic in canvas.GetComponentsInChildren<Graphic>())
                        {
                            if (graphic is TMPro.TMP_Text text) text.ForceMeshUpdate();
                            graphic.SetAllDirty(); graphic.Rebuild(CanvasUpdate.PreRender);
                        }
                        Canvas.ForceUpdateCanvases();
                        camera.Render(); RenderTexture.active = rt;
                        image = new Texture2D(width, height, TextureFormat.RGB24, false);
                        image.ReadPixels(new Rect(0, 0, width, height), 0, 0); image.Apply();
                        File.WriteAllBytes(path, image.EncodeToPNG());
                    }
                    finally { cleanup(); }
                };
                EditorApplication.QueuePlayerLoopUpdate();
                return path;
            }
            catch { cleanup(); throw; }
        }
        private static void Set(object o, string name, object value) => typeof(LobbyPresenter).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(o, value);
        private static void Call(object o, string name) => typeof(LobbyPresenter).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(o, null);
    }
}
#endif
