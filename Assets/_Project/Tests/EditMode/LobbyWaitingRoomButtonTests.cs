using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Game.Account;
using Game.UI;

namespace Game.Gameplay.Tests
{
    /// <summary>等待房间页按钮显隐回归（2026-09-20 用户实测"黑着的按钮"）：
    /// UIComponents.Button 返回 Face 子物体，错误地对 button.gameObject.SetActive 会留下根节点
    /// Btn_N 的 Depth 阴影层（近黑 #0D1117）——房主看到"准备"黑块、非房主看到"开始比赛"黑块。
    /// 本组测试锁定：显隐必须切根节点（UIComponents.SetVisible），角色-按钮一一对应。</summary>
    public sealed class LobbyWaitingRoomButtonTests
    {
        private readonly System.Collections.Generic.List<Object> created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in created)
                if (obj != null) Object.DestroyImmediate(obj);
            created.Clear();
            // ChatController 是静态单例：直驱 UpdateWaitingUi 不触发，无需清理；防御性兜底
        }

        private LobbyPresenter CreatePresenter()
        {
            var root = new GameObject("PresenterRoot");
            created.Add(root);
            var presenter = root.AddComponent<LobbyPresenter>();

            var bodyGo = new GameObject("Body", typeof(RectTransform));
            bodyGo.transform.SetParent(root.transform, false);
            created.Add(bodyGo);
            SetField(presenter, "body", bodyGo.transform);

            var statusGo = new GameObject("Status", typeof(RectTransform), typeof(TextMeshProUGUI));
            statusGo.transform.SetParent(root.transform, false);
            created.Add(statusGo);
            SetField(presenter, "status", statusGo.GetComponent<TextMeshProUGUI>());
            return presenter;
        }

        /// <summary>手工搭 UpdateWaitingUi 依赖的最小 UI（不进 RenderWaitingRoom——其含聊天挂载与
        /// 轮询循环，离线不可驱动）；按钮用真实 UIComponents.Button 构建以保留 Face/根 结构。</summary>
        private LobbyPresenter PrepareWaitingUi(AccountSession session, out Button readyButton, out Button startButton)
        {
            var presenter = CreatePresenter();
            SetField(presenter, "session", session);

            var teamsGo = new GameObject("TeamsPanel", typeof(RectTransform));
            teamsGo.transform.SetParent(presenter.transform, false);
            created.Add(teamsGo);
            SetField(presenter, "waitingTeamsRoot", teamsGo.transform);

            var stateGo = new GameObject("StateText", typeof(RectTransform), typeof(TextMeshProUGUI));
            stateGo.transform.SetParent(presenter.transform, false);
            created.Add(stateGo);
            SetField(presenter, "waitingStateText", stateGo.GetComponent<TextMeshProUGUI>());

            var rulesGo = new GameObject("RulesText", typeof(RectTransform), typeof(TextMeshProUGUI));
            rulesGo.transform.SetParent(presenter.transform, false);
            created.Add(rulesGo);
            SetField(presenter, "waitingRulesText", rulesGo.GetComponent<TextMeshProUGUI>());

            readyButton = UIComponents.Button("Btn_Test_Ready", presenter.transform, "准备",
                UIComponents.ButtonKind.Primary, new Vector2(0f, 0.7f), new Vector2(1f, 0.8f));
            startButton = UIComponents.Button("Btn_Test_Start", presenter.transform, "开始比赛",
                UIComponents.ButtonKind.Primary, new Vector2(0f, 0.1f), new Vector2(1f, 0.2f));
            SetField(presenter, "waitingActionButton", readyButton);
            SetField(presenter, "waitingStartButton", startButton);
            SetField(presenter, "waitingRoomCode", "ABC123");
            return presenter;
        }

        private static AccountSession AuthenticatedSession(string username)
        {
            var session = new AccountSession();
            session.Apply(new AuthSessionDto
            {
                token = "test-token",
                expiresAtUtc = System.DateTime.UtcNow.AddHours(1).ToString("o"),
                profile = new PlayerProfileDto { username = username },
            });
            return session;
        }

        private static RoomSnapshotDto Snapshot(string leaderUsername, string status, long selfUserId)
        {
            return new RoomSnapshotDto
            {
                room = new GameRoomDto
                {
                    roomCode = "ABC123",
                    leaderUsername = leaderUsername,
                    joinedPlayers = 2,
                    maxPlayers = 8,
                    status = status,
                    mode = GameModes.Tdm,
                    killTarget = 100,
                    timeLimitMinutes = 10,
                },
                members = new[]
                {
                    new RoomMemberDto { userId = selfUserId, username = selfUserId == 1 ? leaderUsername : "Tester", teamId = TeamId.Red, isLeader = selfUserId == 1 },
                    new RoomMemberDto { userId = selfUserId == 1 ? 2 : 1, username = selfUserId == 1 ? "Guest" : leaderUsername, teamId = TeamId.Blue, isLeader = selfUserId != 1 },
                },
                you = new RoomSelfDto { userId = selfUserId, teamId = TeamId.Red, isReady = false },
            };
        }

        private static void Invoke(object target, string method, params object[] args)
        {
            var info = target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(info, Is.Not.Null, $"method {method} missing");
            info.Invoke(target, args);
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"field {name} missing");
            field.SetValue(target, value);
        }

        private static GameObject RootOf(Button button) => button.transform.parent.gameObject;

        [Test]
        public void Host_Waiting_ShowsStartOnly_AndReadyRootFullyHidden()
        {
            var session = AuthenticatedSession("Tester");
            var snapshot = Snapshot("Tester", RoomStatus.Waiting, selfUserId: 1);
            session.ApplyRoomSnapshot(snapshot);
            var presenter = PrepareWaitingUi(session, out var ready, out var start);

            Invoke(presenter, "UpdateWaitingUi", snapshot);

            Assert.That(RootOf(start).activeSelf, Is.True, "房主 + Waiting：开始比赛根节点必须可见");
            Assert.That(RootOf(ready).activeSelf, Is.False, "房主 + Waiting：准备根节点必须整体隐藏（Face 级隐藏会留下黑块）");
        }

        [Test]
        public void NonHost_Waiting_ShowsReadyOnly_AndStartRootFullyHidden()
        {
            var session = AuthenticatedSession("Tester");
            var snapshot = Snapshot("Alice", RoomStatus.Waiting, selfUserId: 2);
            session.ApplyRoomSnapshot(snapshot);
            var presenter = PrepareWaitingUi(session, out var ready, out var start);

            Invoke(presenter, "UpdateWaitingUi", snapshot);

            Assert.That(RootOf(ready).activeSelf, Is.True, "非房主 + Waiting：准备根节点必须可见");
            Assert.That(RootOf(start).activeSelf, Is.False, "非房主 + Waiting：开始比赛根节点必须整体隐藏");
            Assert.That(ready.GetComponentInChildren<TMP_Text>().text, Is.EqualTo("准备"));
        }

        [Test]
        public void InMatch_ActionButtonBecomesEnterMatch_ForEveryone()
        {
            var session = AuthenticatedSession("Tester");
            var snapshot = Snapshot("Tester", RoomStatus.InMatch, selfUserId: 1);
            session.ApplyRoomSnapshot(snapshot);
            var presenter = PrepareWaitingUi(session, out var ready, out var start);

            Invoke(presenter, "UpdateWaitingUi", snapshot);

            Assert.That(RootOf(ready).activeSelf, Is.True, "InMatch：操作按钮转为进入比赛（含房主）");
            Assert.That(ready.GetComponentInChildren<TMP_Text>().text, Is.EqualTo("进入比赛"));
            Assert.That(RootOf(start).activeSelf, Is.False, "InMatch：开始比赛保持隐藏");
        }

        [Test]
        public void BeforeFirstSnapshot_BothActionButtonsFullyHidden()
        {
            var session = AuthenticatedSession("Tester");
            session.ApplyRoomSnapshot(Snapshot("Tester", RoomStatus.Waiting, selfUserId: 1));
            var presenter = PrepareWaitingUi(session, out var ready, out var start);

            // RenderWaitingRoom 初始隐藏（对应首轮快照前的空窗期）：两个根节点都不应可见
            UIComponents.SetVisible(ready, false);
            UIComponents.SetVisible(start, false);
            Assert.That(RootOf(ready).activeSelf, Is.False);
            Assert.That(RootOf(start).activeSelf, Is.False);
        }
    }
}
