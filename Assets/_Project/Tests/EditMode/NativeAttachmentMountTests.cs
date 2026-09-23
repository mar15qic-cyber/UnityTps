using System;
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Weapon;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Gameplay.Tests
{
    public sealed class NativeAttachmentMountTests
    {
        private static readonly string[] Guns = {
            "weapon.m4", "weapon.ak", "weapon.rifle03", "weapon.service_pistol",
            "weapon.handgun02", "weapon.handgun03", "weapon.handgun04", "weapon.smg01",
            "weapon.smg02", "weapon.smg03", "weapon.smg04", "weapon.smg05", "weapon.shotgun01" };

        [TestCaseSource(nameof(Guns))]
        public void EverySocketHasTheSameBodyRelativePoseInFpAndTp(string id)
        {
            var w=Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e=>e.itemId==id);
            var fp=PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(w.definition.FirstPersonViewPrefab));
            var tp=PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(w.definition.ThirdPersonViewPrefab));
            try {
                // Check in ADS animation, not a guessed static/world axis.
                w.definition.FirstPersonAnimations.AimIdle.SampleAnimation(fp,0);
                var source=tp.GetComponentsInChildren<MeshFilter>(true).OrderByDescending(m=>m.sharedMesh.vertexCount).First();
                var skin=fp.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .Where(r=>!r.name.Contains("arms")&&!r.name.Contains("knife")).OrderByDescending(r=>r.sharedMesh.vertexCount).First();
                var baked=new Mesh();
                try {
                    skin.BakeMesh(baked);
                    foreach(var ts in tp.GetComponentsInChildren<AttachmentSocket>(true)) {
                        var fs=fp.GetComponentsInChildren<AttachmentSocket>(true).Single(s=>s.Slot==ts.Slot);
                        Assert.That(fs.GeometryVerified && ts.GeometryVerified, Is.True);
                        var actual=baked.vertices.Select(p=>fs.transform.InverseTransformPoint(skin.transform.TransformPoint(p))).ToArray();
                        var expected=source.sharedMesh.vertices.Select(p=>ts.transform.InverseTransformPoint(source.transform.TransformPoint(p))).ToArray();
                        for(int i=0;i<expected.Length;i+=Math.Max(1,expected.Length/80))
                            Assert.That(actual.Min(p=>Vector3.Distance(p,expected[i])),Is.LessThan(.0005f), id+" "+ts.Slot);
                    }
                } finally {Object.DestroyImmediate(baked);}
            } finally {PrefabUtility.UnloadPrefabContents(fp);PrefabUtility.UnloadPrefabContents(tp);}
        }

        [TestCaseSource(nameof(Guns))]
        public void AllFourOpticsSeatTheirClampOnAnActualSurfaceInBothViews(string id)
        {
            var w=Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e=>e.itemId==id);
            var catalog=Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
            foreach(var prefab in new[]{w.definition.FirstPersonViewPrefab,w.definition.ThirdPersonViewPrefab})
            foreach(var entry in catalog.Entries.Where(e=>e.slot==AttachmentSlotType.Optic&&e.HasModel)) {
                var root=PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(prefab));
                try {
                    var view=root.GetComponent<WeaponAttachmentView>() ?? root.AddComponent<WeaponAttachmentView>();
                    view.ApplyAttachments(catalog,id,new[]{entry},false);
                    if(prefab==w.definition.FirstPersonViewPrefab)w.definition.FirstPersonAnimations.AimIdle.SampleAnimation(root,0);
                    var socket=view.GetSocketTransform(AttachmentSlotType.Optic);
                    var optic=view.FindSpawned(entry.itemId);
                    var body=Triangles(root,socket,optic,false);var attachment=Triangles(root,socket,optic,true);
                    int contacts=0;
                    // Inner clamp ceiling: exclude the +/-20 mm bevel and locking lug,
                    // which extend into rail slots rather than defining the seating plane.
                    foreach(float x in new[]{-.013f,-.006f,0,.006f,.013f}) {
                        float seat=Intersect(attachment,x,0,true), support=Intersect(body,x,0,false);
                        Assert.That(float.IsFinite(seat)&&float.IsFinite(support),Is.True,id+" missing footprint");
                        float gap=seat-support;
                        Assert.That(gap,Is.InRange(-.001f,.0036f),id+" "+entry.itemId+" "+prefab.name+" footprint gap");
                        if(Mathf.Abs(gap)<.0006f)contacts++;
                    }
                    Assert.That(contacts,Is.GreaterThanOrEqualTo(2),id+" requires distributed contact, not one highest vertex");
                    foreach(var t in root.GetComponentsInChildren<Transform>(true))
                        if(!t.IsChildOf(optic)&&WeaponAttachmentView.IsOptionalDemoAccessory(t.name))
                            Assert.That(t.gameObject.activeSelf,Is.False,id+" residual demo accessory");
                    view.ApplyAttachments(catalog,id,Array.Empty<AttachmentAssetEntry>(),false);
                    var adapter=socket.Find("OpticMountAdapter");
                    if(adapter!=null)Assert.That(adapter.gameObject.activeSelf,Is.False);
                } finally {PrefabUtility.UnloadPrefabContents(root);}
            }
        }

        [TestCase("weapon.smg01")]
        [TestCase("weapon.smg04")]
        public void IntegralForegripWeaponsRejectStaleGripWithoutRemovingOtherAttachments(string id)
        {
            var w=Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e=>e.itemId==id);
            var catalog=Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
            Assert.That(w.slotCapabilities,Does.Not.Contain("Underbarrel"));
            var grip=catalog.Find("attach.lpw.grip.01");
            var silencer=catalog.Find("attach.lpfp.muffler.01");
            Assert.That(grip,Is.Not.Null);
            Assert.That(silencer,Is.Not.Null);
            var entries=new List<AttachmentAssetEntry>{grip,silencer};
            Assert.That(AttachmentCompatibilityPolicy.IsAllowed(w.definition,grip),Is.False);
            Assert.That(AttachmentCompatibilityPolicy.RemoveUnsupported(w.definition,entries),Is.EqualTo(1));
            Assert.That(entries,Is.EqualTo(new[]{silencer}));
            foreach(var prefab in new[]{w.definition.FirstPersonViewPrefab,w.definition.ThirdPersonViewPrefab})
                Assert.That(prefab.GetComponentsInChildren<AttachmentSocket>(true)
                    .Any(s=>s.Slot==AttachmentSlotType.Underbarrel),Is.False,prefab.name);
        }

        [Test]
        public void OnlyLpfpSilencerGeometryRemainsIncludingPassRewardAliases()
        {
            var catalog=Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
            Assert.That(catalog.Find("attach.lpw.muffler.01"),Is.Null);
            Assert.That(catalog.Find("attach.lpw.muffler.02"),Is.Null);
            var source=catalog.Find("attach.lpfp.muffler.01");
            foreach(var entry in catalog.Entries.Where(e=>e.slot==AttachmentSlotType.Muzzle)) {
                Assert.That(entry.prefab,Is.SameAs(source.prefab));
                Assert.That(entry.HasModel,Is.True);
                Assert.That(entry.mountOffset,Is.EqualTo(source.mountOffset));
            }
        }

        internal static Vector3[] Triangles(GameObject root,Transform frame,Transform mounted,bool wantMounted)
        {
            var result=new List<Vector3>();
            foreach(var r in root.GetComponentsInChildren<Renderer>(true)) {
                if(!r.enabled||!r.gameObject.activeInHierarchy||r.transform.IsChildOf(mounted)!=wantMounted)continue;
                var name=r.name.ToLowerInvariant();
                if(!wantMounted&&(name.Contains("arms")||name.Contains("knife")||name.Contains("bullet")||name.Contains("shell")))continue;
                Mesh m=null;bool owned=false;
                if(r is SkinnedMeshRenderer skin){m=new Mesh();skin.BakeMesh(m);owned=true;}
                else m=r.GetComponent<MeshFilter>()?.sharedMesh;
                if(m==null)continue;
                try {var vs=m.vertices.Select(p=>frame.InverseTransformPoint(r.transform.TransformPoint(p))).ToArray();foreach(int i in m.triangles)result.Add(vs[i]);}
                finally {if(owned)Object.DestroyImmediate(m);}
            }
            return result.ToArray();
        }
        internal static float Intersect(Vector3[] tri,float x,float z,bool bottom)
        {
            float hit=bottom?float.PositiveInfinity:float.NegativeInfinity;
            for(int i=0;i<tri.Length;i+=3) {
                var a=tri[i];var b=tri[i+1];var c=tri[i+2];
                float den=(b.z-c.z)*(a.x-c.x)+(c.x-b.x)*(a.z-c.z);if(Mathf.Abs(den)<1e-10f)continue;
                float u=((b.z-c.z)*(x-c.x)+(c.x-b.x)*(z-c.z))/den;
                float v=((c.z-a.z)*(x-c.x)+(a.x-c.x)*(z-c.z))/den;
                if(u<-.00001f||v<-.00001f||u+v>1.00001f)continue;
                float y=u*a.y+v*b.y+(1-u-v)*c.y;hit=bottom?Mathf.Min(hit,y):Mathf.Max(hit,y);
            }
            return hit;
        }
    }
}
