using UnityEngine;
using System.Reflection;

namespace SplitScreen
{
    public static partial class Main
    {
        // 唯一的版本号来源。modinfo.json(RML)与 Info.json(UMM)里的版本要和它保持一致。
        internal const string ModVersion = "1.0.0";

        static readonly FieldInfo NetworkPlayerCmBackingField =
            typeof(ComponentManager<Network_Player>).GetField("component", BindingFlags.Static | BindingFlags.NonPublic);
        // Player / PlayerInventory 的 CM 背后字段。原先靠给 ComponentManager<T>.Value 的 getter
        // 打 Harmony 补丁来路由,但 Mono 对引用类型泛型【共享同一份 JIT 代码】->
        // 三个实例化(Network_Player / Player / PlayerInventory)共用一个方法体,
        // 补丁互相串台:Cm.Get<Network_Player>() 实测返回过 PlayerInventory 实例
        // (realType=PlayerInventory),vanilla 按错误偏移读字段 -> NRE,乃至
        // Transform.position 读到垃圾指针直接原生闪退。改为【直接读写背后字段】,不再打补丁。
        static readonly FieldInfo PlayerCmBackingField =
            typeof(ComponentManager<Player>).GetField("component", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly FieldInfo InventoryCmBackingField =
            typeof(ComponentManager<PlayerInventory>).GetField("component", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly FieldInfo SaveAndLoadLocalPlayerField =
            typeof(SaveAndLoad).GetField("localPlayer", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly FieldInfo PlayerSeatLocalPlayerField =
            typeof(PlayerSeat).GetField("localPlayer", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly FieldInfo InteractableButtonLocalPlayerField =
            typeof(InteractableButton).GetField("localPlayer", BindingFlags.Static | BindingFlags.NonPublic);

        // 逐个类打补丁(等价于 PatchAll 的内部做法,只是每个类单独兜异常)。
        // 游戏更新后某个目标方法消失时,只跳过那一个补丁类并记日志,
        // 而不是让 PatchAll 半途抛出、留下"只打了一半"的状态。
        internal static void PatchAllIsolated()
        {
            int failed = 0;
            foreach (var type in HarmonyLib.AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
            {
                try { Harmony.CreateClassProcessor(type).Patch(); }
                catch (System.Exception e)
                {
                    failed++;
                    ModEntry.Logger.Log("[Harmony] 补丁类 " + type.FullName + " 应用失败,已跳过: "
                        + (e.InnerException != null ? e.InnerException.Message : e.Message));
                }
            }
            if (failed > 0)
                ModEntry.Logger.Log("[Harmony] 共有 " + failed + " 个补丁类未生效(多半是游戏更新改了目标方法),相关功能可能异常");
        }

        // 帧首看门狗。P2 作用域只在一次调用内有效,本 mod 的 Update 开头不该还留着;
        // 留着说明有 scope 没释放(构造中途或原版中途抛异常)。此时 MaintainLocalPlayerCM 会因
        // Active!=null 早退,没人修,P1 会一直以 P2 身份跑到退出世界。这里强制复位并记日志。
        internal static void RecoverLeakedP2ScopeIfAny()
        {
            if (PlayerContext.Active == null && P2Mode == P2OriginalMode.None) return;
            ModEntry.Logger.Log("[Watchdog] 发现未释放的 P2 作用域 (mode=" + P2Mode + ") -> 强制复位");
            PlayerContext.Clear();
            P2Mode = P2OriginalMode.None;
            isProcessingP2Ray = false;
            try
            {
                var p2 = player2;
                if ((Object)p2 != null && VanillaAccessors.GetIsLocalPlayer(p2))
                    VanillaAccessors.SetIsLocalPlayer(p2, false);
            }
            catch (System.Exception e) { ModEntry.Logger.Log("[Watchdog] 复位 P2 isLocalPlayer 失败: " + e.Message); }
            try { P2InventoryStore.AbandonSwap(); }
            catch (System.Exception e) { ModEntry.Logger.Log("[Watchdog] AbandonSwap 失败: " + e.Message); }
            try { MaintainLocalPlayerCM(); }
            catch (System.Exception e) { ModEntry.Logger.Log("[Watchdog] 修回 CM 失败: " + e.Message); }
        }

        internal static void MaintainLocalPlayerCM()
        {
            if (PlayerContext.Active != null) return;
            if ((Object)player1 == null) return;

            MaintainSaveAndLoadLocalPlayer();
            MaintainInteractableButtonLocalPlayer();

            MaintainPlayerCm();
            MaintainInventoryCm();

            var current = NetworkPlayerCmBackingField?.GetValue(null) as Network_Player;
            if ((Object)current == (Object)player1) return;

            // 非P2上下文下 CM<Network_Player>.Value 唯一合法值=player1;current!=player1(含伪装成本地玩家的克隆)一律修回。

            if (NetworkPlayerCmBackingField != null)
                NetworkPlayerCmBackingField.SetValue(null, player1);
            else
                ComponentManager<Network_Player>.Value = player1;

            LogV("[P1Scope] repaired ComponentManager<Network_Player>.Value backing field");
        }

        // 非 P2 上下文下 CM<Player>/CM<PlayerInventory> 的唯一合法值 = P1 的对应组件。
        // 克隆的 P2 UI 背包会覆盖这个单例(见 SanitizeClone 一节),故每帧修回。
        static void MaintainPlayerCm()
        {
            if (PlayerCmBackingField == null || (Object)player1 == null) return;
            var want = player1.PlayerScript;
            if ((Object)want == null) return;
            if ((Object)(PlayerCmBackingField.GetValue(null) as Player) == (Object)want) return;
            PlayerCmBackingField.SetValue(null, want);
            LogV("[P1Scope] repaired ComponentManager<Player>.Value backing field");
        }

        static void MaintainInventoryCm()
        {
            if (InventoryCmBackingField == null || (Object)player1 == null) return;
            var want = player1.Inventory;
            if ((Object)want == null) return;
            if ((Object)(InventoryCmBackingField.GetValue(null) as PlayerInventory) == (Object)want) return;
            InventoryCmBackingField.SetValue(null, want);
            LogV("[P1Scope] repaired ComponentManager<PlayerInventory>.Value backing field");
        }

        // P2 作用域进出时直接换 CM 三件套(替代原来的 getter 补丁)。返回换入前的旧值供还原。
        internal static void SwapCmToPlayer(Network_Player p,
            out Network_Player prevNp, out Player prevPlayer, out PlayerInventory prevInv)
        {
            prevNp     = NetworkPlayerCmBackingField?.GetValue(null) as Network_Player;
            prevPlayer = PlayerCmBackingField?.GetValue(null) as Player;
            prevInv    = InventoryCmBackingField?.GetValue(null) as PlayerInventory;
            if ((Object)p == null) return;
            try { NetworkPlayerCmBackingField?.SetValue(null, p); } catch { }
            try { if ((Object)p.PlayerScript != null) PlayerCmBackingField?.SetValue(null, p.PlayerScript); } catch { }
            try { if ((Object)p.Inventory != null) InventoryCmBackingField?.SetValue(null, p.Inventory); } catch { }
        }

        internal static void RestoreCm(Network_Player prevNp, Player prevPlayer, PlayerInventory prevInv)
        {
            try { NetworkPlayerCmBackingField?.SetValue(null, prevNp); } catch { }
            try { PlayerCmBackingField?.SetValue(null, prevPlayer); } catch { }
            try { InventoryCmBackingField?.SetValue(null, prevInv); } catch { }
        }

        static void MaintainSaveAndLoadLocalPlayer()
        {
            if (SaveAndLoadLocalPlayerField == null) return;

            var current = SaveAndLoadLocalPlayerField.GetValue(null) as Network_Player;
            if ((Object)current == (Object)player1) return;

            // current!=player1 一律修回(同 MaintainLocalPlayerCM)。

            SaveAndLoadLocalPlayerField.SetValue(null, player1);
            LogV("[P1Scope] repaired SaveAndLoad.localPlayer");
        }

        static void MaintainInteractableButtonLocalPlayer()
        {
            if (InteractableButtonLocalPlayerField == null) return;
            var current = InteractableButtonLocalPlayerField.GetValue(null) as Network_Player;
            if ((Object)current == (Object)player1) return;
            // current!=player1 一律修回。
            InteractableButtonLocalPlayerField.SetValue(null, player1);
            LogV("[P1Scope] repaired InteractableButton.localPlayer");
        }
        // 离开世界时把 mod 在分屏期写过的 vanilla 本地玩家单例还原:
        // P1 仍在世界 → 真 P1(修回);已离世界 → null(让 vanilla 下局 Start/RestoreUser 重新建立真身)。
        // 消除分屏结束后残留分屏期玩家引用污染下一局单人(椅子静态 localPlayer / SaveAndLoad / CM 读到失效玩家 → NRE)。
        internal static void RestoreVanillaLocalPlayerSingletons()
        {
            Network_Player genuine = null;
            try { genuine = PlayerContext.ResolveGenuineLocalPlayer(); } catch { }

            try { NetworkPlayerCmBackingField?.SetValue(null, genuine); } catch { }
            try { SaveAndLoadLocalPlayerField?.SetValue(null, genuine); } catch { }
            try { PlayerSeatLocalPlayerField?.SetValue(null, genuine); } catch { }
            try { InteractableButtonLocalPlayerField?.SetValue(null, genuine); } catch { }

            ModEntry.Logger.Log("[Reset] restore vanilla local-player singletons -> "
                + ((UnityEngine.Object)genuine != null ? genuine.name : "null(left-world,vanilla rebuilds next load)"));
        }

        static string PathOf(Transform t)
        {
            if (t == null) return "null";
            string s = t.name;
            for (var p = t.parent; p != null; p = p.parent)
                s = p.name + "/" + s;
            return s;
        }
    }

    // Transpiler 自检。所有 Transpiler 都是"找到目标指令就替换,找不到就原样放行"的写法,
    // 游戏更新改了目标方法后会静默退化(P2 的输入悄悄读回 P1 的)。这里包一层:
    // 对比改写前后的指令流,一条都没改就记日志。
    // 局限:只能发现"完全没命中";一个 Transpiler 有多处替换、只命中一部分时发现不了。
    internal static class TranspilerGuard
    {
        internal static System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction> Verify(
            System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction> original,
            System.Func<System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction>,
                        System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction>> body)
        {
            var src = new System.Collections.Generic.List<HarmonyLib.CodeInstruction>(original);
            // 先拍快照:有的 Transpiler 直接改原指令对象的 opcode/operand。
            var opcodes = new System.Reflection.Emit.OpCode[src.Count];
            var operands = new object[src.Count];
            for (int i = 0; i < src.Count; i++) { opcodes[i] = src[i].opcode; operands[i] = src[i].operand; }

            var result = new System.Collections.Generic.List<HarmonyLib.CodeInstruction>(body(src));

            bool changed = result.Count != opcodes.Length;
            for (int i = 0; !changed && i < result.Count; i++)
                if (result[i].opcode != opcodes[i] || !Equals(result[i].operand, operands[i])) changed = true;

            if (!changed)
            {
                var owner = body.Method.DeclaringType != null ? body.Method.DeclaringType.Name : "?";
                Main.ModEntry.Logger.Log("[Harmony] Transpiler " + owner + " 没有改动任何指令:目标调用/字段已不在原方法里"
                    + "(多半是游戏更新),对应的 P2 路由已失效");
            }
            return result;
        }
    }
}
