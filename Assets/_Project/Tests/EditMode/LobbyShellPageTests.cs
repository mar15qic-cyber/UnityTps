using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Game.Account;
using Game.UI;

namespace Game.Gameplay.Tests
{
    /// <summary>Offline structure tests for the Docs/20 Step 4 shell: NavRail, lobby home, loading transition.</summary>
    public sealed class LobbyShellPageTests
    {
        private readonly List<GameObject> created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            created.Clear();
        }

        private LobbyPresenter CreatePresenter(bool buildShell)
        {
            var root = new GameObject("PresenterRoot");
            created.Add(root);
            var presenter = root.AddComponent<LobbyPresenter>();
            SetField(presenter, "session", new AccountSession());
            if (buildShell)
            {
                Invoke(presenter, "BuildShell");
            }
            else
            {
                var bodyGo = new GameObject("Body", typeof(RectTransform));
                bodyGo.transform.SetParent(root.transform, false);
                SetField(presenter, "body", bodyGo.transform);
                var statusGo = new GameObject("Status", typeof(RectTransform), typeof(TextMeshProUGUI));
                statusGo.transform.SetParent(root.transform, false);
                SetField(presenter, "status", statusGo.GetComponent<TextMeshProUGUI>());
            }
            return presenter;
        }

        private static void SetField(object target, string name, object value)
        {
            var field = typeof(LobbyPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"field {name} missing");
            field.SetValue(target, value);
        }

        private static T GetField<T>(object target, string name)
        {
            var field = typeof(LobbyPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"field {name} missing");
            return (T)field.GetValue(target);
        }

        private static void Invoke(object target, string method, params object[] args)
        {
            var info = typeof(LobbyPresenter).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(info, Is.Not.Null, $"method {method} missing");
            info.Invoke(target, args);
        }

        private static void Authenticate(LobbyPresenter presenter)
        {
            var session = GetField<AccountSession>(presenter, "session");
            session.Apply(new AuthSessionDto
            {
                token = "test-token",
                expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("o"),
                profile = new PlayerProfileDto { username = "Tester", level = 3, xp = 40, xpToNextLevel = 100, skillPoints = 2, coins = 12345 },
            });
        }

        [Test]
        public void Shell_BuildsNavRailWithSixPillsAndStatus()
        {
            var presenter = CreatePresenter(buildShell: true);
            var rail = presenter.transform.Find("LobbyCanvas/NavRail");
            Assert.That(rail, Is.Not.Null, "NavRail should exist under canvas");
            var pills = rail.GetComponentsInChildren<Button>(true);
            Assert.That(pills.Length, Is.EqualTo(6));
            Assert.That(rail.Find("RailLogo")?.GetComponent<Image>()?.sprite, Is.Not.Null);
            var status = GetField<TMP_Text>(presenter, "status");
            Assert.That(status, Is.Not.Null);
            Assert.That(presenter.transform.Find("LobbyCanvas/PageBody"), Is.Not.Null);
            // Pre-auth shell must hide navigation.
            Assert.That(rail.gameObject.activeSelf, Is.True, "rail created active; presenter hides it during Initialize");
        }

        [Test]
        public void NavSelection_FollowsCurrentPage()
        {
            var presenter = CreatePresenter(buildShell: true);
            SetField(presenter, "currentPage", LobbyPage.Shop);
            Invoke(presenter, "UpdateNavSelection");
            var pills = GetField<Dictionary<LobbyPage, Button>>(presenter, "navPills");
            foreach (var kv in pills)
            {
                var bar = kv.Value.transform.Find("ActiveBar")?.GetComponent<Image>();
                Assert.That(bar, Is.Not.Null);
                Assert.That(bar.enabled, Is.EqualTo(kv.Key == LobbyPage.Shop), $"pill {kv.Key} selection mismatch");
            }
        }

        [Test]
        public void LobbyHome_Authenticated_BuildsProfileCardAndCtas()
        {
            var presenter = CreatePresenter(buildShell: false);
            Authenticate(presenter);
            Invoke(presenter, "RenderLobby");
            var body = GetField<Transform>(presenter, "body");
            var page = body.Find("LobbyPage");
            Assert.That(page, Is.Not.Null);
            Assert.That(page.Find("ProfileCard"), Is.Not.Null);

            var labels = new List<string>();
            foreach (var t in page.GetComponentsInChildren<TMP_Text>(true))
                labels.Add(t.text);
            Assert.That(labels, Has.Member("进入战斗"));
            Assert.That(labels, Has.Member("退出会话"));
            Assert.That(labels, Has.Member("Tester"));
            Assert.That(labels, Has.Member("COINS 12,345"));

            // XP bar: 40/100 => 0.4
            var fills = page.GetComponentsInChildren<Image>(true);
            Image xpFill = null;
            foreach (var img in fills)
                if (img.type == Image.Type.Filled && img.name == "Fill") xpFill = img;
            Assert.That(xpFill, Is.Not.Null);
            Assert.That(xpFill.fillAmount, Is.EqualTo(0.4f).Within(0.001f));
        }

        [Test]
        public void LobbyHome_Unauthenticated_RedirectsToLogin()
        {
            var presenter = CreatePresenter(buildShell: false);
            Invoke(presenter, "RenderLobby"); // session not authenticated => Navigate(Login)
            var body = GetField<Transform>(presenter, "body");
            Assert.That(body.Find("AuthCard"), Is.Not.Null, "unauthenticated lobby access should render the login card");
        }

        [Test]
        public void LoadingPage_HasProgressBarAndLogo()
        {
            var presenter = CreatePresenter(buildShell: false);
            Invoke(presenter, "RenderLoading");
            var body = GetField<Transform>(presenter, "body");
            var card = body.Find("LoadingPage/LoadingPanel");
            Assert.That(card, Is.Not.Null);
            Assert.That(card.Find("Logo")?.GetComponent<Image>()?.sprite, Is.Not.Null);
            var fill = GetField<Image>(presenter, "loadingFill");
            Assert.That(fill, Is.Not.Null);
            Assert.That(fill.fillAmount, Is.EqualTo(0f));
        }
    }
}
