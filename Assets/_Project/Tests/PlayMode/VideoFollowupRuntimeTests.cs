using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Combat;
using Game.Gameplay.Player;
using Game.Gameplay.Settings;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Camera;
using Game.Presentation.HUD;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Gameplay.PlayModeTests
{
    public sealed class VideoFollowupRuntimeTests
    {
        [UnityTest]
        public IEnumerator Digit3CyclesThrowablesWithoutSelectingWeaponSlot()
        {
            yield return SceneManager.LoadSceneAsync("Assets/_Project/Scenes/Arena.unity");
            yield return new WaitForSeconds(1.5f);
            var controller = Object.FindFirstObjectByType<WeaponController>();
            var definition = controller.Definition;
            var input = controller.GetComponent<InputReader>();
            var throwable = controller.GetComponent<ThrowableController>();
            var oldKey = SettingsKeyMap.Get(SettingsKeyMap.Action.SelectThrowable);
            SettingsKeyMap.Set(SettingsKeyMap.Action.SelectThrowable, Key.Digit3, false);
            var keyboard = InputSystem.AddDevice<Keyboard>();
            try
            {
                foreach (var expected in new[] { ThrowableType.Frag, ThrowableType.Flash, ThrowableType.Smoke })
                {
                    InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Digit3));
                    yield return null;
                    yield return null;
                    Assert.True(throwable.IsEquipped);
                    Assert.AreEqual(expected, throwable.SelectedType);
                    Assert.AreSame(definition, controller.Definition);
                    Assert.AreEqual(-1, input.SlotPressed);
                    InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                    yield return null;
                }
            }
            finally { InputSystem.RemoveDevice(keyboard); SettingsKeyMap.Set(SettingsKeyMap.Action.SelectThrowable, oldKey, false); }
        }

        [UnityTest]
        public IEnumerator NativeMountsStayInFpCameraAndSnipersUseOriginalTexture()
        {
            yield return SceneManager.LoadSceneAsync("Assets/_Project/Scenes/Arena.unity");
            yield return new WaitForSeconds(1.5f);
            var controller = Object.FindFirstObjectByType<WeaponController>();
            var input = controller.GetComponent<InputReader>(); input.enabled = false;
            var rig = Object.FindFirstObjectByType<FPWeaponRig>();
            var weapons = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            var saved = new Dictionary<string, string>();
            WeaponAttachmentStore.Load("weapon.m4", saved);
            try
            {
                foreach (var optic in new[] { "attach.lpfp.optic.01", "attach.lpfp.optic.03" })
                {
                    WeaponAttachmentStore.Save("weapon.m4", new Dictionary<string, string> { { "Optic", optic } });
                    controller.EquipDefinition(weapons.Entries.First(e => e.itemId == "weapon.m4").definition);
                    yield return new WaitForSeconds(1.1f);
                    foreach (var socket in rig.ActiveView.GetComponentsInChildren<AttachmentSocket>(true))
                        foreach (var renderer in socket.GetComponentsInChildren<MeshRenderer>(true))
                            Assert.AreEqual(9, renderer.gameObject.layer, renderer.name);
                    yield return Capture("ak-" + optic);
                    controller.GetComponent<ThrowableController>().ResetOfflineInventory();
                    controller.GetComponent<ThrowableController>().SelectNext();
                    controller.GetComponent<ThrowableController>().TryThrow(ThrowableType.Frag);
                    yield return new WaitForSeconds(1f);
                    rig.ApplyDeathState(); rig.ApplyRespawnState();
                    yield return new WaitForSeconds(.3f);
                    Assert.AreEqual(9, rig.ActiveView.GetComponent<WeaponAttachmentView>().GetSocketTransform(AttachmentSlotType.Optic).Find("OpticMountAdapter").gameObject.layer);
                    yield return Capture("ak-after-throw-" + optic);
                }
                foreach (var weapon in weapons.Entries.Where(e => e.IsLpfp && e.definition != null))
                {
                    controller.EquipDefinition(weapon.definition);
                    yield return null;
                    foreach (var socket in rig.ActiveView.GetComponentsInChildren<AttachmentSocket>(true))
                        foreach (var renderer in socket.GetComponentsInChildren<MeshRenderer>(true))
                            Assert.AreEqual(9, renderer.gameObject.layer, weapon.itemId + "/" + renderer.name);
                }
                foreach (var id in new[] { "weapon.sniper01", "weapon.sniper02", "weapon.sniper03" })
                {
                    controller.EquipDefinition(weapons.Entries.First(e => e.itemId == id).definition);
                    yield return new WaitForSeconds(1.3f);
                    typeof(InputReader).GetProperty("AimHeld").SetValue(input, true);
                    yield return new WaitForSeconds(1f);
                    var expected = NativeScopeReticleCatalog.Load().SniperTexture;
                    var overlay = Object.FindFirstObjectByType<OpticAdsView>().GetComponentsInChildren<RawImage>(true).First(r => r.name == "NativeLPFPScopeTexture");
                    if (string.IsNullOrEmpty(PhysicalScopeView.HandlingOpticId))
                    {
                        Assert.True(overlay.enabled, id);
                        Assert.AreSame(expected, overlay.texture, id);
                        Assert.Greater(overlay.rectTransform.rect.height, 300f, "native scope must span the aperture");
                    }
                    else
                        Assert.AreSame(expected, GameObject.Find("PhysicalScopeReticle").GetComponent<MeshRenderer>().sharedMaterial.mainTexture, id);
                    yield return Capture("scope-" + id);
                    typeof(InputReader).GetProperty("AimHeld").SetValue(input, false);
                    yield return new WaitForSeconds(.4f);
                }
            }
            finally { WeaponAttachmentStore.Save("weapon.m4", saved); }
        }

        private static IEnumerator Capture(string name)
        {
            yield return new WaitForEndOfFrame();
            System.IO.Directory.CreateDirectory("Temp/VideoFollowup0923/runtime");
            var image = ScreenCapture.CaptureScreenshotAsTexture();
            System.IO.File.WriteAllBytes("Temp/VideoFollowup0923/runtime/" + name + ".png", image.EncodeToPNG());
            Object.Destroy(image);
        }
    }
}
