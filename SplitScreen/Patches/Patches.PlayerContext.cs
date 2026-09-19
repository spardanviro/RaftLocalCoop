using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // ⚠ 这里曾有三个 ComponentManager<T>.Value 的 getter 补丁
    // (Patch_CM_Get_NetworkPlayer / _Player / _PlayerInventory),已删除。
    // 原因:Mono 对引用类型泛型共享同一份 JIT 代码,三个实例化共用一个方法体,
    // 补丁互相串台 -> Cm.Get<Network_Player>() 会返回 PlayerInventory 实例。
    // 现在由 P2OriginalScope(换入/还原)+ Main.MaintainLocalPlayerCM(每帧修回 P1)接管。
    // 【不要再给泛型方法打 Harmony 补丁】。
    // 退出保存崩溃根治:分屏退出 teardown 时本地玩家(Main.player1)可能已被销毁,
    // ComponentManager<Network_Player>.Value 解析为 null → vanilla RGD_NoteBook(NoteBook) ctor
    // 第 102 行 Value.NoteBookUI 触发 NRE → 整个 CreateRGDGame 失败 → 存档写不完 → 损坏无法载入。
    // vanilla 自身 RestoreNoteBook 对 Value 有空判,唯独 ctor(保存路径)漏了;这里把同一空判补上:
    // Value 为 null 时安全初始化(解锁项取全局静态 NoteBook.unlockedNoteBookIndexes、页码默认 0),
    // 跳过 UI 读取,让保存正常完成。Value 有效时(玩家在场)照走 vanilla,读真实页码,行为不变。
    [HarmonyPatch(typeof(RGD_NoteBook))]
    [HarmonyPatch(MethodType.Constructor, new System.Type[] { typeof(NoteBook) })]
    static class Patch_RGD_NoteBook_Ctor_NullSafe
    {
        static bool Prefix(RGD_NoteBook __instance)
        {
            if ((UnityEngine.Object)ComponentManager<Network_Player>.Value != null) return true;
            __instance.unlockedIndexes = NoteBook.unlockedNoteBookIndexes != null
                ? NoteBook.unlockedNoteBookIndexes.ToArray() : new int[0];
            __instance.latestIndex = 0u;
            return false;
        }
    }

    // 退出世界时控制台报 Message_NetworkBehaviour..ctor 的 NRE(栈:WorldEvent_Tracker_AI.OnDestroy
    // -> WorldEvent.RemoveSpawnedEntity -> WorldEventManager.StopEvent -> RPCStopEvent)。
    // 根因在 vanilla 自身的销毁顺序竞态:NetworkIDManager 已随场景销毁,而世界事件里的 AI 这时才
    // OnDestroy -> 事件计数归零 -> 发停止 RPC;Message_NetworkBehaviour 的构造函数不判空就读
    // behaviour.ObjectIndex(Message_NetworkBehaviour.cs:74)-> NRE。与分屏无关(同 RGD_NoteBook ctor 那类),
    // 但退出流程里刷错误很碍事,按同样方式补上 vanilla 漏掉的空判:凑不齐 RPC 参数时直接不发。
    // 此时已在退出/销毁流程中,单机也没有远端要通知,跳过无副作用。
    // 退出世界时 GamepadCursor.OnDisable 会 InputSystem.RemoveDevice -> InputUser.onChange 广播
    // -> CustomInputConfig.OnControlsChangedEvent -> 各 MenuBox.OnControlChanged。
    // 此刻主菜单的 MenuBox 可能正处于销毁/未初始化的中间态,而 vanilla 直接遍历
    // gamepadObjects/keyboardObjects 且不判空 -> NRE 刷屏(同 RGD_NoteBook ctor 那类原版空判缺失)。
    // 数组与元素都健全时照走原版;有空元素才接管,只跳过没了的那些,其余行为逐字一致。
    [HarmonyPatch(typeof(MenuBox), "OnControlChanged")]
    static class Patch_MenuBox_OnControlChanged_NullSafe
    {
        static readonly System.Reflection.FieldInfo GamepadObjectsField =
            AccessTools.Field(typeof(MenuBox), "gamepadObjects");
        static readonly System.Reflection.FieldInfo KeyboardObjectsField =
            AccessTools.Field(typeof(MenuBox), "keyboardObjects");

        static bool AllAlive(GameObject[] a)
        {
            if (a == null) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] == null) return false;
            return true;
        }

        static void ApplySafe(GameObject[] a, bool value)
        {
            if (a == null) return;
            for (int i = 0; i < a.Length; i++) if (a[i] != null) a[i].SetActive(value);
        }

        static bool Prefix(MenuBox __instance, bool gamepad)
        {
            var g = GamepadObjectsField?.GetValue(__instance) as GameObject[];
            var k = KeyboardObjectsField?.GetValue(__instance) as GameObject[];
            if (AllAlive(g) && AllAlive(k)) return true;
            ApplySafe(g, gamepad);
            ApplySafe(k, !gamepad);
            return false;
        }
    }

    [HarmonyPatch(typeof(WorldEventManager), "RPCStopEvent")]
    static class Patch_WorldEventManager_RPCStopEvent_NullSafe
    {
        static bool Prefix(WorldEvent we)
        {
            if (we == null || we.eventData == null) return false;
            var net = ComponentManager<Raft_Network>.Value;
            if (net == null || (UnityEngine.Object)net.NetworkIDManager == null) return false;
            return true;
        }
    }
}
