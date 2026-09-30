using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FishNet;
using Game.Account;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Health;
using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Camera;
using Game.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.RuntimeAudit
{
    // Excluded from ordinary builds by the assembly's FPS_RUNTIME_AUDIT constraint.
    // Commands are local files in the explicitly supplied evidence directory. No network command listener.
    // Fixture mutations are logged separately; measured fire always uses the existing input/RPC path.
    [DefaultExecutionOrder(-250)]
    public sealed class RuntimeAuditDriver : MonoBehaviour
    {
        [Serializable] public sealed class Command
        {
            public int seq, target = -1, fps, slot = -1, lean, aimTarget = -1, health = 100;
            public string op, caseId, weapon, name;
            public string[] slots;
            public Vector3 position, scale;
            public Vector2 move, look;
            public float yaw, pitch, duration = 1f, clickInterval, aimHeight = 1.2f;
            public bool fire, aim, reload, jump, sprint, trigger;
        }
        [Serializable] public sealed class PlayerState
        {
            public long connection;
            public int objectId, health, ammo, reserve, enabledDamageVolumes, damageVolumes, kills, deaths;
            public uint inputTick, life, respawnTick;
            public bool owner, server, dead, protectedNow, grounded;
            public string name, weapon, action, runtimeState, team, reloadPhase, backpackEligibility;
            public int activeBackpack;
            public uint reloadGeneration;
            public float reloadPhaseElapsed;
            public bool reloadFinishRequested;
            public string[] backpackManifest, throwableItems;
            public int[] throwableCounts;
            public uint submittedEquipment, executedEquipment;
            public string[] slots;
            public float actionRemaining, spread, bloom, ads, cooldown, speed, vertical, pitch, presentedTick;
            public Vector3 position, eye, direction, head;
            public Vector2 recoil, debt;
            public VolumeState[] volumes;
        }
        [Serializable] public sealed class VolumeState { public string name; public bool enabled; public Vector3 center, extents; }
        [Serializable] public sealed class Snapshot
        {
            public string role, scene, protocol, buildId, inputDigest, caseId, phase, uiPage, uiStatus;
            public bool apiReady, roomPending, mapBusy;
            public int seq, frame, targetFps, predicted, accepted, rejected, serverShots;
            public long rtt;
            public uint serverTick;
            public double time, utcMs;
            public float measuredFps;
            public PlayerState[] players;
        }
        [Serializable] public sealed class Record
        {
            public string kind, role, caseId, weapon, region, target, message;
            public long connection;
            public uint shotId, inputTick, life, serverTick;
            public int frame, ammo, reserve, damage, seed, pellets;
            public bool accepted, hit, damaged;
            public double time, utcMs, shotSeconds;
            public float spread, ads, bloom;
            public float gaitPhase;
            public VolumeState[] capturedVolumes;
            public Vector3 origin, direction, fired, point;
            public Snapshot snapshot;
        }
        private string _dir, _role, _case = "startup";
        private StreamWriter _events;
        private Command _input;
        private int _seq, _inputFrame, _predicted, _accepted, _rejected, _serverShots, _fpsFrames, _proxyPort;
        private double _inputUntil, _nextRead, _nextState, _start, _fpsSince, _lastClick = -10;
        private float _measuredFps;
        private FishNet.Managing.NetworkManager _network;
        private ServerLagCompensation _captureSource;
        private readonly HashSet<int> _wired = new();
        private readonly Dictionary<string, GameObject> _fixtures = new();
        private readonly Dictionary<string, PropertyInfo> _inputProperties = new();
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            var directory = Arg("-runtimeAuditDir"); var role = Arg("-runtimeAuditRole");
            if (string.IsNullOrEmpty(directory) || !new[] { "server", "alpha", "bravo", "charlie" }.Contains(role)) return;
            var obj = new GameObject("RuntimeAuditDriver"); Object.DontDestroyOnLoad(obj);
            var driver = obj.AddComponent<RuntimeAuditDriver>();
            driver._dir = Path.GetFullPath(directory); driver._role = role;
            int.TryParse(Arg("-runtimeAuditProxyPort"), out driver._proxyPort);
            Directory.CreateDirectory(driver._dir);
            driver._events = new StreamWriter(Path.Combine(driver._dir, role + ".events.jsonl"), false) { AutoFlush = true };
            driver._start = driver._fpsSince = Time.realtimeSinceStartupAsDouble;
            Application.runInBackground = true;
            Application.logMessageReceived += driver.OnLog;
            driver.Write(new Record { kind = "probe-start", message = "FPS_RUNTIME_AUDIT: explicit local test driver; evidenceDirectory="
                + Application.persistentDataPath + "; telemetry=" + PublicTestTelemetry.Enabled });
            if (role != "server") driver.StartCoroutine(driver.CaptureBootFrames());
        }

        private void Update()
        {
            if (_events == null) return;
            var now = Time.realtimeSinceStartupAsDouble;
            if (_network == null) _network = Object.FindFirstObjectByType<FishNet.Managing.NetworkManager>();
            if (_role == "server" && _captureSource != ServerLagCompensation.Instance)
            {
                ServerLagCompensation.AfterCapture -= CapturePoseEvidence;
                _captureSource = ServerLagCompensation.Instance;
                if (_captureSource != null) ServerLagCompensation.AfterCapture += CapturePoseEvidence;
            }
            if (now - _start > 3600) { Write(new Record { kind = "deadline" }); Application.Quit(); return; }
            // Preserve the production ticket, session and authentication; redirect only the UDP endpoint.
            var launch = NetworkLaunchContext.PeekClientLaunch();
            if (_proxyPort > 0 && launch != null && launch.ServerPort != _proxyPort)
            {
                launch.ServerAddress = "127.0.0.1"; launch.ServerPort = (ushort)_proxyPort;
                Write(new Record { kind = "route-fixture", message = "local UDP proxy " + _proxyPort });
            }
            _fpsFrames++;
            if (now - _fpsSince >= 1) { _measuredFps = (float)(_fpsFrames / (now - _fpsSince)); _fpsFrames = 0; _fpsSince = now; }
            if (now >= _nextRead)
            {
                _nextRead = now + .05;
                var path = Path.Combine(_dir, _role + ".command.json");
                try
                {
                    if (File.Exists(path))
                    {
                        var command = JsonUtility.FromJson<Command>(File.ReadAllText(path));
                        if (command != null && command.seq > _seq) { _seq = command.seq; Execute(command); }
                    }
                }
                catch (IOException) { } // Atomic file replacement may briefly race the reader.
                catch (Exception e) { Write(new Record { kind = "command-error", message = e.ToString() }); }
            }
            var players = Object.FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None);
            foreach (var player in players) Wire(player);
            var owner = players.FirstOrDefault(p => p.IsOwnerPlayer && !p.IsServerInitialized);
            if (owner != null) DriveInput(owner, now);
            if (now >= _nextState) { _nextState = now + .25; SaveState(players); }
        }

        private void Execute(Command c)
        {
            _case = c.caseId ?? _case;
            Write(new Record { kind = "command", message = JsonUtility.ToJson(c) });
            if (c.fps > 0) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = Mathf.Clamp(c.fps, 15, 240); }
            if (c.op == "input")
            {
                _input = c; _inputFrame = Time.frameCount; _lastClick = -10;
                _inputUntil = Time.realtimeSinceStartupAsDouble + Mathf.Clamp(c.duration, .01f, 30f); return;
            }
            if (c.op == "stop") { _input = null; return; }
            if (c.op == "quit") { Write(new Record { kind = "quit" }); Application.Quit(); return; }
            if (c.op == "resolution") { int width = c.slot > 0 ? c.slot : 1280; Screen.SetResolution(width, width * 9 / 16, FullScreenMode.Windowed); return; }
            if (c.op == "lobby-create" || c.op == "lobby-join")
            {
                var presenter = Object.FindFirstObjectByType<LobbyPresenter>();
                if (presenter == null) throw new InvalidOperationException("LobbyPresenter missing");
                var options = ClientAutoPilot.Parse();
                if (c.op == "lobby-create")
                    Invoke(presenter, "StartOnlineCreateAsync", new CreateRoomRequest { maxPlayers = options.MaxPlayers,
                        mode = options.Mode, mapId = "arena", killTarget = options.KillTarget, timeLimitMinutes = options.TimeLimitMinutes });
                else Invoke(presenter, "StartOnlineRoomAsync", File.ReadAllText(Path.Combine(_dir, "room-code.txt")).Trim(), true);
                return;
            }
            if (c.op == "key") { StartCoroutine(PressKey(c.name)); return; }
            if (c.op == "ui") { StartCoroutine(ClickUi(c.name)); return; }
            if (c.op == "ui-list") { CaptureUi(); return; }
            if (c.op == "screenshot") { StartCoroutine(Capture(c.name ?? ("frame-" + _seq))); return; }
            if (c.op == "visual") { StartCoroutine(PreviewVisualProbe.Capture(_dir, _role, c.caseId, c.duration)); return; }
            if (c.op == "map-visual") { PreviewVisualProbe.Map(_dir, _role); return; }
            if (c.op == "rework-view") { StartCoroutine(ReworkViewProbe.Capture(_dir, _role, c.name, c.duration)); return; }
            if (c.op == "shadow-fixture") { ReworkViewProbe.ShadowFixture(c.name); return; }
            if (c.op == "geometry") { Geometry(c); return; }
            if (c.op == "end-match")
            {
                if (_role != "server") throw new InvalidOperationException("Server fixture only");
                var method = typeof(MatchLifecycle).GetMethod("ServerEndMatch", BindingFlags.Static | BindingFlags.NonPublic);
                var reason = Enum.Parse(method.GetParameters()[0].ParameterType, "TimeLimit");
                method.Invoke(null, new object[] { reason, null }); return;
            }
            foreach (var p in Object.FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None)
                .Where(p => c.target >= 0 ? p.OwnerClientId == c.target : p.IsOwnerPlayer))
            {
                var w = p.GetComponent<WeaponController>(); var motor = p.GetComponent<Locomotor>();
                if (c.op == "ammo")
                {
                    w.Actions.Interrupt(ActionInterruptReason.External);
                    Invoke(w.Runtime,"RestoreAmmo",c.slot,c.health);
                    Write(new Record {kind="ammo-fixture",connection=p.OwnerClientId,ammo=c.slot,reserve=c.health});
                }
                if (c.op == "attachment")
                {
                    var catalog = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
                    w.SetAttachments(string.IsNullOrEmpty(c.name) ? Array.Empty<AttachmentAssetEntry>()
                        : new[] { catalog.Entries.First(e => e.itemId == c.name) });
                    foreach (var view in p.GetComponentsInChildren<Game.Presentation.Weapon.WeaponView>())
                    {
                        var attachments = view.GetComponent<WeaponAttachmentView>() ?? view.gameObject.AddComponent<WeaponAttachmentView>();
                        attachments.ApplyAttachments(catalog, w.Definition.CatalogItemId,
                            string.IsNullOrEmpty(c.name) ? Array.Empty<AttachmentAssetEntry>() : new[] { catalog.Entries.First(e => e.itemId == c.name) },
                            view.GetComponent<Game.Presentation.Animation.FPWeaponAnimator>() != null);
                        foreach (var spawned in attachments.Spawned)
                            if (spawned != null) foreach (var t in spawned.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = view.gameObject.layer;
                    }
                    // Explicit fixture counterpart for rigid TP views (which have no WeaponView).
                    foreach (var swapper in p.GetComponentsInChildren<Game.Presentation.Animation.TPWeaponMeshSwapper>(true))
                    {
                        var tp = swapper.CurrentInstance;
                        if (tp == null) continue;
                        var attachments = tp.GetComponent<WeaponAttachmentView>() ?? tp.AddComponent<WeaponAttachmentView>();
                        attachments.ApplyAttachments(catalog,w.Definition.CatalogItemId,
                            string.IsNullOrEmpty(c.name) ? Array.Empty<AttachmentAssetEntry>() : new[] {catalog.Entries.First(e=>e.itemId==c.name)},false);
                        foreach(var spawned in attachments.Spawned)
                            if(spawned!=null)foreach(var t in spawned.GetComponentsInChildren<Transform>(true))t.gameObject.layer=tp.layer;
                        foreach(var device in tp.GetComponentsInChildren<TacticalFlashlight>(true))
                            Game.Presentation.Weapon.TacticalFlashlightShadowFilter.Bind(device,tp);
                    }
                }
                if (c.op == "select-throw") p.GetComponent<ThrowableController>().SelectNext();
                if (c.op == "throw") p.GetComponent<ThrowableController>().TryThrow(p.GetComponent<ThrowableController>().SelectedType);
                if (c.op == "throw-reset" && p.IsServerInitialized) p.GetComponent<ThrowableController>().ServerResetInventory();
                if (c.op == "pose")
                {
                    var state = motor.CaptureSnapshot(); state.Position = c.position; state.Rotation = Quaternion.Euler(0, c.yaw, 0);
                    state.HorizontalVelocity = Vector3.zero; state.VerticalVelocity = 0; state.Grounded = true;
                    state.GroundSpeed = 0; state.Pitch = c.pitch; state.RecoilDebt = Vector2.zero;
                    motor.ApplyAuthoritativeSnapshot(state);
                    var look = p.GetComponentInChildren<FPMouseLook>(true);
                    if (look != null) { typeof(FPMouseLook).GetField("_pitch", Flags).SetValue(look, c.pitch); look.transform.localRotation = Quaternion.Euler(c.pitch, 0, 0); }
                    Write(new Record { kind = "pose-fixture", connection = p.OwnerClientId, point = c.position });
                }
                else if (c.op == "equip")
                {
                    var catalog = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
                    var entries = catalog.Entries.Where(e => e.IsLpfp && e.definition != null).ToArray();
                    var selected = entries.First(e => e.definition.WeaponId == c.weapon || e.itemId == c.weapon).definition;
                    w.Actions.Interrupt(ActionInterruptReason.External);
                    if (c.slots != null && c.slots.Length > 0)
                    {
                        var defs = c.slots.Select(id => entries.First(e => e.definition.WeaponId == id || e.itemId == id).definition).ToArray();
                        typeof(Arsenal).GetField("slots", Flags).SetValue(p.GetComponent<Arsenal>(), defs);
                    }
                    w.EquipDefinition(selected); p.GetComponent<Arsenal>().AlignToEquippedDefinition(selected);
                    // Canonical weapon matrix must not inherit this PC's fallback gunsmith preset.
                    w.SetAttachments(Array.Empty<AttachmentAssetEntry>());
                    Invoke(w.Runtime, "RestoreAmmo", w.Runtime.MagazineSize, w.Stat.ReserveAmmo);
                    Write(new Record { kind = "equip-fixture", connection = p.OwnerClientId, weapon = selected.WeaponId });
                }
                else if (c.op == "reset")
                {
                    w.Actions.Interrupt(ActionInterruptReason.External);
                    Invoke(w.Runtime, "RestoreAmmo", w.Runtime.MagazineSize, w.Stat.ReserveAmmo);
                    if (p.IsServerInitialized)
                    {
                        var target = p.GetComponentInChildren<DamageableTarget>(true);
                        if (target != null && target.IsAlive) target.ResetHealth();
                    }
                }
                else if (c.op == "kill-fixture")
                {
                    if (!p.IsServerInitialized) throw new InvalidOperationException("Server fixture only");
                    var target = p.GetComponentInChildren<DamageableTarget>(true);
                    target.ApplyDamage(1000, target.transform.position, Vector3.forward);
                }
                else if (c.op == "blast-probe")
                {
                    if (!p.IsServerInitialized) throw new InvalidOperationException("Server fixture only");
                    var target = p.GetComponentInChildren<DamageableTarget>(true);
                    var end = target.transform.position + Vector3.up * .9f;
                    var hits = Physics.RaycastAll(c.position, (end - c.position).normalized, Vector3.Distance(c.position, end), ~0, QueryTriggerInteraction.Ignore);
                    Write(new Record { kind = "blast-probe", connection = p.OwnerClientId,
                        accepted = !ThrowableProjectile.IsBlastBlockedByWorld(c.position, end),
                        message = string.Join(";", hits.Select(h => h.collider.name + ":" + h.collider.GetType().Name + ":targetParent=" + (h.collider.GetComponentInParent<DamageableTarget>() != null))) });
                }
            }
        }

        private IEnumerator PressKey(string keyName)
        {
            var keyboard=UnityEngine.InputSystem.Keyboard.current;
            if(keyboard==null)throw new InvalidOperationException("Keyboard unavailable");
            var key=(UnityEngine.InputSystem.Key)Enum.Parse(typeof(UnityEngine.InputSystem.Key),keyName,true);
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState(key));
            yield return null;yield return null;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState());
            Write(new Record{kind="keyboard-input",message=keyName});
        }

        [Serializable] private sealed class UiButtonEvidence { public string name, text; public Vector2 point; public bool interactable; }
        [Serializable] private sealed class UiEvidence { public bool eventSystem; public UiButtonEvidence[] buttons; }
        private void CaptureUi()
        {
            var buttons = Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsSortMode.None).Where(b => b.gameObject.activeInHierarchy).Select(b =>
                new UiButtonEvidence { name = b.name, text = b.GetComponentInChildren<TMPro.TMP_Text>()?.text,
                    interactable = b.interactable, point = UnityEngine.RectTransformUtility.WorldToScreenPoint(null,
                        ((RectTransform)b.transform).TransformPoint(((RectTransform)b.transform).rect.center)) }).ToArray();
            File.WriteAllText(Path.Combine(_dir, _role + ".ui.json"), JsonUtility.ToJson(new UiEvidence {
                eventSystem = UnityEngine.EventSystems.EventSystem.current != null, buttons = buttons }, true));
        }
        private IEnumerator ClickUi(string name)
        {
            CaptureUi();
            var button = Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsSortMode.None).First(b => b.gameObject.activeInHierarchy
                && (b.name == name || b.GetComponentInChildren<TMPro.TMP_Text>()?.text == name));
            var point = UnityEngine.RectTransformUtility.WorldToScreenPoint(null, ((RectTransform)button.transform).TransformPoint(((RectTransform)button.transform).rect.center));
            var mouse = UnityEngine.InputSystem.Mouse.current ?? UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Mouse>();
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(mouse, new UnityEngine.InputSystem.LowLevel.MouseState { position = point });
            yield return null; yield return null;
            var raycasts = new List<UnityEngine.EventSystems.RaycastResult>();
            var events = UnityEngine.EventSystems.EventSystem.current;
            if (events != null) events.RaycastAll(new UnityEngine.EventSystems.PointerEventData(events) { position = point }, raycasts);
            Write(new Record { kind = "ui-pointer", message = name + " hits=" + string.Join(",", raycasts.Select(r => r.gameObject.name)) });
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(mouse, new UnityEngine.InputSystem.LowLevel.MouseState { position = point }.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left));
            yield return null; yield return null;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(mouse, new UnityEngine.InputSystem.LowLevel.MouseState { position = point });
        }
        private IEnumerator CaptureBootFrames()
        {
            while (!UnityEngine.Rendering.SplashScreen.isFinished) yield return null;
            for (int i = 0; i < 24; i++)
            {
                yield return new WaitForEndOfFrame();
                var image = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(_dir, _role + "-boot-" + i.ToString("D2") + ".png"), image.EncodeToPNG());
                Destroy(image);
                Write(new Record { kind = "boot-frame", message = SceneManager.GetActiveScene().name });
                yield return new WaitForSecondsRealtime(.1f);
            }
        }

        private void DriveInput(NetworkCombatAuthority p, double now)
        {
            var input = p.GetComponent<InputReader>(); if (input == null) return;
            // InputReader has sampled/reset this frame (-300); inject explicit virtual-player intent (-250).
            var c = now <= _inputUntil ? _input : null;
            SetInput(input, "Move", c != null ? c.move : Vector2.zero);
            SetInput(input, "Sprint", c != null && c.sprint);
            SetInput(input, "LookDelta", c != null ? c.look : Vector2.zero);
            SetInput(input, "AimHeld", c != null && c.aim);
            SetInput(input, "FireHeld", c != null && c.fire);
            bool click = c != null && c.fire && (Time.frameCount == _inputFrame || c.clickInterval > 0 && now - _lastClick >= c.clickInterval);
            SetInput(input, "FirePressed", click);
            if (click) _lastClick = now;
            // Keep an explicit reload intent for its bounded command duration. A one-render-frame
            // reflection pulse can fall between player ticks while PNG capture blocks rendering.
            SetInput(input, "ReloadPressed", c != null && c.reload);
            SetInput(input, "SlotPressed", c != null && Time.frameCount == _inputFrame ? c.slot : -1);
            SetInput(input, "SwapAxis", 0f);
            SetInput(input, "QuickSwapPressed", false);
            SetInput(input, "LeanIntent", (sbyte)(c != null ? Mathf.Clamp(c.lean, -1, 1) : 0));
            if (c != null && c.jump && Time.frameCount == _inputFrame) typeof(InputReader).GetField("_jumpBufferTimer", Flags).SetValue(input, .15f);
            if (c != null && c.aimTarget >= 0)
            {
                var target = Object.FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None).FirstOrDefault(x => x.OwnerClientId == c.aimTarget);
                var camera = Camera.main;
                if (target != null && camera != null)
                {
                    var desired = (target.transform.position + Vector3.up * c.aimHeight - camera.transform.position).normalized;
                    var current = camera.transform.forward;
                    var yaw = Mathf.DeltaAngle(Mathf.Atan2(current.x, current.z) * Mathf.Rad2Deg, Mathf.Atan2(desired.x, desired.z) * Mathf.Rad2Deg);
                    var up = (Mathf.Asin(desired.y) - Mathf.Asin(current.y)) * Mathf.Rad2Deg;
                    SetInput(input, "LookDelta", new Vector2(Mathf.Clamp(yaw * 10, -300, 300), Mathf.Clamp(up * 10, -300, 300)));
                }
            }
        }

        private void SetInput(InputReader input, string name, object value)
        {
            if (!_inputProperties.TryGetValue(name, out var property)) _inputProperties[name] = property = typeof(InputReader).GetProperty(name, Flags);
            property.SetValue(input, value);
        }
        private static object Invoke(object obj, string method, params object[] args) => obj.GetType().GetMethod(method, Flags).Invoke(obj, args);
        private static T Field<T>(object obj, string name) { var f = obj?.GetType().GetField(name, Flags); return f == null ? default : (T)f.GetValue(obj); }

        private void Wire(NetworkCombatAuthority p)
        {
            if (!p.IsSpawned || !_wired.Add(p.GetInstanceID())) return;
            var w = p.GetComponent<WeaponController>();
            p.OnOwnerPredictedShot += (shot, id) => { _predicted++; RecordShot("predicted", p, w, shot, id); };
            if (p.IsServerInitialized) w.OnShotFired += shot => { _serverShots++; RecordShot("server-shot", p, w, shot, Field<uint>(p, "_pendingShotRequestId")); };
            p.OnShotConfirmed += (shot, accepted) =>
            {
                if (accepted) _accepted++; else _rejected++;
                Write(new Record { kind = "confirm", connection = p.OwnerClientId, shotId = shot.ShotRequestId, life = shot.LifeEpoch,
                    accepted = accepted, weapon = shot.WeaponId, damage = shot.DamageAmount, hit = shot.FinalHit,
                    point = shot.FinalPoint, fired = shot.FiredDirection, region = shot.BodyRegion.ToString(), pellets = shot.PelletCount,
                    ammo = w.Runtime?.CurrentAmmo ?? -1, reserve = w.Runtime?.ReserveAmmo ?? -1 });
            };
            var target = p.GetComponentInChildren<DamageableTarget>(true);
            if (target != null && p.IsServerInitialized)
            {
                target.OnHealthChanged += (health, max) => Write(new Record { kind = "health", connection = p.OwnerClientId, damage = health });
                target.OnDied += () => Write(new Record { kind = "death", connection = p.OwnerClientId, snapshot = CaptureState() });
            }
        }
        private void RecordShot(string kind, NetworkCombatAuthority p, WeaponController w, WeaponShot s, uint id)
        {
            var adapter = p.GetComponent<PlayerNetworkAdapter>();
            Write(new Record { kind = kind, connection = p.OwnerClientId, shotId = id, inputTick = adapter != null ? adapter.LocalInputTick : 0,
                life = p.LifeEpochForPresentation, weapon = w.Definition.WeaponId, shotSeconds = s.ShotSeconds, origin = s.Origin, direction = s.Direction,
                fired = s.FiredDirection, point = s.Result.Point, hit = s.Result.Hit, damaged = s.Result.Damaged,
                target = s.Result.Target != null ? s.Result.Target.transform.root.name : "", region = s.Result.BodyRegion.ToString(),
                damage = s.Result.DamageAmount, spread = s.FinalSpreadDegrees, ads = s.Ads01, seed = s.Seed,
                bloom = Field<WeaponAccuracyState>(w, "_accuracy")?.CurrentBloom ?? -1,
                ammo = w.Runtime.CurrentAmmo, reserve = w.Runtime.ReserveAmmo, pellets = s.Pellets?.Length ?? 1 });
        }
        private Snapshot CaptureState(NetworkCombatAuthority[] players = null)
        {
            var manifest = GameProtocolIdentity.TryReadDeployedManifest(); var nm = _network;
            var presenter = Object.FindFirstObjectByType<LobbyPresenter>();
            var snapshot = new Snapshot { role = _role, scene = SceneManager.GetActiveScene().name, protocol = GameProtocolIdentity.ProtocolId,
                buildId = manifest?.buildId, inputDigest = manifest?.inputDigest, caseId = _case, seq = _seq, frame = Time.frameCount,
                time = Time.realtimeSinceStartupAsDouble, utcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), measuredFps = _measuredFps,
                targetFps = Application.targetFrameRate, predicted = _predicted, accepted = _accepted, rejected = _rejected,
                serverShots = _serverShots, rtt = nm != null ? nm.TimeManager.RoundTripTime : -1, serverTick = nm != null ? nm.TimeManager.Tick : 0,
                phase = MatchLifecycle.Phase.ToString(), uiPage = presenter != null ? typeof(LobbyPresenter).GetField("currentPage", Flags)?.GetValue(presenter)?.ToString() : "",
                uiStatus = presenter != null ? Field<TMPro.TMP_Text>(presenter, "status")?.text : "",
                apiReady = presenter != null && Field<bool>(presenter, "apiAvailable"),
                roomPending = presenter != null && Field<bool>(presenter, "roomEntryPending"), mapBusy = MapContentUpdater.Busy };
            snapshot.players = (players ?? Object.FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None)).Where(p => p.IsSpawned).Select(p =>
            {
                var w = p.GetComponent<WeaponController>(); var m = p.GetComponent<Locomotor>(); var ms = m != null ? m.CaptureSnapshot() : default;
                var a = p.GetComponent<PlayerNetworkAdapter>(); var throwable=p.GetComponent<ThrowableController>(); var damage = p.GetComponentsInChildren<Collider>(true)
                    .Where(c => c.GetComponent<HitVolumeTag>()?.Role == HitVolumeRole.DamageSurface).ToArray();
                var head = damage.FirstOrDefault(c => c.name.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0);
                return new PlayerState { connection = p.OwnerClientId, objectId = p.ObjectId, name = p.DisplayName, owner = p.IsOwnerPlayer,
                    team = p.TeamId, submittedEquipment = Field<uint>(p, "_submittedEquipmentCommand"),
                    executedEquipment = Field<uint>(p, "_executedEquipmentCommand"),
                    server = p.IsServerInitialized, health = p.Health, dead = p.IsDead, protectedNow = p.IsInvincibleNow, life = p.LifeEpochForPresentation,
                    respawnTick = p.RespawnAtTick, position = p.transform.position, pitch = ms.Pitch, grounded = ms.Grounded, speed = ms.GroundSpeed,
                    vertical = ms.VerticalVelocity, inputTick = a != null ? (p.IsServerInitialized ? a.ServerInputTick : a.LocalInputTick) : 0, weapon = w?.Definition?.WeaponId,
                    ammo = w?.Runtime?.CurrentAmmo ?? -1, reserve = w?.Runtime?.ReserveAmmo ?? -1,
                    action = w?.Actions?.CurrentAction.ToString(), actionRemaining = w?.Actions?.Remaining ?? 0,
                    reloadPhase=w?.ReloadPhase.ToString(),reloadPhaseElapsed=w?.ReloadPhaseElapsed??0,reloadGeneration=w?.ReloadGeneration??0,reloadFinishRequested=w!=null&&w.ReloadFinishRequested,
                    activeBackpack=p.GetComponent<NetworkWeaponState>()?.ActiveBackpackIndex??-1,backpackEligibility=p.OwnerBackpackEligibility.ToString(),backpackManifest=a?.OwnerBackpackManifest,
                    throwableItems=throwable!=null?new[]{throwable.Inventory.Item(0),throwable.Inventory.Item(1),throwable.Inventory.Item(2)}:null,
                    throwableCounts=throwable!=null?new[]{throwable.Inventory.Count(0),throwable.Inventory.Count(1),throwable.Inventory.Count(2)}:null,
                    runtimeState = w?.Runtime?.State.ToString(), spread = w?.CurrentSpreadDegrees ?? 0, cooldown = w?.Runtime?.CooldownRemaining ?? 0,
                    recoil = w?.CurrentRecoilOffset ?? Vector2.zero, debt = w?.RecoilCompensationDebt ?? Vector2.zero,
                    eye = w?.AimOrigin ?? Vector3.zero, direction = w?.AimDirection ?? Vector3.forward,
                    bloom = Field<WeaponAccuracyState>(w, "_accuracy")?.CurrentBloom ?? 0, ads = p.GetComponent<PlayerAimState>()?.Ads01 ?? 0,
                    damageVolumes = damage.Length, enabledDamageVolumes = damage.Count(c => c.enabled), kills = p.Kills, deaths = p.Deaths,
                    head = head != null ? head.bounds.center : Vector3.zero,
                    slots = p.GetComponent<Arsenal>()?.Slots.Select(d => d != null ? d.WeaponId : "").ToArray(),
                    volumes = damage.Select(c => new VolumeState { name = c.name, enabled = c.enabled, center = c.bounds.center, extents = c.bounds.extents }).ToArray() };
            }).ToArray();
            return snapshot;
        }
        private void SaveState(NetworkCombatAuthority[] players)
        {
            var state = CaptureState(players); var path = Path.Combine(_dir, _role + ".state.json");
            var temp = path + ".tmp"; File.WriteAllText(temp, JsonUtility.ToJson(state));
            try { if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
            catch (IOException) { } // A read-only observer may briefly hold a Windows handle without delete sharing.
            Write(new Record { kind = "state", snapshot = state });
        }
        private void OnLog(string message, string trace, LogType type)
        {
            if (_events == null) return;
            if (message.StartsWith("[FireTrace]") || message.StartsWith("[CombatRay]") || message.StartsWith("[CombatDamage]")
                || message.StartsWith("[Day3][Reconcile]") || type == LogType.Exception)
                Write(new Record { kind = "game-log", message = message });
        }
        private void Write(Record r)
        {
            if (_events == null) return; r.role = _role; r.caseId = _case; r.time = Time.realtimeSinceStartupAsDouble;
            r.utcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); r.frame = Time.frameCount;
            if (_network != null) r.serverTick = _network.TimeManager.Tick;
            _events.WriteLine(JsonUtility.ToJson(r));
        }
        private void Geometry(Command c)
        {
            if (string.IsNullOrEmpty(c.name)) return;
            if (_fixtures.TryGetValue(c.name, out var old)) Destroy(old);
            if (c.scale == Vector3.zero) { _fixtures.Remove(c.name); return; }
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube); box.name = "AuditFixture_" + c.name;
            box.transform.position = c.position; box.transform.localScale = c.scale; box.GetComponent<Collider>().isTrigger = c.trigger;
            _fixtures[c.name] = box; Physics.SyncTransforms();
        }
        private IEnumerator Capture(string name)
        {
            yield return new WaitForEndOfFrame();
            var image = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(_dir, _role + "-" + Path.GetFileName(name) + ".png"), image.EncodeToPNG());
            Destroy(image); Write(new Record { kind = "screenshot", message = name });
        }
        private void CapturePoseEvidence()
        {
            // Explicit diagnostic cases only: capture the actual post-tick bone volumes,
            // including multiple network ticks occurring inside one rendering frame.
            if (_events == null || !(_case.Contains("server-fps-") || _case.Contains("movement-"))) return;
            foreach (var p in Object.FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
            {
                if (!p.IsSpawned || !p.IsServerInitialized) continue;
                var m = p.GetComponent<Locomotor>();
                Write(new Record { kind = "pose-capture", connection = p.OwnerClientId,
                    life = p.LifeEpochForPresentation, inputTick = p.GetComponent<PlayerNetworkAdapter>().ServerInputTick,
                    gaitPhase = m != null ? m.GaitPhase : 0, origin = p.transform.position,
                    capturedVolumes = p.GetComponentsInChildren<Collider>(true)
                        .Where(c => c.GetComponent<HitVolumeTag>()?.Role == HitVolumeRole.DamageSurface)
                        .Select(c => new VolumeState { name = c.name, enabled = c.enabled,
                            center = p.transform.InverseTransformPoint(c.transform.position) }).ToArray() });
            }
        }
        private void OnDestroy() { ServerLagCompensation.AfterCapture -= CapturePoseEvidence; Application.logMessageReceived -= OnLog; _events?.Dispose(); _events = null; }
    }
}
