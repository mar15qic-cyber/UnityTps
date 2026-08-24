namespace Game.Core
{
    /// <summary>
    /// 数值热载接口（方案 §12）：Gameplay 只见此接口，实现方为 HotUpdateManager 的 Lua/JSON 适配器。
    /// 伤害/XP 公式只在服务器端调用；Day10-11 接入实现。
    /// </summary>
    public interface IBalanceConfig
    {
        WeaponStat GetWeaponStat(string weaponId);          // damage/rpm/magSize/reloadTime/spread/adsFov...
        int GetFinalDamage(string weaponId, int upDamageLv); // 伤害公式
        int GetXpForLevel(int level);                         // XP 曲线
        int GetUpgradeCost(string statId, int currentLv);     // 升级消耗
    }

    /// <summary>武器静态数值（来自 Lua/JSON 配置，只读；严禁运行时状态）。</summary>
    [System.Serializable]
    public struct WeaponStat
    {
        public int Damage;
        public int Rpm;
        public int MagSize;
        public int ReserveAmmo;
        public float ReloadTime;
        public float Spread;
        public float MaxRange;
        public float AdsFov;
    }
}
