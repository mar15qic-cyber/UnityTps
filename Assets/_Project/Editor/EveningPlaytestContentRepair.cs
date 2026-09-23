using System;
using System.Linq;
using Game.Gameplay.Weapon;
using Game.Presentation.Weapon;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    public static class EveningPlaytestContentRepair
    {
        [MenuItem("Tools/UnityFps/Repair Evening Playtest Content")]
        public static void Repair()
        {
            const string native = "Assets/Low Poly FPS Pack/";
            var fire = AssetDatabase.LoadAssetAtPath<AudioClip>(native + "Components/Audio/Shoot/shoot.wav");
            var suppressed = AssetDatabase.LoadAssetAtPath<AudioClip>(native + "Components/Audio/Shoot/shoot_silencer.wav");
            if (fire == null || suppressed == null) throw new InvalidOperationException("LPFP source audio missing");
            foreach (var guid in AssetDatabase.FindAssets("t:WeaponDefinition", new[] { "Assets/_Project/ScriptableObjects/Weapons" }))
            {
                var definition = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (definition == null || !definition.name.StartsWith("Day", StringComparison.Ordinal)) continue;
                var audio = definition.AudioProfile;
                if (audio != null)
                {
                    audio.FireVariants = new[] { Entry(fire) };
                    audio.FireVariantsSuppressed = new[] { Entry(suppressed) };
                    EditorUtility.SetDirty(audio);
                }
                if (definition.name == "Day3_Shotgun01")
                {
                    var clips = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(definition.FirstPersonAnimations.Idle))
                        .OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal)).ToArray();
                    var so = new SerializedObject(definition);
                    foreach (var pair in new[] { ("ReloadOpen", "reload_open@"), ("ReloadInsert", "reload_insert@"), ("ReloadClose", "reload_close@") })
                        so.FindProperty("firstPersonAnimations." + pair.Item1).objectReferenceValue = clips.Single(c => c.name.StartsWith(pair.Item2, StringComparison.Ordinal));
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                if (!definition.name.StartsWith("Day3_SMG", StringComparison.Ordinal)) continue;
                string number = definition.name.Substring("Day3_SMG".Length);
                string sourcePath = native + "Prefabs/Example_Prefabs/Arms/SMG_" + number + "_Example_Prefab/SMG_" + number + "_FPSController.prefab";
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
                var particles = source.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Muzzleflash Particles");
                var sourceBone = particles.parent.parent;
                var local = sourceBone.InverseTransformPoint(particles.position);
                string viewPath = AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab);
                var view = PrefabUtility.LoadPrefabContents(viewPath);
                try
                {
                    var muzzle = view.GetComponent<WeaponView>().Muzzle;
                    var bone = view.transform.Find("Armature/weapon");
                    muzzle.position = bone.TransformPoint(local);
                    PrefabUtility.SaveAsPrefabAsset(view, viewPath);
                }
                finally { PrefabUtility.UnloadPrefabContents(view); }
            }
            AssetDatabase.SaveAssets();
        }

        private static WeaponAudioProfile.ClipEntry Entry(AudioClip clip) => new()
        { Clip = clip, VolumeRange = Vector2.one, PitchRange = Vector2.one };
    }
}
