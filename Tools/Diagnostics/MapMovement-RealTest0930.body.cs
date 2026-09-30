UnityEditor.EditorApplication.CallbackFunction probe=null;probe=delegate{
 if(UnityEditor.EditorApplication.isCompiling||UnityEditor.EditorApplication.isUpdating)return;
 UnityEditor.EditorApplication.update-=probe;
 var rows=new System.Collections.Generic.List<string>{"Scene,Case,Start,End,Passed"};
 foreach(var name in new[]{"Arena","Map_Stackyard","Map_Depot55","Map_Ridgeline","Map_TrainingYard","Map_NightRelay"}){
  var scene=UnityEditor.SceneManagement.EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/"+name+".unity");
  try{
   var barriers=scene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<Game.Gameplay.Combat.MapMovementBarrier>(true)).Select(m=>m.transform).Concat(scene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<UnityEngine.Transform>(true)).Where(t=>t.name=="AccessRamp")).ToArray();
   foreach(var marker in barriers){
    var box=marker.GetComponent<UnityEngine.BoxCollider>();var mesh=marker.GetComponent<UnityEngine.MeshCollider>();
    var player=new UnityEngine.GameObject("AuditMoveCapsule");UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(player,scene);
    try{
     var cc=player.AddComponent<UnityEngine.CharacterController>();cc.height=1.8f;cc.radius=.5f;cc.center=new UnityEngine.Vector3(0,.98f,.45f);cc.stepOffset=.4f;cc.skinWidth=.08f;cc.slopeLimit=45;
     UnityEngine.Vector3 start,direction;float distance,height=0;
     if(box!=null&&marker.name!="AccessRamp"){var b=box.bounds;direction=b.size.x<b.size.z?UnityEngine.Vector3.right:UnityEngine.Vector3.forward;if(UnityEngine.Vector3.Dot(b.center,direction)<0)direction=-direction;distance=6;start=b.center-direction*(UnityEngine.Vector3.Dot(b.extents,new UnityEngine.Vector3(UnityEngine.Mathf.Abs(direction.x),0,UnityEngine.Mathf.Abs(direction.z)))+1.1f);start.y=.02f;}
     else if(box!=null&&marker.name=="AccessRamp"){
      var ax=marker.TransformPoint(new UnityEngine.Vector3(-.5f,.5f,0));var bx=marker.TransformPoint(new UnityEngine.Vector3(.5f,.5f,0));var az=marker.TransformPoint(new UnityEngine.Vector3(0,.5f,-.5f));var bz=marker.TransformPoint(new UnityEngine.Vector3(0,.5f,.5f));
      var a=UnityEngine.Mathf.Abs(ax.y-bx.y)>UnityEngine.Mathf.Abs(az.y-bz.y)?ax:az;var b=UnityEngine.Mathf.Abs(ax.y-bx.y)>UnityEngine.Mathf.Abs(az.y-bz.y)?bx:bz;
      if(a.y>b.y){var swap=a;a=b;b=swap;}direction=b-a;direction.y=0;distance=direction.magnitude+1.6f;direction.Normalize();start=a-direction*.8f;start.y=UnityEngine.Mathf.Max(.02f,a.y+.02f);height=b.y;
     }
     else if(mesh!=null&&mesh.sharedMesh!=null){var v=mesh.sharedMesh.vertices;var a=mesh.transform.TransformPoint((v[0]+v[1])*.5f);var b=mesh.transform.TransformPoint((v[2]+v[3])*.5f);if(a.y>b.y){var swap=a;a=b;b=swap;}direction=b-a;direction.y=0;distance=direction.magnitude+2;direction.Normalize();start=a-direction*.8f;start.y=a.y+.02f;height=b.y;}
     else{continue;}
     player.transform.position=start;player.transform.rotation=UnityEngine.Quaternion.LookRotation(direction);UnityEngine.Physics.SyncTransforms();
     for(int step=0;step<(int)(distance/.04f);step++)cc.Move(direction*.04f+UnityEngine.Vector3.down*.012f);
     var end=player.transform.position;bool passed=box!=null&&marker.name!="AccessRamp"?UnityEngine.Vector3.Dot(end-box.bounds.center,direction)<.6f:end.y>=height-.45f;
     rows.Add(name+","+marker.name+",\""+start+"\",\""+end+"\","+passed);
    }finally{UnityEngine.Object.DestroyImmediate(player);}
   }
  }finally{UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);}
 }
 System.IO.File.WriteAllLines("Logs/RealTest0930/map-movement.csv",rows.ToArray());UnityEngine.Debug.Log(string.Join("\n",rows.ToArray()));
};UnityEditor.EditorApplication.update+=probe;return "Scheduled map boundary/stair CharacterController sweep";