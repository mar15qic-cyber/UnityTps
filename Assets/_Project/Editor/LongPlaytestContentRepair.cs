using System;
using System.Collections.Generic;
using System.Linq;
using Game.EditorTools;
using Game.Gameplay.Combat;
using Game.Gameplay.Weapon;
using Game.UI;
using UnityEditor;
using UnityEngine;

public static class LongPlaytestContentRepair
{
    [MenuItem("Tools/UnityFps/Repair Long Playtest Content")]
    public static void Run()
    {
        var weapons = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
        var grips = new List<LobbyWeaponGripCatalog.Grip>();
        foreach (var entry in weapons.Entries.Where(e => e.IsLpfp && e.definition != null))
        {
            var definition = entry.definition;
            var so = new SerializedObject(definition);
            foreach (var field in new[] { "firstPersonAnimations", "rifle01Animations", "rifle02Animations", "rifle03Animations" })
            {
                var idle = so.FindProperty(field + ".Idle").objectReferenceValue as AnimationClip;
                if (idle == null) continue;
                var clip = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(idle)).OfType<AnimationClip>()
                    .FirstOrDefault(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal) && c.name.Contains("grenade_throw"));
                if (clip == null) throw new InvalidOperationException("Missing exported throw: " + entry.itemId);
                so.FindProperty(field + ".ThrowGrenade").objectReferenceValue = clip;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(definition);
            var registration = ResolveRegistration(entry.itemId);
            var fp = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab));
            try
            {
                definition.FirstPersonAnimations.Idle.SampleAnimation(fp, 0f);
                var bone = fp.transform.Find("Armature/weapon");
                var hand = fp.transform.Find("Armature/arm_R/lower_arm_R/hand_R");
                Vector3 Point(Transform t) => Quaternion.Inverse(registration.Rotation)
                    * (bone.InverseTransformPoint(t.position) - registration.Position);
                grips.Add(new LobbyWeaponGripCatalog.Grip {
                    weaponId = entry.itemId, wrist = Point(hand),
                    index = Point(hand.Find("finger_01_R")), middle = Point(hand.Find("middle_finger_01_R")),
                    indexDistal = Point(hand.Find("finger_01_R/finger_02_R/finger_03_R"))
                });
            }
            finally { PrefabUtility.UnloadPrefabContents(fp); }
            if (entry.itemId == "weapon.smg05") entry.slotCapabilities = entry.slotCapabilities.Where(s => s != "Underbarrel").ToArray();
        }
        var attachments = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
        foreach (var id in new[] { "attach.lpw.tactical.laser", "attach.lpw.tactical.light" })
        {
            var entry = attachments.Find(id);
            entry.mountEuler = new Vector3(180f, 0f, 0f);
            var points = entry.prefab.GetComponentsInChildren<MeshFilter>(true).SelectMany(m =>
                m.sharedMesh.vertices.Select(v => entry.MountRotation * entry.prefab.transform.InverseTransformPoint(m.transform.TransformPoint(v)))).ToArray();
            entry.mountOffset = new Vector3(-(points.Min(v => v.x) + points.Max(v => v.x)) * .5f,
                -points.Min(v => v.y), -(points.Min(v => v.z) + points.Max(v => v.z)) * .5f);
        }
        EditorUtility.SetDirty(attachments); AssetDatabase.SaveAssetIfDirty(attachments);
        const string path = "Assets/_Project/Resources/UI/LobbyWeaponGripCatalog.asset";
        var catalog = AssetDatabase.LoadAssetAtPath<LobbyWeaponGripCatalog>(path);
        if (catalog == null) { catalog = ScriptableObject.CreateInstance<LobbyWeaponGripCatalog>(); AssetDatabase.CreateAsset(catalog, path); }
        catalog.grips = grips.ToArray();
        EditorUtility.SetDirty(catalog); EditorUtility.SetDirty(weapons);
        AssetDatabase.SaveAssetIfDirty(catalog); AssetDatabase.SaveAssetIfDirty(weapons);
        Debug.Log("[LongPlaytestRepair] Exported throws and 16 native hand contact frames saved.");
    }

    private static NativeAttachmentMountRepair.Registration ResolveRegistration(string id)
    {
        if (NativeAttachmentMountRepair.Registrations.TryGetValue(id, out var result)) return result;
        var rotation = new Quaternion(-0.2862948f, 0f, 0f, 0.9581417f);
        return id switch {
            "weapon.sniper01" => new(new Vector3(-0.00066412f, 0.10671988f, 0.1144416f), rotation),
            "weapon.sniper02" => new(new Vector3(-0.00102966f, 0.13277069f, 0.14450932f), rotation),
            "weapon.sniper03" => new(new Vector3(-0.00156903f, 0.08625868f, 0.06285719f), rotation),
            _ => throw new InvalidOperationException("No verified native registration: " + id)
        };
    }
}
