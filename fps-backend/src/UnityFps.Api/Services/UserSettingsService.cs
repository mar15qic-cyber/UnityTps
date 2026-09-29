using System.Data;
using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

/// <summary>
/// 每玩家设置偏好（2026-09-07）：UserSetting 键值对按用户隔离保存，
/// 键集合开放（键位/音量/灵敏度等，后续新增设置项直接加键，无需迁移）。
/// PUT 为合并语义：仅 upsert 请求中出现的键，未提及的键保持不变——
/// 多端（大厅页/Arena 菜单/未来新设置页）写互不覆盖整份配置。
/// </summary>
public sealed class UserSettingsService(AppDbContext db)
{
    private const int MaxKeysPerRequest = 128;
    private const int MaxKeysPerUser = 128;

    public async Task<UserSettingsDto> GetAsync(long userId, CancellationToken cancellationToken = default)
    {
        var rows = await db.UserSettings.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.SettingKey).Take(MaxKeysPerUser)
            .Select(x => new { x.SettingKey, x.SettingValue })
            .ToListAsync(cancellationToken);
        return new UserSettingsDto(rows.ToDictionary(x => x.SettingKey, x => x.SettingValue, StringComparer.Ordinal));
    }

    public async Task<UserSettingsDto> SaveAsync(long userId, IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        if (values.Count > MaxKeysPerRequest)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "SETTINGS_TOO_MANY",
                $"单次最多保存 {MaxKeysPerRequest} 个设置键");
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 64)
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, "SETTINGS_INVALID_KEY",
                    "设置键必须为 1-64 个字符");
            if (value is null || value.Length > 256)
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, "SETTINGS_INVALID_VALUE",
                    "设置值必须不超过 256 个字符");
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            var result = await SaveValidatedAsync(userId, values, cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    private async Task<UserSettingsDto> SaveValidatedAsync(long userId,
        IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (values.Count > 0)
        {
            var keys = values.Keys.ToList();
            var existing = await db.UserSettings
                .Where(x => x.UserId == userId && keys.Contains(x.SettingKey))
                .ToListAsync(cancellationToken);
            var addedKeys = values.Keys.Count(key => existing.All(row => row.SettingKey != key));
            var totalKeys = await db.UserSettings.CountAsync(x => x.UserId == userId, cancellationToken);
            if (addedKeys > 0 && totalKeys + addedKeys > MaxKeysPerUser)
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, "SETTINGS_TOO_MANY",
                    $"每个账户最多保存 {MaxKeysPerUser} 个设置键");
            var now = DateTime.UtcNow;
            foreach (var (key, value) in values)
            {
                var row = existing.SingleOrDefault(x => x.SettingKey == key);
                if (row is null)
                    db.UserSettings.Add(new UserSetting
                    {
                        UserId = userId, SettingKey = key, SettingValue = value, UpdatedAtUtc = now
                    });
                else
                {
                    row.SettingValue = value;
                    row.UpdatedAtUtc = now;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(userId, cancellationToken);
    }
}
