using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Game.RuntimeAudit
{
    internal static class ReworkViewProbe
    {
        [Serializable] private sealed class Sample
        {
            public string weapon, name;
            public int frame, vertices, visibleEnds, fpShadowCasters;
            public bool hintVisible;
            public Vector3 overlayLocalPosition, worldPosition;
            public float time, ads;
            public Vector3[] visibleEndViewport;
        }

        internal static IEnumerator Capture(string directory, string role, string name, float seconds)
        {
            var baked = new Mesh();
            Mesh source = null;
            int[] ends = Array.Empty<int>();
            float until = Time.realtimeSinceStartup + seconds;
            try
            {
                while (Time.realtimeSinceStartup < until)
                {
                    yield return new WaitForEndOfFrame();
                    var player = Object.FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None).FirstOrDefault(p => p.IsOwnerPlayer && !p.IsServerInitialized);
                    if (player == null) continue;
                    var rig = player.GetComponentInChildren<FPWeaponRig>(true);
                    var view = rig != null ? rig.ActiveView : null;
                    if (view == null) continue;
                    var overlay = player.GetComponentsInChildren<Camera>(true).FirstOrDefault(c => c.cullingMask == 1 << LayerMask.NameToLayer("FirstPersonView"));
                    var arms = view.GetComponentsInChildren<SkinnedMeshRenderer>().FirstOrDefault(r => r.name == "arms");
                    var data = new Sample { name = name, frame = Time.frameCount, time = Time.realtimeSinceStartup,
                        weapon = player.GetComponent<WeaponController>().Definition.WeaponId,
                        ads = player.GetComponent<WeaponController>().GetComponent<Game.Gameplay.Player.PlayerAimState>().Ads01,
                        overlayLocalPosition = overlay != null ? overlay.transform.localPosition : default,
                        worldPosition = Camera.main != null ? Camera.main.transform.position : default,
                        fpShadowCasters = view.GetComponentsInChildren<Renderer>(true).Count(r => r.shadowCastingMode != ShadowCastingMode.Off) };
                    var hud = Object.FindFirstObjectByType<Game.Presentation.HUD.BackpackSwitchHudView>();
                    var hint = hud != null ? hud.transform.Find("BackpackAvailable") : null;
                    data.hintVisible = hint != null && hint.gameObject.activeInHierarchy;
                    if (arms != null && overlay != null)
                    {
                        if (source != arms.sharedMesh) { source = arms.sharedMesh; ends = BoundaryVertices(source); }
                        arms.BakeMesh(baked);
                        var vertices = baked.vertices;
                        data.vertices = source.vertexCount;
                        data.visibleEndViewport = ends.Select(i => overlay.WorldToViewportPoint(arms.transform.TransformPoint(vertices[i])))
                            .Where(p => p.z > overlay.nearClipPlane && p.x >= 0 && p.x <= 1 && p.y >= 0 && p.y <= 1).ToArray();
                        data.visibleEnds = data.visibleEndViewport.Length;
                    }
                    File.AppendAllText(Path.Combine(directory, role + "-" + name + ".frustum.jsonl"), JsonUtility.ToJson(data) + "\n");
                }
            }
            finally { Object.Destroy(baked); }
        }

        private static int[] BoundaryVertices(Mesh mesh)
        {
            var vertices = mesh.vertices;
            var weld = new Dictionary<Vector3Int,int>();
            var ids = new int[vertices.Length];
            for (int i=0;i<vertices.Length;i++)
            {
                var key=Vector3Int.RoundToInt(vertices[i]*100000);
                if (!weld.TryGetValue(key,out ids[i])) { ids[i]=weld.Count;weld.Add(key,ids[i]); }
            }
            var counts = new Dictionary<(int,int),int>();
            var indices = new Dictionary<(int,int),int>();
            var triangles = mesh.triangles;
            for(int i=0;i<triangles.Length;i+=3) for(int j=0;j<3;j++)
            {
                int a=triangles[i+j],b=triangles[i+(j+1)%3];
                var key=(Math.Min(ids[a],ids[b]),Math.Max(ids[a],ids[b]));
                if(!counts.ContainsKey(key)){counts[key]=0;indices[key]=a;} counts[key]++;
            }
            return counts.Where(e=>e.Value==1).Select(e=>indices[e.Key]).Distinct().ToArray();
        }

        internal static void ShadowFixture(string name)
        {
            if (name == "tp-legacy" || name == "tp-filtered")
            {
                foreach(var filter in Object.FindObjectsByType<Game.Presentation.Weapon.TacticalFlashlightShadowFilter>(FindObjectsSortMode.None))
                    filter.enabled = name == "tp-filtered";
                return;
            }
            var player = Object.FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None).First(p=>p.IsOwnerPlayer&&!p.IsServerInitialized);
            var rig=player.GetComponentInChildren<FPWeaponRig>(true);
            foreach(var r in rig.ActiveView.GetComponentsInChildren<Renderer>(true))
                r.shadowCastingMode=name=="fp-on"?ShadowCastingMode.On:ShadowCastingMode.Off;
        }
    }
}
