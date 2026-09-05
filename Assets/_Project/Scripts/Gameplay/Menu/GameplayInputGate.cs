using UnityEngine;

namespace Game.Gameplay.Menu
{
    /// <summary>
    /// 玩法输入门控（Phase A，静态单一真相）：表达「本地玩家此刻是否允许采样游戏输入」。
    /// 原因独立可叠加：菜单打开 / 硬锁（终局、场景切换）/ 本地死亡 / 关闭菜单后的恢复宽限帧。
    /// InputReader 只读本类决定是否采样（MenuOpen 时输出全零快照并清内部意图）；
    /// PlayerNetworkAdapter 经 InputBlocked 跳过命令上报与战斗请求（菜单内不发新玩家命令）；
    /// 光标状态由 GameplayMenuController 统一拥有，本类不碰光标。
    /// 菜单绝不写 Time.timeScale（网络游戏不暂停）——本类无任何时间缩放入口（红线自检）。
    /// </summary>
    public static class GameplayInputGate
    {
        private static bool _menuOpen;
        private static bool _hardLocked;
        private static bool _dead;
        private static int _resumeGraceFrames;

        /// <summary>菜单是否打开（InputReader 据此清 ADS 切换意图等内部态）。</summary>
        public static bool MenuOpen => _menuOpen;

        /// <summary>终局/场景切换硬锁是否生效。</summary>
        public static bool HardLocked => _hardLocked;

        /// <summary>本地玩家是否死亡（门控来源；表现冻结仍由既有链路处理）。</summary>
        public static bool Dead => _dead;

        /// <summary>恢复宽限帧剩余数（关闭菜单后短暂屏蔽，防止「返回游戏」的点击误开火/误视角）。</summary>
        public static int ResumeGraceFrames => _resumeGraceFrames;

        /// <summary>游戏输入是否被禁止（任一原因命中即为 true）。</summary>
        public static bool InputBlocked => _menuOpen || _hardLocked || _dead || _resumeGraceFrames > 0;

        /// <summary>菜单开合（GameplayMenuController 调用；开=屏蔽输入+解锁光标由控制器做）。</summary>
        public static void SetMenuOpen(bool open)
        {
            if (_menuOpen == open) return;
            _menuOpen = open;
            if (!open)
                _resumeGraceFrames = 1; // 关闭后下一帧才恢复采样（同帧点击不再进入游戏输入）
        }

        /// <summary>硬锁（终局/场景切换）。锁死后即便「关闭菜单」也保持屏蔽，直到显式 Reset。</summary>
        public static void SetHardLocked(bool locked)
        {
            _hardLocked = locked;
            if (locked) _menuOpen = false;
        }

        /// <summary>本地死亡状态（控制器每帧镜像；死亡时强制关菜单+屏蔽输入）。</summary>
        public static void SetDead(bool dead) => _dead = dead;

        /// <summary>手动延长宽限（测试用；常规流程 SetMenuOpen(false) 自带 1 帧）。</summary>
        public static void GrantResumeGrace(int frames) => _resumeGraceFrames = Mathf.Max(_resumeGraceFrames, frames);

        /// <summary>宽限帧递减（控制器 Update 每帧调用；仅消耗宽限，不影响其他原因）。</summary>
        public static void TickFrame()
        {
            if (_resumeGraceFrames > 0) _resumeGraceFrames--;
        }

        /// <summary>全部原因复位（场景卸载清理与 EditMode 测试共用；不碰 Time.timeScale——本类从未写过它）。</summary>
        public static void ResetAll()
        {
            _menuOpen = false;
            _hardLocked = false;
            _dead = false;
            _resumeGraceFrames = 0;
        }
    }
}
