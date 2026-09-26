using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class TPWeaponMountTests
    {
        [Test]
        public void AllSixteenProductionWeapons_HaveARealHandMountAndForwardMuzzle()
        {
            const string folder = "Assets/_Project/Prefabs/Weapons/";
            var rifle = AssetDatabase.LoadAssetAtPath<GameObject>(folder + "TP_Weapon_AssaultRifle_01.prefab");
            Assert.That(rifle, Is.Not.Null);
            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder.TrimEnd('/') }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!System.IO.Path.GetFileName(path).StartsWith("TP_Weapon_")) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(prefab, Is.Not.Null, path);
                count++;
                if (!prefab.name.Contains("Handgun"))
                {
                    Assert.That(Vector3.Distance(prefab.transform.localPosition, rifle.transform.localPosition),
                        Is.LessThan(.002f), path + " has a missing root position override");
                    Assert.That(Quaternion.Angle(prefab.transform.localRotation, rifle.transform.localRotation),
                        Is.LessThan(1f), path + " has a missing root rotation override");
                }
                var muzzle = prefab.transform.Find("Muzzle");
                Assert.That(muzzle, Is.Not.Null, path + " lacks TP muzzle");
                Assert.That(Vector3.Dot(prefab.transform.forward, muzzle.forward), Is.GreaterThan(.98f), path);
            }
            Assert.That(count, Is.EqualTo(16));
        }
    }
}
