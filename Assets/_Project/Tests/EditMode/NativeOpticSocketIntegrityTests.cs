using System.Linq;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests.EditMode
{
    /// <summary>
    /// 阶段 B 原生 16 枪 socket 矩阵：FP/TP 均必须只有一个 Attach_Optic，
    /// 挂在统一父节点，局部 scale 不得引入非 uniform 比例，局部 -X/+Y 轴符合挂点约定。
    /// </summary>
    public sealed class NativeOpticSocketIntegrityTests
    {
        private sealed class SocketSpec
        {
            public readonly string ItemId;
            public readonly string FpPath;
            public readonly string TpPath;

            public SocketSpec(string itemId, string fpPath, string tpPath)
            {
                ItemId = itemId;
                FpPath = fpPath;
                TpPath = tpPath;
            }
        }

        private static readonly SocketSpec[] NativeSpecs =
        {
            new SocketSpec("weapon.m4", "Assets/_Project/Prefabs/Weapons/FP_Rifle_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_01.prefab"),
            new SocketSpec("weapon.ak", "Assets/_Project/Prefabs/Weapons/FP_Rifle02_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_02.prefab"),
            new SocketSpec("weapon.rifle03", "Assets/_Project/Prefabs/Weapons/FP_Rifle03_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_03.prefab"),
            new SocketSpec("weapon.service_pistol", "Assets/_Project/Prefabs/Weapons/FP_ServicePistol_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_01.prefab"),
            new SocketSpec("weapon.handgun02", "Assets/_Project/Prefabs/Weapons/FP_Handgun02_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_02.prefab"),
            new SocketSpec("weapon.handgun03", "Assets/_Project/Prefabs/Weapons/FP_Handgun03_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_03.prefab"),
            new SocketSpec("weapon.handgun04", "Assets/_Project/Prefabs/Weapons/FP_Handgun04_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_04.prefab"),
            new SocketSpec("weapon.smg01", "Assets/_Project/Prefabs/Weapons/FP_SMG01_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_SMG_01.prefab"),
            new SocketSpec("weapon.smg02", "Assets/_Project/Prefabs/Weapons/FP_SMG02_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_SMG_02.prefab"),
            new SocketSpec("weapon.smg03", "Assets/_Project/Prefabs/Weapons/FP_SMG03_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_SMG_03.prefab"),
            new SocketSpec("weapon.smg04", "Assets/_Project/Prefabs/Weapons/FP_SMG04_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_SMG_04.prefab"),
            new SocketSpec("weapon.smg05", "Assets/_Project/Prefabs/Weapons/FP_SMG05_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_SMG_05.prefab"),
            new SocketSpec("weapon.shotgun01", "Assets/_Project/Prefabs/Weapons/FP_Shotgun01_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Shotgun_01.prefab"),
            new SocketSpec("weapon.sniper01", "Assets/_Project/Prefabs/Weapons/FP_Sniper01_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Sniper_01.prefab"),
            new SocketSpec("weapon.sniper02", "Assets/_Project/Prefabs/Weapons/FP_Sniper02_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Sniper_02.prefab"),
            new SocketSpec("weapon.sniper03", "Assets/_Project/Prefabs/Weapons/FP_Sniper03_View.prefab", "Assets/_Project/Prefabs/Weapons/TP_Weapon_Sniper_03.prefab"),
        };

        [Test]
        public void NativeSixteenWeapons_HaveUniqueOpticSocketsWithStableAxes()
        {
            Assert.That(NativeSpecs, Has.Length.EqualTo(16));
            foreach (var spec in NativeSpecs)
            {
                var fp = AssetDatabase.LoadAssetAtPath<GameObject>(spec.FpPath);
                var tp = AssetDatabase.LoadAssetAtPath<GameObject>(spec.TpPath);
                Assert.NotNull(fp, spec.ItemId + " FP prefab");
                Assert.NotNull(tp, spec.ItemId + " TP prefab");

                AssertSocket(spec.ItemId + " FP", fp, "Armature/weapon/Attach_Optic", false);
                AssertSocket(spec.ItemId + " TP", tp, "Attach_Optic", true);
            }
        }

        private static void AssertSocket(string label, GameObject prefab, string socketPath, bool tpRootFrame)
        {
            var all = prefab.GetComponentsInChildren<Transform>(true)
                .Where(t => t != null && t.name == "Attach_Optic").ToArray();
            Assert.That(all, Has.Length.EqualTo(1), label + " Attach_Optic 必须唯一");

            var socket = prefab.transform.Find(socketPath);
            Assert.NotNull(socket, label + " socket parent/path");
            Assert.That(socket.GetComponent<AttachmentSocket>(), Is.Not.Null, label + " AttachmentSocket 组件");
            Assert.That(socket.GetComponent<AttachmentSocket>().Slot, Is.EqualTo(AttachmentSlotType.Optic), label + " slot");
            Assert.That(socket.localScale.x, Is.EqualTo(1f).Within(0.0001f), label + " localScale.x");
            Assert.That(socket.localScale.y, Is.EqualTo(1f).Within(0.0001f), label + " localScale.y");
            Assert.That(socket.localScale.z, Is.EqualTo(1f).Within(0.0001f), label + " localScale.z");
            Assert.That(socket.parent.lossyScale.x, Is.EqualTo(socket.parent.lossyScale.y).Within(0.0001f), label + " parent scale x/y");
            Assert.That(socket.parent.lossyScale.y, Is.EqualTo(socket.parent.lossyScale.z).Within(0.0001f), label + " parent scale y/z");

            // AttachmentSocket 约定：局部 -X 指向枪口，局部 +Y 指向枪械上方。
            // Native TP derivation is authored in the prefab root's +Z/+Y frame;
            // FP derivation is measured in the Armature/weapon bone frame.
            var expectedForward = tpRootFrame
                ? Vector3.forward
                : socket.parent.InverseTransformDirection(Vector3.forward).normalized;
            var expectedUp = tpRootFrame
                ? Vector3.up
                : socket.parent.InverseTransformDirection(Vector3.up).normalized;
            var actualForward = (socket.localRotation * Vector3.left).normalized;
            var actualUp = (socket.localRotation * Vector3.up).normalized;
            Assert.That(Vector3.Dot(actualForward, expectedForward), Is.GreaterThan(0.995f), label + " forward axis");
            Assert.That(Vector3.Dot(actualUp, expectedUp), Is.GreaterThan(0.995f), label + " up axis");
        }
    }
}
