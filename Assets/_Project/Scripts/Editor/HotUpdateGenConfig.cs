using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using XLua;

namespace Game.EditorTools
{
    /// <summary>
    /// 热更新（xLua 主线，Docs/交接/2026-09-17 热更新计划）互操作面配置：
    /// 修改本文件后必须执行菜单 XLua/Generate Code 重建 Gen 目录，随后 start_compilation_pipeline。
    ///
    /// ★ asmdef 循环约束（2026-09-17 实证）：Gen 产物在 XLua.Runtime 程序集内
    /// （Gen 的 WrapPusher/EnumWrap/PackUnpack/DelegatesGensBridge 是 XLua 核心类型的
    /// partial 扩展，必须与 Src 同程序集），因此 Gen 只能 wrap "XLua.Runtime 可引用"的类型。
    /// Game.UI/Game.Account 与 XLua.Runtime 相互引用会成环 → Game.* 类型一律走
    /// xLua 反射模式（Mono/JIT 下功能完整，Lua 直接 CS.Game.UI.UIComponents 调用）。
    /// 若未来 IL2CPP 化需要 Gen 业务面：把 Lua 调用的 C# 面抽到独立桥程序集
    /// （Game.Hotface，引用 Game.UI 但不引用 XLua），XLua.Runtime 引用该桥——桥自身不使用
    /// XLua 类型即无环。
    ///
    /// 红线：不得把 Game.Gameplay 网络层（FishNet SyncVar/RPC、GameProtocolIdentity）加进本清单。
    /// </summary>
    public static class HotUpdateGenConfig
    {
        [LuaCallCSharp]
        public static List<Type> HotUpdateLuaCallCSharp = new List<Type>
        {
            // 仅 Unity/TMP 核心类型（XLua.Runtime 程序集可引用，无环）：
            typeof(RectTransform),
            typeof(UnityEngine.UI.Button),
            typeof(UnityEngine.UI.Image),
            typeof(TMPro.TMP_Text),
            typeof(TextAlignmentOptions),
            typeof(FontStyles),
            // Game.*（UIComponents/UITypography/UITheme/UIArt/UISprites、HotLuaFacade 等）
            // 刻意不入 Gen —— 反射模式调用，见类头注释。
        };

        [CSharpCallLua]
        public static List<Type> HotUpdateCSharpCallLua = new List<Type>
        {
            // 热页渲染委托：root（页面容器 RectTransform）交给 Lua 填充
            typeof(Action<RectTransform>),
            // Facade 错误回调
            typeof(Action<string>),
        };
    }
}
