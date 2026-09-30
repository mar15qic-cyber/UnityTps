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
    /// <summary>好友页 + 大厅 FriendsCard 结构测试（2026-09-20 需求2，离线反射驱动）。</summary>
    public sealed class LobbyFriendsPageTests
    {
        private readonly List<Object> created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in created)
                if (obj != null) Object.DestroyImmediate(obj);
            created.Clear();
        }

        private LobbyPresenter CreatePresenter()
        {
            var root = new GameObject("PresenterRoot");
            created.Add(root);
            var presenter = root.AddComponent<LobbyPresenter>();
            SetField(presenter, "session", new AccountSession()); // 未认证用例也需非空 session（RenderFriends 判定）

            var bodyGo = new GameObject("Body", typeof(RectTransform));
            bodyGo.transform.SetParent(root.transform, false);
            created.Add(bodyGo);
            SetField(presenter, "body", bodyGo.transform);

            var statusGo = new GameObject("Status", typeof(RectTransform), typeof(TextMeshProUGUI));
            statusGo.transform.SetParent(root.transform, false);
            created.Add(statusGo);
            SetField(presenter, "status", statusGo.GetComponent<TextMeshProUGUI>());
            Invoke(presenter, "BuildShell");
            return presenter;
        }

        private static LobbyPresenter Authenticate(LobbyPresenter presenter, string username = "Tester")
        {
            SetField(presenter, "session", new AccountSession());
            var session = GetField<AccountSession>(presenter, "session");
            session.Apply(new AuthSessionDto
            {
                token = "test-token",
                expiresAtUtc = System.DateTime.UtcNow.AddHours(1).ToString("o"),
                profile = new PlayerProfileDto { username = username, identityTag = "4821" },
            });
            return presenter;
        }

        private static void Invoke(object target, string method, params object[] args)
        {
            var info = target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(info, Is.Not.Null, $"method {method} missing");
            info.Invoke(target, args);
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"field {name} missing");
            field.SetValue(target, value);
        }

        private static T GetField<T>(object target, string name)
        {
            var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"field {name} missing");
            return (T)field.GetValue(target);
        }

        private static Transform Body(LobbyPresenter presenter) => GetField<Transform>(presenter, "body");

        private static FriendListDto SampleList()
        {
            return new FriendListDto
            {
                friends = new[]
                {
                    new FriendEntryDto { userId = 2, username = "bobby", identityTag = "5678", presence = FriendPresence.Online },
                    new FriendEntryDto { userId = 3, username = "fighter", identityTag = "1111", presence = FriendPresence.InMatch },
                },
                incoming = new[]
                {
                    new FriendRequestEntryDto { requestId = 10, userId = 4, username = "carol", identityTag = "9999", createdAtUtc = System.DateTime.UtcNow },
                },
                outgoing = new[]
                {
                    new FriendRequestEntryDto { requestId = 11, userId = 5, username = "dave", identityTag = "0000", createdAtUtc = System.DateTime.UtcNow },
                },
            };
        }

        [Test]
        public void SocialRefreshPreservesLobbyCharacterAndSupportsScrolling()
        {
            var presenter = Authenticate(CreatePresenter());
            presenter.Navigate(LobbyPage.Lobby);
            var character = Body(presenter).Find("LobbyPage/CharacterDisplay");
            SetField(presenter, "cachedFriends", SampleList());
            Invoke(presenter, "ToggleSocial");
            Invoke(presenter, "RefreshSocialViews");
            Assert.That(Body(presenter).Find("LobbyPage/CharacterDisplay"), Is.SameAs(character));
            var drawer = GetField<GameObject>(presenter, "socialDrawer");
            Assert.That(drawer.GetComponentInChildren<ScrollRect>(), Is.Not.Null);
            Assert.That(drawer.GetComponentsInChildren<TMP_Text>(), Has.Some.Matches<TMP_Text>(t => t.text == "bobby#5678"));
            SetField(presenter, "socialTab", 1);
            Invoke(presenter, "RefreshSocialViews");
            Assert.That(drawer.GetComponentsInChildren<TMP_Text>(), Has.Some.Matches<TMP_Text>(t => t.text == "同意"));
        }

        [Test]
        public void AddFriendModalBlocksUnderlyingUiAndClosesOnNavigation()
        {
            var presenter = Authenticate(CreatePresenter());
            presenter.Navigate(LobbyPage.Lobby);
            Invoke(presenter, "OpenAddFriendModal");
            var modal = GetField<GameObject>(presenter, "socialModal");
            Assert.That(modal.GetComponent<Image>().raycastTarget, Is.True);
            Assert.That(modal.GetComponentInChildren<TMP_InputField>(), Is.Not.Null);
            var token = GetField<System.Threading.CancellationTokenSource>(presenter, "modalCts").Token;
            presenter.Navigate(LobbyPage.Settings);
            Assert.That(token.IsCancellationRequested, Is.True);
            Assert.That(GetField<GameObject>(presenter, "socialModal"), Is.Null);
        }

        [Test]
        public void FriendListDoesNotTruncateAfterFirstFewRows()
        {
            var presenter = Authenticate(CreatePresenter());
            presenter.Navigate(LobbyPage.Lobby);
            var rows = new FriendEntryDto[45];
            for (int i = 0; i < rows.Length; i++) rows[i] = new FriendEntryDto { userId = i, username = "Friend" + i, identityTag = "1234", presence = FriendPresence.Online };
            SetField(presenter, "cachedFriends", new FriendListDto { friends = rows });
            Invoke(presenter, "ToggleSocial");
            Assert.That(GetField<Transform>(presenter, "socialRows").childCount, Is.EqualTo(46));
        }
    }
}
