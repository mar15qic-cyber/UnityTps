using System.Reflection;
using Game.Account;
using Game.Gameplay.Network;
using Game.UI.Chat;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 审计 2026-09-16 §7.5/§7.6 定向回归：聊天完整对象生命周期。
    /// 旧缺陷链：StopAndClear 只清数据不卸载 Canvas 直挂的 ChatHud → 窗口残留到大厅/登录页且
    /// Enter 可重新打开；EnsureRunning 每次 += 视图事件 → 重复入房后一次发送多次请求；
    /// Instance==null 时静态 ChatRoomSession 不清。
    /// </summary>
    public sealed class ChatLifecycleTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly System.Collections.Generic.List<Object> _spawned = new();

        [TearDown]
        public void TearDown()
        {
            ChatController.StopAndClear(); // 兜底：清静态实例与会话，防跨用例泄漏
            foreach (var obj in _spawned)
                if (obj != null) Object.DestroyImmediate(obj);
            _spawned.Clear();
        }

        private Canvas Canvas(string name)
        {
            var go = new GameObject(name, typeof(Canvas));
            _spawned.Add(go);
            return go.GetComponent<Canvas>();
        }

        private ChatController Mount(Canvas canvas)
            => ChatController.EnsureRunning(canvas, null, new AccountSession());

        private static ChatHudView View(Canvas canvas)
            => canvas.GetComponentInChildren<ChatHudView>(true);

        private static int HistoryCount(ChatHudView view)
            => ((System.Collections.Generic.List<ChatClientMessage>)typeof(ChatHudView)
                .GetField("_history", NonPublic).GetValue(view)).Count;

        /// <summary>用反射触发事件背板字段：验证控制器侧订阅数量（重复 += 会推多次）。</summary>
        private static void RaiseSendRequested(ChatHudView view)
        {
            var field = typeof(ChatHudView).GetField("SendRequested", NonPublic);
            var handler = (System.Action<string, string>)field.GetValue(view);
            handler?.Invoke(ChatRules.ChannelAll, "hello");
        }

        /// <summary>§5.3-4：挂载只认目标画布——已挂在 A 画布的实例不得被 B 画布的挂载复用；
        /// 旧实现 FindFirstObjectByType 全场找，任何画布的旧 Hud 都会"挂载成功"。</summary>
        [Test]
        public void EnsureRunning_IsCanvasScoped_RemountsOnTargetCanvas()
        {
            var canvasA = Canvas("CanvasA");
            var canvasB = Canvas("CanvasB");
            var session = new AccountSession();

            ChatController.EnsureRunning(canvasA, null, session);
            var viewA = View(canvasA);
            Assert.That(viewA, Is.Not.Null);

            ChatController.EnsureRunning(canvasB, null, session);

            var viewB = View(canvasB);
            Assert.That(viewB, Is.Not.Null, "目标画布必须挂上新实例");
            Assert.That(viewB.gameObject.activeSelf, Is.True);
            Assert.That(View(canvasA), Is.Null, "旧画布实例必须被真正卸载");
            Assert.That(ChatController.Instance, Is.Not.Null);
        }

        [Test]
        public void StopAndClear_UnmountsView_ClearsInstanceAndStaticSession()
        {
            var canvas = Canvas("Canvas");
            var session = new AccountSession();
            ChatRoomSession.EnsureRoom("ROOM1");
            ChatRoomSession.TryRegister(new ChatClientMessage { Body = "hi", Channel = ChatRules.ChannelAll }, "H:1");
            var controller = Mount(canvas);

            ChatController.StopAndClear();

            Assert.That(ChatController.Instance, Is.Null, "静态实例必须清空");
            Assert.That(View(canvas), Is.Null, "聊天根必须被真正销毁（旧实现只清数据，Hud 留在场外）");
            Assert.That(ChatRoomSession.RoomCode, Is.Empty, "退房必须清静态会话（旧实现无实例时直接 return）");
            Assert.That(ChatRoomSession.Messages.Count, Is.EqualTo(0));
        }

        [Test]
        public void StopKeepSession_Unmounts_ButKeepsRoomHistory()
        {
            var canvas = Canvas("Canvas");
            ChatRoomSession.EnsureRoom("ROOM2");
            ChatRoomSession.TryRegister(new ChatClientMessage { Body = "keep", Channel = ChatRules.ChannelAll }, "H:2");
            Mount(canvas);

            ChatController.StopKeepSession();

            Assert.That(ChatController.Instance, Is.Null);
            Assert.That(ChatRoomSession.RoomCode, Is.EqualTo("ROOM2"), "留房跨页/跨场景必须保留历史");
            Assert.That(ChatRoomSession.Messages.Count, Is.EqualTo(1), "返房补水语义：同房历史不丢");
        }

        /// <summary>§7.5：反复进入退出后只保留一份 SendRequested 订阅——
        /// 旧实现每次 EnsureRunning 都 +=，一次发送会推多条系统提示/多个请求。</summary>
        [Test]
        public void RepeatedMount_BindsViewEventsExactlyOnce()
        {
            var canvas = Canvas("Canvas");
            var session = new AccountSession();

            Mount(canvas);
            Mount(canvas);
            Mount(canvas);

            var view = View(canvas);
            RaiseSendRequested(view);

            Assert.That(HistoryCount(view), Is.EqualTo(1),
                "三次挂载后一次 SendRequested 只能产生一条本地提示（= 只有一份控制器订阅）");
        }

        [Test]
        public void StopAndClear_IsIdempotent_AndSafeWithoutInstance()
        {
            ChatController.StopAndClear(); // 无实例：也必须清静态会话（旧实现直接 return）
            Assert.That(ChatRoomSession.RoomCode, Is.Empty);

            var canvas = Canvas("Canvas");
            Mount(canvas);
            ChatController.StopAndClear();
            ChatController.StopAndClear(); // 第二次：无异常、状态不变
            Assert.That(ChatController.Instance, Is.Null);
        }

        /// <summary>§7.6：Instance 为空时 Stop 也清静态历史；换房=新会话（旧游标/去重不串房）。</summary>
        [Test]
        public void RoomSwitch_StartsFreshSession()
        {
            ChatRoomSession.EnsureRoom("ROOM_A");
            ChatRoomSession.TryRegister(new ChatClientMessage { Body = "a", Channel = ChatRules.ChannelAll }, "H:A1");
            ChatRoomSession.HttpCursor = 5;

            ChatRoomSession.EnsureRoom("ROOM_B");

            Assert.That(ChatRoomSession.Messages.Count, Is.EqualTo(0), "换房必须清历史");
            Assert.That(ChatRoomSession.HttpCursor, Is.EqualTo(0ul), "换房必须清游标（旧房游标不得带入新房）");
        }

        /// <summary>§5.3-6：上下文闸门关闭后展开窗收起、焦点释放（Enter 闸门在 Update 内，
        /// 真实键盘路径由实机验证；本用例锁状态机语义）。</summary>
        [Test]
        public void SetInteractiveFalse_ReleasesFocus_AndClosesWindow()
        {
            var canvas = Canvas("Canvas");
            var controller = Mount(canvas);
            var view = View(canvas);

            view.SetInteractive(false);

            Assert.That(view.IsOpen, Is.False, "闸门关闭必须收起展开窗");
            Assert.That(Game.Gameplay.Menu.GameplayInputGate.ChatFocused, Is.False, "不得继续占用聊天输入焦点");
            Assert.That(controller, Is.Not.Null);
        }
    }
}
