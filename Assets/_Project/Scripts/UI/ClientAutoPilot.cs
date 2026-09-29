using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// 联测自动参与驱动器（2026-09-13 集成测试矩阵 B/C）：启动参数
    /// -itUser &lt;name&gt; -itPass &lt;pwd&gt; -itAction create|join[:ROOMCODE] [-itMax N] [-itMode TDM|KillRace]
    /// 存在时，在 AppRoot 就绪后以生产 API/UI 路径完成"登录→建房/加入→准备→(房主)全员就绪后开局→
    /// 随 start 票据经 EnterBattleAsync 进战"。仅代替手指点击：全部动作走 LobbyPresenter 既有校验与
    /// 网络链（不写 NetworkLaunchContext、不绕过 RoomConnectionGate、不伪造任何状态）。
    /// 线程纪律：网络 Task 一律 yield WaitUntil 轮询完成，绝不主线程 GetResult 阻塞
    /// （HttpClient 续体依赖主线程 SyncContext，阻塞即死锁——首轮联测实证）。
    /// 无参数时零介入（生产客户端不受影响）；日志统一 IT_AUTOPILOT。
    /// </summary>
    public static class ClientAutoPilot
    {
        public sealed class Options
        {
            public string User;
            public string Pass;
            public string Action;       // create | join
            public string RoomCode;     // join 用（join:CODE 显式或 -itRoomFile 轮询）
            public string RoomCodeFile; // 建房端写房码 / join 端读取的共享文件
            public int MaxPlayers = 2;
            public int MinReadyPlayers = 2; // 房主开局前等待的就绪人数（含自己）；联测编排用
            public string Mode = "TDM";
            public string MapId = "arena";
            public int KillTarget = 20;
            public int TimeLimitMinutes = 5;
            /// <summary>-itNoReady：join 端不点准备，留在房间等房主开赛后再点「进入比赛」（未准备成员场景）。</summary>
            public bool NoReady;
        }

        public static Options Parse()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string name)
            {
                for (int i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                        return args[i + 1];
                return null;
            }
            string user = Arg("-itUser");
            string pass = Arg("-itPass");
            string action = Arg("-itAction");
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass) || string.IsNullOrEmpty(action)) return null;
            var options = new Options
            {
                User = user,
                Pass = pass,
                RoomCodeFile = Arg("-itRoomFile"),
                RoomCode = null,
                MaxPlayers = int.TryParse(Arg("-itMax"), out var max) ? max : 2,
                MinReadyPlayers = int.TryParse(Arg("-itWaitPlayers"), out var wait) ? wait : 2,
                Mode = Arg("-itMode") ?? "TDM",
                MapId = Arg("-itMap") ?? "arena",
                // killTarget 默认按模式取白名单值（RoomSettingRules：TDM {50,100,150}、
                // KillRace {10,20,30}）——旧固定 20 是 KillRace 值，TDM 建房被 422 SETTING_INVALID 拒。
                KillTarget = int.TryParse(Arg("-itKill"), out var kill)
                    ? kill
                    : (string.Equals(Arg("-itMode") ?? "TDM", "KillRace", StringComparison.OrdinalIgnoreCase) ? 20 : 50),
                TimeLimitMinutes = int.TryParse(Arg("-itTime"), out var time) ? time : 5,
                NoReady = Array.Exists(args,
                    a => string.Equals(a, "-itNoReady", StringComparison.OrdinalIgnoreCase)),
            };
            if (action.StartsWith("join", StringComparison.OrdinalIgnoreCase))
            {
                options.Action = "join";
                var colon = action.IndexOf(':');
                options.RoomCode = colon >= 0 && colon + 1 < action.Length ? action[(colon + 1)..].ToUpperInvariant() : null;
            }
            else
            {
                options.Action = "create";
            }
            return options;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var options = Parse();
            if (options == null) return;
            var go = new GameObject("ClientAutoPilot");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var driver = go.AddComponent<Driver>();
            driver.options = options;
        }

        private sealed class Driver : MonoBehaviour
        {
            public Options options;
            private IApiClient _api;
            private AccountSession _session;
            private LobbyPresenter _presenter;
            private AuthSessionDto _auth;

            private IEnumerator Start() => Run();

            private IEnumerator WaitTask<T>(Task<T> task)
            {
                while (!task.IsCompleted) yield return new WaitForSeconds(0.1f);
            }

            private IEnumerator WaitTask(Task task)
            {
                while (!task.IsCompleted) yield return new WaitForSeconds(0.1f);
            }

            private IEnumerator Run()
            {
                // 等 AppRoot 就绪（Boot→Lobby 初始化完成）
                float deadline = Time.unscaledTime + 90f;
                while ((AppRoot.Instance?.ApiClient == null || AppRoot.Instance?.Session == null)
                       && Time.unscaledTime < deadline)
                    yield return new WaitForSeconds(0.5f);
                var app = AppRoot.Instance;
                if (app?.ApiClient == null || app.Session == null)
                {
                    Debug.LogError("[IT_AUTOPILOT] FAIL AppRoot not ready in 90s");
                    yield break;
                }
                _api = app.ApiClient;
                _session = app.Session;

                // 登录三件套（SOP）：Register 优先，已存在 → Login
                var registerTask = _api.RegisterAsync(options.User, options.Pass);
                yield return WaitTask(registerTask);
                if (registerTask.Result.Success && registerTask.Result.Data != null)
                {
                    _auth = registerTask.Result.Data;
                    Debug.Log($"[IT_AUTOPILOT] registered user={options.User}");
                }
                else
                {
                    var loginTask = _api.LoginAsync(options.User, options.Pass);
                    yield return WaitTask(loginTask);
                    if (!loginTask.Result.Success || loginTask.Result.Data == null)
                    {
                        Debug.LogError($"[IT_AUTOPILOT] FAIL auth register={registerTask.Result.Code} login={loginTask.Result.Code}");
                        yield break;
                    }
                    _auth = loginTask.Result.Data;
                    Debug.Log($"[IT_AUTOPILOT] logged-in user={options.User}");
                }
                _session.Apply(_auth);
                if (_api is ApiClient concrete)
                    concrete.SetToken(_auth.token);

                // 找 LobbyPresenter（生产常驻对象）
                deadline = Time.unscaledTime + 30f;
                while ((_presenter = FindFirstObjectByType<LobbyPresenter>()) == null && Time.unscaledTime < deadline)
                    yield return new WaitForSeconds(0.5f);
                if (_presenter == null)
                {
                    Debug.LogError("[IT_AUTOPILOT] FAIL LobbyPresenter not found");
                    yield break;
                }

                // Finding the presenter is earlier than its asynchronous health/profile bootstrap.
                // Use the same ready gate as a user clicking the enabled online controls.
                deadline = Time.unscaledTime + 60f;
                while ((!GetPrivateField<bool>(_presenter, "apiAvailable") || MapContentUpdater.Busy)
                       && Time.unscaledTime < deadline)
                    yield return new WaitForSeconds(.2f);
                if (!GetPrivateField<bool>(_presenter, "apiAvailable") || MapContentUpdater.Busy)
                {
                    Debug.LogError("[IT_AUTOPILOT] FAIL lobby admission not ready");
                    yield break;
                }

                if (options.Action == "create")
                {
                    var request = new CreateRoomRequest
                    {
                        maxPlayers = options.MaxPlayers,
                        mode = options.Mode,
                        mapId = options.MapId,
                        killTarget = options.KillTarget,
                        timeLimitMinutes = options.TimeLimitMinutes,
                    };
                    var createTask = (Task)_presenter.GetType().GetMethod("StartOnlineCreateAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(_presenter, new object[] { request });
                    yield return WaitTask(createTask);
                    if (createTask.IsFaulted || _session.Room == null)
                    {
                        Debug.LogError("[IT_AUTOPILOT] FAIL create: " + (createTask.Exception != null
                            ? createTask.Exception.ToString() : GetPrivateField<TMPro.TMP_Text>(_presenter, "status")?.text));
                        yield break;
                    }
                    Debug.Log($"[IT_AUTOPILOT] create requested map={options.MapId} mode={options.Mode} max={options.MaxPlayers}");
                }
                else
                {
                    deadline = Time.unscaledTime + 180f;
                    while (string.IsNullOrEmpty(options.RoomCode) && Time.unscaledTime < deadline)
                    {
                        // join 无码：轮询共享文件（-itRoomFile；建房端落盘房码，联测编排用）
                        if (options.RoomCodeFile != null && System.IO.File.Exists(options.RoomCodeFile))
                        {
                            var text = System.IO.File.ReadAllText(options.RoomCodeFile).Trim();
                            if (text.Length >= 6)
                            {
                                options.RoomCode = text.ToUpperInvariant();
                                break;
                            }
                        }
                        yield return new WaitForSeconds(1f);
                    }
                    if (string.IsNullOrEmpty(options.RoomCode))
                    {
                        Debug.LogError("[IT_AUTOPILOT] FAIL join without room code");
                        yield break;
                    }
                    InvokeAsync(_presenter, "StartOnlineRoomAsync", options.RoomCode, true);
                    Debug.Log($"[IT_AUTOPILOT] join requested room={options.RoomCode}");
                }

                // 等待房/进战监控：join 端自动准备；create 端全员就绪后开局；房码落盘供编排
                string roomCodeFile = options.RoomCodeFile;
                bool roomCodeWritten = options.Action != "create" || string.IsNullOrEmpty(roomCodeFile);
                bool entryActionSent = options.Action != "join";
                deadline = Time.unscaledTime + 300f;
                while (Time.unscaledTime < deadline)
                {
                    var waitingCode = GetPrivateField<string>(_presenter, "waitingRoomCode");
                    var currentPage = GetPrivateField<object>(_presenter, "currentPage");

                    if (!roomCodeWritten && !string.IsNullOrEmpty(waitingCode))
                    {
                        try
                        {
                            System.IO.File.WriteAllText(roomCodeFile, _session.Room.RoomCode);
                            Debug.Log($"[IT_AUTOPILOT] room code written by host");
                        }
                        catch (Exception exception)
                        {
                            Debug.LogWarning($"[IT_AUTOPILOT] room code write failed: {exception.Message}");
                        }
                        roomCodeWritten = true;
                    }

                    if (waitingCode == null || currentPage == null
                        || !currentPage.ToString().Contains("WaitingRoom"))
                    {
                        // 已进战（Arena）或回大厅：驱动职责完成（进战链路由 EnterBattleAsync 自持）
                        if (IsInArena())
                        {
                            Debug.Log("[IT_AUTOPILOT] in-arena (enter battle done)");
                            yield break;
                        }
                        yield return new WaitForSeconds(0.5f);
                        continue;
                    }

                    if (options.Action == "join" && !entryActionSent)
                    {
                        var status = _session?.Room?.Status;
                        bool matchRunning = string.Equals(status, "Starting", StringComparison.Ordinal)
                            || string.Equals(status, "InMatch", StringComparison.Ordinal);
                        if (options.NoReady && !matchRunning)
                        {
                            // 未准备成员：留在等待房间（不点准备），等房主开赛后再手动入场
                        }
                        else
                        {
                            entryActionSent = true;
                            // 同一按钮：Waiting=准备/取消准备；Starting/InMatch="进入比赛"（显式 Join 补名单）
                            InvokeVoid(_presenter, "OnWaitingReadyClicked");
                            Debug.Log(options.NoReady
                                ? "[IT_AUTOPILOT] enter-battle clicked (unready guest)"
                                : "[IT_AUTOPILOT] ready clicked");
                        }
                    }

                    if (options.Action == "create")
                    {
                        long selfUserId = 0;
                        bool allReady = false;
                        // GetRoomDetailAsync 将 HTTP 错误归一为 ApiResult（不抛）；协程内不 try-catch
                        //（yield 限制），异常即协程终止并在日志可见——可接受
                        var detailTask = _api.GetRoomDetailAsync(waitingCode, CancellationToken.None);
                        yield return WaitTask(detailTask);
                        var detail = detailTask.Result;
                        var members = detail.Data?.members;
                        selfUserId = detail.Data?.you?.userId ?? 0;
                        allReady = detail.Success && members != null
                            && members.Length >= options.MinReadyPlayers && selfUserId > 0;
                        if (allReady)
                        {
                            foreach (var member in members)
                                if (!member.isReady && member.userId != selfUserId)
                                {
                                    allReady = false;
                                    break;
                                }
                        }
                        if (allReady)
                        {
                            InvokeAsyncNoArg(_presenter, "OnWaitingStartClicked");
                            Debug.Log("[IT_AUTOPILOT] start clicked (all ready)");
                            yield break; // start 后进战由等待页轮询自持
                        }
                    }
                    yield return new WaitForSeconds(1f);
                }
                Debug.LogError("[IT_AUTOPILOT] FAIL waiting-room timeout");
            }

            private static bool IsInArena()
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                return HotMapCatalog.TryGetSceneName(AppRoot.Instance?.Session?.Room?.MapId, out var expected)
                    && scene.name == expected;
            }

            private static void InvokeAsync(object target, string method, params object[] arguments)
            {
                target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(target, arguments);
            }

            private static void InvokeVoid(object target, string method)
            {
                target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(target, null);
            }

            private static void InvokeAsyncNoArg(object target, string method)
            {
                target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(target, null);
            }

            private static T GetPrivateField<T>(object target, string name)
            {
                var field = target.GetType().GetField(name,
                    BindingFlags.NonPublic | BindingFlags.Instance);
                return field != null ? (T)field.GetValue(target) : default;
            }
        }
    }
}
