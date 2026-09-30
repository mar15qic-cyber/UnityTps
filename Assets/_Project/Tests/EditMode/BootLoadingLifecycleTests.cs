using System.Linq;
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using Object=UnityEngine.Object;

namespace Game.Gameplay.Tests
{
 public sealed class BootLoadingLifecycleTests
 {
  [Test]
  public void RetryInputDoesNotBelongToPersistentLoadingOverlay()
  {
   var existing=Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include,FindObjectsSortMode.None).Select(e=>e.GetInstanceID()).ToArray();
   var scene=EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/Boot.unity");
   try
   {
    var entry=scene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<BootEntry>()).Single();
    var overlay=scene.GetRootGameObjects().Single(r=>r.name=="BootLoading");
    var flags=BindingFlags.Instance|BindingFlags.NonPublic;
    typeof(BootEntry).GetField("_loadingRoot",flags).SetValue(entry,overlay);
    typeof(BootEntry).GetMethod("Update",flags).Invoke(entry,null);
    foreach(var events in Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None))
     Assert.False(events.transform.IsChildOf(overlay.transform),"Lobby must not inherit an EventSystem destroyed with the loading overlay");
   }
   finally
   {
    foreach(var events in Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include,FindObjectsSortMode.None).Where(e=>!existing.Contains(e.GetInstanceID())))Object.DestroyImmediate(events.gameObject);
    EditorSceneManager.ClosePreviewScene(scene);
   }
  }
 }
}
