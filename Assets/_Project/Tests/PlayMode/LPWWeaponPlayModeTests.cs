using System.Collections;
using System.Linq;
using Game.Core;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Weapon;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Gameplay.PlayModeTests
{
    public sealed class LPWWeaponPlayModeTests
    {
        [UnityTest]
        public IEnumerator EveryCanonicalWeapon_EquipsFiresEmptiesReloadsAndShowsBothViews()
        {
            var registry = Resources.Load<LPWProductionRuntimeRegistry>("LPWProductionRuntimeRegistry");
            Assert.That(registry, Is.Not.Null);
            var definitions = registry.WeaponAssets.Entries
                .Where(x => x.itemId.StartsWith("weapon.lpw.") && x.definition != null)
                .Select(x => x.definition).ToArray();
            Assert.That(definitions, Has.Length.EqualTo(29));

            foreach (var definition in definitions)
            {
                var itemId = registry.WeaponAssets.Entries.First(x => x.definition == definition).itemId;

                var fp = Object.Instantiate(definition.FirstPersonViewPrefab);
                var tp = Object.Instantiate(definition.ThirdPersonViewPrefab);
                Assert.That(fp.GetComponentsInChildren<Transform>(true).Any(x => x.name == "LPW_Gun"), Is.True, itemId + " FP gun");
                Assert.That(tp.transform.Find("Muzzle"), Is.Not.Null, itemId + " TP muzzle");
                Assert.That(tp.transform.Find("LeftHandTarget"), Is.Not.Null, itemId + " TP hand target");

                var root = new GameObject("Runtime_" + definition.WeaponId);
                var actions = root.AddComponent<ActionSystem>();
                root.AddComponent<CombatResolver>();
                var controller = root.AddComponent<WeaponController>();
                controller.Initialize(definition, registry.Balance);
                yield return null;

                Assert.That(controller.TryFire(), Is.True, itemId + " fire");
                TickRuntime(controller.Runtime, 10f);
                Assert.That(controller.TryReload(), Is.True, itemId + " tactical reload");
                actions.Tick(controller.Stat.ReloadTime + 0.1f);
                Assert.That(controller.Runtime.CurrentAmmo, Is.EqualTo(controller.Runtime.MagazineSize), itemId + " tactical reload complete");

                controller.Initialize(definition, registry.Balance);
                for (var round = 0; round < controller.Runtime.MagazineSize; round++)
                {
                    Assert.That(controller.TryFire(), Is.True, itemId + " empty sequence");
                    TickRuntime(controller.Runtime, 10f);
                }
                Assert.That(controller.TryFire(), Is.False, itemId + " dry fire");
                Assert.That(controller.TryReload(), Is.True, itemId + " empty reload");
                actions.Tick(controller.Stat.ReloadTime + 0.1f);
                Assert.That(controller.Runtime.CurrentAmmo, Is.GreaterThan(0), itemId + " empty reload complete");

                Object.Destroy(root);
                Object.Destroy(fp);
                Object.Destroy(tp);
                yield return null;
            }
        }

        private static void TickRuntime(WeaponRuntime runtime, float seconds)
        {
            var method = typeof(WeaponRuntime).GetMethod("Tick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(runtime, new object[] { seconds });
        }
    }
}
