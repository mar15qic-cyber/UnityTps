using UnityEngine;

namespace Game.Gameplay.Combat
{
    /// <summary>
    /// 投掷物配包映射（2026-09-30 Phase C）：后端目录 throwableId → 每生命三类投掷物数量。
    /// 服务器权威消费（ThrowableController.ServerApplyLoadout）；空串/null = 不带雷。
    /// JsonUtility 把 JSON null 解析为空串——一律按 IsNullOrEmpty 归一。
    /// 未知 id：告警 + 零雷（不猜——宁缺勿错，玩家可回大厅修配装）。
    /// </summary>
    public static class ThrowableLoadoutPolicy
    {
        public const string StandardItemId = "throwable.standard";
        public const string FragAssaultItemId = "throwable.frag_assault";

        /// <summary>解析配包（纯函数）。返回 false = 空/未知（零雷）。counts 顺序 = [Frag, Flash, Smoke]。</summary>
        public static bool TryResolve(string throwableItemId, ThrowableCatalog catalog, int[] counts, Object logContext = null)
        {
            for (int i = 0; i < 3; i++) counts[i] = 0;
            if (catalog == null || string.IsNullOrEmpty(throwableItemId)) return false;

            switch (throwableItemId)
            {
                case StandardItemId:
                    // 标准投掷包 = 原版全局默认手感的配装化（保持既有平衡）
                    counts[(int)ThrowableType.Frag] = catalog.Frag != null ? catalog.Frag.InitialCount : 0;
                    counts[(int)ThrowableType.Flash] = catalog.Flash != null ? catalog.Flash.InitialCount : 0;
                    counts[(int)ThrowableType.Smoke] = catalog.Smoke != null ? catalog.Smoke.InitialCount : 0;
                    return true;
                case FragAssaultItemId:
                    // 突击破片包：全破片，牺牲闪光/烟雾
                    counts[(int)ThrowableType.Frag] = 3;
                    counts[(int)ThrowableType.Flash] = 0;
                    counts[(int)ThrowableType.Smoke] = 0;
                    return true;
                default:
                    Debug.LogWarning($"[ThrowableLoadoutPolicy] 未知投掷配包：{throwableItemId}——按零雷处理（玩家可回大厅修配装）", logContext);
                    return false;
            }
        }
    }
}
