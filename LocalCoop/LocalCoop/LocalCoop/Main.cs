using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Raft 分屏 Mod  v0.21
    //
    //  核心思路：P2 以"假在线玩家"身份初始化，但由本地手柄控制。
    //
    //  架构分层：
    //    SplitScreenRuntime  — 生命周期 + 子路由器持有
    //    PlayerSlot          — 单玩家上下文（input / UI / 交互状态）
    //    InputRouter         — P2 手柄 InputAction 生命周期
    //    UiRouter            — P2 菜单 / 存储 / 覆层 UI
    //    InteractionRouter   — P2 Pickup 射线 + IRaycastable
    //    Main.Rendering.cs   — Layer / Camera 渲染（后续单独重构）
    //    Patches/*.cs        — 薄转发层，调用 Runtime 服务
    // ═══════════════════════════════════════════════════════════════════════
    public static partial class Main
    {
        // ── 钩子激活标志（防 Load → OnToggle 双重注册） ──────────────────
        static bool _hooksActive     = false;
        const string RuntimeGuardKey = "RaftMod.SplitScreen.ActiveAssembly";
        // 上一次加载所用的 Harmony id。存在 AppDomain 数据里 = 跨程序集可见
        // (Mono 不能卸载程序集,重载后读不到旧程序集的静态,只能走这个通道)。
        const string HarmonyIdKey = "RaftMod.SplitScreen.HarmonyId";
        // 每次加载生成【唯一】id。用固定 id 会被 HarmonyX 当成"该 owner 的补丁已存在"而跳过,
        // 结果重载后跑的仍是上一次加载的旧补丁(实测:重载后新加的诊断日志一行都不打,
        // 而崩溃栈里 _Patch1 还挂着 = 旧 wrapper 仍在)。那会让整个 mod 带着已被置空的
        // SplitScreenRuntime 继续跑,进场景闪退只是其中一个症状。
        static readonly string HarmonyId = "RaftMod.SplitScreen." + System.Guid.NewGuid().ToString("N");
        // 手改这个字符串就能一眼确认游戏里跑的是不是最新源码(排查 stale 用)。
        const string CodeStamp = "STAMP-B";
        static readonly string RuntimeGuardOwner = Assembly.GetExecutingAssembly().GetName().Name;

        
        internal static bool Boot()
        {
            // 先把上一次加载遗留的补丁用【它自己的 id】彻底摘掉,再用新 id 打。
            // 少了这一步,旧 wrapper 会一直挂在方法上抢先执行。
            try
            {
                var prevId = System.AppDomain.CurrentDomain.GetData(HarmonyIdKey) as string;
                if (!string.IsNullOrEmpty(prevId)) new Harmony(prevId).UnpatchAll(prevId);
                new Harmony(
ModEntry
.Info.Id).UnpatchAll(
ModEntry
.Info.Id);   // 兼容旧版固定 id 的残留
            }
            catch (System.Exception e) { 
ModEntry
.Logger.Log("[Harmony] 清理上次残留补丁失败: " + e.Message); }
            System.AppDomain.CurrentDomain.SetData(HarmonyIdKey, HarmonyId);
            // [LOADDIAG] 常驻:判定本次加载有没有产生新程序集。
            // 依据必须【不依赖任何跨重载的静态】——上一轮我用静态计数器做判据,
            // 而若重载复用同一程序集,静态根本不会重置,那个测量从设计上就是错的。
            // 这里打的是程序集名(RML 每次编译生成随机名)+ 本次的 HarmonyId,
            // 两次加载若名字相同 = 复用旧程序集 = 改的代码压根没进游戏。
            {
                var asm = Assembly.GetExecutingAssembly();
                var prevAsm = System.AppDomain.CurrentDomain.GetData("RaftMod.SplitScreen.PrevAsm") as string;
                var name = asm.GetName().Name;
                
ModEntry
.Logger.Log("[LOADDIAG] asm=" + name + " prevAsm=" + (prevAsm ?? "(none)")
                    + " same=" + (name == prevAsm) + " harmonyId=" + HarmonyId.Substring(HarmonyId.Length - 8)
                    + " codeStamp=" + CodeStamp);
                System.AppDomain.CurrentDomain.SetData("RaftMod.SplitScreen.PrevAsm", name);
            }
            Harmony  = new Harmony(HarmonyId);
            
            
            ResetUnityStatics();
            EnableHooks();
            if (!AllocateLayers())
                ModEntry.Logger.Log("[Layer] 警告：F9 生成 P2 将被阻止，直到满足层分配条件。");
            // 构建戳：日志里一眼确认游戏加载的是哪次构建的 DLL(根治"以为修了其实跑旧 DLL"的困惑)。
            //  取已加载程序集文件的写入时间——UMM 影子拷贝后即本次部署/启动时间。
            string buildStamp;
            try { buildStamp = System.IO.File.GetLastWriteTime(System.Reflection.Assembly.GetExecutingAssembly().Location).ToString("MM-dd HH:mm:ss"); }
            catch { buildStamp = "?"; }
            ModEntry.Logger.Log($"SplitScreen v{ModVersion} [DLL构建戳 {buildStamp}] — F9 spawn P2, X=P2复活, Y=P2菜单");
            return _hooksActive;
        }

        // 重载时把本程序集里【持有 Unity 对象引用的静态】全部清空。
        //
        // 实测(见 [LOADDIAG]):源码没变时 RML 会复用已在内存里的同一程序集(加载秒开,
        // asm 名两次相同)-> 静态字段【不会重置】-> FP rig 克隆、骨骼列表、座位缓存、
        // 玩家引用等全部带着【上一个世界的已销毁对象】活到下一个世界。
        // 这正是"没进过世界重载没事、进过一次再重载就闪退"的原因:没进过世界时这些静态还是空的。
        // (SplitScreenRuntime.Instance 有 Destroy/Create 管着,但散落在各模块的静态没人管。)
        //
        // 用反射一次覆盖全部 100+ 个,而不是逐个写重置函数——后者既容易漏,
        // 也会让以后新增的静态重新掉进同一个坑。源码变化时会编译出新程序集,
        // 那种情况下静态本就是干净的,这里等于空跑。
        static void ResetUnityStatics()
        {
            int cleared = 0;
            try
            {
                var objType = typeof(UnityEngine.Object);
                foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
                {
                    FieldInfo[] fields;
                    try { fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                    catch { continue; }
                    foreach (var f in fields)
                    {
                        try
                        {
                            if (f.IsLiteral) continue;
                            if (objType.IsAssignableFrom(f.FieldType))
                            {
                                if (f.IsInitOnly) continue;
                                if (f.GetValue(null) == null) continue;
                                f.SetValue(null, null);
                                cleared++;
                                continue;
                            }
                            // 集合:只要元素/键类型是 Unity 对象就清空(readonly 也能清,因为只是调 Clear)
                            if (!f.FieldType.IsGenericType) continue;
                            bool holdsUnityObj = false;
                            foreach (var arg in f.FieldType.GetGenericArguments())
                                if (objType.IsAssignableFrom(arg)) { holdsUnityObj = true; break; }
                            if (!holdsUnityObj) continue;
                            var val = f.GetValue(null);
                            if (val == null) continue;
                            var clear = f.FieldType.GetMethod("Clear", System.Type.EmptyTypes);
                            if (clear != null) { clear.Invoke(val, null); cleared++; }
                        }
                        catch { }
                    }
                }
            }
            catch (System.Exception e) { 
ModEntry
.Logger.Log("[Reset] ResetUnityStatics 异常: " + e.Message); }
            if (cleared > 0) 
ModEntry
.Logger.Log("[Reset] 重载清理:" + cleared + " 个持有 Unity 对象的静态已清空");
        }

        static bool OnToggle(bool active)
        {
            if (active) EnableHooks();
            else        DisableHooks();
            return true;
        }

        // ── 钩子启停（幂等） ─────────────────────────────────────────────
        static void EnableHooks()
        {
            if (_hooksActive) return;
            var activeOwner = System.AppDomain.CurrentDomain.GetData(RuntimeGuardKey) as string;
            if (!string.IsNullOrEmpty(activeOwner) && activeOwner != RuntimeGuardOwner)
            {
                ModEntry.Logger.Log($"[RuntimeGuard] {RuntimeGuardOwner} disabled because {activeOwner} is already active");
                return;
            }
            System.AppDomain.CurrentDomain.SetData(RuntimeGuardKey, RuntimeGuardOwner);
            _hooksActive = true;
            SplitScreenRuntime.Create();
            
            
            PatchAllIsolated();
            Camera.onPreCull   += OnCameraPreCull;
            Camera.onPostRender += OnCameraPostRender;
        }

        internal static void Shutdown() { DisableHooks(); }

        internal static void DisableHooks()
        {
            if (!_hooksActive) return;
            _hooksActive     = false;
            Harmony.UnpatchAll(HarmonyId);
            Camera.onPreCull   -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            if ((System.AppDomain.CurrentDomain.GetData(RuntimeGuardKey) as string) == RuntimeGuardOwner)
                System.AppDomain.CurrentDomain.SetData(RuntimeGuardKey, null);
            ResetSplitPostFxProfiles();
            RestoreVanillaLocalPlayerSingletons();   
            SplitScreenRuntime.Destroy(); 
        }

        
        
        
        
        
        // ⚠ 曾经在这里给 ComponentManager<T>.Value 的 getter 打 Harmony 补丁做 P2 路由。
        // 那是错的:Mono 对引用类型泛型【共享同一份 JIT 代码】,
        // ComponentManager<Network_Player> / <Player> / <PlayerInventory> 三个实例化共用一个方法体,
        // 三个补丁互相串台 —— 谁先 return false 谁说了算。实测 Cm.Get<Network_Player>()
        // 返回过 PlayerInventory 实例(realType=PlayerInventory),vanilla 按错误偏移读字段:
        //   RGD_Player.RestorePlayer -> Stats/Inventory 全空 -> NRE,世界进不去;
        //   BlockCollisionConsolidator -> CameraTransform 读到垃圾指针 -> Transform.position
        //   直通原生、不做假 null 检查 -> 直接原生闪退。一个根因,两种症状。
        // 补丁内部无法知道当前是哪个实例化在跑(ref __result 指向同一个返回槽),加判断也治不了。
        // 现在改为直接读写 CM 的私有静态背后字段:
        //   进出 P2 作用域 -> P2OriginalScope 换入/还原(SwapCmToPlayer / RestoreCm);
        //   非 P2 上下文 -> MaintainLocalPlayerCM 每帧修回 P1(本就已有,现扩展到三件套)。
        // 【不要再给泛型方法打补丁】。
        internal static void PatchCMGetters() { }

        
        static bool wasF9;
        internal static bool PendingLocalCoop;
        internal static SplitMode PendingSplitMode = SplitMode.SideBySide;
        internal static SplitMode ActiveSplitMode = SplitMode.SideBySide;
        static int _localCoopArmFrames = -1;
        internal static bool WorldReadyForSplit;
        internal static bool _splitActive;             // F9 成功生成 P2 后置 true；用于检测"离开世界"
        static bool _pendingP1GamepadRepair;           // 离开世界后待把手柄归还 P1（延迟到 P1 PlayerInput 就绪）

        internal static void OnUpdate(float dt)
        {
            if (!_hooksActive) return;
            RecoverLeakedP2ScopeIfAny();   // 帧首:不该有残留的 P2 作用域
            if (Keyboard.current == null) return;

            // 离开世界检测：分屏激活过，但 P2 已被销毁(退到主菜单/重载世界) → 重置全部分屏状态。
            //  否则：手柄仍被旧 P2 输入占用(P1 无法控制)、_p1UiMoved 残留(F9 不再搬 P1 UI)、
            //  DontDestroyOnLoad 的 P2 HUD 残留(只剩提示图)。
            if (_splitActive && player2 == null) ResetForWorldLeave();

            // 延迟修复：分屏结束后把手柄归还 P1（重新配对 + 把控制方案切回 "Gamepad"）。
            //  必须等"世界真正载入"再做：主菜单也有一个临时 PlayerInput[0]，在那上面切方案没用，
            //  随后世界重载会换成游戏内的 PlayerInput。用"本地玩家已存在"作为世界就绪信号；
            //  修复成功(找到 P1 input)后清除标志，之后用户可自由切回键鼠（auto-switch 已开）。
            if (_pendingP1GamepadRepair && !_splitActive)
            {
                // 必须真正在游戏世界(MainScene)才修复：否则在主菜单切到 "Gamepad" 方案会让
                //  CustomInputConfig.Gamepad=true → Cursor.visible=false → 主菜单鼠标消失、无法操作。
                //  额外 !IsLeavingGame：退出过程中(场景仍是 MainScene 的瞬间)不修复，否则会在退到主菜单时
                //  把手柄重新配回 P1 → 主菜单自动切到 Gamepad 方案 → 鼠标消失。
                bool inWorld = !GameManager.IsLeavingGame
                               && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "MainScene";
                var rn = inWorld ? Cm.Get<Raft_Network>() : null;   // Raft_Network getter 未被 patch，安全；Cm 加 destroyed 双检
                if (inWorld && rn != null && rn.GetLocalPlayer() != null &&
                    SplitScreenRuntime.Instance != null &&
                    SplitScreenRuntime.Instance.Input.RepairGamepadToP1())
                    _pendingP1GamepadRepair = false;
            }

            // 主菜单兜底拆除：回到主菜单后只要仍残留 P2 HUD（任何退出路径），立即彻底销毁。
            //  成因：_p2HudCanvas/相机是 DontDestroyOnLoad，原先仅靠 ResetForWorldLeave(_splitActive && player2==null)
            //  拆除；但"P1 ESC 退主菜单"这条路径里 player2 置空有时机延迟/不触发，导致 P2 的 UI 相机继续把【已打开的】
            //  背包面板渲染在主菜单正中之上 → 鼠标其实可见(诊断证实 Cursor.visible 始终为 true)，但菜单按钮被盖住 →
            //  "看似无法操作"。背包【关闭】时只剩左下角状态条，不挡菜单 → 正常。用游戏自带 Raft_Network.InMenuScene
            //  作为可靠信号，每帧检查直到清干净（DestroyP2StatHud 会置空 _p2HudCanvas，下一帧不再进入）。
            if (Raft_Network.InMenuScene)
            {
                WorldReadyForSplit = false;
                _localCoopArmFrames = -1;
                if (_p2HudCanvas != null)
                {
                    ModEntry.Logger.Log("[Reset] 主菜单仍残留 P2 HUD → 强制拆除（防背包面板盖住主菜单）");
                    DestroyP2StatHud();
                    if (_splitActive) ResetForWorldLeave();
                }
                // 兜底：主菜单上若 Helper.CursorVisible 被误置 false（鼠标消失），恢复一次。
                //  正常主菜单 CursorVisible 恒为 true；置 false 只会来自异常路径 → 安全恢复。
                if (!Helper.CursorVisible)
                {
                    Helper.SetCursorVisibleAndLockState(true, CursorLockMode.None);
                    ModEntry.Logger.Log("[Reset] 主菜单 CursorVisible=false → 已恢复鼠标");
                }
            }

            bool f9 = Keyboard.current.f9Key.isPressed;
            if (f9 && !wasF9)
            {
                string block = GetSplitSpawnBlockReason();
                if (block != null) ModEntry.Logger.Log("[SpawnGate] F9 ignored: " + block);
                else TrySpawnP2();
            }
            wasF9 = f9;

            if (PendingLocalCoop && !_splitActive && player2 == null)
            {
                bool inCoopWorld = WorldReadyForSplit
                                   && !GameManager.IsLeavingGame
                                   && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "MainScene";
                var coopNet = inCoopWorld ? Cm.Get<Raft_Network>() : null;
                bool coopReady = inCoopWorld && coopNet != null && coopNet.GetLocalPlayer() != null && LAYER_P1_HAND >= 0;
                if (coopReady)
                {
                    if (_localCoopArmFrames < 0) _localCoopArmFrames = 60;
                    else if (_localCoopArmFrames > 0) _localCoopArmFrames--;
                    else
                    {
                        TrySpawnP2();
                        if (player2 != null) { PendingLocalCoop = false; _localCoopArmFrames = -1; }
                        else _localCoopArmFrames = 30;
                    }
                }
                else _localCoopArmFrames = -1;
            }

            TickRaiseSecondDisplayWindow();
            SplitScreenRuntime.Instance?.Tick(dt);
        }

        // ── 离开世界 / 重载时重置分屏（保留 Harmony 补丁，仅清会话状态） ──────
        internal static void ResetForWorldLeave()
        {
            WorldReadyForSplit = false;
            _localCoopArmFrames = -1;
            P2Mode = P2OriginalMode.None;
            _splitActive = false;
            SplitFramePacing.Restore();
            ModEntry.Logger.Log("[Reset] 检测到离开世界(P2 已销毁) → 重置分屏会话状态");
            try
            {
                if (player2 != null)
                    P2SaveStore.SaveP2(player2);
            }
            catch (System.Exception e)
            {
                ModEntry.Logger.Log("[Reset] SaveP2 before reset failed: " + e.Message);
            }
            HideSecondDisplayWindow();   // 双显示器:直接把第二块屏的窗口收掉,回到点亮之前的样子
            DestroyP2StatHud();            // 销毁 DontDestroyOnLoad 的 P2 HUD/相机（否则残留只剩提示图）
            ResetSplitPostFxProfiles();
            ResetP1Ui();                   // 复位 _p1UiMoved → 下次 F9 重新把 P1 UI 搬到左半屏
            ResetP2Hotbar();               // 复位 P2 手持工具状态
            ResetP2Build();                // 复位 P2 建造缓存(BlockCreator 引用跨世界失效)
            P2ToolRunner.Reset();          // 复位 P2 工具运行器缓存(UseItemController 跨世界失效)
            ResetP2Binoc();                
            ClearP2Carry();                // 复位 P2 搬运动物状态(跨世界清引用)
            P2ZiplineDriver.Reset();       // 复位 P2 滑索驱动器缓存(ZiplinePlayer 引用跨世界失效)
            ResetP2Menu();                 // 关 P2 设备菜单并还原面板
            ForceResetP2BuildMenu();       // 关 P2 建造菜单并还原面板/blockCreator
            // 生命周期不变量核验:正常离开世界路径下 SaveAndLoad.SaveUser(→P2SaveStore.SaveP2 落盘)在
            //  场景卸载前先跑 → P2 销毁 → 本方法清内存 _p2Slots,即"先落盘、后清内存"。这里核验该不变量:
            //  若本会话未检测到落盘就要清内存,则告警(暴露任何绕过 SaveUser 的离开路径,避免静默丢数据)。
            if (P2SaveStore.LastSaveFrame < 0)
                ModEntry.Logger.Log("[Reset] 警告:本次离开世界前未检测到 P2 存档落盘,即将清空内存 P2 背包"
                                  + "(若此路径未经 SaveUser,自上次存档后的 P2 改动会丢失)");
            else
                ModEntry.Logger.Log($"[Reset] P2 存档已于第 {P2SaveStore.LastSaveFrame} 帧落盘,清内存安全");
            P2InventoryStore.Reset();      // 若仍换入 P2 背包则先恢复 P1，并清空 P2 背包数据
            P2EquipmentStore.Clear();      // 纯托管静态,不清会把装备带进下一个世界
            SplitScreenDeathFlow.ResetForWorldLeave();   // 死亡/复活流程的静态(协程句柄等)
            PlayerContext.Clear();         
            RestoreVanillaLocalPlayerSingletons();   
            SplitScreenRuntime.Destroy();  
            SplitScreenRuntime.Create();   // 全新运行时（player1/player2/mask/各标志全部归零）
            _pendingP1GamepadRepair = true;
        }
    }
}
