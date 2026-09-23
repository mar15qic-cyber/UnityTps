using System;
namespace Game.Gameplay.Network
{
    public static class MapContentIdentity
    {
        public static string ClientHash { get; set; } = "";
        public static bool Matches(string expected, string actual) => string.Equals(expected ?? "", actual ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
