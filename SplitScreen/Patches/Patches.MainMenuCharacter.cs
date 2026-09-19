using HarmonyLib;

namespace SplitScreen
{
    [HarmonyPatch(typeof(CharacterBox), nameof(CharacterBox.Open))]
    static class Patch_CharacterBox_Open_P2Clone
    {
        static void Postfix(CharacterBox __instance)
        {
            Main.EnsureP2CharacterBox(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterBox), nameof(CharacterBox.Close))]
    static class Patch_CharacterBox_Close_P2Clone
    {
        static void Prefix(CharacterBox __instance)
        {
            if (__instance != null && !__instance.name.StartsWith("P2_"))
                Main.HideP2CharacterBox();
        }
    }
}
