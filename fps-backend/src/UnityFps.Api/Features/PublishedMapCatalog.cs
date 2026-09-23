using System.Text.Json;
using System.Text.RegularExpressions;
using UnityFps.Api.Data;

namespace UnityFps.Api.Features;

public static class PublishedMapCatalog
{
    public static IReadOnlyList<MapCatalogDto> Read(IReadOnlyList<MapCatalogDto> builtin)
    {
        var path = Environment.GetEnvironmentVariable("FPS_MAP_CATALOG");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return builtin;
        try
        {
            var maps = JsonSerializer.Deserialize<MapCatalogDto[]>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            Validate(maps);
            return builtin.Where(b => !maps.Any(m => m.MapId == b.MapId)).Concat(maps).ToArray();
        }
        catch { throw new InvalidOperationException("Published map catalog is invalid; admission refused."); }
    }
    public static void Validate(IReadOnlyList<MapCatalogDto> maps)
    {
        if (maps.Count > 32 || maps.Select(m => m.MapId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != maps.Count) throw new InvalidDataException("Duplicate maps");
        foreach (var m in maps)
            if (!Regex.IsMatch(m.MapId ?? "", "^[a-z0-9_]{1,32}$") || !Regex.IsMatch(m.SceneName ?? "", "^[a-zA-Z0-9_]{1,80}$")
                || !Regex.IsMatch(m.ContentHash ?? "", "^[a-fA-F0-9]{64}$") || !long.TryParse(m.ContentVersion, out var v) || v <= 0
                || m.Modes == null || m.Modes.Length == 0 || m.Modes.Any(x => !GameModes.All.Contains(x)) || m.MaxCapacity < 2 || m.MaxCapacity > 16)
                throw new InvalidDataException("Invalid map metadata");
    }
}
