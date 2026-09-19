using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SplitScreen
{
    public static partial class Main
    {
        static readonly FieldInfo s_npIsLocalPlayerField =
            typeof(Network_Player).GetField("isLocalPlayer", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void SanitizeClonedUiRoot(GameObject root)
        {
            if (root == null) return;

            foreach (var module in root.GetComponentsInChildren<BaseInputModule>(true))
                if (module != null) Object.Destroy(module);

            foreach (var eventSystem in root.GetComponentsInChildren<EventSystem>(true))
                if (eventSystem != null) Object.Destroy(eventSystem);

            // 源头铲污染:克隆 PlayerInventory 的 GO 会连带克隆一个 isLocalPlayer=true 的 Network_Player。
            // 它不经 vanilla InitializeComponents 注册,但 mod 的 ResolveGenuineLocalPlayer(FindObjectsOfType
            // 找 IsLocalPlayer && Stats)会把它当成本地玩家,写进 ComponentManager<Network_Player>.Value;
            // 该克隆随背包开关被停用 → 收音机等缓存到它 → 在 inactive 对象启协程报错。
            // 这里把克隆体上所有 Network_Player 的 isLocalPlayer 置 false —— ResolveGenuine 与 vanilla 注册
            // 都门控 IsLocalPlayer,置 false 后它再也不会被当作本地玩家单例,污染从源头消失。
            foreach (var np in root.GetComponentsInChildren<Network_Player>(true))
            {
                if (np == null) continue;
                if (s_npIsLocalPlayerField != null) s_npIsLocalPlayerField.SetValue(np, false);
            }
        }
    }
}
