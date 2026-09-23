using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Camera;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Gameplay.PlayModeTests
{
    // Regression matrix: real Arena equip/ADS, aperture geometry and screenshots.
    public sealed class LowZoomOpticAuditTests
    {
        [UnityTest, Category("VisualAudit")]
        public IEnumerator CaptureAllCompatibleLowZoomCombinations()
        {
            void DisableLiveInput(Scene scene, LoadSceneMode mode)
            {
                foreach (var reader in Object.FindObjectsByType<InputReader>(FindObjectsSortMode.None))
                    reader.enabled = false;
            }
            SceneManager.sceneLoaded += DisableLiveInput;
            try { yield return SceneManager.LoadSceneAsync("Assets/_Project/Scenes/Arena.unity"); }
            finally { SceneManager.sceneLoaded -= DisableLiveInput; }
            yield return new WaitForSeconds(1.5f);
            var controller = Object.FindFirstObjectByType<WeaponController>();
            var input = controller.GetComponent<InputReader>();
            bool inputEnabled = true; // scene-authored enabled state, before the capture hook
            bool aimHeld = input.AimHeld;
            input.enabled = false;
            var rig = Object.FindFirstObjectByType<FPWeaponRig>();
            var weapons = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            var attachments = AttachmentAssetCatalog.LoadOrDefault();
            var saved = new Dictionary<string, Dictionary<string, string>>();
            var rows = new List<string>();
            const string directory = "Temp/LowZoomFix0923/runtime";
            Directory.CreateDirectory(directory);
            try
            {
                foreach (var weapon in weapons.Entries.Where(e => e.IsLpfp && e.definition != null
                             && !e.itemId.StartsWith("weapon.sniper")))
                {
                    string id = weapon.itemId;
                    saved[id] = new Dictionary<string, string>();
                    WeaponAttachmentStore.Load(id, saved[id]);
                    foreach (var optic in new[] { "attach.lpfp.optic.01", "attach.lpfp.optic.03" })
                    {
                        WeaponAttachmentStore.Save(id, new Dictionary<string, string> { { "Optic", optic } });
                        controller.EquipDefinition(weapon.definition);
                        typeof(InputReader).GetProperty("AimHeld").SetValue(input, true);
                        yield return new WaitForSeconds(1f);
                        Assert.AreEqual(optic, controller.CurrentOpticAim.ItemId, id);
                        var view = rig.ActiveView.GetComponent<WeaponAttachmentView>();
                        var socket = view.GetSocketTransform(AttachmentSlotType.Optic);
                        var geometry = view.FindSpawned(optic);
                        Assert.NotNull(geometry, id + optic);
                        var fp = controller.GetComponentsInChildren<Camera>(true).First(c => c.name == "FP View Camera");
                        Assert.True(view.TryGetPhysicalScopeAim(optic, out var aim), id + optic);
                        var reticle = geometry.Find("PhysicalScopeReticle");
                        Assert.NotNull(reticle, id + optic);
                        Assert.AreEqual(optic, PhysicalScopeView.HandlingOpticId, "No overlay fallback: " + id + optic);
                        Assert.True(reticle.GetComponent<MeshRenderer>().enabled, id + optic);
                        // Independent aperture dimensions measured from the source meshes.
                        var measuredCenter = optic.EndsWith("01") ? new Vector3(0, 0, -.043809f)
                            : new Vector3(0, .0262827f, .0342f);
                        float innerRadius = optic.EndsWith("01") ? .014305f : .012775f;
                        Assert.Less(Vector3.Distance(reticle.localPosition, measuredCenter), .0001f, id + optic);
                        Assert.Less(reticle.localScale.x, innerRadius * Mathf.Cos(Mathf.PI / 12f), id + optic);
                        var window = fp.WorldToViewportPoint(socket.TransformPoint(aim.WindowCenterLocal));
                        var center = reticle == null ? Vector3.zero : fp.WorldToViewportPoint(reticle.position);
                        Assert.That(center.x, Is.EqualTo(.5f).Within(.01f), id + optic + " center x");
                        Assert.That(center.y, Is.EqualTo(.5f).Within(.01f), id + optic + " center y");
                        rows.Add(id + "|" + weapon.definition.DisplayName + "|" + optic
                            + "|windowLocal=" + aim.WindowCenterLocal.ToString("F5")
                            + "|eyeLocal=" + aim.EyePointLocal.ToString("F5")
                            + "|windowViewport=" + window.ToString("F5")
                            + "|reticleViewport=" + center.ToString("F5")
                            + "|reticleLocal=" + (reticle == null ? "missing" : reticle.localPosition.ToString("F5"))
                            + "|radius=" + (reticle == null ? "missing" : reticle.localScale.x.ToString("F5"))
                            + "|handled=" + PhysicalScopeView.HandlingOpticId);
                        if (id == "weapon.ak")
                        {
                            foreach (var mesh in geometry.GetComponentsInChildren<MeshFilter>(true))
                            {
                                var vertices = mesh.sharedMesh.vertices.Select(v => socket.InverseTransformPoint(mesh.transform.TransformPoint(v))).ToArray();
                                File.WriteAllLines(directory + "/" + optic + "-" + mesh.name + "-vertices.txt",
                                    vertices.Select(v => v.ToString("F6")));
                            }
                        }
                        yield return new WaitForEndOfFrame();
                        var image = ScreenCapture.CaptureScreenshotAsTexture(id == "weapon.ak" ? 3 : 1);
                        File.WriteAllBytes(directory + "/" + id + "-" + optic + ".png", image.EncodeToPNG());
                        Object.Destroy(image);
                        yield return AssertLensRenders(geometry.Find("PhysicalScopeLens"), fp, id + optic);
                        if (id == "weapon.ak")
                        {
                            var scopeCamera = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)
                                .First(c => c.name == "PhysicalScopeCamera");
                            Assert.AreEqual(Camera.main.transform, scopeCamera.transform.parent,
                                "Scope must follow the main world camera, not a minimap or preview");
                            File.WriteAllLines(directory + "/" + optic + "-cameras.txt",
                                Camera.allCameras.Select(c => c.name + " position=" + c.transform.position.ToString("F4")
                                    + " forward=" + c.transform.forward.ToString("F4") + " mask=" + c.cullingMask
                                    + " fov=" + c.fieldOfView + " target=" + c.targetTexture
                                    + " clear=" + c.clearFlags + " near=" + c.nearClipPlane));
                            var previousRt = RenderTexture.active;
                            RenderTexture.active = scopeCamera.targetTexture;
                            var scopeImage = new Texture2D(scopeCamera.targetTexture.width, scopeCamera.targetTexture.height);
                            scopeImage.ReadPixels(new Rect(0, 0, scopeImage.width, scopeImage.height), 0, 0);
                            scopeImage.Apply();
                            RenderTexture.active = previousRt;
                            File.WriteAllBytes(directory + "/" + optic + "-scopeRT.png", scopeImage.EncodeToPNG());
                            Object.Destroy(scopeImage);
                            for (int shot = 0; shot < 3; shot++)
                            {
                                Assert.True(controller.TryFire(), "M4 burst " + optic);
                                yield return new WaitForSeconds(.12f);
                                Assert.Less(Vector3.Distance(reticle.localPosition, measuredCenter), .0001f,
                                    "Reticle must remain in the glass during recoil");
                            }
                            yield return new WaitForEndOfFrame();
                            var recoilImage = ScreenCapture.CaptureScreenshotAsTexture();
                            File.WriteAllBytes(directory + "/" + id + "-" + optic + "-recoil.png", recoilImage.EncodeToPNG());
                            Object.Destroy(recoilImage);
                            typeof(InputReader).GetProperty("AimHeld").SetValue(input, false);
                            yield return new WaitForSeconds(.4f);
                            Assert.False(reticle.GetComponent<MeshRenderer>().enabled, "Hipfire must hide reticle");
                            typeof(InputReader).GetProperty("AimHeld").SetValue(input, true);
                            yield return new WaitForSeconds(.8f);
                            Assert.True(reticle.GetComponent<MeshRenderer>().enabled, "Re-ADS restores reticle");
                            Assert.AreEqual(1, geometry.GetComponentsInChildren<MeshRenderer>()
                                .Count(r => r.name == "PhysicalScopeReticle"), "No stale duplicate reticles");
                        }
                        typeof(InputReader).GetProperty("AimHeld").SetValue(input, false);
                        yield return new WaitForSeconds(.2f);
                    }
                }
                Assert.AreEqual(26, rows.Count);
            }
            finally
            {
                File.WriteAllLines(directory + "/measurements.txt", rows);
                foreach (var pair in saved) WeaponAttachmentStore.Save(pair.Key, pair.Value);
                typeof(InputReader).GetProperty("AimHeld").SetValue(input, aimHeld);
                input.enabled = inputEnabled;
            }
        }

        private static IEnumerator AssertLensRenders(Transform lens, Camera camera, string context)
        {
            Assert.NotNull(lens, context);
            var mesh = lens.GetComponent<MeshFilter>().sharedMesh;
            var triangles = mesh.triangles;
            var vertices = mesh.vertices;
            Assert.Greater(Vector3.Dot(Vector3.Cross(vertices[triangles[1]] - vertices[triangles[0]],
                vertices[triangles[2]] - vertices[triangles[0]]), Vector3.forward), 0f,
                "Lens winding must face the eye");
            var material = lens.GetComponent<MeshRenderer>().sharedMaterial;
            var texture = material.GetTexture("_BaseMap");
            var color = material.GetColor("_BaseColor");
            Assert.IsInstanceOf<RenderTexture>(texture, "Live scope image " + context);
            Assert.True(((RenderTexture)texture).IsCreated(), context);
            try
            {
                // Verify actual rasterization, not just a correctly positioned invisible mesh.
                material.SetTexture("_BaseMap", Texture2D.whiteTexture);
                material.SetColor("_BaseColor", Color.magenta);
                yield return new WaitForEndOfFrame();
                var image = ScreenCapture.CaptureScreenshotAsTexture();
                try
                {
                    var sample = camera.WorldToScreenPoint(lens.TransformPoint(new Vector3(.3f, .3f, 0f)));
                    var pixel = image.GetPixel(Mathf.RoundToInt(sample.x), Mathf.RoundToInt(sample.y));
                    Assert.True(pixel.r > .7f && pixel.b > .7f && pixel.g < .3f,
                        "Lens must render inside glass " + context + " " + pixel);
                    var outside = camera.WorldToScreenPoint(lens.TransformPoint(new Vector3(1.3f, 0f, 0f)));
                    var outsidePixel = image.GetPixel(Mathf.RoundToInt(outside.x), Mathf.RoundToInt(outside.y));
                    Assert.False(outsidePixel.r > .7f && outsidePixel.b > .7f && outsidePixel.g < .3f,
                        "Lens must not extend outside aperture " + context);
                    var center = camera.WorldToScreenPoint(lens.position);
                    float radius = Vector2.Distance(center,
                        camera.WorldToScreenPoint(lens.TransformPoint(Vector3.up)));
                    int visibleMarkings = 0;
                    for (int y = Mathf.CeilToInt(center.y - radius * .7f); y < center.y + radius * .7f; y++)
                    for (int x = Mathf.CeilToInt(center.x - radius * .7f); x < center.x + radius * .7f; x++)
                    {
                        if (Vector2.Distance(new Vector2(x, y), center) > radius * .7f) continue;
                        var mark = image.GetPixel(x, y);
                        if (mark.r < .95f && mark.b < .95f) visibleMarkings++;
                    }
                    Assert.Greater(visibleMarkings, 0, "Reticle must remain visible when minified " + context);
                }
                finally { Object.Destroy(image); }
            }
            finally
            {
                material.SetTexture("_BaseMap", texture);
                material.SetColor("_BaseColor", color);
            }
            var scopeCamera = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)
                .First(c => c.targetTexture == texture);
            Assert.AreEqual(Camera.main.transform, scopeCamera.transform.parent, context);
            float apertureHeight = Mathf.Abs(camera.WorldToViewportPoint(lens.TransformPoint(Vector3.up)).y
                - camera.WorldToViewportPoint(lens.TransformPoint(Vector3.down)).y);
            float displayedMagnification = apertureHeight * Mathf.Tan(Camera.main.fieldOfView * .5f * Mathf.Deg2Rad)
                / Mathf.Tan(scopeCamera.fieldOfView * .5f * Mathf.Deg2Rad);
            Assert.That(displayedMagnification, Is.EqualTo(3f).Within(.03f), "Displayed magnification " + context);
            var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var targetMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            targetMaterial.SetColor("_BaseColor", Color.green);
            target.GetComponent<Renderer>().sharedMaterial = targetMaterial;
            target.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 5f
                - Camera.main.transform.right * .05f;
            target.transform.rotation = Camera.main.transform.rotation;
            target.transform.localScale = new Vector3(.1f, .2f, .02f);
            var rightTarget = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var rightMaterial = new Material(targetMaterial);
            rightMaterial.SetColor("_BaseColor", Color.red);
            rightTarget.GetComponent<Renderer>().sharedMaterial = rightMaterial;
            rightTarget.transform.position = target.transform.position + Camera.main.transform.right * .1f;
            rightTarget.transform.rotation = target.transform.rotation;
            rightTarget.transform.localScale = target.transform.localScale;
            try
            {
                yield return new WaitForEndOfFrame();
                yield return new WaitForEndOfFrame(); // world camera may consume the previous RT frame
                var previous = RenderTexture.active;
                var pixelImage = new Texture2D(1, 1);
                try
                {
                    var rt = (RenderTexture)texture;
                    RenderTexture.active = rt;
                    pixelImage.ReadPixels(new Rect(rt.width / 4, rt.height / 2, 1, 1), 0, 0);
                    pixelImage.Apply();
                    var targetPixel = pixelImage.GetPixel(0, 0);
                    Assert.True(targetPixel.g > .7f && targetPixel.r < .3f && targetPixel.b < .3f,
                        "Scope RT must contain the world aim target " + context + " " + targetPixel);
                }
                finally { RenderTexture.active = previous; Object.Destroy(pixelImage); }
                var screen = ScreenCapture.CaptureScreenshotAsTexture();
                try
                {
                    var leftPoint = camera.WorldToScreenPoint(lens.TransformPoint(new Vector3(.4f, .25f, 0f)));
                    var leftPixel = screen.GetPixel(Mathf.RoundToInt(leftPoint.x), Mathf.RoundToInt(leftPoint.y));
                    Assert.True(leftPixel.g > .55f && leftPixel.r < .3f,
                        "Left world target must stay left in the glass (no mirrored RT) " + context + " " + leftPixel);
                }
                finally { Object.Destroy(screen); }
            }
            finally
            {
                Object.Destroy(target); Object.Destroy(targetMaterial);
                Object.Destroy(rightTarget); Object.Destroy(rightMaterial);
            }
            // Let the next normal frame replace the validation marker in the RT.
            yield return new WaitForEndOfFrame();
        }
    }
}
