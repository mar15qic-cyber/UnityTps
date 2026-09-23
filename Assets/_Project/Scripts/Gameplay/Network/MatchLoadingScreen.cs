using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Gameplay.Network
{
    /// <summary>Persistent transition screen: asset loading and network readiness are separate stages.</summary>
    public sealed class MatchLoadingScreen : MonoBehaviour
    {
        private static MatchLoadingScreen _instance;
        private static Task _loadTask;
        private static string _loadingScene;
        private float _target, _display;
        private bool _initialized, _waitForMatch, _sceneLoaded, _complete;
        private string _label;
        private Font _font;
        public static bool Active => _instance != null;

        public static void Begin(bool enteringMatch)
        {
            if (_instance == null)
            {
                var go = new GameObject("MatchLoadingScreen");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<MatchLoadingScreen>();
            }
            if (_instance._initialized && _instance._waitForMatch == enteringMatch && !_instance._complete) return;
            _instance._initialized = true;
            _instance._waitForMatch = enteringMatch;
            _instance._sceneLoaded = _instance._complete = false;
            _instance._target = _instance._display = 0f;
            _instance._label = enteringMatch ? "正在加载战场" : "正在返回大厅";
        }

        public static void ReportSceneProgress(float progress)
        {
            if (_instance == null) return;
            _instance._target = Mathf.Max(_instance._target, Mathf.Clamp01(progress) * (_instance._waitForMatch ? .75f : .95f));
        }

        public static void SceneReady()
        {
            if (_instance == null) return;
            _instance._sceneLoaded = true;
            if (!_instance._waitForMatch) _instance.Complete();
        }

        public static Task LoadAsync(string scene, bool enteringMatch)
        {
            if (_loadTask != null && !_loadTask.IsCompleted)
            {
                if (_loadingScene == scene) return _loadTask;
                throw new InvalidOperationException("A scene transition is already in progress.");
            }
            Begin(enteringMatch);
            _loadingScene = scene;
            return _loadTask = LoadInternalAsync(scene);
        }

        private static async Task LoadInternalAsync(string scene)
        {
            try
            {
                // Present the curtain before scene activation / Awake work starts.
                await Task.Yield();
                await Task.Yield();
                var operation = SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                if (operation == null) throw new InvalidOperationException("Scene could not be loaded: " + scene);
                operation.allowSceneActivation = false;
                while (operation.progress < .9f)
                {
                    ReportSceneProgress(operation.progress / .9f);
                    await Task.Yield();
                }
                ReportSceneProgress(1f);
                await Task.Yield();
                operation.allowSceneActivation = true;
                while (!operation.isDone) await Task.Yield();
                SceneReady();
            }
            catch
            {
                Dismiss();
                throw;
            }
        }

        public static void Dismiss()
        {
            if (_instance != null)
            {
                _instance.gameObject.SetActive(false);
                Destroy(_instance.gameObject);
            }
            _instance = null;
        }

        private void Complete() { _target = 1f; _complete = true; }

        private void Update()
        {
            if (_sceneLoaded && _waitForMatch && !_complete)
            {
                var session = ClientMatchSessionCoordinator.CurrentSession;
                if (session == null || session.Phase == ClientSessionPhase.Idle) Complete(); // offline
                else if (session.IsTerminal)
                    Dismiss(); // the existing failure/return flow owns navigation and its message
                else
                {
                    _label = "正在连接服务器";
                    if (session.Phase == ClientSessionPhase.Authenticated) { _target = .88f; _label = "正在同步玩家"; }
                    if (session.Phase == ClientSessionPhase.OwnerReady || session.Phase == ClientSessionPhase.Playing)
                    {
                        _target = .96f;
                        _label = "正在准备对局";
                        if (session.Phase == ClientSessionPhase.Playing && MatchLifecycle.Phase == MatchPhase.InProgress) Complete();
                    }
                }
            }
            _display = Mathf.MoveTowards(_display, _target, Time.unscaledDeltaTime * .8f);
            if (_complete && _display >= 1f) Dismiss();
        }

        private void OnGUI()
        {
            GUI.depth = -10000;
            var previous = GUI.color;
            GUI.color = new Color(.035f, .065f, .075f, 1f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            float width = Screen.width * .72f, left = (Screen.width - width) * .5f, top = Screen.height * .86f;
            if (_font == null) _font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 22);
            var style = new GUIStyle(GUI.skin.label) { font = _font, fontSize = Mathf.Max(16, Screen.height / 48) };
            GUI.color = Color.white;
            GUI.Label(new Rect(left, top - 48f, width, 40f), (_label ?? "正在返回大厅") + "   " + Mathf.FloorToInt(_display * 100f) + "%", style);
            GUI.color = new Color(.15f, .23f, .25f);
            GUI.DrawTexture(new Rect(left, top, width, 7f), Texture2D.whiteTexture);
            GUI.color = new Color(.25f, .85f, .73f);
            GUI.DrawTexture(new Rect(left, top, width * _display, 7f), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private void OnDestroy() { if (_font != null) Destroy(_font); if (_instance == this) _instance = null; }
    }
}
