using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Weapon;
using Game.UI;
using UnityEngine;

namespace Game.Debugging
{
    /// <summary>Play-mode-only 29-gun ADS invariance probe. Add it at runtime; it never saves the scene.</summary>
    public sealed class LPWAdsRuntimeVerifier : MonoBehaviour
    {
        private struct Measurement
        {
            public float Sight;
            public float Axis;
            public float Roll;
            public float RightGrip;
            public float LeftGrip;
        }

        private IEnumerator Start()
        {
            GameObject[] players = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(x => x != null && x.name == "Player" && x.scene.IsValid()).ToArray();
            if (players.Length != 1) throw new System.InvalidOperationException("Expected one scene Player.");
            GameObject player = players[0];
            WeaponController controller = player.GetComponent<WeaponController>();
            LPWProductionRuntimeRegistry registry = Resources.Load<LPWProductionRuntimeRegistry>(
                "LPWProductionRuntimeRegistry");
            if (registry == null || registry.Balance == null)
                throw new System.InvalidOperationException("LPW runtime balance registry is missing.");
            FieldInfo balanceField = typeof(WeaponController).GetField("balanceConfigAsset",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (balanceField?.GetValue(controller) == null)
                balanceField.SetValue(controller, registry.Balance);
            player.SetActive(true);
            // Awake may already have run while this scene object was inactive. Mirror the
            // serialized test dependency into the runtime interface before Arsenal.Start.
            FieldInfo runtimeBalanceField = typeof(WeaponController).GetField("_balance",
                BindingFlags.Instance | BindingFlags.NonPublic);
            runtimeBalanceField?.SetValue(controller, registry.Balance);
            yield return null;
            yield return null;

            Arsenal arsenal = player.GetComponent<Arsenal>();
            PlayerAimState aim = player.GetComponent<PlayerAimState>();
            FieldInfo slotsField = typeof(Arsenal).GetField("slots", BindingFlags.Instance | BindingFlags.NonPublic);
            WeaponDefinition[] slots = slotsField != null ? slotsField.GetValue(arsenal) as WeaponDefinition[] : null;
            if (slots == null || slots.Length != 29)
                throw new System.InvalidOperationException("Expected 29 LPW slots, got " + (slots == null ? 0 : slots.Length));

            FieldInfo adsField = typeof(PlayerAimState).GetField("<Ads01>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            adsField.SetValue(aim, 1f);
            aim.enabled = false;
            UnityEngine.Camera camera = player.GetComponentsInChildren<UnityEngine.Camera>(true)
                .Single(x => x.name == "FP View Camera");
            FPWeaponRig rig = player.GetComponentInChildren<FPWeaponRig>(true);
            MethodInfo showView = typeof(FPWeaponRig).GetMethod("ShowView",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo aimIdle = typeof(FPWeaponAnimator).GetMethod("ExecuteAimIdle",
                BindingFlags.Instance | BindingFlags.NonPublic);

            StringBuilder report = new();
            report.AppendLine("id,sightPx,axisDeg,rollDeg,rightGripMm,leftGripMm,idleEditSightPx,rootPosDriftMm,rootRotDriftDeg");
            for (int i = 0; i < slots.Length; i++)
            {
                foreach (FPWeaponAnimator item in player.GetComponentsInChildren<FPWeaponAnimator>(true))
                    item.enabled = true;
                controller.EquipDefinition(slots[i]);
                showView?.Invoke(rig, new object[] { slots[i], false });
                yield return null;
                yield return null;

                FPWeaponAnimator animator = player.GetComponentsInChildren<FPWeaponAnimator>(true)
                    .FirstOrDefault(x => x.gameObject.activeInHierarchy);
                if (animator != null)
                {
                    aimIdle?.Invoke(animator, null);
                    animator.enabled = false;
                }
                // Let the hand IK and target smoothing finish. Measuring two frames after
                // a switch only reports the transition, not the steady ADS pose.
                yield return new WaitForSeconds(.25f);
                yield return null;

                WeaponView view = player.GetComponentsInChildren<WeaponView>(false)
                    .FirstOrDefault(x => x.isActiveAndEnabled);
                FPWeaponPoseProfile profile = view != null ? view.GetComponent<FPWeaponPoseProfile>() : null;
                if (profile == null || profile.WeaponRoot == null)
                    throw new System.InvalidOperationException("Pose profile missing for " + slots[i].WeaponId);

                Transform weaponRoot = profile.WeaponRoot;
                Vector3 savedPosition = weaponRoot.localPosition;
                Quaternion savedRotation = weaponRoot.localRotation;
                Measurement baseline = Measure(camera, view, profile);

                weaponRoot.localPosition = savedPosition + new Vector3(.012f, -.006f, .018f);
                yield return null;
                yield return null;
                Measurement afterIdleEdit = Measure(camera, view, profile);
                weaponRoot.localPosition = savedPosition;
                weaponRoot.localRotation = savedRotation;
                yield return null;

                float positionDrift = Vector3.Distance(weaponRoot.localPosition, savedPosition) * 1000f;
                float rotationDrift = Quaternion.Angle(weaponRoot.localRotation, savedRotation);
                report.AppendLine($"{slots[i].WeaponId},{baseline.Sight:F3},{baseline.Axis:F4},{baseline.Roll:F4},"
                    + $"{baseline.RightGrip:F3},{baseline.LeftGrip:F3},"
                    + $"{afterIdleEdit.Sight:F3},{positionDrift:F4},{rotationDrift:F4}");

                ScreenCapture.CaptureScreenshot($"Temp/ads_verify_{i:00}.png");
                yield return new WaitForEndOfFrame();
            }

            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/lpw_ads_runtime_report.csv"));
            File.WriteAllText(path, report.ToString());
            Debug.Log("[LPWAdsRuntimeVerifier] COMPLETE\n" + report, this);
            enabled = false;
        }

        private static Measurement Measure(UnityEngine.Camera camera, WeaponView view, FPWeaponPoseProfile profile)
        {
            bool hasSightLine = profile.TryGetSightLine(view.SightReference,
                out Vector3 rearSight, out Vector3 frontSight);
            Vector3 screen = camera.WorldToScreenPoint(hasSightLine ? rearSight : view.SightReference.position);
            Vector2 center = new(camera.pixelWidth * .5f, camera.pixelHeight * .5f);
            Vector3 axis = hasSightLine
                ? (frontSight - rearSight).normalized
                : -profile.WeaponRoot.right;
            Vector3 up = Vector3.ProjectOnPlane(profile.WeaponRoot.up, camera.transform.forward).normalized;
            Transform leftHand = FindDeep(profile.transform, "hand_L");
            return new Measurement
            {
                Sight = Vector2.Distance(new Vector2(screen.x, screen.y), center),
                Axis = Vector3.Angle(axis, camera.transform.forward),
                Roll = Vector3.Angle(up, camera.transform.up),
                RightGrip = profile.RightHand != null && profile.RightHandGrip != null
                    ? Vector3.Distance(profile.RightHand.position, profile.RightHandGrip.position) * 1000f
                    : float.PositiveInfinity,
                LeftGrip = leftHand != null && profile.LeftSupportGrip != null
                    ? Vector3.Distance(leftHand.position, profile.LeftSupportGrip.position) * 1000f
                    : float.PositiveInfinity
            };
        }

        private static Transform FindDeep(Transform root, string targetName)
        {
            if (root == null) return null;
            if (root.name == targetName) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), targetName);
                if (found != null) return found;
            }
            return null;
        }
    }
}
