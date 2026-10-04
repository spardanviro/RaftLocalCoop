using System;
using System.Globalization;
using System.IO;

namespace SplitScreen
{
    public static class P2CharacterSettings
    {
        const string FileName = "SplitScreen_P2_Character.dat";

        static string PathForSettings()
        {
            string dir = SaveAndLoad.PlayerPath;
            if (string.IsNullOrEmpty(dir))
                dir = SaveAndLoad.GetSettingsPath();
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, FileName);
        }

        public static RGD_Settings_Character LoadOrDefault()
        {
            var fallback = ComponentManager<Settings>.Value?.Current?.character;
            var result = new RGD_Settings_Character
            {
                Name = "P2_GAMEPAD",
                ModelIndex = fallback != null ? fallback.ModelIndex : 0,
                OutfitIndex = fallback != null ? fallback.OutfitIndex : 0,
                Platform = 0
            };
            // 诊断日志：每次 InitializeComponents 之后都报告关键组件状态
            try
            {
                string path = PathForSettings();
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return result;
                foreach (var raw in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = raw.Substring(0, eq);
                    string value = raw.Substring(eq + 1);
                    if (key == "Name") result.Name = value;
                    else if (key == "ModelIndex") result.ModelIndex = int.Parse(value, CultureInfo.InvariantCulture);
                    else if (key == "OutfitIndex") result.OutfitIndex = int.Parse(value, CultureInfo.InvariantCulture);
                    else if (key == "Platform") result.Platform = int.Parse(value, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception e)
            {
                Main.ModEntry?.Logger?.Log("[P2Character] load failed: " + e.Message);
            }

            return result;
        }

        public static void Save(RGD_Settings_Character character)
        {
            if (character == null) return;
            try
            {
                string path = PathForSettings();
                if (string.IsNullOrEmpty(path)) return;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                File.WriteAllLines(path, new[]
                {
                    "Name=" + (character.Name ?? "P2_GAMEPAD"),
                    "ModelIndex=" + character.ModelIndex.ToString(CultureInfo.InvariantCulture),
                    "OutfitIndex=" + character.OutfitIndex.ToString(CultureInfo.InvariantCulture),
                    "Platform=" + character.Platform.ToString(CultureInfo.InvariantCulture)
                });
            }
            catch (Exception e)
            {
                Main.ModEntry?.Logger?.Log("[P2Character] save failed: " + e.Message);
            }
        }
    }
}
