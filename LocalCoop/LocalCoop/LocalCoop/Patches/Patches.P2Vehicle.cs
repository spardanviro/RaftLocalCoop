using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    // 船帆(Sail)两处分屏问题的修复:
    // #1 P1 用不了船帆:Sail.localPlayer 在 Start(=ComponentManager<Network_Player>.Value)时因
    //    分屏生成/加载时序缓存成 null → vanilla OnIsRayed 第118行 `localPlayer == null` 短路提前返回,
    //    P1 瞄船帆既无提示也开不了。→ P1 路径 localPlayer 悬空时兜底改指 player1。
    // #2 P2 设备射线 NRE:RunDeviceRayAsP2 经 P2InteractionContext 把 Sail.localPlayer 覆盖成 P2 克隆,
    //    vanilla 第118行读 `localPlayer.RessurectComponent.IsCarrying`/`.BedComponent.Sleeping`,而 P2 克隆
    //    这些子组件可能为空 → NRE(且该 vanilla 路径的 Interact 判定用 PlayerInput.GetPlayerByIndex(0)=P1
    //    输入,P2 本就无法正常用船帆)→ P2 设备射线期间直接跳过 vanilla,避免崩溃与错误刷屏。
    //    (P2 的船帆开合如需支持,后续可仿 SteeringWheel 做专属补丁。)
    [HarmonyPatch(typeof(Sail), "OnIsRayed")]
    static class Patch_Sail_OnIsRayed_P2Safe
    {
        static readonly FieldInfo LocalPlayerField = AccessTools.Field(typeof(Sail), "localPlayer");

        static bool Prefix(Sail __instance)
        {
            if (Main.isProcessingP2Ray) return false;   // #2:P2 设备射线跳过 vanilla,防 P2 克隆空子组件 NRE
            if (__instance == null || LocalPlayerField == null) return true;
            var lp = LocalPlayerField.GetValue(__instance) as Network_Player;
            if (lp == null && Main.player1 != null)      // #1:P1 路径 localPlayer 悬空 → 兜底 P1
                LocalPlayerField.SetValue(__instance, Main.player1);
            return true;
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    [HarmonyPatch(typeof(Paddle), "OnPaddle")]
    static class Patch_Paddle_OnPaddle_P2
    {
        sealed class State { public P2OriginalScope Scope; public Network_Player Np; public Quaternion SavedRot; public bool RotOverridden; }

        static void Prefix(Paddle __instance, ref State __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return;

            __state = new State { Scope = P2OriginalScope.Tool(), Np = np };

            // 原版 PaddlePaddle 用 np.transform.forward(根朝向)作施力/水花方向；真实本地玩家的身体根会跟随相机 yaw，
            //  但 P2 的根只停在 rootYaw(不随视角转)——视角 yaw 在 playerPivot(FP)/环绕镜头(TP)上，故根前向永远是错的固定方向。
            //  统一修复：临时把根 yaw 对齐到【P2 相机水平朝向】(FP/TP 都是 P2 实际看的方向)，让原版按"P2 看的方向"施力；
            //  OnPaddle 同步返回后立即还原(动画事件期、渲染前还原，模型不会抖)。
            var cam = np.Camera;
            if (cam != null)
            {
                __state.SavedRot = np.transform.rotation;
                __state.RotOverridden = true;
                np.transform.rotation = Quaternion.Euler(0f, cam.transform.eulerAngles.y, 0f);
            }
        }

        static void Postfix(State __state) => Cleanup(__state);
        static Exception Finalizer(Exception __exception, State __state) { Cleanup(__state); return __exception; }

        static void Cleanup(State st)
        {
            if (st == null) return;
            if (st.RotOverridden && st.Np != null) st.Np.transform.rotation = st.SavedRot;
            st.Scope?.Dispose();
        }
    }

    
    
    
    
    
    
    
    
    
    
    [HarmonyPatch(typeof(SteeringWheel), "OnIsRayed")]
    static class Patch_SteeringWheel_OnIsRayed_P2
    {
        static MethodInfo _rotate;
        const float SteerDegPerFrame = 2f;

        static bool Prefix(SteeringWheel __instance)
        {
            if (!Main.isProcessingP2Ray) return true;   // P1 → 原版

            var gp = Main.GetP2BoundGamepad();
            if (gp != null)
            {
                float steer = gp.dpad.left.isPressed ? -1f : (gp.dpad.right.isPressed ? 1f : 0f);
                if (steer != 0f)
                {
                    if (_rotate == null)
                        _rotate = typeof(SteeringWheel).GetMethod("Rotate", BindingFlags.Instance | BindingFlags.NonPublic);
                    try { _rotate?.Invoke(__instance, new object[] { steer * SteerDegPerFrame }); }
                    catch (System.Exception e) { Main.ModEntry.Logger.Log("[P2Steer] Rotate 异常: " + e.Message); }
                }
            }
            return false;   
        }
    }
}
