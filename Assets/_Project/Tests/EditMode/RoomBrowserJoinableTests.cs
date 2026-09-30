using System.Reflection;
using Game.Account;
using Game.UI;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>房间浏览器加入谓词锁定（FD-0020，2026-09-29 方案 A）：
    /// r5 实机"对局中房间点击无反应"根因=谓词恒要求 Waiting。后端 InMatch 补人链路
    /// （RoomService.L194-238 roster+签票）完整，谓词只需放行入口：
    /// Waiting/InMatch 且未满员可加入；Starting/Returning/Closed/满员/null 拒绝。
    /// 谓词为 private static（不改可见性惯例），经反射直驱。</summary>
    public sealed class RoomBrowserJoinableTests
    {
        private static bool CanJoin(GameRoomDto room)
        {
            var method = typeof(LobbyPresenter).GetMethod("RoomCanJoin",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method, "LobbyPresenter.RoomCanJoin 谓词不存在（改名会破坏本锁定）");
            return (bool)method.Invoke(null, new object[] { room });
        }

        private static GameRoomDto Room(string status, int joined, int max) => new()
        {
            roomId = 42,
            status = status,
            joinedPlayers = joined,
            maxPlayers = max,
            mode = "TDM",
            mapId = "arena",
            leaderUsername = "host",
        };

        [Test]
        public void Waiting_WithSpace_CanJoin()
        {
            Assert.IsTrue(CanJoin(Room("Waiting", 3, 8)));
        }

        [Test]
        public void Waiting_Full_CannotJoin()
        {
            Assert.IsFalse(CanJoin(Room("Waiting", 8, 8)));
        }

        [Test]
        public void InMatch_WithSpace_CanJoin_LateJoinPlanA()
        {
            // 方案 A 核心：局中补人放行入口——后端负责 roster/队伍容量/票据签发
            Assert.IsTrue(CanJoin(Room("InMatch", 3, 8)));
        }

        [Test]
        public void InMatch_Full_CannotJoin()
        {
            Assert.IsFalse(CanJoin(Room("InMatch", 8, 8)));
        }

        [Test]
        public void Starting_CannotJoin()
        {
            Assert.IsFalse(CanJoin(Room("Starting", 3, 8)));
        }

        [Test]
        public void Returning_CannotJoin()
        {
            Assert.IsFalse(CanJoin(Room("Returning", 3, 8)));
        }

        [Test]
        public void NullRoom_CannotJoin()
        {
            Assert.IsFalse(CanJoin(null));
        }
    }
}
