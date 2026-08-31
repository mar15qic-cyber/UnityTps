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
    /// <summary>
    /// Offline structure smoke for the restyled auth chain (Docs/20 Step 3). Pages are private
    /// render methods on LobbyPresenter; tests drive them via reflection with a minimal field
    /// setup so no API/backend is required.
    /// </summary>
    public sealed class LobbyAuthPageTests
    {
        private readonly List<GameObject> created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                if (go != null) Object.DestroyImmediate(go);
            created.Clear();
        }

        private LobbyPresenter CreatePresenter()
        {
            var root = new GameObject("PresenterRoot");
            created.Add(root);
            var presenter = root.AddComponent<LobbyPresenter>();

            var bodyGo = new GameObject("Body", typeof(RectTransform));
            bodyGo.transform.SetParent(root.transform, false);
            created.Add(bodyGo);
            SetField(presenter, "body", bodyGo.transform);

            var statusGo = new GameObject("Status", typeof(RectTransform), typeof(TextMeshProUGUI));
            statusGo.transform.SetParent(root.transform, false);
            SetField(presenter, "status", statusGo.GetComponent<TextMeshProUGUI>());
            SetField(presenter, "session", new AccountSession());
            return presenter;
        }

        private static void SetField(object target, string name, object value)
        {
            var field = typeof(LobbyPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"field {name} missing");
            field.SetValue(target, value);
        }

        private static void Invoke(object target, string method, params object[] args)
        {
            var info = typeof(LobbyPresenter).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(info, Is.Not.Null, $"method {method} missing");
            info.Invoke(target, args);
        }

        private Transform BodyOf(LobbyPresenter presenter) =>
            typeof(LobbyPresenter).GetField("body", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(presenter) as Transform;

        [Test]
        public void LoginPage_BuildsCardWithInputsLogoAndButtons()
        {
            var presenter = CreatePresenter();
            Invoke(presenter, "RenderLoginPage");
            var card = BodyOf(presenter).Find("AuthCard");
            Assert.That(card, Is.Not.Null);

            var inputs = card.GetComponentsInChildren<TMP_InputField>(true);
            Assert.That(inputs.Length, Is.EqualTo(2));
            TMP_InputField password = null;
            foreach (var input in inputs)
                if (input.contentType == TMP_InputField.ContentType.Password) password = input;
            Assert.That(password, Is.Not.Null, "password input should be password content type");

            var logo = card.Find("Logo")?.GetComponent<Image>();
            Assert.That(logo, Is.Not.Null);
            Assert.That(logo.sprite, Is.Not.Null);

            var labels = new List<string>();
            foreach (var t in card.GetComponentsInChildren<TMP_Text>(true))
                labels.Add(t.text);
            Assert.That(labels, Has.Member("登录"));
            Assert.That(labels, Has.Member("创建账号"));
            Assert.That(labels, Has.Member("登录作战网络"));

            var buttons = card.GetComponentsInChildren<Button>(true);
            Assert.That(buttons.Length, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void RegisterPage_ShowsRegisterCopyAndSwitchButton()
        {
            var presenter = CreatePresenter();
            Invoke(presenter, "RenderRegisterPage");
            var card = BodyOf(presenter).Find("AuthCard");
            Assert.That(card, Is.Not.Null);

            var labels = new List<string>();
            foreach (var t in card.GetComponentsInChildren<TMP_Text>(true))
                labels.Add(t.text);
            Assert.That(labels, Has.Member("建立身份"));
            Assert.That(labels, Has.Member("注册并继续"));
            Assert.That(labels, Has.Member("返回登录"));
        }

        [Test]
        public void IdentityPage_ShowsWelcomeAndUnlockBadges()
        {
            var presenter = CreatePresenter();
            Invoke(presenter, "RenderIdentity");
            var card = BodyOf(presenter).Find("IdentityPanel");
            Assert.That(card, Is.Not.Null);

            var labels = new List<string>();
            foreach (var t in card.GetComponentsInChildren<TMP_Text>(true))
                labels.Add(t.text);
            Assert.That(labels, Has.Member("身份确认"));
            Assert.That(labels, Has.Member("进入大厅"));
            Assert.That(labels, Has.Member("M4"));
            Assert.That(labels, Has.Member("AK"));
            Assert.That(labels, Has.Member("Service Pistol"));
        }

        [Test]
        public void AuthPages_EnterMotionLeavesCardFullyVisible()
        {
            var presenter = CreatePresenter();
            Invoke(presenter, "RenderLoginPage");
            var card = BodyOf(presenter).Find("AuthCard");
            var group = card.GetComponent<CanvasGroup>();
            Assert.That(group, Is.Not.Null, "PlayEnter should attach a CanvasGroup");
            // EditMode: tweens complete synchronously, final alpha must be 1.
            Assert.That(group.alpha, Is.EqualTo(1f));
        }
    }
}
