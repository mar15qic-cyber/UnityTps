using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.Gameplay.Combat;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Weapon;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.RuntimeAudit
{
    // Test assembly only. Observes rendered frames; never changes camera, gun or tracer transforms.
    internal static class PreviewVisualProbe
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        [Serializable] private sealed class Sample
        {
            public string caseId, weapon, exit, phase;
            public int frame, traces, wrongTracerLayers;
            public float elapsed, gameElapsed, throwElapsed, boreError = -1, tracerPixelError = -1, lightError = -1;
            public bool heldVisible, throwing, weaponVisible;
            public Vector3 heldViewport;
        }
        private static object Field(object o, string name) => o?.GetType().GetField(name, Flags)?.GetValue(o);
        [Serializable] private sealed class RenderEvidence
        {
            public string path, type;
            public int layer;
            public bool enabled, active;
            public Vector3 position, scale, size, worldViewport, fpViewport;
        }
        [Serializable] private sealed class FrameEvidence
        {
            public int frame, worldMask, fpMask;
            public float gameTime, worldFov, fpFov;
            public RenderEvidence[] renderers;
        }
        private static string Hierarchy(Transform t) => t.parent == null ? t.name : Hierarchy(t.parent) + "/" + t.name;
        private static void CaptureRenderEvidence(string file, Camera world, Camera fp)
        {
            var renderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
                .Where(r => r.name.IndexOf("grenade", StringComparison.OrdinalIgnoreCase) >= 0
                    || r.name.IndexOf("throw", StringComparison.OrdinalIgnoreCase) >= 0
                    || r.name.IndexOf("flashbang", StringComparison.OrdinalIgnoreCase) >= 0
                    || r is LineRenderer && r.enabled || r.name == "arms")
                .Select(r => new RenderEvidence { path = Hierarchy(r.transform), type = r.GetType().Name,
                    layer = r.gameObject.layer, enabled = r.enabled, active = r.gameObject.activeInHierarchy,
                    position = r.bounds.center, size = r.bounds.size, scale = r.transform.lossyScale,
                    worldViewport = world != null ? world.WorldToViewportPoint(r.bounds.center) : default,
                    fpViewport = fp != null ? fp.WorldToViewportPoint(r.bounds.center) : default }).ToArray();
            File.WriteAllText(file, JsonUtility.ToJson(new FrameEvidence { frame = Time.frameCount, gameTime = Time.time,
                worldMask = world != null ? world.cullingMask : 0, fpMask = fp != null ? fp.cullingMask : 0,
                worldFov = world != null ? world.fieldOfView : 0, fpFov = fp != null ? fp.fieldOfView : 0,
                renderers = renderers }, true));
        }
        public static IEnumerator Capture(string directory, string role, string caseId, float duration)
        {
            var path = Path.Combine(directory, role + ".visual.jsonl");
            var mesh = new Mesh();
            float start = Time.realtimeSinceStartup;
            float gameStart = Time.time;
            bool shotCaptured = false;
            int throwFrame = 0;
            try
            {
                while (Time.realtimeSinceStartup - start < duration)
                {
                    yield return new WaitForEndOfFrame();
                    var player = Object.FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None).FirstOrDefault(p => p.IsOwnerPlayer && !p.IsServerInitialized);
                    if (player == null) continue;
                    var view = player.GetComponentsInChildren<WeaponView>().FirstOrDefault(v => v.isActiveAndEnabled);
                    if (view == null) continue;
                    var exit = (Transform)typeof(WeaponView).GetMethod("ResolvePresentationMuzzle", Flags).Invoke(view, null);
                    var fp = (Camera)Field(view, "_fpCamera"); var world = (Camera)Field(view, "_worldCamera");
                    var w = player.GetComponent<WeaponController>();
                    var s = new Sample { caseId = caseId, weapon = w.Definition.CatalogItemId, frame = Time.frameCount,
                        elapsed = Time.realtimeSinceStartup - start, exit = exit.parent.name + "/" + exit.name };
                    var body = view.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r =>
                        System.Text.RegularExpressions.Regex.IsMatch(r.name, "^(assault_rifle|smg|handgun|sniper|sniper_rifle|shotgun)_\\d+$"));
                    if (body != null)
                    {
                        s.weaponVisible = body.enabled && body.gameObject.activeInHierarchy;
                        body.BakeMesh(mesh);
                        var points = mesh.vertices.Select(p => view.Muzzle.InverseTransformPoint(body.transform.TransformPoint(p))).ToArray();
                        float front = points.Max(p => p.z);
                        float depth = s.weapon == "weapon.m4" ? .0257f : s.weapon == "weapon.smg04" ? .02745f : 0;
                        var ring = points.Where(p => Mathf.Abs(p.z - (front - depth)) < .0003f).ToArray();
                        if (ring.Length >= 12)
                        {
                            var bore = view.Muzzle.TransformPoint(new Vector3((ring.Min(p => p.x) + ring.Max(p => p.x)) * .5f,
                                (ring.Min(p => p.y) + ring.Max(p => p.y)) * .5f, front));
                            s.boreError = Vector3.Distance(bore, view.Muzzle.Find("MuzzleExit").position);
                        }
                    }
                    foreach (var segment in (Array)Field(view, "_overlayTracers"))
                    {
                        var line = (LineRenderer)Field(segment, "Line");
                        if (line == null || !line.enabled || line.positionCount < 2 || fp == null || world == null) continue;
                        if ((world.cullingMask & (1 << line.gameObject.layer)) == 0 || (fp.cullingMask & (1 << line.gameObject.layer)) != 0)
                            s.wrongTracerLayers++;
                        var a = fp.WorldToScreenPoint(exit.position); var b = world.WorldToScreenPoint(line.GetPosition(0));
                        s.tracerPixelError = Mathf.Max(s.tracerPixelError, Vector2.Distance(a, b)); s.traces++;
                    }
                    var light = (Light)Field(view, "_muzzleLight");
                    if (light != null && light.enabled) s.lightError = Vector3.Distance(light.transform.position, exit.position);
                    var animator = view.GetComponent<FPWeaponAnimator>();
                    s.gameElapsed = Time.time - gameStart;
                    s.throwElapsed = animator != null && (bool)Field(animator, "_throwPlaying") ? Time.time - (float)Field(animator, "_throwStartedAt") : -1f;
                    var held = (GameObject)Field(animator, "_heldThrowable");
                    var throwable = player.GetComponent<ThrowableController>();
                    s.throwing = throwable != null && throwable.IsThrowing;
                    s.phase = throwable != null ? throwable.Presentation.Phase.ToString() : "missing";
                    if (held != null && held.activeInHierarchy && fp != null)
                    {
                        var renderer = held.GetComponentInChildren<Renderer>();
                        s.heldViewport = fp.WorldToViewportPoint(renderer.bounds.center);
                        s.heldVisible = s.heldViewport.z > 0 && s.heldViewport.x > 0 && s.heldViewport.x < 1 && s.heldViewport.y > 0 && s.heldViewport.y < 1;
                    }
                    File.AppendAllText(path, JsonUtility.ToJson(s) + "\n");
                    bool capture = s.traces > 0 && !shotCaptured;
                    if (caseId.Contains("throw") && s.elapsed >= throwFrame * .12f && throwFrame < 13) { capture = true; throwFrame++; }
                    if (capture)
                    {
                        shotCaptured |= s.traces > 0;
                        var image = ScreenCapture.CaptureScreenshotAsTexture();
                        File.WriteAllBytes(Path.Combine(directory, role + "-" + caseId + "-" + Time.frameCount + ".png"), image.EncodeToPNG());
                        CaptureRenderEvidence(Path.Combine(directory, role + "-" + caseId + "-" + Time.frameCount + ".render.json"), world, fp);
                        Object.Destroy(image);
                    }
                }
            }
            finally { Object.Destroy(mesh); }
            File.WriteAllText(Path.Combine(directory, role + ".visual.done"), caseId);
        }
        [Serializable] private sealed class MapResult { public string scene; public int renderers, materials; public string[] invalid; }
        public static void Map(string directory, string role)
        {
            var renderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            var materials = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToArray();
            var result = new MapResult { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                renderers = renderers.Length, materials = materials.Length,
                invalid = materials.Where(m => m.shader == null || !m.shader.isSupported || m.shader.name == "Hidden/InternalErrorShader")
                    .Select(m => m.name + ":" + (m.shader == null ? "null" : m.shader.name)).ToArray() };
            File.WriteAllText(Path.Combine(directory, role + ".map-visual.json"), JsonUtility.ToJson(result, true));
        }
    }
}
