using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Game.Core;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>Excel-safe UTF-8 CSV round trip. All rows validate before any SO is changed.</summary>
    public sealed class WeaponTuningCsv : IPreprocessBuildWithReport
    {
        private const string BalancePath = "Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset";
        private const string CsvPath = "Assets/_Project/ScriptableObjects/Weapons/Tuning/MainlineWeaponTuning.csv";
        private static readonly string[] Ids =
        {
            "pistol.day2", "handgun.02", "handgun.03", "handgun.04",
            "rifle.day3", "rifle.02", "rifle.03", "smg.01", "smg.02", "smg.03",
            "smg.04", "smg.05", "shotgun.01", "sniper.01", "sniper.02", "sniper.03"
        };
        private static readonly string[] Fields =
        {
            "Stat.Damage", "Stat.Rpm", "Stat.MagSize", "Stat.ReserveAmmo", "Stat.ReloadTime",
            "Stat.Spread", "Stat.MaxRange", "Stat.AdsFov",
            "Stat.Recoil.PitchDeg", "Stat.Recoil.YawDeg", "Stat.Recoil.FirstShotMultiplier",
            "Stat.Recoil.Accumulation", "Stat.Recoil.MaxAccumulation", "Stat.Recoil.RecoveryDelay",
            "Stat.Recoil.RecoverySpeed", "Stat.Recoil.SpringFrequency", "Stat.Recoil.SpringDamping",
            "Stat.Recoil.ShakePositionAmplitude", "Stat.Recoil.ViewModelKickBack",
            "Stat.Recoil.ViewModelKickPitch", "Stat.Recoil.AdsRecoilMultiplier",
            "Stat.Accuracy.BaseHipSpread", "Stat.Accuracy.BaseAdsSpread",
            "Stat.Accuracy.MovementSpreadMax", "Stat.Accuracy.SprintSpreadExtra",
            "Stat.Accuracy.AirborneSpreadExtra", "Stat.Accuracy.ShotBloomPerShot",
            "Stat.Accuracy.MaxBloom", "Stat.Accuracy.BloomRecoveryDelay",
            "Stat.Accuracy.BloomRecoverySpeed", "Stat.Ballistic.PelletCount",
            "Stat.Ballistic.PelletSpread", "AdsGroundSpeedMultiplier",
            "HitRegions.Head", "HitRegions.Torso", "HitRegions.Arm", "HitRegions.Leg"
        };

        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report) => ValidateCatalog();

        [MenuItem("Tools/Balance/Validate Mainline Weapon Profiles")]
        public static void ValidateCatalog()
        {
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(BalancePath);
            if (balance == null) throw new BuildFailedException("Missing mainline Balance asset");
            var profiles = balance.MainlineProfiles;
            if (profiles == null || profiles.Length != Ids.Length)
                throw new BuildFailedException($"Expected {Ids.Length} mainline profiles; found {profiles?.Length ?? 0}");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                string error = profile == null ? "null profile" : null;
                if (profile == null || !profile.Validate(out error))
                    throw new BuildFailedException($"Invalid profile: {profile?.name ?? "null"} {error}");
                if (!seen.Add(profile.WeaponId))
                    throw new BuildFailedException($"Duplicate profile ID: {profile.WeaponId}");
            }
            foreach (string id in Ids)
                if (!seen.Contains(id)) throw new BuildFailedException($"Missing mainline profile: {id}");
        }

        [MenuItem("Tools/Balance/Export Mainline Tuning CSV")]
        public static void Export()
        {
            ValidateCatalog();
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(BalancePath);
            var rows = new List<string> { "WeaponId," + string.Join(",", Fields) };
            foreach (var id in Ids)
            {
                var profile = balance.MainlineProfiles.First(p => p.WeaponId == id);
                var serialized = new SerializedObject(profile);
                var values = new List<string> { id };
                foreach (var path in Fields)
                {
                    var property = serialized.FindProperty(path);
                    if (property == null) throw new InvalidOperationException($"Missing CSV field: {path}");
                    values.Add(property.propertyType == SerializedPropertyType.Integer
                        ? property.intValue.ToString(CultureInfo.InvariantCulture)
                        : property.floatValue.ToString("R", CultureInfo.InvariantCulture));
                }
                rows.Add(string.Join(",", values));
            }
            File.WriteAllLines(CsvPath, rows, new UTF8Encoding(true));
            AssetDatabase.ImportAsset(CsvPath);
        }

        [MenuItem("Tools/Balance/Import Mainline Tuning CSV")]
        public static void Import()
        {
            ValidateCatalog();
            var lines = File.ReadAllLines(CsvPath, Encoding.UTF8);
            if (lines.Length != Ids.Length + 1) throw new InvalidDataException("CSV must contain exactly 16 data rows");
            var header = SplitCsv(lines[0]);
            var expected = new List<string> { "WeaponId" }; expected.AddRange(Fields);
            if (!header.SequenceEqual(expected)) throw new InvalidDataException("CSV columns do not match the tuning schema");
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(BalancePath);
            var profiles = balance.MainlineProfiles.ToDictionary(p => p.WeaponId, StringComparer.Ordinal);
            var staged = new Dictionary<string, WeaponTuningProfile>(StringComparer.Ordinal);
            try
            {
                for (int row = 1; row < lines.Length; row++)
                {
                    var cells = SplitCsv(lines[row]);
                    if (cells.Count != expected.Count || !profiles.ContainsKey(cells[0]) || staged.ContainsKey(cells[0]))
                        throw new InvalidDataException($"Invalid or duplicate weapon ID at CSV row {row + 1}");
                    var temp = ScriptableObject.CreateInstance<WeaponTuningProfile>();
                    temp.WeaponId = cells[0];
                    staged.Add(cells[0], temp);
                    var serialized = new SerializedObject(temp);
                    for (int i = 0; i < Fields.Length; i++)
                    {
                        var property = serialized.FindProperty(Fields[i]);
                        if (property == null) throw new InvalidDataException($"Unknown field {Fields[i]}");
                        string value = cells[i + 1];
                        if (property.propertyType == SerializedPropertyType.Integer)
                        {
                            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer))
                                throw new InvalidDataException($"Invalid integer {Fields[i]} row {row + 1}");
                            property.intValue = integer;
                        }
                        else
                        {
                            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
                                || float.IsNaN(number) || float.IsInfinity(number))
                                throw new InvalidDataException($"Invalid float {Fields[i]} row {row + 1}");
                            property.floatValue = number;
                        }
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    if (!temp.Validate(out string error)) throw new InvalidDataException($"{cells[0]}: {error}");
                }
                if (staged.Count != Ids.Length) throw new InvalidDataException("Missing weapon rows");
                foreach (var pair in staged)
                {
                    var target = profiles[pair.Key];
                    Undo.RecordObject(target, "Import weapon tuning CSV");
                    var destination = new SerializedObject(target);
                    var source = new SerializedObject(pair.Value);
                    foreach (var path in Fields)
                    {
                        var d = destination.FindProperty(path);
                        var s = source.FindProperty(path);
                        if (d.propertyType == SerializedPropertyType.Integer) d.intValue = s.intValue;
                        else d.floatValue = s.floatValue;
                    }
                    destination.ApplyModifiedProperties();
                    EditorUtility.SetDirty(target);
                }
                AssetDatabase.SaveAssets();
            }
            finally { foreach (var temp in staged.Values) UnityEngine.Object.DestroyImmediate(temp); }
        }

        private static List<string> SplitCsv(string line)
        {
            var cells = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = !quoted;
                else if (c == ',' && !quoted) { cells.Add(cell.ToString()); cell.Clear(); }
                else cell.Append(c);
            }
            if (quoted) throw new InvalidDataException("Unclosed quoted CSV field");
            cells.Add(cell.ToString().TrimStart('\ufeff'));
            return cells;
        }
    }
}
