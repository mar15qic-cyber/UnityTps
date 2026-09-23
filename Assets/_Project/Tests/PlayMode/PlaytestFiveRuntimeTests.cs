using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FishNet.Object;
using Game.Gameplay.Combat;
using Game.Gameplay.Player;
using Game.Gameplay.Settings;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Camera;
using Game.Presentation.FX;
using Game.Presentation.HUD;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Gameplay.PlayModeTests
{
    public sealed class PlaytestFiveRuntimeTests
    {
        [UnityTest]
        public IEnumerator BasicOpticMatrixAndThrowFlashFeedback()
        {
            yield return SceneManager.LoadSceneAsync("Assets/_Project/Scenes/Arena.unity");
            yield return new WaitForSeconds(1.5f);
            var controller = Object.FindFirstObjectByType<WeaponController>();
            var input = controller.GetComponent<InputReader>(); input.enabled = false;
            var rig = Object.FindFirstObjectByType<FPWeaponRig>();
            var aim = controller.GetComponent<PlayerAimState>();
            var weapons = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            var attachments = AttachmentAssetCatalog.LoadOrDefault();
            var saved = new Dictionary<string, Dictionary<string, string>>();
            var savedStyle = SettingsRuntime.ReticleStyle;
            var savedColor = SettingsRuntime.ReticleColor;
            SettingsRuntime.SetReticleLive(OpticReticleStyle.CircleDot, OpticReticleColor.Red);
            var ids = new[] { "weapon.m4", "weapon.ak", "weapon.rifle03", "weapon.smg01",
                "weapon.smg02", "weapon.smg03", "weapon.smg04", "weapon.smg05", "weapon.shotgun01" };
            try
            {
                foreach (var id in ids)
                {
                    saved[id] = new Dictionary<string, string>(); WeaponAttachmentStore.Load(id, saved[id]);
                    foreach (var optic in new[] { "attach.rifle.optic", "attach.lpfp.optic.02" })
                    {
                        WeaponAttachmentStore.Save(id, new Dictionary<string, string> { { "Optic", optic } });
                        controller.EquipDefinition(weapons.Entries.First(e => e.itemId == id).definition);
                        typeof(InputReader).GetProperty("AimHeld").SetValue(input, true);
                        yield return new WaitForSeconds(.85f);
                        Assert.Greater(aim.Ads01, .95f, id);
                        var hud = Object.FindFirstObjectByType<OpticAdsView>();
                        var clip = hud.transform.Find("OpticReticle/OpticWindowClip").GetComponent<RectMask2D>();
                        Assert.True(clip.enabled, id + optic);
                        var native = clip.GetComponentInChildren<RawImage>();
                        Assert.True(native.enabled, id + optic);
                        Assert.Greater(clip.rectTransform.rect.width, 2f, id + optic);
                        var socket = rig.ActiveView.GetComponent<WeaponAttachmentView>().GetSocketTransform(AttachmentSlotType.Optic);
                        attachments.Calibration.TryGetOpticAim(id, optic, out var row);
                        var fp = controller.GetComponentsInChildren<Camera>(true).First(c => c.name == "FP View Camera");
                        var viewport = fp.WorldToViewportPoint(socket.TransformPoint(row.WindowCenterLocal));
                        Assert.That(viewport.x, Is.EqualTo(.5f).Within(.025f), id + optic);
                        Assert.That(viewport.y, Is.EqualTo(.5f).Within(.025f), id + optic);
                        yield return Capture(id + "-" + optic);
                        typeof(InputReader).GetProperty("AimHeld").SetValue(input, false);
                        yield return new WaitForSeconds(.2f);
                    }
                }
                var throwable = controller.GetComponent<ThrowableController>();
                throwable.ResetOfflineInventory(); throwable.SelectNext();
                float start = Time.time;
                throwable.TryThrow(ThrowableType.Frag);
                yield return new WaitForSeconds(.22f);
                Assert.AreEqual(0, throwable.Count(ThrowableType.Frag), "release must not replay the equip wind-up");
                Assert.Less(Time.time - start, .35f);
                yield return Capture("throw-release");
                yield return new WaitForSeconds(2f);

                var fx = Object.FindFirstObjectByType<ThrowableScreenEffects>();
                var camera = Camera.main;
                var definition = Resources.Load<ThrowableCatalog>("ThrowableCatalog").Flash;
                var handle = typeof(ThrowableScreenEffects).GetMethod("HandleEffect", BindingFlags.NonPublic | BindingFlags.Instance);
                handle.Invoke(fx, new object[] { ThrowableType.Flash, camera.transform.position + camera.transform.forward * .1f, definition });
                yield return null;
                Assert.AreEqual(1f, fx.CurrentAlpha);
                Assert.Less(AudioListener.volume, SettingsRuntime.MasterVolume * .3f);
                yield return Capture("flash-direct");
                yield return new WaitForSecondsRealtime(definition.EffectSeconds + .1f);
                Assert.AreEqual(0f, fx.CurrentAlpha);
                Assert.AreEqual(SettingsRuntime.MasterVolume, AudioListener.volume, .001f);
                handle.Invoke(fx, new object[] { ThrowableType.Flash, camera.transform.position + camera.transform.right * .1f, definition });
                yield return null;
                Assert.That(fx.CurrentAlpha, Is.InRange(.01f, .7f));
                yield return Capture("flash-peripheral");
            }
            finally
            {
                foreach (var pair in saved) WeaponAttachmentStore.Save(pair.Key, pair.Value);
                SettingsRuntime.SetReticleLive(savedStyle, savedColor);
                input.enabled = true;
            }
        }

        [UnityTest]
        public IEnumerator RejectedSessionReturnsToExplicitLoginNotice()
        {
            var app = AppRoot.Ensure();
            app.Session.Apply(new Game.Account.AuthSessionDto { token = "rejected-test-session",
                expiresAtUtc = System.DateTime.UtcNow.AddHours(1).ToString("O") });
            app.ApiClient.SetToken(app.Session.Token);
            typeof(AppRoot).GetMethod("RejectSession", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(app, new object[] { "rejected-test-session" });
            yield return null;
            yield return null;
            Assert.IsFalse(app.Session.IsAuthenticated);
            Assert.IsNull(app.Session.Token);
            Assert.AreEqual("Lobby", SceneManager.GetActiveScene().name);
            var lobby = Object.FindFirstObjectByType<LobbyPresenter>();
            Assert.NotNull(lobby);
            Assert.AreEqual(LobbyPage.SessionExpired, typeof(LobbyPresenter)
                .GetField("currentPage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(lobby));
            yield return Capture("session-rejected");
        }

        [UnityTest]
        public IEnumerator ProjectileReflectsOffWallWithoutAirborneFreeze()
        {
            var catalog = Resources.Load<ThrowableCatalog>("ThrowableCatalog");
            var origin = new Vector3(5000f, 100f, 5000f);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.position = origin + Vector3.forward;
            wall.transform.localScale = new Vector3(5f, 5f, .2f);
            var instance = Object.Instantiate(catalog.NetworkProjectilePrefab, origin, Quaternion.identity);
            instance.GetComponent<NetworkObject>().SetIsNetworked(false);
            try
            {
                var projectile = instance.GetComponent<ThrowableProjectile>();
                projectile.OfflineInitialize(ThrowableType.Frag, Vector3.forward * 12f, null);
                var body = instance.GetComponent<Rigidbody>();
                bool bounced = false;
                for (int i = 0; i < 16; i++)
                {
                    yield return new WaitForFixedUpdate();
                    if (body.linearVelocity.z < -1f) { bounced = true; break; }
                }
                Assert.True(bounced, "wall must produce a clear reflected velocity");
                Assert.False(body.IsSleeping(), "airborne projectile must continue falling");
            }
            finally { Object.Destroy(instance); Object.Destroy(wall); }
        }

        private static IEnumerator Capture(string name)
        {
            yield return new WaitForEndOfFrame();
            System.IO.Directory.CreateDirectory("Temp/PlaytestFive0923/runtime");
            var image = ScreenCapture.CaptureScreenshotAsTexture();
            System.IO.File.WriteAllBytes("Temp/PlaytestFive0923/runtime/" + name + ".png", image.EncodeToPNG());
            Object.Destroy(image);
        }
    }
}
