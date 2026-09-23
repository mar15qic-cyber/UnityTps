using System;
using System.Linq;
using FishNet.Component.Transforming;
using FishNet.Managing.Object;
using FishNet.Object;
using Game.Gameplay.Combat;
using Game.Gameplay.Weapon;
using Game.Presentation.Audio;
using UnityEditor;
using UnityEngine;

/// <summary>一次性正式资产接线；重复运行只补缺失引用，不覆盖手动调好的 SO 数值。</summary>
public static class ThrowableContentBuilder
{
    private const string Root = "Assets/_Project";
    private const string Lpfp = "Assets/Low Poly FPS Pack";
    private const string ResourceRoot = Root + "/Resources";
    private const string PrefabRoot = Root + "/Prefabs/Throwables";
    private const string AudioRoot = Lpfp + "/Components/Audio";

    [MenuItem("Tools/UnityFps/Build Throwable Content")]
    public static void BuildAll()
    {
        EnsureFolder(ResourceRoot);
        EnsureFolder(PrefabRoot);
        EnsureFolder(Root + "/ScriptableObjects/Audio/PerWeapon");
        var projectile = BuildProjectile();
        var fragFx = BuildFragEffect();
        RepairFragMaterials();
        var smokeFx = BuildSmokeEffect();
        BuildDefinition("Frag", ThrowableType.Frag, "Hand_Grenade.prefab", fragFx,
            AudioRoot + "/Explosions/explosion-1.wav", 1.75f, 3.5f, 0.75f);
        BuildDefinition("Flash", ThrowableType.Flash, "Flashbang_1.prefab", null,
            AudioRoot + "/Misc/flashbang-effect.wav", 1.25f, 9f, 1.75f);
        BuildDefinition("Smoke", ThrowableType.Smoke, "Smoke_Grenade.prefab", smokeFx,
            null, 0.75f, 3f, 14.25f);
        var catalogPath = ResourceRoot + "/ThrowableCatalog.asset";
        if (Load<ThrowableCatalog>(catalogPath) == null
            && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(catalogPath)))
            AssetDatabase.DeleteAsset(catalogPath); // repair the first import's missing MonoScript
        var catalog = LoadOrCreate<ThrowableCatalog>(catalogPath);
        catalog.Frag = Load<ThrowableDefinition>(ResourceRoot + "/Throwable_Frag.asset");
        catalog.Flash = Load<ThrowableDefinition>(ResourceRoot + "/Throwable_Flash.asset");
        catalog.Smoke = Load<ThrowableDefinition>(ResourceRoot + "/Throwable_Smoke.asset");
        catalog.NetworkProjectilePrefab = projectile;
        if (catalog.ReleaseDelaySeconds <= 0f) catalog.ReleaseDelaySeconds = 0.15f;
        if (catalog.ThrowActionSeconds <= catalog.ReleaseDelaySeconds) catalog.ThrowActionSeconds = 0.5f;
        EditorUtility.SetDirty(catalog);
        BuildAudioConfig();
        BindWeaponAssets();
        BindPlayerPrefab();
        var registry = Load<DefaultPrefabObjects>("Assets/DefaultPrefabObjects.asset");
        var nob = projectile.GetComponent<NetworkObject>();
        if (registry == null || nob == null) throw new InvalidOperationException("FishNet prefab registry missing");
        if (!registry.Prefabs.Contains(nob)) registry.AddObject(nob, true, false);
        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[ThrowableContentBuilder] formal throwable assets, audio and player prefab wired");
    }

    private static GameObject BuildProjectile()
    {
        var path = PrefabRoot + "/ThrowableProjectile.prefab";
        var existing = Load<GameObject>(path);
        if (existing != null)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var serialized = new SerializedObject(root.GetComponent<NetworkTransform>());
                serialized.FindProperty("_clientAuthoritative").boolValue = false;
                serialized.FindProperty("_interpolation").intValue = 1;
                serialized.FindProperty("_extrapolation").intValue = 0;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            return Load<GameObject>(path);
        }
        var go = new GameObject("ThrowableProjectile");
        go.AddComponent<NetworkObject>();
        var networkTransform = go.AddComponent<NetworkTransform>();
        var transformData = new SerializedObject(networkTransform);
        transformData.FindProperty("_clientAuthoritative").boolValue = false;
        transformData.FindProperty("_interpolation").intValue = 1;
        transformData.FindProperty("_extrapolation").intValue = 0;
        transformData.ApplyModifiedPropertiesWithoutUndo();
        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity = true;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        var sphere = go.AddComponent<SphereCollider>();
        sphere.radius = 0.10f;
        go.AddComponent<ThrowableProjectile>();
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
        UnityEngine.Object.DestroyImmediate(go);
        return prefab;
    }

    private static GameObject BuildFragEffect()
    {
        var path = PrefabRoot + "/FragExplosion.prefab";
        var existing = Load<GameObject>(path);
        if (existing != null) return existing;
        var source = Load<GameObject>(Lpfp + "/Prefabs/Example_Prefabs/Explosions/Explosion Prefab.prefab");
        if (source == null) throw new InvalidOperationException("LPFP explosion prefab missing");
        var go = (GameObject)PrefabUtility.InstantiatePrefab(source);
        go.name = "FragExplosion";
        foreach (var component in go.GetComponentsInChildren<ExplosionScript>(true))
            UnityEngine.Object.DestroyImmediate(component);
        foreach (var component in go.GetComponentsInChildren<AudioSource>(true))
            UnityEngine.Object.DestroyImmediate(component);
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
        UnityEngine.Object.DestroyImmediate(go);
        return prefab;
    }

    private static GameObject BuildSmokeEffect()
    {
        var path = PrefabRoot + "/SmokeCloud.prefab";
        var existing = Load<GameObject>(path);
        if (existing != null) return existing;
        var material = Load<Material>(Root + "/Art/FX/Materials/Smoke_Particle_URP.mat");
        if (material == null) throw new InvalidOperationException("URP smoke material missing");
        var go = new GameObject("SmokeCloud");
        var particles = go.AddComponent<ParticleSystem>();
        var main = particles.main;
        main.loop = true;
        main.startLifetime = 2.8f;
        main.startSpeed = 0.05f;
        main.startSize = new ParticleSystem.MinMaxCurve(1.3f, 2.1f);
        main.startColor = new Color(0.82f, 0.84f, 0.86f, 0.62f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 260;
        var emission = particles.emission;
        emission.rateOverTime = 45f;
        var shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 2.25f;
        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.material = material;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
        UnityEngine.Object.DestroyImmediate(go);
        return prefab;
    }

    public static void RepairFragMaterials()
    {
        const string path = PrefabRoot + "/FragExplosion.prefab";
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            foreach (var renderer in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (source == null || !source.shader.name.StartsWith("Legacy Shaders/Particles/")) continue;
                    var materialPath = Root + "/Art/FX/Materials/Frag_" + source.name.Replace(" ", "_") + "_URP.mat";
                    var converted = Load<Material>(materialPath);
                    if (converted == null)
                    {
                        converted = new Material(Shader.Find(source.shader.name.Contains("Additive")
                            ? "GameFX/ParticleAdditive" : "GameFX/ParticleAlphaBlend"));
                        converted.SetTexture("_MainTex", source.mainTexture);
                        converted.SetColor("_TintColor", source.HasProperty("_TintColor") ? source.GetColor("_TintColor") : Color.white);
                        AssetDatabase.CreateAsset(converted, materialPath);
                    }
                    materials[i] = converted;
                }
                renderer.sharedMaterials = materials;
            }
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    private static void BuildDefinition(string name, ThrowableType type, string model, GameObject effect,
        string detonateClip, float fuse, float radius, float duration)
    {
        var path = ResourceRoot + "/Throwable_" + name + ".asset";
        bool created = Load<ThrowableDefinition>(path) == null;
        var definition = LoadOrCreate<ThrowableDefinition>(path);
        if (!created) return;
        definition.Type = type;
        definition.InitialCount = 1;
        definition.ModelPrefab = Load<GameObject>(Lpfp + "/Prefabs/Models_Only/Grenades/" + model);
        definition.EffectPrefab = effect;
        definition.ThrowClip = Load<AudioClip>(AudioRoot + "/Grenade_Throw/grenade-throw.wav");
        definition.ImpactClip = Load<AudioClip>(AudioRoot + "/Impacts/thud.wav");
        definition.DetonateClip = detonateClip == null ? null : Load<AudioClip>(detonateClip);
        definition.FuseSeconds = fuse;
        definition.Radius = radius;
        definition.EffectSeconds = duration;
        definition.ForwardSpeed = 12f;
        definition.UpwardSpeed = 2f;
        definition.InheritedHorizontalVelocity = 0.3f;
        definition.Mass = 0.38f;
        definition.AirDrag = 0.02f;
        definition.Bounciness = 0.55f;
        definition.DynamicFriction = 0.32f;
        definition.RollStopSpeed = 0.18f;
        definition.FragMaxDamage = type == ThrowableType.Frag ? 100f : 0f;
        definition.FlashStrength = type == ThrowableType.Flash ? 1f : 0f;
        definition.SmokeDensity = type == ThrowableType.Smoke ? 0.85f : 0f;
        EditorUtility.SetDirty(definition);
        if (!definition.IsValid(out var reason)) throw new InvalidOperationException($"{name}: {reason}");
    }

    private static void BuildAudioConfig()
    {
        var path = ResourceRoot + "/CombatAudioConfig.asset";
        bool created = Load<CombatAudioConfig>(path) == null;
        var config = LoadOrCreate<CombatAudioConfig>(path);
        if (!created) return;
        config.WalkingLoop = Load<AudioClip>(AudioRoot + "/Movement/walking_loop.wav");
        config.RunningLoop = Load<AudioClip>(AudioRoot + "/Movement/running_loop.wav");
        EditorUtility.SetDirty(config);
        if (!config.IsValid) throw new InvalidOperationException("CombatAudioConfig invalid");
    }

    private static void BindWeaponAssets()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:WeaponDefinition", new[] { Root + "/ScriptableObjects/Weapons" }))
        {
            var definition = Load<WeaponDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            if (definition == null || definition.name.IndexOf("Day", StringComparison.Ordinal) != 0) continue;
            var family = Family(definition.WeaponId);
            if (family == null) continue;
            var clip = FindThrowClip(family);
            var serialized = new SerializedObject(definition);
            serialized.FindProperty("firstPersonAnimations.ThrowGrenade").objectReferenceValue = clip;
            if (family.StartsWith("Assault_Rifle", StringComparison.Ordinal))
            {
                for (int i = 1; i <= 3; i++)
                    serialized.FindProperty("rifle0" + i + "Animations.ThrowGrenade").objectReferenceValue =
                        FindThrowClip("Assault_Rifle_0" + i);
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            BindReloadProfile(definition, family);
            EditorUtility.SetDirty(definition);
        }
    }

    private static AnimationClip FindThrowClip(string family)
    {
        var fbx = Lpfp + "/Components/Meshes/Arms/" + family + "/arms_" + family.ToLowerInvariant() + ".fbx";
        var clip = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>()
            .FirstOrDefault(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal)
                && c.name.IndexOf("grenade_throw", StringComparison.OrdinalIgnoreCase) >= 0);
        if (clip == null) throw new InvalidOperationException($"grenade_throw missing: {fbx}");
        return clip;
    }

    private static void BindReloadProfile(WeaponDefinition definition, string family)
    {
        var source = definition.AudioProfile;
        if (source == null) throw new InvalidOperationException($"{definition.name} audio profile missing");
        var path = Root + "/ScriptableObjects/Audio/PerWeapon/Audio_" + definition.name + ".asset";
        var profile = Load<WeaponAudioProfile>(path);
        if (profile == null)
        {
            profile = UnityEngine.Object.Instantiate(source);
            profile.name = "Audio_" + definition.name;
            AssetDatabase.CreateAsset(profile, path);
            var dir = AudioRoot + "/Gun_Reloads/" + family + "_Reloads/";
            var stem = family.ToLowerInvariant();
            if (family.StartsWith("Handgun", StringComparison.Ordinal))
            { dir = AudioRoot + "/Gun_Reloads/Handgun_01-04_Reloads/"; stem = "handgun_01-04"; }
            profile.MagOut = profile.MagIn = profile.BoltRack = default;
            if (family == "Sniper_01" || family == "Shotgun_01")
            {
                profile.ReloadAmmoLeft = profile.ReloadOutOfAmmo = default;
                profile.MagOut = Stage(dir + stem + "_reload_open.mp3", 0.15f);
                profile.MagIn = Stage(dir + stem + "_reload_insert.mp3", 0.5f);
                profile.BoltRack = Stage(dir + stem + "_reload_close.mp3", 0.86f);
            }
            else
            {
                profile.ReloadAmmoLeft = Entry(dir + stem + "_reload_ammo_left.mp3");
                profile.ReloadOutOfAmmo = Entry(dir + stem + "_reload_out_of_ammo.mp3");
            }
            EditorUtility.SetDirty(profile);
        }
        var serialized = new SerializedObject(definition);
        serialized.FindProperty("audioProfile").objectReferenceValue = profile;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static WeaponAudioProfile.ClipEntry Entry(string path)
    {
        var clip = Load<AudioClip>(path);
        if (clip == null) throw new InvalidOperationException($"reload clip missing: {path}");
        return new WeaponAudioProfile.ClipEntry { Clip = clip, VolumeRange = Vector2.one,
            PitchRange = new Vector2(0.97f, 1.03f) };
    }

    private static WeaponAudioProfile.StageEntry Stage(string path, float time)
    {
        var clip = Load<AudioClip>(path);
        if (clip == null) throw new InvalidOperationException($"stage clip missing: {path}");
        return new WeaponAudioProfile.StageEntry { Clip = clip, NormalizedTime = time };
    }

    private static string Family(string id)
    {
        if (id == "pistol.day2") return "Handgun_01";
        if (id == "rifle.day3") return "Assault_Rifle_01";
        if (id == null) return null;
        var parts = id.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int number)) return null;
        string prefix = parts[0] switch
        {
            "handgun" => "Handgun", "rifle" => "Assault_Rifle", "smg" => "SMG",
            "shotgun" => "Shotgun", "sniper" => "Sniper", _ => null
        };
        return prefix == null ? null : prefix + "_" + number.ToString("00");
    }

    private static void BindPlayerPrefab()
    {
        var path = Root + "/Prefabs/Player/Player_Day2_Rebuilt.prefab";
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            if (root.GetComponent<ThrowableController>() == null) root.AddComponent<ThrowableController>();
            if (root.GetComponent<FootstepAudioView>() == null) root.AddComponent<FootstepAudioView>();
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    private static T LoadOrCreate<T>(string path) where T : ScriptableObject
    {
        var asset = Load<T>(path);
        if (asset != null) return asset;
        asset = ScriptableObject.CreateInstance<T>();
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    private static T Load<T>(string path) where T : UnityEngine.Object => AssetDatabase.LoadAssetAtPath<T>(path);

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = path.Substring(0, path.LastIndexOf('/'));
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, path.Substring(path.LastIndexOf('/') + 1));
    }
}
