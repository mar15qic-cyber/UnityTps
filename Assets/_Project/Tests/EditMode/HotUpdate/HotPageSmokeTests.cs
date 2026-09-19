using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Account;
using Game.UI;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>热更页冒烟（P1 验证门）：Lua 引导注册 → 委托渲染容器 → Lua 直注册链路 → 幂等覆盖
    /// + presenter 级路由（页签生成与 NavigateHot 分发，复用 LobbyShellPageTests 离线构造模式）。
    /// 本套件即"每个 Lua 页配冒烟"约定的样板（Lua 无编译期检查，冒烟防改坏）。</summary>
    public sealed class HotPageSmokeTests
    {
        private GameObject runtimeGo;

        [SetUp]
        public void SetUp()
        {
            HotPageRegistry.ClearAll();
            HotUpdateRuntime.HotFilesRoot = null;
            // EditMode 下 AddComponent 不触发 Awake：直调幂等 Initialize（项目既有惯例）
            runtimeGo = new GameObject("HotRuntimeTestHost");
            runtimeGo.SetActive(false);
            runtimeGo.AddComponent<HotUpdateRuntime>().Initialize();
            Assert.IsNotNull(HotUpdateRuntime.Instance, "LuaEnv 宿主应完成初始化");
        }

        [TearDown]
        public void TearDown()
        {
            if (runtimeGo != null) UnityEngine.Object.DestroyImmediate(runtimeGo);
            HotPageRegistry.ClearAll();
            HotUpdateRuntime.HotFilesRoot = null;
        }

        [Test]
        public void Bootstrap_RegistersHelloPage_And_RendersIntoRoot()
        {
            Assert.IsNull(HotUpdateRuntime.Instance.BootstrapError,
                "内置引导不应失败：" + HotUpdateRuntime.Instance.BootstrapError);
            Assert.IsTrue(HotPageRegistry.TryGet("hello", out var page), "内置引导脚本应注册 hello 演示页");
            var rootGo = new GameObject("HotRoot", typeof(RectTransform));
            try
            {
                page.Render(rootGo.GetComponent<RectTransform>());
                Assert.Greater(rootGo.transform.childCount, 0, "Lua 渲染委托应在页面容器下创建子节点");
                var texts = rootGo.GetComponentsInChildren<TMPro.TMP_Text>();
                Assert.IsTrue(texts.Any(t => t.text.Contains("热更")),
                    "页面应包含标题文本（实际：" + string.Join("|", texts.Select(t => t.text)) + "）");
            }
            finally { UnityEngine.Object.DestroyImmediate(rootGo); }
        }

        [Test]
        public void LuaRegister_ViaFacade_BridgeWorks()
        {
            var env = HotUpdateRuntime.Instance.Env;
            Assert.IsNotNull(env);
            env.DoString(@"
                CS.Game.UI.HotLuaFacade.RegisterPage('inline_demo', '内联页', function(root)
                    CS.Game.UI.UITypography.Text('T_Inline', root.transform, '内联注册OK',
                        CS.Game.UI.UITheme.FontBody, CS.Game.UI.UITheme.TextPrimary,
                        CS.UnityEngine.Vector2(0, 0), CS.UnityEngine.Vector2(1, 1),
                        CS.TMPro.TextAlignmentOptions.Center, CS.TMPro.FontStyles.Bold)
                end)");
            Assert.IsTrue(HotPageRegistry.TryGet("inline_demo", out var page), "Lua 直注册应经 Facade 写入注册表");
            var rootGo = new GameObject("InlineRoot", typeof(RectTransform));
            try
            {
                page.Render(rootGo.GetComponent<RectTransform>());
                var texts = rootGo.GetComponentsInChildren<TMPro.TMP_Text>();
                Assert.IsTrue(texts.Any(t => t.text == "内联注册OK"),
                    "Lua 闭包渲染应产生文本（实际：" + string.Join("|", texts.Select(t => t.text)) + "）");
            }
            finally { UnityEngine.Object.DestroyImmediate(rootGo); }
        }

        [Test]
        public void Register_SameId_Twice_Overwrites_KeepsSingleEntry()
        {
            System.Action<RectTransform> a = _ => { };
            System.Action<RectTransform> b = _ => { };
            HotLuaFacade.RegisterPage("dup", "第一次", a);
            HotLuaFacade.RegisterPage("dup", "第二次", b);
            var all = HotPageRegistry.All.Where(p => p.Id == "dup").ToList();
            Assert.AreEqual(1, all.Count, "同 id 注册应覆盖而不是重复");
            Assert.AreEqual("第二次", all[0].Label);
        }

        [Test]
        public void Presenter_BuildsHotPill_And_NavigateHot_RoutesAndRenders()
        {
            // presenter 级路由：BuildShell 读注册表生成页签；NavigateHot 走完整前置链并调 Lua 渲染委托
            var presenterGo = new GameObject("HotPresenterRoot");
            try
            {
                HotLuaFacade.RegisterPage("test_hot", "测试热页", root =>
                {
                    var child = new GameObject("RenderedByLua", typeof(RectTransform));
                    child.transform.SetParent(root, false);
                });
                var presenter = presenterGo.AddComponent<LobbyPresenter>();
                SetField(presenter, "session", new AccountSession());
                SetField(presenter, "apiAvailable", true);
                InvokePrivate(presenter, "BuildShell");

                var bar = presenterGo.transform.Find("LobbyCanvas/ShellTopBar");
                Assert.IsNotNull(bar, "BuildShell 应建顶栏");
                Assert.IsNotNull(bar.Find("NavHot_test_hot"), "注册表中的热页应生成顶栏页签");

                // 未认证：NavigateHot 应改道登录页
                presenter.NavigateHot("test_hot");
                Assert.AreEqual(LobbyPage.Login, (LobbyPage)GetField(presenter, "currentPage"),
                    "未认证访问热页应被登录门拦截");

                Authenticate(presenter);
                presenter.NavigateHot("test_hot");
                Assert.AreEqual(LobbyPage.Hot, (LobbyPage)GetField(presenter, "currentPage"));
                Assert.IsNotNull(presenterGo.transform.Find("LobbyCanvas/PageBody/HotPage_test_hot"),
                    "热页应在 body 下创建页面容器");
                Assert.IsNotNull(presenterGo.transform.Find("LobbyCanvas/PageBody/HotPage_test_hot/RenderedByLua"),
                    "Lua 渲染委托应在页面容器下创建子节点");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(presenterGo);
                HotPageRegistry.ClearAll();
            }
        }

        // ---- 战绩页（P3 热更试点）：Lua 渲染分支冒烟（stub 数据走 facade 数据契约形状）----

        private void RenderCareerViaLua(string luaBody, out List<string> texts, out List<string> childNames)
        {
            var rootGo = new GameObject("CareerRoot", typeof(RectTransform));
            try
            {
                var env = HotUpdateRuntime.Instance.Env;
                env.Global.Set("__careerRoot", rootGo.GetComponent<RectTransform>());
                try
                {
                    env.DoString(luaBody);
                }
                finally
                {
                    env.Global.Set("__careerRoot", (object)null);
                }
                texts = rootGo.GetComponentsInChildren<TMPro.TMP_Text>().Select(t => t.text).ToList();
                childNames = rootGo.GetComponentsInChildren<Transform>(true).Select(t => t.name).ToList();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rootGo);
            }
        }

        [Test]
        public void CareerPage_Registers_And_Renders_DataBranch()
        {
            Assert.IsTrue(HotPageRegistry.TryGet("career", out _), "内置引导应注册战绩页");
            RenderCareerViaLua(@"
                local dto = {
                    page = 1, totalCount = 3, totalPages = 1,
                    summary = { totalMatches = 3, wins = 2, totalKills = 36, totalDeaths = 17, totalXp = 1200, totalCoins = 600, winRate = 66.7 },
                    rows = {
                        { playedAt = '2026-09-17T18:44:12.000Z', isWin = false, kills = 4, deaths = 9, score = 0, xp = 240, coins = 120 },
                        { playedAt = '2026-09-17T18:10:02.000Z', isWin = true, kills = 20, deaths = 5, score = 0, xp = 700, coins = 350 },
                    },
                }
                CareerPageRender(__careerRoot, dto, nil)
            ", out var texts, out var names);
            Assert.IsTrue(texts.Any(t => t.Contains("作战战绩")), "应有标题");
            Assert.IsTrue(texts.Any(t => t.Contains("总场次 3") && t.Contains("66.7%")), "应有汇总卡：" + string.Join("|", texts));
            Assert.IsTrue(texts.Any(t => t.Contains("失败")), "负局行存在");
            Assert.IsTrue(texts.Any(t => t.Contains("胜利") && t.Contains("20 / 5")), "胜局行存在");
            Assert.IsTrue(texts.Any(t => t.Contains("第 1 / 1 页")), "应有分页信息");
            Assert.IsTrue(names.Any(n => n == "Btn_CareerPrev") && names.Any(n => n == "Btn_CareerNext"), "应有分页按钮");
        }

        [Test]
        public void CareerPage_Renders_EmptyBranch()
        {
            RenderCareerViaLua(@"
                CareerPageRender(__careerRoot, { summary = { totalMatches = 0 }, rows = {} }, nil)
            ", out var texts, out _);
            Assert.IsTrue(texts.Any(t => t.Contains("还没有对局记录")), "空态文案存在");
        }

        [Test]
        public void CareerPage_Renders_ErrorBranch_WithRetry()
        {
            RenderCareerViaLua(@"
                CareerPageRender(__careerRoot, nil, 'BACKEND_DOWN')
            ", out var texts, out var names);
            Assert.IsTrue(texts.Any(t => t.Contains("战绩加载失败")), "错误文案存在");
            Assert.IsTrue(names.Any(n => n == "Btn_CareerRetry"), "重试按钮存在");
        }

        // ---- 离线构造助手（复用 LobbyShellPageTests 的反射模式）----

        private static void SetField(object target, string name, object value)
        {
            var field = typeof(LobbyPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"field {name} missing");
            field.SetValue(target, value);
        }

        private static object GetField(object target, string name)
        {
            var field = typeof(LobbyPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"field {name} missing");
            return field.GetValue(target);
        }

        private static void InvokePrivate(object target, string method)
        {
            var info = typeof(LobbyPresenter).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(info, $"method {method} missing");
            info.Invoke(target, Array.Empty<object>());
        }

        private static void Authenticate(LobbyPresenter presenter)
        {
            var session = (AccountSession)GetField(presenter, "session");
            session.Apply(new AuthSessionDto
            {
                token = "test-token",
                expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("o"),
                profile = new PlayerProfileDto { username = "Tester", level = 3, xp = 40, xpToNextLevel = 100, skillPoints = 2, coins = 12345 },
            });
        }
    }
}
