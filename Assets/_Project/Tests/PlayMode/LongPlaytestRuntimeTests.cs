using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Animancer;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.HUD;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Gameplay.PlayModeTests
{
    public sealed class LongPlaytestRuntimeTests
    {
        [UnityTest]
        public IEnumerator AllPlayableMapsProduceNonEmptyOverheadTextures()
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var scene in new[] { "Arena", "Map_Stackyard", "Map_Depot55", "Map_Ridgeline" })
            {
                yield return SceneManager.LoadSceneAsync("Assets/_Project/Scenes/" + scene + ".unity");
                yield return new WaitForSeconds(2f);
                var radar = Object.FindFirstObjectByType<TacticalMinimapView>();
                Assert.NotNull(radar, scene);
                Assert.True((bool)typeof(TacticalMinimapView).GetField("_mapReady", flags).GetValue(radar), scene);
                var entry = Resources.Load<Game.Gameplay.Network.MapRadarCatalog>("MapRadarCatalog").Find(scene);
                var image = radar.transform.Find("RadarPanel/MapImage").GetComponent<UnityEngine.UI.RawImage>();
                Assert.AreSame(entry.image.texture, image.texture, "radar must use the channel image: " + scene);
                Assert.AreEqual(Color.white, image.color, "channel artwork must not be recolored");
                Assert.IsNull(GameObject.Find("TacticalMinimapCamera"), "no separate map rendering camera");
                var owner = Object.FindFirstObjectByType<WeaponController>();
                var uv = entry.WorldToUv(owner.transform.position);
                Assert.That(uv.x, Is.InRange(0f, 1f)); Assert.That(uv.y, Is.InRange(0f, 1f));
                yield return Capture("hud-" + scene);
            }
        }

        [UnityTest]
        public IEnumerator AllNativeWeapons_SelectCycleThrowAndRestore()
        {
            yield return SceneManager.LoadSceneAsync("Assets/_Project/Scenes/Arena.unity");
            yield return new WaitForSeconds(1.5f);
            var controller = Object.FindFirstObjectByType<WeaponController>();
            var input = controller.GetComponent<InputReader>();
            input.enabled = false;
            var throwable = controller.GetComponent<ThrowableController>();
            var actions = controller.GetComponent<ActionSystem>();
            var errors = new List<string>();
            var weapons = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            System.IO.Directory.CreateDirectory("Temp/LongPlaytestFix/runtime");
            foreach (var weapon in weapons.Entries.Where(e => e.IsLpfp && e.definition != null))
            {
                throwable.Unequip(); actions.Interrupt(ActionInterruptReason.External);
                controller.EquipDefinition(weapon.definition);
                throwable.ResetOfflineInventory();
                yield return new WaitForSeconds(1.4f);
                Assert.True(throwable.SelectNext(), weapon.itemId);
                Assert.AreEqual(ThrowableType.Frag, throwable.SelectedType);
                Assert.True(throwable.SelectNext()); Assert.AreEqual(ThrowableType.Flash, throwable.SelectedType);
                Assert.True(throwable.SelectNext()); Assert.AreEqual(ThrowableType.Smoke, throwable.SelectedType);
                Assert.True(throwable.SelectNext()); Assert.AreEqual(ThrowableType.Frag, throwable.SelectedType);
                yield return new WaitForSeconds(.15f);
                var held = GameObject.Find("HeldThrowable");
                if (held == null) { errors.Add(weapon.itemId + ": no held model"); continue; }
                var point = Camera.main.WorldToViewportPoint(held.transform.position);
                if (point.z <= 0f || point.x < 0 || point.x > 1 || point.y < 0 || point.y > 1)
                    errors.Add(weapon.itemId + ": ready hand outside camera " + point);
                yield return Capture("ready-" + weapon.itemId);
                int ammo = controller.Runtime.CurrentAmmo;
                SetInput(input, "FirePressed", true); SetInput(input, "FireHeld", true);
                yield return null;
                SetInput(input, "FirePressed", false); SetInput(input, "FireHeld", false);
                Assert.AreEqual(ammo, controller.Runtime.CurrentAmmo, "selection fired a bullet: " + weapon.itemId);
                var animator = Object.FindFirstObjectByType<FPWeaponAnimator>();
                var animancer = animator.GetComponent<AnimancerComponent>();
                Assert.That(animancer.States.Current.Key.ToString(), Does.Contain("grenade_throw"));
                var first = held.transform.position;
                yield return new WaitForSeconds(.22f);
                if (held != null && Vector3.Distance(first, held.transform.position) < .01f)
                    errors.Add(weapon.itemId + ": throw hand did not animate");
                yield return Capture("throw-" + weapon.itemId);
                yield return new WaitForSeconds(throwable.ThrowActionSeconds);
                Assert.False(throwable.IsEquipped, weapon.itemId);
                Assert.AreEqual(0, throwable.Count(ThrowableType.Frag), weapon.itemId);
                Assert.False(actions.IsBusy, weapon.itemId);
            }
            Assert.IsEmpty(errors, string.Join("\n", errors));
            foreach (var type in new[] { ThrowableType.Flash, ThrowableType.Smoke })
            {
                throwable.ResetOfflineInventory();
                throwable.SelectNext();
                while (throwable.SelectedType != type) throwable.SelectNext();
                yield return new WaitForSeconds(.2f);
                yield return Capture("ready-" + type);
                SetInput(input, "FirePressed", true); yield return null; SetInput(input, "FirePressed", false);
                yield return new WaitForSeconds(.22f);
                yield return Capture("throw-" + type);
                yield return new WaitForSeconds(throwable.ThrowActionSeconds);
                Assert.AreEqual(0, throwable.Count(type));
                Assert.False(throwable.IsEquipped);
            }
            throwable.ResetOfflineInventory(); throwable.SelectNext();
            SetInput(input, "FirePressed", true); yield return null; SetInput(input, "FirePressed", false);
            actions.Interrupt(ActionInterruptReason.SwitchWeapon);
            yield return new WaitForSeconds(.8f);
            Assert.AreEqual(1, throwable.Count(ThrowableType.Frag), "interruption before release spent inventory");
            Assert.False(throwable.IsEquipped);
        }

        private static void SetInput(InputReader input, string name, object value)
            => typeof(InputReader).GetProperty(name).SetValue(input, value);

        private static IEnumerator Capture(string name)
        {
            yield return new WaitForEndOfFrame();
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            System.IO.File.WriteAllBytes("Temp/LongPlaytestFix/runtime/" + name + ".png", texture.EncodeToPNG());
            Object.Destroy(texture);
        }
    }
}
