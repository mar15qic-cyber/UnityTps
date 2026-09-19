using System.Reflection;
using Game.Account;
using Game.Gameplay.Network;
using Game.UI.Chat;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 2026-09-18 实机问题2 定向回归（主流 FPS 聊天交互）：
    /// ① Enter=发送并关闭（旧实现发送后保持展开需 Esc 关）；② 空草稿 Enter=直接关闭；
    /// ③ 发送失败 RestorePendingDraft 带草稿重新打开（Enter 即关后草稿不得沉在不可见输入行）；
    /// ④ 背景板收起态隐藏/展开态显示（左下无常驻大块阴影），淡出语义不变。
    /// </summary>
    public sealed class ChatEnterInteractionTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly System.Collections.Generic.List<Object> _spawned = new();

        [TearDown]
        public void TearDown()
        {
            ChatController.StopAndClear();
            foreach (var obj in _spawned)
                if (obj != null) Object.DestroyImmediate(obj);
            _spawned.Clear();
            Game.Gameplay.Menu.GameplayInputGate.ResetAll();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private ChatHudView MountView()
        {
            var go = new GameObject("ChatCanvas", typeof(Canvas));
            _spawned.Add(go);
            ChatController.EnsureRunning(go.GetComponent<Canvas>(), null, new AccountSession());
            var view = go.GetComponentInChildren<ChatHudView>(true);
            EnsureBuilt(view);
            return view;
        }

        /// <summary>EditMode 不触发 MonoBehaviour 生命周期：反射补齐 OnEnable → Build。</summary>
        private static void EnsureBuilt(ChatHudView view)
        {
            if (typeof(ChatHudView).GetField("_panel", NonPublic).GetValue(view) == null)
                typeof(ChatHudView).GetMethod("OnEnable", NonPublic).Invoke(view, null);
        }

        private static void Invoke(ChatHudView view, string method, params object[] args)
            => typeof(ChatHudView).GetMethod(method, NonPublic).Invoke(view, args);

        private static object Field(ChatHudView view, string field)
            => typeof(ChatHudView).GetField(field, NonPublic).GetValue(view);

        private static void SetField(ChatHudView view, string field, object value)
            => typeof(ChatHudView).GetField(field, NonPublic).SetValue(view, value);

        private static bool PanelImageEnabled(ChatHudView view)
            => ((UnityEngine.UI.Image)Field(view, "_panelImage")).enabled;

        [Test]
        public void OnSubmit_WithText_SendsAndCloses()
        {
            var view = MountView();
            string sent = null;
            view.SendRequested += (_, body) => sent = body;
            Invoke(view, "Open", ChatRules.ChannelAll);
            Assert.That(view.IsOpen, Is.True, "前置：Enter 呼出");
            Assert.That(PanelImageEnabled(view), Is.True, "展开态显示背景板");

            Invoke(view, "OnSubmit", "hello");

            Assert.That(sent, Is.EqualTo("hello"), "正文必须发出");
            Assert.That(view.IsOpen, Is.False, "Enter=发送并关闭（主流 FPS）");
            Assert.That(PanelImageEnabled(view), Is.False, "收起态隐藏背景板");
            // 在途正文由控制器异步收敛（HTTP 同步确认/RPC 回显确认），此处只锁视图侧发出事实
        }

        [Test]
        public void OnSubmit_Empty_ClosesWithoutSend()
        {
            var view = MountView();
            bool anySend = false;
            view.SendRequested += (_, __) => anySend = true;
            Invoke(view, "Open", ChatRules.ChannelAll);

            Invoke(view, "OnSubmit", "   ");

            Assert.That(anySend, Is.False, "空白正文不发送");
            Assert.That(view.IsOpen, Is.False, "空草稿 Enter=直接关闭");
        }

        [Test]
        public void RestorePendingDraft_AfterSendClose_ReopensWithDraft()
        {
            var view = MountView();
            Invoke(view, "Open", ChatRules.ChannelAll);
            Invoke(view, "OnSubmit", "retry me");
            Assert.That(view.IsOpen, Is.False, "前置：发送即关");
            // 发送处于在途（RPC 未确认）的状态由测试直接构造（控制器可能已同步确认）
            SetField(view, "_pendingSend", "retry me");

            view.RestorePendingDraft(); // 发送失败还原

            Assert.That(view.IsOpen, Is.True, "失败还原必须重新打开（否则草稿沉在不可见输入行）");
            var input = (TMPro.TMP_InputField)Field(view, "_input");
            Assert.That(input.text, Is.EqualTo("retry me"), "草稿原样还原");
            Assert.That(Field(view, "_pendingSend"), Is.Null, "在途标记已清");
        }

        [Test]
        public void PanelBackground_HiddenByDefault_VisibleWhenOpen()
        {
            var view = MountView();
            Assert.That(PanelImageEnabled(view), Is.False, "初始收起态：无背景板（无常驻阴影）");
            Invoke(view, "Open", ChatRules.ChannelAll);
            Assert.That(PanelImageEnabled(view), Is.True);
            Invoke(view, "Close", true);
            Assert.That(PanelImageEnabled(view), Is.False, "Esc 关闭后背景板同样隐藏");
        }
    }
}
