namespace Game.Gameplay.Combat
{
    public struct ThrowableSlots
    {
        public string Item0, Item1, Item2;
        public int Count0, Count1, Count2;
        public string Item(int slot) => slot == 0 ? Item0 : slot == 1 ? Item1 : slot == 2 ? Item2 : null;
        public int Count(int slot) => slot == 0 ? Count0 : slot == 1 ? Count1 : slot == 2 ? Count2 : 0;
        public void SetCount(int slot, int value)
        { if (slot == 0) Count0 = value; else if (slot == 1) Count1 = value; else if (slot == 2) Count2 = value; }
        public static ThrowableSlots Create(string[] ids, ThrowableCatalog catalog) => new ThrowableSlots
        {
            Item0 = ids != null && ids.Length > 0 ? ids[0] : null,
            Item1 = ids != null && ids.Length > 1 ? ids[1] : null,
            Item2 = ids != null && ids.Length > 2 ? ids[2] : null,
            Count0 = ids != null && ids.Length > 0 && catalog.Get(ids[0]) != null ? 1 : 0,
            Count1 = ids != null && ids.Length > 1 && catalog.Get(ids[1]) != null ? 1 : 0,
            Count2 = ids != null && ids.Length > 2 && catalog.Get(ids[2]) != null ? 1 : 0
        };
        public static string[] Legacy(string id) => id == "throwable.standard"
            ? new[] { "throwable.frag", "throwable.flash", "throwable.smoke" }
            : id == "throwable.frag_assault" ? new[] { "throwable.frag", "throwable.frag", "throwable.frag" }
            : new string[3];
        public static string DefaultId(ThrowableType type) => type == ThrowableType.Frag ? "throwable.frag" : type == ThrowableType.Flash ? "throwable.flash" : "throwable.smoke";
        public static string DisplayName(string id) => id switch
        {
            "throwable.frag" => "破片手雷", "throwable.frag_02" => "破片手雷 II", "throwable.frag_03" => "破片手雷 III",
            "throwable.flash" => "闪光弹", "throwable.smoke" => "烟雾弹", _ => "未装备"
        };
        public static string ViewKey(string id) => id switch
        { "throwable.frag_02" => "Frag2", "throwable.frag_03" => "Frag3", "throwable.flash" => "Flash", "throwable.smoke" => "Smoke", _ => "Frag" };
    }
}
