using System;
using System.Reflection;
using HarmonyLib;

namespace SplitScreen
{
    //
    //      static readonly FieldInfo f = VanillaAccessors.Field(typeof(X), "y");
    internal static class VanillaAccessors
    {
        internal static FieldInfo Field(Type t, string name)
        {
            var f = AccessTools.Field(t, name);
            if (f == null)
                Main.ModEntry?.Logger?.Log($"[VanillaAccessors] 必需私有字段未找到: {t.Name}.{name}(原版版本变动?)");
            return f;
        }

        internal static MethodInfo Method(Type t, string name)
        {
            var m = AccessTools.Method(t, name);
            if (m == null)
                Main.ModEntry?.Logger?.Log($"[VanillaAccessors] 必需方法未找到: {t.Name}.{name}(原版版本变动?)");
            return m;
        }

        static readonly FieldInfo s_npIsLocal = Field(typeof(Network_Player), "isLocalPlayer");

        public static bool GetIsLocalPlayer(Network_Player np)
            => np != null && s_npIsLocal != null && (bool)s_npIsLocal.GetValue(np);

        public static void SetIsLocalPlayer(Network_Player np, bool value)
        {
            if (np != null && s_npIsLocal != null) s_npIsLocal.SetValue(np, value);
        }

        public static readonly FieldInfo ThirdPersonLocalCameraRotation =
            Field(typeof(ThirdPerson), "localCameraRotation");
    }
}
