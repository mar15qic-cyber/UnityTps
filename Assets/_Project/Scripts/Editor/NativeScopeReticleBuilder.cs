using Game.Gameplay.Settings;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    public static class NativeScopeReticleBuilder
    {
        [MenuItem("Tools/Attachments/Rebuild Native LPFP Reticle Catalog")]
        public static void Build()
        {
            const string path = "Assets/_Project/Resources/NativeScopeReticleCatalog.asset";
            const string matPath = "Assets/_Project/Resources/NativeScopeReticleUI.mat";
            const string source = "Assets/Low Poly FPS Pack/Components/Textures_&_Sprites/Scope_Textures/";
            var shader = Shader.Find("Game/UI/NativeScopeReticle");
            if (shader == null) throw new System.InvalidOperationException("Missing native reticle shader");
            var material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, matPath); }
            var catalog = AssetDatabase.LoadAssetAtPath<NativeScopeReticleCatalog>(path);
            if (catalog == null) { catalog = ScriptableObject.CreateInstance<NativeScopeReticleCatalog>(); AssetDatabase.CreateAsset(catalog, path); }
            var rows = new[] {
                (OpticReticleStyle.Dot, OpticReticleColor.Red, "Red_Dot_Sight_Texture"),
                (OpticReticleStyle.CircleDot, OpticReticleColor.Red, "Red_Dot_Sight_2_Texture"),
                (OpticReticleStyle.Chevron, OpticReticleColor.Red, "Red_Arrow_Sight_Texture"),
                (OpticReticleStyle.Chevron, OpticReticleColor.Blue, "Blue_Arrow_Sight_Texture"),
                (OpticReticleStyle.Diamond, OpticReticleColor.Red, "Red_Square_Sight_Texture"),
                (OpticReticleStyle.Diamond, OpticReticleColor.Blue, "Blue_Square_Sight_Texture"),
                (OpticReticleStyle.Diamond, OpticReticleColor.Orange, "Orange_Square_Sight_Texture"),
                (OpticReticleStyle.ThreePost, OpticReticleColor.Blue, "Dot_Sight_Blue_Texture") };
            var so = new SerializedObject(catalog);
            const string sniperMaterialPath = "Assets/_Project/Resources/NativeSniperScope.mat";
            var sniperMaterial = AssetDatabase.LoadAssetAtPath<Material>(sniperMaterialPath);
            if (sniperMaterial == null)
            {
                sniperMaterial = new Material(Shader.Find("Game/UI/NativeSniperScope"));
                AssetDatabase.CreateAsset(sniperMaterial, sniperMaterialPath);
            }
            var sniperTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(source + "Sniper_01_Scope_Texture.png");
            sniperMaterial.mainTexture = sniperTexture;
            EditorUtility.SetDirty(sniperMaterial); AssetDatabase.SaveAssetIfDirty(sniperMaterial);
            so.FindProperty("sniperTexture").objectReferenceValue = sniperTexture;
            so.FindProperty("sniperMaterial").objectReferenceValue = sniperMaterial;
            so.FindProperty("additiveMaterial").objectReferenceValue = material;
            var entries = so.FindProperty("entries"); entries.arraySize = rows.Length;
            for (int i=0;i<rows.Length;i++)
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(source + rows[i].Item3 + ".png");
                if (texture == null) throw new System.InvalidOperationException("Missing source texture: " + rows[i].Item3);
                var entry = entries.GetArrayElementAtIndex(i);
                entry.FindPropertyRelative("style").intValue = (int)rows[i].Item1;
                entry.FindPropertyRelative("color").intValue = (int)rows[i].Item2;
                entry.FindPropertyRelative("texture").objectReferenceValue = texture;
                // Native arrow's bright apex is at bottom-origin y=248..249, not texture center.
                entry.FindPropertyRelative("aimUv").vector2Value = rows[i].Item1 == OpticReticleStyle.Chevron
                    ? new Vector2(.5f, 249f/512f) : new Vector2(.5f, .5f);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(catalog);
            AssetDatabase.SaveAssetIfDirty(material);
        }
    }
}
