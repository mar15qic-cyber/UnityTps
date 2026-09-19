using System.Collections;
using System.Reflection;
using Game.Gameplay.Action;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Weapon;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Presentation.PlayModeTests
{
    /// <summary>
    /// ADS 开火抖动修复的运行时采样回归（Docs/24 抖动审计验收）：
    /// 在 Arena_LPWTest 真实场景中逐帧采样 Legacy 自动步枪（G36）、完整 V2 AUG、
    /// 半自动手枪在 60/120 FPS 下的 ADS 开火表现，锁定：
    /// ① 路由：ProceduralOnly 每发恰好一次 ProceduralFire、零 AimFire 重启；
    /// ② 数值有限且单帧变化有界；
    /// ③ 不存在连续正负交替的一帧振荡；
    /// ④ 后坐恢复后无累计漂移；
    /// ⑤ 枪模不进近裁剪面/不全屏放大/不消失；
    /// ⑥ FireRay 与屏幕中心对齐不回归（相机后坐力保持开启并正常回落）。
    /// 驱动方式：禁用 InputReader 采样，经反射写入其意图属性（与真实输入同一消费链路：
    /// PlayerAimState/FPWeaponAnimator FSM/WeaponController 自动与半自动开火均只读这些属性）。
    /// </summary>
    public sealed class FPWeaponAdsFireSamplingTests
    {
        private const string ScenePath = "Assets/_Project/Scenes/Arena_LPWTest.unity";
        private const string LegacyAutoRifleId = "lpw.rifle.03";   // G36（Legacy 自动步枪）
        private const string CompleteV2RifleId = "lpw.rifle.02";   // AUG（完整 Anchored Dual Pose V2）
        private const string PistolId = "lpw.pistol.01";          // 半自动手枪

        [UnityTest]
        public IEnumerator LegacyRifleG36_120Fps_AutoAdsFire()
        {
            yield return RunScenario(LegacyAutoRifleId, 120f);
        }

        [UnityTest]
        public IEnumerator LegacyRifleG36_60Fps_AutoAdsFire()
        {
            yield return RunScenario(LegacyAutoRifleId, 60f);
        }

        [UnityTest]
        public IEnumerator CompleteV2Aug_120Fps_AutoAdsFire()
        {
            yield return RunScenario(CompleteV2RifleId, 120f);
        }

        [UnityTest]
        public IEnumerator CompleteV2Aug_60Fps_AutoAdsFire()
        {
            yield return RunScenario(CompleteV2RifleId, 60f);
        }

        [UnityTest]
        public IEnumerator Pistol_120Fps_SemiAutoAdsFire()
        {
            yield return RunScenario(PistolId, 120f);
        }

        [UnityTest]
        public IEnumerator Pistol_60Fps_SemiAutoAdsFire()
        {
            yield return RunScenario(PistolId, 60f);
        }

        // ------------------------------------------------------------------

        private static IEnumerator RunScenario(string weaponId, float fps)
        {
            Assert.That(fps, Is.GreaterThan(10f));
            using (var sceneScope = SceneScope.Load(ScenePath))
            {
                yield return sceneScope.WaitLoaded();

                GameObject player = FindPlayer();
                if (player == null) yield break;
                if (!player.activeInHierarchy) player.SetActive(true);

                var controller = player.GetComponent<WeaponController>();
                var aim = player.GetComponentInChildren<PlayerAimState>();
                var actions = player.GetComponent<ActionSystem>();
                var inputReader = player.GetComponent<InputReader>();
                var arsenal = player.GetComponent<Arsenal>();
                Assert.That(controller, Is.Not.Null, "WeaponController missing");
                Assert.That(aim, Is.Not.Null, "PlayerAimState missing");

                var mainCam = UnityEngine.Camera.main;
                Assert.That(mainCam, Is.Not.Null, "Main Camera missing");
                var fpRoot = mainCam.transform.Find("FP_Weapon_Root");
                var viewCamTransform = mainCam.transform.Find("FP View Camera");
                Assert.That(fpRoot, Is.Not.Null, "FP_Weapon_Root missing");
                Assert.That(viewCamTransform, Is.Not.Null, "FP View Camera missing");
                var viewCam = viewCamTransform.GetComponent<UnityEngine.Camera>();

                yield return WaitUntilControllerInitialized(controller);
                yield return SwitchToWeapon(arsenal, actions, weaponId);
                yield return WaitUntilViewReady(fpRoot, controller);

                WeaponView view = FindActiveView(fpRoot);
                Assert.That(view, Is.Not.Null, weaponId + " 没有激活的 WeaponView");
                var profile = view.GetComponent<FPWeaponPoseProfile>();
                var animator = view.GetComponent<FPWeaponAnimator>();
                Assert.That(animator, Is.Not.Null);

                var animField = typeof(FPWeaponAnimator).GetField("_proceduralAdsFire",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var timerField = typeof(FPWeaponAnimator).GetField("_aimFireTimer",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                bool routingFlag = animField != null && (bool)animField.GetValue(animator);
                Assert.That(routingFlag, Is.True,
                    weaponId + " ProceduralOnly 必须路由到程序化开火（不得再用完整 V2 门控 Legacy 武器）");

                bool automatic = controller.Definition.FireMode == WeaponFireMode.Automatic;
                var aimHeldProp = typeof(InputReader).GetProperty("AimHeld",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fireHeldProp = typeof(InputReader).GetProperty("FireHeld",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var firePressedProp = typeof(InputReader).GetProperty("FirePressed",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var ammoProp = typeof(WeaponRuntime).GetProperty("CurrentAmmo",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                Renderer[] gunRenderers = profile != null && profile.WeaponRoot != null
                    ? profile.WeaponRoot.GetComponentsInChildren<Renderer>(true) : null;
                Transform aimPoint = view.SightReference != null ? view.SightReference : view.Muzzle;
                Transform gunRoot = profile != null ? profile.WeaponRoot : null;

                // ---- 采样缓冲 ----
                const int Baseline = 10;
                const int Burst = 100;
                const int Recovery = 90;
                const int PostRecovery = 12; // 相机后坐债务完全恢复后的末段采样（漂移判定窗口）
                int N = Baseline + Burst + Recovery + PostRecovery;
                var rootPos = new Vector3[N]; var rootRot = new Quaternion[N];
                var gunPos = new Vector3[N]; var gunRot = new Quaternion[N];
                var camRot = new Quaternion[N];
                var sightVP = new Vector2[N];
                var gunMinDist = new float[N]; var gunVisible = new bool[N]; var insideGun = new bool[N];
                var fireRayAngle = new float[N];
                bool aimFireActive = false;
                int aimFireFrames = 0, procFireAtStart = animator.ProceduralFireCount;

                inputReader.enabled = false; // 接管意图属性（消费链路与真实输入一致）
                float prevCapture = Time.captureDeltaTime;
                Time.captureDeltaTime = 1f / fps; // 固定步长：60/120 FPS 逐帧采样，剔除编辑器卡顿噪声
                try
                {
                    aimHeldProp.SetValue(inputReader, true);
                    int guard = 0;
                    while (aim.Ads01 < 1f && guard++ < 600) yield return null;
                    for (int i = 0; i < 20; i++) yield return null; // 入镜收敛 + 瞄准解冻结

                    int idx = 0;
                    for (int phase = 0; phase < 4; phase++)
                    {
                        bool firing = phase == 1;
                        if (firing && automatic) fireHeldProp.SetValue(inputReader, true);
                        if (phase == 3)
                        {
                            // 相机后坐债务完全恢复后再取末段：WeaponRecoilState 线性恢复
                            //（12° 债务 ≈2s）期间刚性连带使共享平移持续微调——那是恢复
                            // 过程而非漂移；漂移断言只在债务归零后的帧窗判定。
                            int settle = 0;
                            while (controller.CurrentRecoilOffset.magnitude > 0.02f && settle++ < 900)
                                yield return null;
                        }
                        int count = phase == 0 ? Baseline : phase == 1 ? Burst : phase == 2 ? Recovery : PostRecovery;
                        for (int f = 0; f < count; f++)
                        {
                            if (firing)
                            {
                                // 满弹供弹：本测试锁定表现稳定性，不测弹药耗尽
                                ammoProp.SetValue(controller.Runtime, controller.Runtime.MagazineSize);
                                if (!automatic) firePressedProp.SetValue(inputReader, true);
                            }
                            else
                            {
                                fireHeldProp.SetValue(inputReader, false);
                                firePressedProp.SetValue(inputReader, false);
                            }

                            int i = idx++;
                            rootPos[i] = fpRoot.localPosition;
                            rootRot[i] = fpRoot.localRotation;
                            gunPos[i] = gunRoot != null ? gunRoot.localPosition : Vector3.zero;
                            gunRot[i] = gunRoot != null ? gunRoot.localRotation : Quaternion.identity;
                            camRot[i] = mainCam.transform.rotation;
                            if (aimPoint != null)
                            {
                                var vp = viewCam.WorldToViewportPoint(aimPoint.position);
                                sightVP[i] = new Vector2(vp.x, vp.y);
                            }
                            float minD = float.MaxValue; bool inside = false; bool visible = false;
                            if (gunRenderers != null)
                            {
                                Vector3 cp = viewCam.transform.position;
                                foreach (var r in gunRenderers)
                                {
                                    if (r == null || !r.enabled) continue;
                                    var b = r.bounds;
                                    if (b.Contains(cp)) { inside = true; minD = 0f; }
                                    else minD = Mathf.Min(minD, Vector3.Distance(cp, b.ClosestPoint(cp)));
                                    if (r.isVisible) visible = true;
                                }
                            }
                            gunMinDist[i] = minD == float.MaxValue ? -1f : minD;
                            insideGun[i] = inside; gunVisible[i] = visible;
                            fireRayAngle[i] = Vector3.Angle(controller.AimDirection, mainCam.transform.forward);
                            aimFireActive = timerField != null && (float)timerField.GetValue(animator) > 0f;
                            if (firing && aimFireActive) aimFireFrames++;
                            yield return null;
                        }
                    }
                    fireHeldProp.SetValue(inputReader, false);
                    firePressedProp.SetValue(inputReader, false);
                    aimHeldProp.SetValue(inputReader, false);
                }
                finally
                {
                    Time.captureDeltaTime = prevCapture;
                    inputReader.enabled = true;
                }

                // ---- 断言 ----
                int procFired = animator.ProceduralFireCount - procFireAtStart;
                Assert.That(aimFireFrames, Is.EqualTo(0),
                    weaponId + "@" + fps + " ADS 开火期间不得有任何 AimFire 播放/FromStart 重启（实测 " + aimFireFrames + "）");
                Assert.That(procFired, Is.GreaterThan(3), weaponId + "@" + fps + " 应发生多次程序化开火（实测 " + procFired + "）");

                int burstEnd = Baseline + Burst;
                float nearClip = viewCam.nearClipPlane;
                int nonFinite = 0, invisible = 0, nearClipFrames = 0, insideFrames = 0;
                float maxRootPosDelta = 0f, maxRootRotDelta = 0f, maxGunRotDelta = 0f, maxCamRotDelta = 0f;
                float maxSightDelta = 0f, minDist = float.MaxValue, maxFireRay = 0f;
                var maxRootPosDeltaAt = "";
                for (int i = Baseline; i < burstEnd; i++)
                {
                    if (!Finite(rootPos[i]) || !Finite(rootRot[i]) || !Finite(gunPos[i])
                        || !Finite(gunRot[i]) || !Finite(camRot[i]) || !float.IsFinite(sightVP[i].x)) nonFinite++;
                    if (!gunVisible[i]) invisible++;
                    if (insideGun[i]) insideFrames++;
                    if (gunMinDist[i] >= 0f && gunMinDist[i] <= nearClip) nearClipFrames++;
                    if (gunMinDist[i] >= 0f) minDist = Mathf.Min(minDist, gunMinDist[i]);
                    if (i > Baseline)
                    {
                        float posDelta = Vector3.Distance(rootPos[i], rootPos[i - 1]);
                        if (posDelta > maxRootPosDelta) { maxRootPosDelta = posDelta; maxRootPosDeltaAt = "frame " + i; }
                        maxRootRotDelta = Mathf.Max(maxRootRotDelta, Quaternion.Angle(rootRot[i], rootRot[i - 1]));
                        maxGunRotDelta = Mathf.Max(maxGunRotDelta, Quaternion.Angle(gunRot[i], gunRot[i - 1]));
                        maxCamRotDelta = Mathf.Max(maxCamRotDelta, Quaternion.Angle(camRot[i], camRot[i - 1]));
                        maxSightDelta = Mathf.Max(maxSightDelta, Vector2.Distance(sightVP[i], sightVP[i - 1]));
                    }
                    maxFireRay = Mathf.Max(maxFireRay, fireRayAngle[i]);
                }

                Assert.That(nonFinite, Is.EqualTo(0), weaponId + "@" + fps + " 出现非有限姿态值");
                Assert.That(maxRootPosDelta, Is.LessThan(0.15f),
                    weaponId + "@" + fps + " FP_Weapon_Root 单帧位置变化超界（" + maxRootPosDelta.ToString("F3") + " @ " + maxRootPosDeltaAt + "）");
                Assert.That(maxRootRotDelta, Is.LessThan(20f),
                    weaponId + "@" + fps + " FP_Weapon_Root 单帧旋转变化超界（" + maxRootRotDelta.ToString("F2") + "°）");
                Assert.That(maxGunRotDelta, Is.LessThan(20f),
                    weaponId + "@" + fps + " WeaponRoot 单帧旋转变化超界（" + maxGunRotDelta.ToString("F2") + "°）");
                Assert.That(maxCamRotDelta, Is.LessThan(15f),
                    weaponId + "@" + fps + " 相机单帧旋转变化超界（" + maxCamRotDelta.ToString("F2") + "°）");
                Assert.That(maxSightDelta, Is.LessThan(0.25f),
                    weaponId + "@" + fps + " 瞄具屏幕位置单帧变化超界（" + maxSightDelta.ToString("F3") +
                    "；上限容纳 V2 枪层弹簧的单次冲量帧 ~0.12，远低于修复前病理值 14.8；高频振荡由交替断言捕捉）");
                Assert.That(maxFireRay, Is.LessThan(2f),
                    weaponId + "@" + fps + " FireRay 与屏幕中心射线偏差超界（" + maxFireRay.ToString("F2") + "°）");

                // 近裁剪/消失：枪模任何帧不得进入近裁剪面、相机不得在枪内、可见率 ≥95%
                Assert.That(insideFrames, Is.EqualTo(0), weaponId + "@" + fps + " 相机进入枪模包围盒帧数 " + insideFrames);
                Assert.That(nearClipFrames, Is.EqualTo(0), weaponId + "@" + fps + " 枪模进入近裁剪面帧数 " + nearClipFrames);
                Assert.That((double)invisible / Burst, Is.LessThan(0.05),
                    weaponId + "@" + fps + " 枪模不可见帧 " + invisible + "/" + Burst);

                // 连续正负交替的一帧振荡（位置各轴；旋转用相邻帧角增量序列）
                int altRun = MaxAlternationRun(Baseline, burstEnd, i => rootPos[i].x, 2e-4f);
                Assert.That(altRun, Is.LessThanOrEqualTo(8),
                    weaponId + "@" + fps + " rootPos.x 连续符号交替 " + altRun + " 帧（修复前实测 26+）");
                float rotDelta(int i) => Quaternion.Angle(rootRot[i], rootRot[i - 1]);
                int rotAltRun = MaxAlternationRun(Baseline + 1, burstEnd, rotDelta, 0.05f);
                Assert.That(rotAltRun, Is.LessThanOrEqualTo(8),
                    weaponId + "@" + fps + " 旋转增量连续交替 " + rotAltRun + " 帧");

                // 恢复后无累计漂移：末段均值 vs 基线段均值（位置 + 朝向 + 瞄具屏幕位置）
                Vector3 posDrift = Mean(Baseline, 5, i => rootPos[i]) - Mean(N - 5, 5, i => rootPos[i]);
                Assert.That(posDrift.magnitude, Is.LessThan(0.01f),
                    weaponId + "@" + fps + " 恢复后位置漂移 " + posDrift.ToString("F4"));
                Vector3 dirBase = rootRot[Baseline + 4] * Vector3.forward;
                Vector3 dirEnd = rootRot[N - 1] * Vector3.forward;
                Assert.That(Vector3.Angle(dirBase, dirEnd), Is.LessThan(2f),
                    weaponId + "@" + fps + " 恢复后朝向漂移 " + Vector3.Angle(dirBase, dirEnd).ToString("F2") + "°");
                Vector2 sightDrift = sightVP[N - 1] - sightVP[Baseline];
                Assert.That(sightDrift.magnitude, Is.LessThan(0.05f),
                    weaponId + "@" + fps + " 恢复后瞄具屏幕漂移 " + sightDrift.ToString("F3"));

                // 相机后坐力保持开启：连射期相机必须踢动且停火后回落（不得以清零方式掩盖）
                float basePitch = PitchOf(camRot[Baseline]);
                float minPitch = 0f;
                for (int i = Baseline; i < burstEnd; i++) minPitch = Mathf.Min(minPitch, PitchOf(camRot[i]));
                Assert.That(basePitch - minPitch, Is.GreaterThan(0.3f),
                    weaponId + "@" + fps + " 相机后坐踢动缺失（不得关闭相机后坐力）");
                Assert.That(Mathf.Abs(PitchOf(camRot[N - 1]) - basePitch), Is.LessThan(1.5f),
                    weaponId + "@" + fps + " 相机后坐未回落（" + PitchOf(camRot[N - 1]).ToString("F2") + " vs " + basePitch.ToString("F2") + "）");

                Debug.Log($"[AdsFireSampling] {weaponId}@{fps}FPS auto={automatic} shots={procFired} " +
                          $"maxRootPosΔ={maxRootPosDelta:F4} maxRootRotΔ={maxRootRotDelta:F2}° maxGunRotΔ={maxGunRotDelta:F2}° " +
                          $"maxCamRotΔ={maxCamRotDelta:F2}° maxSightΔ={maxSightDelta:F4} gunMinDist={minDist:F4} " +
                          $"fireRayMax={maxFireRay:F2}° altRun={altRun}/{rotAltRun} camKick={basePitch - minPitch:F2}° " +
                          $"posDrift={posDrift.magnitude:F5} sightDrift={sightDrift.magnitude:F4} invisible={invisible}/{Burst}");
            }
        }

        // ------------------------------------------------------------------

        private static float PitchOf(Quaternion rotation)
        {
            float pitch = rotation.eulerAngles.x;
            return pitch > 180f ? pitch - 360f : pitch;
        }

        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        private static bool Finite(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y)
            && float.IsFinite(q.z) && float.IsFinite(q.w);

        private static Vector3 Mean(int from, int count, System.Func<int, Vector3> src)
        {
            var sum = Vector3.zero;
            for (int i = from; i < from + count; i++) sum += src(i);
            return sum / Mathf.Max(1, count);
        }

        private static int MaxAlternationRun(int from, int to, System.Func<int, float> series, float epsilon)
        {
            int maxRun = 0, run = 0, prevSign = 0;
            for (int i = from + 1; i < to; i++)
            {
                float delta = series(i) - series(i - 1);
                if (Mathf.Abs(delta) < epsilon) continue;
                int sign = delta > 0f ? 1 : -1;
                if (prevSign != 0 && sign != prevSign) { run++; if (run > maxRun) maxRun = run; }
                else run = 0;
                prevSign = sign;
            }
            return maxRun;
        }

        private static GameObject FindPlayer()
        {
            var players = Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var go in players)
                if (go.name == "Player" && go.transform.parent == null) return go;
            Assert.Fail("场景中没有 Player 根对象");
            return null;
        }

        private static IEnumerator WaitUntilControllerInitialized(WeaponController controller)
        {
            int guard = 0;
            while (!controller.IsInitialized && guard++ < 600) yield return null;
            Assert.That(controller.IsInitialized, Is.True, "WeaponController 未初始化");
        }

        private static IEnumerator SwitchToWeapon(Arsenal arsenal, ActionSystem actions, string weaponId)
        {
            // Arsenal 的槽位由 Arena_LPWTest 内的 LPWAimInTestOverlay（Assembly-CSharp）
            // 按 manifest 顺序配置；测试程序集不能引用该程序集，经反射读私有槽位数组定位。
            var slotsField = typeof(Arsenal).GetField("slots", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(slotsField, Is.Not.Null, "Arsenal.slots 字段缺失");
            var slots = slotsField.GetValue(arsenal) as WeaponDefinition[];
            Assert.That(slots, Is.Not.Null, "Arsenal 槽位未配置");
            int index = -1;
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] != null && slots[i].WeaponId == weaponId) { index = i; break; }
            Assert.That(index, Is.GreaterThanOrEqualTo(0), "槽位中找不到 " + weaponId);

            if (arsenal.ActiveIndex == index) yield break;
            Assert.That(arsenal.TrySelectSlot(index), Is.True, "TrySelectSlot(" + weaponId + ") 失败");
            int guard = 0;
            while (guard++ < 900)
            {
                yield return null;
                if (arsenal.ActiveIndex == index
                    && actions.CurrentAction != PlayerActionType.SwitchWeapon)
                    break;
            }
            Assert.That(arsenal.ActiveIndex, Is.EqualTo(index), weaponId + " 切枪未完成");
            for (int i = 0; i < 10; i++) yield return null; // 出枪动画收尾
        }

        private static IEnumerator WaitUntilViewReady(Transform fpRoot, WeaponController controller)
        {
            int guard = 0;
            while (guard++ < 600)
            {
                WeaponView view = FindActiveView(fpRoot);
                if (view != null && controller.Definition != null
                    && controller.Definition.FirstPersonViewPrefab != null
                    && view.name == controller.Definition.FirstPersonViewPrefab.name)
                    break;
                yield return null;
            }
        }

        private static WeaponView FindActiveView(Transform fpRoot)
        {
            WeaponView best = null;
            foreach (var candidate in fpRoot.GetComponentsInChildren<WeaponView>(false))
                if (candidate != null && candidate.isActiveAndEnabled) { best = candidate; break; }
            return best;
        }

        /// <summary>加载场景并在作用域结束时恢复空测试场景（避免污染后续用例）。
        /// Arena_LPWTest 不在 Build Profiles 场景列表中——Play Mode 内使用
        /// EditorSceneManager.LoadSceneInPlayMode（官方 API，允许加载未进构建列表的场景）。</summary>
        private sealed class SceneScope : System.IDisposable
        {
            private Scene _scene;

            public static SceneScope Load(string path)
            {
                var scope = new SceneScope();
#if UNITY_EDITOR
                scope._scene = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                    path, new LoadSceneParameters(LoadSceneMode.Single));
#else
                scope._scene = SceneManager.LoadScene(path, new LoadSceneParameters(LoadSceneMode.Single));
#endif
                return scope;
            }

            public IEnumerator WaitLoaded()
            {
                int guard = 0;
                while (!_scene.isLoaded && guard++ < 900) yield return null;
                Assert.That(_scene.isLoaded, Is.True, "场景加载失败: " + _scene.path);
                for (int i = 0; i < 5; i++) yield return null;
            }

            public void Dispose()
            {
                // Player 的 Intent 属性/光标状态由各用例 finally 恢复；
                // 场景本身无需清理：下一用例 Single 模式整体替换，测试运行器
                // 退出 Play Mode 后由编辑器恢复原场景。
            }
        }
    }
}
