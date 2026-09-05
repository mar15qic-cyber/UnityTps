using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 激光指示器光束（2026-09-05 用户需求）：腰射判定纯函数 + 装配挂载规则——
    /// 激光配件（WeaponAttachmentView.LaserItemId）仅在 laserBeamEnabled=true 的装配
    /// （本地第一人称视图）上挂 LaserSightBeam；TP/预览/校准路径（false）与普通配件不挂。
    /// 光束端点取主相机屏幕中心命中点（与准星同源语义），运行时行为属实机验收。
    /// </summary>
    public sealed class LaserSightBeamTests
    {
        private GameObject _root;
        private GameObject _laserPrefab;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_laserPrefab != null) Object.DestroyImmediate(_laserPrefab);
        }

        private (WeaponAttachmentView view, Transform tacticalSocket) BuildFakeWeapon()
        {
            _root = new GameObject("FakeWeapon");
            var view = _root.AddComponent<WeaponAttachmentView>();

            var socketGo = new GameObject("Attach_Tactical");
            socketGo.transform.SetParent(_root.transform, false);
            var socket = socketGo.AddComponent<AttachmentSocket>();
            var slotField = typeof(AttachmentSocket).GetField("slot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            slotField.SetValue(socket, AttachmentSlotType.Tactical);
            return (view, socketGo.transform);
        }

        private AttachmentAssetEntry MakeLaserEntry()
        {
            if (_laserPrefab == null)
            {
                _laserPrefab = new GameObject("Laser_01");
                new GameObject("Emitter").transform.SetParent(_laserPrefab.transform, false);
            }
            return new AttachmentAssetEntry
            {
                itemId = WeaponAttachmentView.LaserItemId,
                slot = AttachmentSlotType.Tactical,
                prefab = _laserPrefab,
            };
        }

        // ---- 纯函数：腰射判定 ----

        [Test]
        public void ShouldBeam_HipFireTrue_AdsFalse()
        {
            Assert.That(LaserSightBeam.ShouldBeam(0f), Is.True, "纯腰射出束");
            Assert.That(LaserSightBeam.ShouldBeam(0.49f), Is.True, "开镜过渡前半程仍算腰射");
            Assert.That(LaserSightBeam.ShouldBeam(LaserSightBeam.AimGateAds01), Is.False, "过半即收束");
            Assert.That(LaserSightBeam.ShouldBeam(1f), Is.False, "完全开镜不出束");
        }

        // ---- 装配挂载规则 ----

        [Test]
        public void Apply_LaserOnFirstPerson_MountsBeamComponent()
        {
            var (view, _) = BuildFakeWeapon();
            view.ApplyAttachments(null, "weapon.test", new[] { MakeLaserEntry() }, laserBeamEnabled: true);

            Assert.That(view.Spawned.Count, Is.EqualTo(1));
            var beam = view.Spawned[0].GetComponent<LaserSightBeam>();
            Assert.That(beam, Is.Not.Null, "本地第一人称装配激光必须挂光束组件");
            var line = view.Spawned[0].GetComponentInChildren<LineRenderer>(true);
            Assert.That(line, Is.Not.Null, "光束组件应自带 LineRenderer");
            Assert.That(line.positionCount, Is.EqualTo(2), "光束=起点(激光器)到终点(准星命中点)两段");
            // 双相机渲染语义（实测截图问题）：光束层必须跟随配件克隆（与器件同相机投影，起点贴合；
            // 放 Default 层交世界相机会与武器相机的器件投影拼出视差——起点脱开、远端糊斑）
            Assert.That(line.gameObject.name, Is.EqualTo("LaserBeamLine"));
            Assert.That(line.gameObject.layer, Is.EqualTo(view.Spawned[0].layer), "光束层=配件克隆层（同相机渲染）");
        }

        [Test]
        public void Apply_LaserOnNonFirstPerson_NoBeam()
        {
            var (view, _) = BuildFakeWeapon();
            view.ApplyAttachments(null, "weapon.test", new[] { MakeLaserEntry() }, laserBeamEnabled: false);

            Assert.That(view.Spawned.Count, Is.EqualTo(1), "配件模型仍应挂载（他人可见激光器本体）");
            Assert.That(view.Spawned[0].GetComponent<LaserSightBeam>(), Is.Null,
                "TP/预览/校准路径不得挂光束（射线只服务本地瞄准）");
            Assert.That(view.Spawned[0].GetComponent<LineRenderer>(), Is.Null);
        }

        [Test]
        public void Apply_NonLaserAttachment_NeverMountsBeam()
        {
            var (view, _) = BuildFakeWeapon();
            view.ApplyAttachments(null, "weapon.test", new[]
            {
                new AttachmentAssetEntry { itemId = "attach.lpw.grip.01", slot = AttachmentSlotType.Tactical, prefab = _laserPrefab != null ? _laserPrefab : new GameObject("Grip_01") },
            }, laserBeamEnabled: true);

            Assert.That(view.Spawned.Count, Is.EqualTo(1), "假枪挂点为 Tactical：非激光配件应正常挂载");
            Assert.That(view.Spawned[0].GetComponent<LaserSightBeam>(), Is.Null, "非激光配件永不挂束");
        }

        [Test]
        public void DeviceAxis_FollowsSocketNegativeX()
        {
            // 指向语义（用户二次定案）：光束沿挂点 -X 前向（枪身实际指向），不吸附准星——
            // 换弹/切枪枪身倾斜时光束随枪走（真实 FPS 语义）
            var clone = new GameObject("Clone");
            var socket = new GameObject("Socket");
            socket.transform.SetParent(null); // 根级挂点
            socket.transform.rotation = Quaternion.Euler(0f, 90f, 0f); // 挂点朝向 Y90
            clone.transform.SetParent(socket.transform, false);

            var axis = LaserSightBeam.DeviceAxis(clone.transform);
            Assert.That(axis.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(axis.y, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(axis.z, Is.EqualTo(1f).Within(0.0001f), "Y90 挂点的 -X 前向 = 世界 +Z");

            Object.DestroyImmediate(socket);
            Object.DestroyImmediate(clone);
        }

        [Test]
        public void DeviceAxis_NullParent_FallsBackForward()
        {
            var clone = new GameObject("OrphanClone");
            Assert.That(LaserSightBeam.DeviceAxis(clone.transform), Is.EqualTo(Vector3.forward));
            Object.DestroyImmediate(clone);
        }

        [Test]
        public void Reapply_Laser_MountsExactlyOneBeam()
        {
            var (view, _) = BuildFakeWeapon();
            view.ApplyAttachments(null, "weapon.test", new[] { MakeLaserEntry() }, laserBeamEnabled: true);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("Destroy may not be called from edit mode"));
            view.ApplyAttachments(null, "weapon.test", new[] { MakeLaserEntry() }, laserBeamEnabled: true);

            Assert.That(view.Spawned.Count, Is.EqualTo(1));
            var beams = view.Spawned[0].GetComponents<LaserSightBeam>();
            Assert.That(beams.Length, Is.EqualTo(1), "换装重挂后光束组件恰好一份（旧克隆随 Clear 销毁）");
        }
    }
}
