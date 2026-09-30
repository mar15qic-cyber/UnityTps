var lines=new System.Collections.Generic.List<string>{"Scene,Models,Probes,SurfaceHits,FalseBlocks,MissingCollisions,MaxDistanceError,MovementBarriers,VisibleBoundaryColliders,AggregateBoxes"};
var intersect=typeof(UnityEditor.HandleUtility).GetMethod("IntersectRayMesh",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
foreach(var name in new[]{"Arena","Map_Stackyard","Map_Depot55","Map_Ridgeline","Map_TrainingYard","Map_NightRelay"})
{
 var scene=UnityEditor.SceneManagement.EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/"+name+".unity");
 try{
 var roots=scene.GetRootGameObjects();
 int probes=0,hits=0,falseBlocks=0,missing=0,models=0;float error=0;
 var filters=roots.SelectMany(r=>r.GetComponentsInChildren<UnityEngine.MeshFilter>(true)).Where(f=>f.gameObject.activeInHierarchy&&f.sharedMesh!=null&&(UnityEditor.AssetDatabase.GetAssetPath(f.sharedMesh).StartsWith("Assets/")||f.GetComponents<UnityEngine.Collider>().Any(c=>!c.isTrigger&&c.GetComponent<Game.Gameplay.Combat.MapMovementBarrier>()==null))&&f.GetComponentInParent<Game.Gameplay.Player.InputReader>()==null).ToArray();
 UnityEngine.Physics.SyncTransforms();
 foreach(var filter in filters){
  models++;var colliders=filter.GetComponents<UnityEngine.Collider>().Where(c=>!c.isTrigger&&!(c.GetComponent<Game.Gameplay.Combat.MapMovementBarrier>()!=null)).ToArray();
  if(colliders.Length==0){missing++;continue;}
  var bounds=filter.sharedMesh.bounds;
  foreach(var direction in new[]{UnityEngine.Vector3.right,UnityEngine.Vector3.left,UnityEngine.Vector3.up,UnityEngine.Vector3.down,UnityEngine.Vector3.forward,UnityEngine.Vector3.back}){
   var axisA=UnityEngine.Vector3.Cross(direction,UnityEngine.Vector3.up);if(axisA.sqrMagnitude<.1f)axisA=UnityEngine.Vector3.right;axisA.Normalize();var axisB=UnityEngine.Vector3.Cross(direction,axisA);
   for(int i=-2;i<=2;i++)for(int j=-2;j<=2;j++){
    var start=bounds.center+direction*(bounds.extents.magnitude+1)+UnityEngine.Vector3.Scale(axisA,bounds.extents)*(i*.35f+.073f)+UnityEngine.Vector3.Scale(axisB,bounds.extents)*(j*.35f+.053f);
    var ray=new UnityEngine.Ray(filter.transform.TransformPoint(start),filter.transform.TransformDirection(-direction));
    var args=new object[]{ray,filter.sharedMesh,filter.transform.localToWorldMatrix,default(UnityEngine.RaycastHit)};
    bool visual=(bool)intersect.Invoke(null,args);var meshHit=(UnityEngine.RaycastHit)args[3];
    bool blocked=false;float distance=100000;foreach(var collider in colliders){UnityEngine.RaycastHit hit;if(collider.Raycast(ray,out hit,1000)){blocked=true;distance=UnityEngine.Mathf.Min(distance,hit.distance);}}
    if(visual&&!blocked&&Vector3.Dot(meshHit.normal,ray.direction)>0){visual=false;}
    probes++;if(visual)hits++;
    if(blocked&&!visual)falseBlocks++;
    if(visual&&!blocked)missing++;
    if(visual&&blocked)error=UnityEngine.Mathf.Max(error,UnityEngine.Mathf.Abs(meshHit.distance-distance));
   }
  }
 }
 var boxes=roots.SelectMany(r=>r.GetComponentsInChildren<UnityEngine.BoxCollider>(true));
 int aggregates=boxes.Count(b=>b.GetComponent<UnityEngine.MeshFilter>()==null&&b.GetComponentInParent<Game.Gameplay.Player.InputReader>()==null&&b.GetComponentsInChildren<UnityEngine.MeshFilter>(true).Any(f=>f.sharedMesh!=null&&UnityEditor.AssetDatabase.GetAssetPath(f.sharedMesh).StartsWith("Assets/")));
 int barriers=roots.SelectMany(r=>r.GetComponentsInChildren<Game.Gameplay.Combat.MapMovementBarrier>(true)).Count();
 int visible=roots.SelectMany(r=>r.GetComponentsInChildren<UnityEngine.MeshCollider>(true)).Count(c=>c.name=="VisibleShotSurface");
 lines.Add(name+","+models+","+probes+","+hits+","+falseBlocks+","+missing+","+error.ToString("F6",System.Globalization.CultureInfo.InvariantCulture)+","+barriers+","+visible+","+aggregates);
 }finally{UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);}
}
System.IO.File.WriteAllLines("Logs/RealTest0930/map-collision-all-rays.csv",lines.ToArray());return string.Join("\n",lines.ToArray());