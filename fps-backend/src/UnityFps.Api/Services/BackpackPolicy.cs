using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

/// <summary>
/// CF 三背包策略（2026-09-30 背包系统 Phase A，唯一真相——LoadoutService/AuthService/
/// ServerInstanceService 共用，禁止各自实现）：
/// ① 每用户 3 行 PlayerLoadout（BackpackIndex 0/1/2）；背包 0 注册即建行，背包 1/2 懒创建——
///    读取（GET/登录会话/票据 consume）按本类合成默认，首次 PUT 落库；
/// ② 缺失背包默认值：武器拷贝背包 0（背包 0 也缺则回系统默认 m4+service_pistol）+ 默认投掷物
///    + 空附件 + Version=1；
/// ③ 投掷物槽（本轮解冻）：ThrowableId 对应 CatalogItem ItemType/SlotType="Throwable"，
///    null = 不带雷；
/// ④ ActiveIndex 本轮恒 0（进入对局固定背包 1）。
/// </summary>
public static class BackpackPolicy
{
    public const int BackpackCount = 3;
    public const int DefaultActiveIndex = 0;
    public const string DefaultThrowableItemId = "throwable.standard";
    public const string ThrowableSlotType = "Throwable";

    /// <summary>?backpack= 查询参数（1..3）→ 内部下标（0..2）；null/缺省 = 1（背包 1）。越界由调用方 400。</summary>
    public static bool TryNormalizeParam(int? backpack, out int index)
    {
        index = (backpack ?? 1) - 1;
        return index >= 0 && index < BackpackCount;
    }

    /// <summary>由用户现有配装行构建恒长 3 的背包集合（缺失行按策略合成，不落库）。</summary>
    public static LoadoutDto[] BuildBackpackSet(IEnumerable<PlayerLoadout> rows)
    {
        var byIndex = new Dictionary<int, PlayerLoadout>();
        foreach (var row in rows)
            if (row.BackpackIndex >= 0 && row.BackpackIndex < BackpackCount && !byIndex.ContainsKey(row.BackpackIndex))
                byIndex[row.BackpackIndex] = row;

        var result = new LoadoutDto[BackpackCount];
        for (var i = 0; i < BackpackCount; i++)
            result[i] = byIndex.TryGetValue(i, out var row) ? row.ToDto() : SynthesizeDefault(i, byIndex).ToDto();
        return result;
    }

    /// <summary>缺失背包的内存合成（不写库）：武器拷贝背包 0（无背包 0 回系统默认），附件空，Version=1。</summary>
    private static PlayerLoadout SynthesizeDefault(int index, IReadOnlyDictionary<int, PlayerLoadout> byIndex)
    {
        byIndex.TryGetValue(0, out var zero);
        return new PlayerLoadout
        {
            BackpackIndex = index,
            PrimaryWeaponId = zero?.PrimaryWeaponId ?? "weapon.m4",
            SecondaryWeaponId = zero?.SecondaryWeaponId ?? "weapon.service_pistol",
            ThrowableId = zero is null ? DefaultThrowableItemId : zero.ThrowableId,
            Throwables = Enumerable.Range(0, 3).Select(i => new PlayerLoadoutThrowable { SlotIndex = i, ItemId = (zero is null ? ThrowableSlotPolicy.FromLegacy(DefaultThrowableItemId) : ThrowableSlotPolicy.Read(zero))[i] }).ToList(),
            Version = 1,
            UpdatedAtUtc = DateTime.UtcNow,
        };
    }
}
