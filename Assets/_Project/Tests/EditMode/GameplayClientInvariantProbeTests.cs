using Game.Gameplay.Network;
using Game.Gameplay.Player;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Gate A-1（2026-09-08 P0 追加复审 §1.1）定向测试：探针 CollectEnabled 必须按
    /// Behaviour.isActiveAndEnabled 计数——远端玩家的 InputReader 是「激活对象上的禁用
    /// 组件」，FindObjectsInactive.Exclude 只按对象激活态过滤，只查 activeInHierarchy 会把它
    /// 误计为第二个 InputReader（正常双人局误报 VIOLATION，Gate B 无法可信执行）。
    /// Camera / AudioListener / Owner Adapter 同理。
    /// 断言采用「禁用→计数不变；启用→计数 +1」的增量语义：对编辑器当前打开场景的既有
    /// 相机/监听器免疫（EditMode 测试运行在用户当前场景，环境组件不可假设为空）。
    /// </summary>
    public sealed class GameplayClientInvariantProbeTests
    {
        private readonly System.Collections.Generic.List<Object> _created = new System.Collections.Generic.List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var created in _created)
                if (created != null) Object.DestroyImmediate(created);
            _created.Clear();
        }

        private T Create<T>(string name, bool enabledComponent) where T : Behaviour
        {
            var go = new GameObject(name);
            var behaviour = go.AddComponent<T>();
            behaviour.enabled = enabledComponent;
            _created.Add(go);
            return behaviour;
        }

        private Camera CreateMainCamera(bool enabledComponent)
        {
            var go = new GameObject("ProbeTestMainCamera");
            go.tag = "MainCamera";
            var camera = go.AddComponent<Camera>();
            camera.enabled = enabledComponent;
            _created.Add(go);
            return camera;
        }

        [Test]
        public void EnabledLocalInputReader_PlusDisabledRemoteInputReader_CountsExactlyOne()
        {
            Create<InputReader>("LocalPlayer_Input", enabledComponent: true);
            int countWithLocalOnly = GameplayClientInvariantProbe.Scan().InputReaders;

            Create<InputReader>("RemotePlayer_Input", enabledComponent: false); // 远端：对象激活、组件禁用
            int countWithDisabledRemote = GameplayClientInvariantProbe.Scan().InputReaders;

            Assert.That(countWithDisabledRemote, Is.EqualTo(countWithLocalOnly),
                "激活对象上的禁用 InputReader 不得计入（回归：正常双人局误报 VIOLATION）");

            // 对照组：同一对象启用后必须被计入（防「永远不计」假绿）
            var remote = Create<InputReader>("RemotePlayer_Input_Enabled", enabledComponent: true);
            Assert.That(GameplayClientInvariantProbe.Scan().InputReaders, Is.EqualTo(countWithLocalOnly + 1));
            remote.enabled = false;
        }

        [Test]
        public void DisabledCameraAndListenerComponents_MustNotBeCounted()
        {
            int camerasBefore = GameplayClientInvariantProbe.Scan().MainCameras;
            int listenersBefore = GameplayClientInvariantProbe.Scan().AudioListeners;

            CreateMainCamera(enabledComponent: false);
            Create<AudioListener>("AudioListenerDisabled", enabledComponent: false);

            var scan = GameplayClientInvariantProbe.Scan();
            Assert.That(scan.MainCameras, Is.EqualTo(camerasBefore), "禁用 Camera 组件不得计入");
            Assert.That(scan.AudioListeners, Is.EqualTo(listenersBefore), "禁用 AudioListener 组件不得计入");

            // 对照组：启用后必须计入（防「永远不计」假绿）
            CreateMainCamera(enabledComponent: true);
            Create<AudioListener>("AudioListener", enabledComponent: true);
            var enabled = GameplayClientInvariantProbe.Scan();
            Assert.That(enabled.MainCameras, Is.EqualTo(camerasBefore + 1));
            Assert.That(enabled.AudioListeners, Is.EqualTo(listenersBefore + 1));
        }

        [Test]
        public void DisabledOwnerAdapter_MustNotBeCounted_AsOwnerPlayer()
        {
            int ownersBefore = GameplayClientInvariantProbe.Scan().OwnerPlayers;

            Create<PlayerNetworkAdapter>("OwnerDisabled", enabledComponent: false);
            Assert.That(GameplayClientInvariantProbe.Scan().OwnerPlayers, Is.EqualTo(ownersBefore),
                "禁用的 Owner Adapter 不得计入 ownerPlayers");

            // 对照组：启用后必须计入（防「永远不计」假绿）
            Create<PlayerNetworkAdapter>("OwnerEnabled", enabledComponent: true);
            Assert.That(GameplayClientInvariantProbe.Scan().OwnerPlayers, Is.EqualTo(ownersBefore + 1));
        }
    }
}
