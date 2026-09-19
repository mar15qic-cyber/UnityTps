using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>asmdef 方向守卫（热更红线，2026-09-17 计划 §3.1）：
    /// ① Game.Gameplay（协议/网络/战斗层）不得引用 UI/Account/xLua——热更面永不触碰服务器权威层；
    /// ② XLua.Runtime（Gen 产物所在程序集）不得引用 Game.*——Gen 面无环约束（Game.* 走反射模式）；
    /// ③ Game.UI 持有 XLua.Runtime 引用（Facade/运行时宿主所在）。</summary>
    public sealed class AssemblyBoundaryGuardTests
    {
        private static string ReadAsmdef(string assetsRelativePath)
        {
            var full = Path.Combine(Application.dataPath, assetsRelativePath);
            Assert.IsTrue(File.Exists(full),
                "asmdef 不存在：" + assetsRelativePath + "（文件被重命名/移动后请同步更新本守卫）");
            return File.ReadAllText(full);
        }

        [Test]
        public void Gameplay_NeverReferences_Ui_Account_XLua()
        {
            var text = ReadAsmdef("_Project/Scripts/Gameplay/Game.Gameplay.asmdef");
            StringAssert.DoesNotContain("Game.UI", text);
            StringAssert.DoesNotContain("Game.Account", text);
            StringAssert.DoesNotContain("XLua", text);
        }

        [Test]
        public void XLuaRuntime_NeverReferences_GameAssemblies()
        {
            var text = ReadAsmdef("XLua/XLua.Runtime.asmdef");
            StringAssert.DoesNotContain("Game.UI", text);
            StringAssert.DoesNotContain("Game.Account", text);
            StringAssert.DoesNotContain("Game.Gameplay", text);
            StringAssert.DoesNotContain("Game.Core", text);
        }

        [Test]
        public void GameUi_KeepsXLuaRuntimeReference()
        {
            var text = ReadAsmdef("_Project/Scripts/UI/Game.UI.asmdef");
            StringAssert.Contains("XLua.Runtime", text);
        }
    }
}
