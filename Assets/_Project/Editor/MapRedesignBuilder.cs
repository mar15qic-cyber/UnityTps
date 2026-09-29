using System;
using System.IO;
using System.Linq;
using Game.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>Rebuilds the combat environment while retaining each scene's player, HUD, network systems and spawner.</summary>
    public static class MapRedesignBuilder
    {
        private const string SceneRoot = "Assets/_Project/Scenes/";
        private const string Military = "Assets/SimpleMilitary/Prefabs/";
        private const string MaterialRoot = "Assets/_Project/Materials/Maps/";
        private static readonly string[] Maps = { "Map_Stackyard", "Map_Depot55", "Map_Ridgeline", "Map_TrainingYard", "Map_NightRelay" };
        private static Material concrete, darkConcrete, steel, blue, red, sand, rock, olive, yellow, nightGround, nightSteel;

        [MenuItem("Tools/Maps/Rebuild Redesigned Arenas")]
        public static void RebuildAll()
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            if (setup.Any(s => SceneManager.GetSceneByPath(s.path).isDirty))
                throw new InvalidOperationException("Save active scene edits before rebuilding maps.");
            EnsureMaterials();
            try
            {
                BuildArenaFence();
                foreach (var name in Maps) BuildMap(name);
                EnhanceTeamArenas();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                foreach (var entry in new[] { ("arena", "Arena"), ("map_01", Maps[0]), ("map_02", Maps[1]),
                             ("map_03", Maps[2]), ("map_04", Maps[3]), ("map_05", Maps[4]) })
                    RoomMapPreviewBuilder.Build(entry.Item1, entry.Item2);
                VideoFollowupContentRepair.BuildMapMetadata();
                Debug.Log("[MapRedesignBuilder] Six scene environments and map previews rebuilt.");
            }
            finally { EditorSceneManager.RestoreSceneManagerSetup(setup); }
        }

        [MenuItem("Tools/Maps/Enhance Team Arenas")]
        public static void EnhanceTeamArenas()
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            if (setup.Any(s => SceneManager.GetSceneByPath(s.path).isDirty))
                throw new InvalidOperationException("Save active scene edits before enhancing maps.");
            EnsureMaterials();
            try
            {
                foreach (string name in new[] { "Map_Stackyard", "Map_Ridgeline", "Map_TrainingYard" })
                {
                    var scene = EditorSceneManager.OpenScene(SceneRoot + name + ".unity", OpenSceneMode.Single);
                    var stage = Root(scene, "--- Environment ---").transform.Find("DesignedCombatSpace");
                    if (stage == null) throw new InvalidOperationException(name + " missing DesignedCombatSpace");
                    var old = stage.Find("TeamRouteEnhancement");
                    if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
                    var root = new GameObject("TeamRouteEnhancement").transform;
                    root.SetParent(stage, false);
                    float halfWidth = name == "Map_Ridgeline" ? 23f : name == "Map_TrainingYard" ? 20f : 21f;
                    float halfDepth = name == "Map_Ridgeline" ? 17f : name == "Map_TrainingYard" ? 15f : 16f;
                    foreach (int side in new[] { -1, 1 })
                    {
                        var team = side < 0 ? red : blue;
                        float x = side * (halfWidth - 4.7f);
                        // A readable red/blue staging area around each four-player spawn group.
                        Cube(side < 0 ? "RedSpawnDeck" : "BlueSpawnDeck", root,
                            new Vector3(x, .015f, 0), new Vector3(6.5f, .03f, halfDepth * 1.75f), team);
                        Cube("TeamGatePost", root, new Vector3(side * (halfWidth - 8f), 1.45f, -halfDepth * .45f),
                            new Vector3(.35f, 2.9f, .35f), team);
                        Cube("TeamGatePost", root, new Vector3(side * (halfWidth - 8f), 1.45f, halfDepth * .45f),
                            new Vector3(.35f, 2.9f, .35f), team);
                        Cube("TeamGateBeam", root, new Vector3(side * (halfWidth - 8f), 2.85f, 0),
                            new Vector3(.4f, .3f, halfDepth * .9f), team);
                    }
                    if (name == "Map_Stackyard")
                    {
                        // Split the container arena into a covered center crossing and exposed edge flank.
                        foreach (int side in new[] { -1, 1 })
                            Cube("OffsetContainerCover", root, new Vector3(side * 3.5f, 1.15f, side * 3.6f),
                                new Vector3(5f, 2.3f, 1.4f), side < 0 ? red : blue);
                    }
                    else if (name == "Map_Ridgeline")
                    {
                        // Open long-range sightline over a low creek; these low rocks support sniper play.
                        Cube("NorthOverlook", root, new Vector3(0, .75f, 12.2f), new Vector3(6f, 1.5f, 2.5f), rock);
                        Cube("SouthOverlook", root, new Vector3(0, .75f, -12.2f), new Vector3(6f, 1.5f, 2.5f), rock);
                        foreach (int side in new[] { -1, 1 })
                            Cube("CreekCrossingCover", root, new Vector3(side * 5.2f, .55f, 0),
                                new Vector3(1.8f, 1.1f, 1.7f), sand);
                    }
                    else
                    {
                        // Training Yard has short sightlines and staggered breach obstacles.
                        Cube("BreachNorth", root, new Vector3(-1.8f, .85f, 4.5f),
                            new Vector3(4.5f, 1.7f, .65f), steel);
                        Cube("BreachSouth", root, new Vector3(1.8f, .85f, -4.5f),
                            new Vector3(4.5f, 1.7f, .65f), steel);
                    }
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
                foreach (var entry in new[] { ("map_01", "Map_Stackyard"), ("map_03", "Map_Ridgeline"),
                             ("map_04", "Map_TrainingYard") })
                    RoomMapPreviewBuilder.Build(entry.Item1, entry.Item2);
            }
            finally { EditorSceneManager.RestoreSceneManagerSetup(setup); }
        }

        private static void BuildArenaFence()
        {
            var scene = EditorSceneManager.OpenScene(SceneRoot + "Arena.unity", OpenSceneMode.Single);
            var environment = Root(scene, "--- Environment ---").transform;
            var old = environment.Find("SafetyFence");
            if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
            var fence = new GameObject("SafetyFence").transform;
            fence.SetParent(environment);
            // The authored floor is 24 x 24 m. A 2.2 m collision barrier sits just inside every edge.
            Boundary(fence, 23.8f, 23.8f, steel, true);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private static void BuildMap(string name)
        {
            var path = SceneRoot + name + ".unity";
            if (!File.Exists(path))
            {
                File.Copy(SceneRoot + "Arena.unity", path);
                AssetDatabase.ImportAsset(path);
            }
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var environment = Root(scene, "--- Environment ---").transform;
            foreach (Transform child in environment.Cast<Transform>().ToArray())
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            var targetRoot = Root(scene, "--- Targets ---");
            targetRoot.SetActive(false);
            var stage = new GameObject("DesignedCombatSpace").transform;
            stage.SetParent(environment);
            bool night = name == "Map_NightRelay";
            int width = name == "Map_Depot55" ? 68 : night ? 72 : name == "Map_Ridgeline" ? 46 : name == "Map_TrainingYard" ? 40 : 42;
            int depth = name == "Map_Depot55" ? 46 : night ? 50 : name == "Map_Ridgeline" ? 34 : name == "Map_TrainingYard" ? 30 : 32;
            Cube("Ground", stage, new Vector3(0, -.25f, 0), new Vector3(width, .5f, depth), night ? nightGround : name == "Map_Ridgeline" ? sand : concrete);
            Boundary(stage, width, depth, night ? nightSteel : steel, false);
            SetSpawns(scene, width, depth);
            SetLighting(scene, night);
            switch (name)
            {
                case "Map_Stackyard": Stackyard(stage); break;
                case "Map_Depot55": Depot(stage); break;
                case "Map_Ridgeline": Ridgeline(stage); break;
                case "Map_TrainingYard": TrainingYard(stage); break;
                case "Map_NightRelay": NightRelay(stage); break;
            }
            LightmapSettings.lightmaps = Array.Empty<LightmapData>();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private static GameObject Root(Scene scene, string name)
            => scene.GetRootGameObjects().Single(x => x.name == name);

        private static void SetSpawns(Scene scene, float width, float depth)
        {
            var spawns = Root(scene, "SpawnPoints").transform.Cast<Transform>().OrderBy(t => t.name).ToArray();
            if (spawns.Length != 8) throw new InvalidOperationException(scene.name + " requires eight spawn transforms.");
            for (int i = 0; i < spawns.Length; i++)
            {
                int side = i < 4 ? -1 : 1;
                float z = new[] { -depth * .25f, 0f, depth * .25f, 0f }[i % 4];
                float x = side * (width * .5f - (i % 4 == 3 ? 6.5f : 4.5f));
                spawns[i].position = new Vector3(x, .2f, z);
                spawns[i].rotation = Quaternion.Euler(0, side < 0 ? 90 : -90, 0);
            }
        }

        private static void SetLighting(Scene scene, bool night)
        {
            var lights = Root(scene, "--- Lighting ---").GetComponentsInChildren<Light>(true);
            foreach (var light in lights)
            {
                light.enabled = true;
                light.lightmapBakeType = LightmapBakeType.Realtime;
                light.intensity = night ? .18f : 1.1f;
                light.color = night ? new Color(.33f, .43f, .7f) : Color.white;
                light.transform.rotation = Quaternion.Euler(night ? 22 : 48, -28, 0);
            }
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = night ? new Color(.10f, .13f, .19f) : new Color(.46f, .49f, .53f);
            RenderSettings.fog = night;
            if (night)
            {
                RenderSettings.skybox = null;
                RenderSettings.fogColor = new Color(.025f, .035f, .065f);
                RenderSettings.fogDensity = .009f;
            }
        }

        private static void Boundary(Transform parent, float width, float depth, Material material, bool arena)
        {
            float x = width * .5f - .12f, z = depth * .5f - .12f;
            // The invisible rails are continuous. The visible military fence has gaps between posts but cannot be crossed.
            Cube("Boundary_N", parent, new Vector3(0, 1.05f, z), new Vector3(width, 2.1f, .24f), material, visible: !arena);
            Cube("Boundary_S", parent, new Vector3(0, 1.05f, -z), new Vector3(width, 2.1f, .24f), material, visible: !arena);
            Cube("Boundary_E", parent, new Vector3(x, 1.05f, 0), new Vector3(.24f, 2.1f, depth), material, visible: !arena);
            Cube("Boundary_W", parent, new Vector3(-x, 1.05f, 0), new Vector3(.24f, 2.1f, depth), material, visible: !arena);
            if (!arena) return;
            for (float p = -9.6f; p <= 9.7f; p += 4.8f)
            {
                Prop("Environments/fence_02", parent, new Vector3(p, .5f, z), Quaternion.identity, new Vector3(1f, .45f, 1f));
                Prop("Environments/fence_02", parent, new Vector3(p, .5f, -z), Quaternion.identity, new Vector3(1f, .45f, 1f));
                Prop("Environments/fence_02", parent, new Vector3(x, .5f, p), Quaternion.Euler(0, 90, 0), new Vector3(1f, .45f, 1f));
                Prop("Environments/fence_02", parent, new Vector3(-x, .5f, p), Quaternion.Euler(0, 90, 0), new Vector3(1f, .45f, 1f));
            }
        }

        private static void Stackyard(Transform root)
        {
            // Three compact lanes: container tunnels north/south and a contested central gantry.
            foreach (int side in new[] { -1, 1 })
            {
                for (int lane = -1; lane <= 1; lane += 2)
                {
                    float z = lane * 9.2f;
                    Container(root, new Vector3(side * 7.5f, 1.25f, z), side < 0 ? red : blue);
                    Container(root, new Vector3(side * 12.8f, 1.25f, lane * 5.3f), darkConcrete);
                    Prop("Environments/crate_01", root, new Vector3(side * 4.2f, 0, lane * 12f), Quaternion.identity);
                }
                Cube("GantryLeg", root, new Vector3(side * 2.5f, 1.6f, 0), new Vector3(.65f, 3.2f, .65f), yellow);
                Cube("ForkliftCover", root, new Vector3(side * 11f, .8f, 0), new Vector3(2.8f, 1.6f, 2.3f), olive);
            }
            Cube("GantryBeam", root, new Vector3(0, 3.2f, 0), new Vector3(5.7f, .5f, .7f), yellow);
            Cube("CenterCover", root, new Vector3(0, .85f, 0), new Vector3(2.4f, 1.7f, 2.4f), steel);
        }

        private static void Depot(Transform root)
        {
            // Medium motor pool: garage blocks the cross-map sightline, fuel trucks form paired flank routes.
            BuildingShell(root, "CentralGarage", 0, 0, 14, 15, 3.7f, concrete);
            Cube("DepotSign", root, new Vector3(0, 3.35f, -7.6f), new Vector3(5, .65f, .18f), yellow);
            foreach (int side in new[] { -1, 1 })
            {
                BuildingShell(root, "VehicleBay", side * 22, 0, 11, 12, 3.2f, darkConcrete);
                Prop("Vehicles/truck_troop_a", root, new Vector3(side * 17, 0, 16), Quaternion.Euler(0, side < 0 ? 90 : -90, 0), Vector3.one * .78f);
                Prop("Vehicles/truck_fuel_a", root, new Vector3(side * 19, 0, -16), Quaternion.Euler(0, side < 0 ? 90 : -90, 0), Vector3.one * .72f);
                Cube("FreightPallet", root, new Vector3(side * 9.5f, .75f, 15), new Vector3(3, 1.5f, 2), olive);
                Cube("FreightPallet", root, new Vector3(side * 9.5f, .75f, -15), new Vector3(3, 1.5f, 2), olive);
                Prop("Environments/sandbags_01", root, new Vector3(side * 27.5f, 0, 12), Quaternion.Euler(0, 90, 0), Vector3.one * .5f);
                Prop("Environments/sandbags_01", root, new Vector3(side * 27.5f, 0, -12), Quaternion.Euler(0, 90, 0), Vector3.one * .5f);
            }
        }

        private static void Ridgeline(Transform root)
        {
            // Small canyon: paired rock shelves and mirrored ramps make the elevated routes equally reachable.
            foreach (int side in new[] { -1, 1 })
            {
                foreach (int lane in new[] { -1, 1 })
                {
                    Cube("RockShelf", root, new Vector3(side * 8.5f, 1.05f, lane * 10f), new Vector3(10, 2.1f, 4), rock);
                    Cube("AccessRamp", root, new Vector3(side * 15.2f, 1.1f, lane * 10f), new Vector3(5.3f, .4f, 3.7f), sand,
                        Quaternion.Euler(0, 0, -side * 25));
                    Cube("Boulder", root, new Vector3(side * 3.5f, .95f, lane * 6.6f), new Vector3(2.6f, 1.9f, 2.1f), rock,
                        Quaternion.Euler(0, side * 23, 0));
                }
                Cube("RidgeFace", root, new Vector3(side * 21f, 1.3f, 0), new Vector3(2.2f, 2.6f, 11), rock);
            }
            Cube("DryCreek", root, new Vector3(0, .025f, 0), new Vector3(3.5f, .05f, 28), darkConcrete);
            Cube("CenterStone", root, new Vector3(0, 1f, 0), new Vector3(3, 2, 3), rock);
        }

        private static void TrainingYard(Transform root)
        {
            // Short, dense drills with mirrored blockhouse entrances and an open central breach.
            foreach (int side in new[] { -1, 1 })
            {
                BuildingShell(root, "Blockhouse", side * 9.7f, 0, 7.5f, 7, 2.8f, concrete);
                for (int lane = -1; lane <= 1; lane++)
                {
                    float z = lane * 9.5f;
                    Cube("PracticeBarrier", root, new Vector3(side * 3.6f, .85f, z), new Vector3(1.1f, 1.7f, 3), lane == 0 ? yellow : olive);
                    if (lane != 0)
                        Prop("Environments/crate_02", root, new Vector3(side * 15.3f, 0, z), Quaternion.identity, Vector3.one * .8f);
                }
                Cube("EntryShield", root, new Vector3(side * 16.6f, 1.05f, 0), new Vector3(.5f, 2.1f, 4), steel);
            }
            Cube("BreachDivider", root, new Vector3(0, 1.05f, 0), new Vector3(1.8f, 2.1f, 4.5f), darkConcrete);
        }

        private static void NightRelay(Transform root)
        {
            // Medium night arena: sparse paired lamps, two service buildings and a central radio mast.
            foreach (int side in new[] { -1, 1 })
            {
                BuildingShell(root, "RelayStation", side * 18, 0, 12, 12, 3.3f, nightSteel);
                Prop("Vehicles/armor_car_a", root, new Vector3(side * 26, 0, 14), Quaternion.Euler(0, side < 0 ? 90 : -90, 0), Vector3.one * .8f);
                Prop("Environments/tent_01", root, new Vector3(side * 25, 0, -15), Quaternion.Euler(0, 90, 0), Vector3.one * .5f);
                foreach (int lane in new[] { -1, 1 })
                {
                    Cube("Generator", root, new Vector3(side * 8, .8f, lane * 15), new Vector3(3.3f, 1.6f, 2.4f), olive);
                    Lamp(root, new Vector3(side * 21, 4.4f, lane * 17), side < 0 ? new Color(1f, .65f, .33f) : new Color(.55f, .75f, 1f));
                }
            }
            Cube("RadioMast", root, new Vector3(0, 5.5f, 0), new Vector3(.9f, 11, .9f), steel);
            Cube("RadioBase", root, new Vector3(0, 1.25f, 0), new Vector3(6, 2.5f, 6), nightSteel);
            Cube("AntennaCrossbar", root, new Vector3(0, 9, 0), new Vector3(7, .35f, .35f), steel);
            Lamp(root, new Vector3(0, 7, 0), new Color(.7f, .85f, 1f), 12);
        }

        private static void BuildingShell(Transform root, string name, float x, float z, float width, float depth, float height, Material material)
        {
            var shell = new GameObject(name).transform;
            shell.SetParent(root);
            float halfX = width * .5f, halfZ = depth * .5f;
            Cube("Roof", shell, new Vector3(x, height, z), new Vector3(width, .3f, depth), material);
            foreach (int side in new[] { -1, 1 })
            {
                Cube("SideWall", shell, new Vector3(x, height * .5f, z + side * halfZ), new Vector3(width, height, .4f), material);
                // Two wide east/west entrances prevent a closed box or a one-team choke advantage.
                Cube("DoorJamb", shell, new Vector3(x + side * halfX, height * .5f, z - depth * .34f), new Vector3(.4f, height, depth * .32f), material);
                Cube("DoorJamb", shell, new Vector3(x + side * halfX, height * .5f, z + depth * .34f), new Vector3(.4f, height, depth * .32f), material);
            }
        }

        private static void Container(Transform root, Vector3 position, Material color)
        {
            Cube("ShippingContainer", root, position, new Vector3(6, 2.5f, 2.7f), color);
            for (int i = -2; i <= 2; i++)
                Cube("ContainerRib", root, position + new Vector3(i * 1.1f, 0, 1.37f), new Vector3(.08f, 2.5f, .08f), steel);
        }

        private static void Lamp(Transform root, Vector3 position, Color color, float range = 10)
        {
            Cube("LampPole", root, new Vector3(position.x, position.y * .5f, position.z), new Vector3(.2f, position.y, .2f), steel);
            Cube("LampHousing", root, position, new Vector3(.8f, .3f, .8f), yellow);
            var light = new GameObject("SparseNightLight").AddComponent<Light>();
            light.transform.SetParent(root);
            light.transform.position = position;
            light.type = LightType.Point;
            light.color = color;
            light.range = range;
            light.intensity = 1.2f;
            light.lightmapBakeType = LightmapBakeType.Realtime;
            light.shadows = LightShadows.Soft;
        }

        private static GameObject Cube(string name, Transform parent, Vector3 position, Vector3 size, Material material,
            Quaternion rotation = default, bool visible = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent);
            go.transform.position = position;
            go.transform.localScale = size;
            go.transform.rotation = rotation == default ? Quaternion.identity : rotation;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.enabled = visible;
            return go;
        }

        private static GameObject Prop(string path, Transform parent, Vector3 position, Quaternion rotation, Vector3 scale = default)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Military + path + ".prefab");
            if (prefab == null) throw new InvalidOperationException("Missing SimpleMilitary prop: " + path);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.transform.SetParent(parent);
            go.transform.position = position;
            go.transform.rotation = rotation;
            go.transform.localScale = scale == default ? Vector3.one : scale;
            // SimpleMilitary's source materials use a shader that is pink under this URP project.
            // Preserve the meshes, but bind this project's URP/Lit palette at the scene instance.
            var surface = path.Contains("Vehicles/") ? steel : path.Contains("fence_") ? darkConcrete
                : path.Contains("crate_") || path.Contains("sandbags_") ? olive : sand;
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterials = Enumerable.Repeat(surface, renderer.sharedMaterials.Length).ToArray();
            // This tent has open sides. A bounds box fills those openings and blocks
            // eye-authoritative shots, so use the visible mesh for its collision.
            if (path == "Environments/tent_01")
            {
                var filter = go.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    throw new InvalidOperationException("Tent mesh is required for collision.");
                var meshCollider = go.GetComponent<MeshCollider>();
                if (meshCollider == null) meshCollider = go.AddComponent<MeshCollider>();
                meshCollider.sharedMesh = filter.sharedMesh;
                meshCollider.convex = false;
                return go;
            }
            if (go.GetComponentInChildren<Collider>(true) == null)
            {
                var bounds = go.GetComponentsInChildren<Renderer>(true).Select(r => r.bounds).ToArray();
                if (bounds.Length > 0)
                {
                    var combined = bounds[0];
                    foreach (var b in bounds.Skip(1)) combined.Encapsulate(b);
                    var collider = go.AddComponent<BoxCollider>();
                    collider.center = go.transform.InverseTransformPoint(combined.center);
                    var localSize = go.transform.InverseTransformVector(combined.size);
                    collider.size = new Vector3(Mathf.Abs(localSize.x), Mathf.Abs(localSize.y), Mathf.Abs(localSize.z));
                }
            }
            return go;
        }

        private static void EnsureMaterials()
        {
            Directory.CreateDirectory(MaterialRoot);
            concrete = Material("Concrete", new Color(.52f, .53f, .49f));
            darkConcrete = Material("DarkConcrete", new Color(.31f, .36f, .37f));
            steel = Material("Steel", new Color(.29f, .34f, .37f));
            blue = Material("TeamBlue", new Color(.18f, .35f, .48f));
            red = Material("TeamRed", new Color(.52f, .29f, .23f));
            sand = Material("Sand", new Color(.56f, .46f, .31f));
            rock = Material("Rock", new Color(.39f, .36f, .31f));
            olive = Material("Olive", new Color(.33f, .38f, .27f));
            yellow = Material("SafetyYellow", new Color(.76f, .57f, .15f));
            nightGround = Material("NightGround", new Color(.18f, .24f, .33f));
            nightSteel = Material("NightSteel", new Color(.28f, .36f, .46f));
        }

        private static Material Material(string name, Color color)
        {
            var path = MaterialRoot + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            material.color = color;
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
