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

    /// <summary>武器静态数值（来自 Lua/JSON 配置，只读；严禁运行时状态）。
    /// CP3 起分组嵌套（Docs/13 §6.1）：Recoil/Accuracy/Ballistic 三组；旧平铺 Spread 保留
    /// 兼容读入（迁移期映射 BaseHipSpread，CP4 接线后弃用）。新字段反序列化默认 0，
    /// 由 DemoBalanceConfig.Sanitize 迁移守卫兜底为中性/legacy 值。</summary>
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
        public RecoilProfileData Recoil;
        public AccuracyProfileData Accuracy;
        public BallisticProfileData Ballistic;
    }

    /// <summary>每武器后坐数值（度/米/秒；Docs/13 §6.1 v3）。</summary>
    [System.Serializable]
    public struct RecoilProfileData
    {
        public float PitchDeg;               // 每发垂直后坐基准（度）        Clamp [0.05, 15]
        public float YawDeg;                 // 水平随机半径（度）            Clamp [0, 5]
        public float FirstShotMultiplier;    // 第一枪冲量倍率                Clamp [1, 3]
        public float Accumulation;           // 连射每发累计系数（1=线性）    Clamp [0.5, 2]
        public float MaxAccumulation;        // 累计上限（发数等效）          Clamp [1, 30]
        public float RecoveryDelay;          // 停火恢复延迟（秒）=Burst 衰减门控 + 新 burst 判定阈值 Clamp [0, 1]
        public float RecoverySpeed;          // Burst/瞄准债务恢复速率（发/秒或度/秒） Clamp [0.5, 20]
        public float SpringFrequency;        // AimOffset 弹簧频率（Hz）      Clamp [2, 20]
        public float SpringDamping;          // AimOffset 弹簧阻尼比          Clamp [0.1, 1]
        public float ShakePositionAmplitude; // 纯手感位置脉冲幅度            Clamp [0, 1]
        public float ViewModelKickBack;      // Viewmodel 后移（米）          Clamp [0, 0.2]
        public float ViewModelKickPitch;     // Viewmodel 上抬（度）          Clamp [0, 15]
        public float AdsRecoilMultiplier;    // ADS 后坐总倍率                Clamp [0.1, 1]
    }

    /// <summary>每武器精度数值（度/秒；Docs/13 §6.1 v3）。</summary>
    [System.Serializable]
    public struct AccuracyProfileData
    {
        public float BaseHipSpread;          // 腰射基础散布（度）            Clamp [0, 10]
        public float BaseAdsSpread;          // ADS 基础散布（度）            Clamp [0, BaseHipSpread]
        public float MovementSpreadMax;      // 移动额外散布上限（度）        Clamp [0, 10]
        public float SprintSpreadExtra;      // 冲刺额外散布（度）            Clamp [0, 10]
        public float AirborneSpreadExtra;    // 跳跃/滞空额外散布（度）        Clamp [0, 10]
        public float ShotBloomPerShot;       // 每发 Bloom（度）              Clamp [0, 5]
        public float MaxBloom;               // Bloom 上限（度）              Clamp [0, 10]
        public float BloomRecoveryDelay;     // Bloom 恢复延迟（秒）          Clamp [0, 1]
        public float BloomRecoverySpeed;     // Bloom 衰减速率（度/秒）       Clamp [1, 60]
    }

    /// <summary>每武器弹道数值（Shotgun 多弹丸；Docs/13 §6.1 v3）。</summary>
    [System.Serializable]
    public struct BallisticProfileData
    {
        public int PelletCount;              // 每发弹丸数（默认 1）          Clamp [1, 16]
        public float PelletSpread;           // 弹丸独立散布（度）            Clamp [0, 15]
    }
}
