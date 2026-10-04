using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SplitScreen
{

    // 湿砖块拾取/烘干崩溃修复(P1/P2 都受影响):vanilla Brick_Wet.OnFinishedPlacement
    // 放置时缓存 playerNetworkManager = ComponentManager<Network_Player>.Value.PlayerNetworkManager。
    // 该 getter 被 mod 改写:P2 放置(P2 上下文)时返回 P2 克隆(isLocalPlayer=false)→
    // 缓存进坏 PNM,之后 OnIsRayed 拾取 / OnFinishedDrying 发 RPC 用它崩溃;
    // 重载走 RGD_Brick 在 P1 上下文重建故正常。照 vanilla 单机语义
    // (Value 恒为真本地玩家=分屏里的 P1),放置后把该字段校正为 P1 的 PNM。
    // P1 放的砖本就是这个值→无变化;只纠正 P2 放的砖。
    [HarmonyPatch(typeof(Brick_Wet), "OnFinishedPlacement")]
    static class Patch_Brick_Wet_OnFinishedPlacement_P2
    {
        static FieldInfo _fPnm;

        static void Postfix(Brick_Wet __instance)
        {
            if (Main.player1 == null) return;
            var genuine = Main.player1.PlayerNetworkManager;
            if (genuine == null) return;
            if (_fPnm == null)
                _fPnm = AccessTools.Field(typeof(Brick_Wet), "playerNetworkManager");
            if (_fPnm != null) _fPnm.SetValue(__instance, genuine);
        }
    }

    // P2 temporarily reuses the original BuildMenu panel, but its Update owns
    // a P1-canvas cost cursor. P2's independent menu/controller supplies its
    // own navigation and material UI while that panel is borrowed.
    [HarmonyPatch(typeof(BuildMenu), "Update")]
    static class Patch_BuildMenu_Update_P2MenuIsolation
    {
        static bool Prefix()
        {
            if (!Main.IsP2BuildMenuOpen) return true;
            Main.HideVanillaBuildCostCursor();
            return false;
        }
    }

    
    
    
    [HarmonyPatch(typeof(BlockCreator), "Update")]
    static class Patch_BlockCreator_Update_P2
    {
        // 统一代理层(收束)：用 P2OriginalScope.Build() 替代手写 isLocalPlayer 翻转(强制本地 + PlayerContext=P2 +
        //  P2Mode=Build，退出自动恢复，__state 保证不漏恢复)。仍保留 _p2BuildActive 旧标志 → 现有读取点
        //  (AimRay/DTM/CIC/CreateBlock 的 _p2BuildActive 门控)不变，行为一致。
        static void Prefix(BlockCreator __instance, ref P2OriginalScope __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return;        

            Main.EnsureP2BuildInit(__instance);
            __state = P2OriginalScope.Build();   // P2Mode=Build → Main._p2BuildActive 计算属性自动为真
        }

        static void Postfix(P2OriginalScope __state) => __state?.Dispose();
        static Exception Finalizer(Exception __exception, P2OriginalScope __state) { __state?.Dispose(); return __exception; }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: BlockCreator.HasEnoughResourcesToBuild(CostMultiple[]) — 让 P2 的"材料是否足够"按【P2 背包】判定。
    //   原版读缓存字段 playerInventory(=共享单例)。P2 建造时(背包关着)单例装的是 P1 内容 → 误判材料不足 →
    //   幽灵恒红、无法建造；打开 P2 背包(SwapInP2)后单例才装 P2 内容 → 变绿。
    //   修复：P2 建造中(_p2BuildActive)且未换入(背包关着)时，改按 P2 背包(_p2Slots)逐物品累计判定(无副作用，逐帧安全)。
    //   背包开着(IsSwapped)→ 单例已是 P2 内容, 原版结果本就正确, 不覆盖。Block 重载内部转调本重载 → 一处覆盖即可。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(BlockCreator), "HasEnoughResourcesToBuild", new Type[] { typeof(CostMultiple[]) })]
    static class Patch_BlockCreator_HasEnough_P2
    {
        static void Postfix(BlockCreator __instance, CostMultiple[] buildCost, ref bool __result)
        {
            if (!Main._p2BuildActive || P2InventoryStore.IsSwapped) return;
            if (Main.player2 == null || __instance.GetComponentInParent<Network_Player>() != Main.player2) return;
            // 创造模式(无限资源)：原版 GetItemCount 返回 int.MaxValue → vanilla 恒 True(免费建造)。
            //  不覆盖,沿用原版结果(绿)。否则会被下方"P2 库存"判定误改红(创造模式 P2 背包可能为空)。
            if (GameModeValueManager.GetCurrentGameModeValue().playerSpecificVariables.unlimitedResources) return;
            if (Cheat.UseGodMode) { __result = true; return; }
            if (buildCost == null) { __result = false; return; }
            for (int i = 0; i < buildCost.Length; i++)
            {
                var cm = buildCost[i];
                if (cm == null) continue;
                int have = 0;   // P2 库存 = 背包(_p2Slots) + 热栏(_p2Hotbar)
                if (cm.items != null)
                    foreach (var it in cm.items)
                        if (it != null)
                            have += P2InventoryStore.CountBackpackItem(it.UniqueIndex) + Main.CountP2HotbarItem(it.UniqueIndex);
                if (have < cm.amount) { __result = false; return; }
            }
            __result = true;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: BlockCreator.CreateBlock — P2 放置可放置物时把 hotslotIndex 参数强制为 -1。
    //   原版可放置物分支(CreateBlock 内)用 hotslotIndex 决定从哪个槽扣 1 + 是否重选幽灵。
    //   该 index 来自 playerNetwork.Inventory.hotbar.GetSelectedSlotIndex()，但：
    //    (a) GetSelectedSlotIndex 是 `return slotIndex;` 被 Mono 内联 → 补它无效；
    //    (b) P2.Inventory 与 P1 共享 → 取到【P1】的选中槽：误扣 P1 物品；P1 槽空时走 ReselectCurrentSlot
    //        → P2 幽灵丢失、无法连续放置。
    //   故直接在 CreateBlock 入口把 hotslotIndex 改 -1 → 可放置物分支整体跳过(不扣物/不 Reselect)，
    //   selectedBlock 置空后下一帧 Update 自动重建幽灵 → 可连续放置。仅 _p2BuildActive 期间介入。
    //   (P2 快捷栏数量扣减后续单独接 P2InventoryStore，不走 P1 共享库存。)
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(BlockCreator), "CreateBlock", new Type[] {
        typeof(Item_Base), typeof(Vector3), typeof(Vector3), typeof(DPS), typeof(int),
        typeof(bool), typeof(uint), typeof(uint), typeof(uint) })]
    static class Patch_BlockCreator_CreateBlock_P2
    {
        
        
        
        static void Prefix(BlockCreator __instance, ref int hotslotIndex, bool replicating, ref P2InventoryScope __state)
        {
            __state = null;
            // Save restoration creates P1's raft through the same overload while
            // P2 build state may still be stale from the previous session.
            if (SaveAndLoad.IsGameLoading) return;
            if (replicating || !Main.WorldReadyForSplit) return;
            if (!Main._p2BuildActive) return;
            if (Main.player2 == null || __instance.GetComponentInParent<Network_Player>() != Main.player2) return;
            hotslotIndex = -1;
            if (!replicating) __state = new P2InventoryScope();
        }

        // CreateBlock 第 983 行无条件 selectedBuildableItem=null，原版可放置物分支靠 SetBlockTypeToBuild 重选恢复；
        //  但我们把 hotslotIndex 改 -1 跳过了那段 → selectedBuildableItem 留空 → 幽灵不再重建。
        //  故这里(仅 P2 建造 + 可放置物 + 非复制)：先从 P2 快捷栏扣 1，仍有剩则重选(重建幽灵继续放)，
        //  用光则不重建(下一帧 TickP2Hotbar 取消手持)。全程不动 P1 共享库存。
        //  __state(P2InventoryScope)在 Postfix 正常释放(换出 P2 背包，持久化扣减)，Finalizer 兜底(Dispose 幂等)。
        static void Postfix(BlockCreator __instance, Item_Base blockItem, bool replicating, P2InventoryScope __state)
        {
            try
            {
                if (SaveAndLoad.IsGameLoading) return;
                if (replicating || !Main.WorldReadyForSplit) return;
                if (!Main._p2BuildActive || blockItem == null) return;
                if (Main.player2 == null || __instance.GetComponentInParent<Network_Player>() != Main.player2) return;
                if (blockItem.settings_buildable == null || !blockItem.settings_buildable.Placeable) return;
                if (Main.ConsumeP2HeldPlaceable(blockItem))
                    __instance.SetBlockTypeToBuild(blockItem.UniqueName);
            }
            finally { __state?.Dispose(); }   // 正常路径释放;异常路径由 Finalizer 兜底
        }

        static Exception Finalizer(Exception __exception, P2InventoryScope __state) { __state?.Dispose(); return __exception; }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: BuildComponent.OnSelect / OnDeSelect — 让 P2 持【可放置物品】时进入放置模式
    //   原版流程：选中可放置物 → UseItemController.StartUsing → GetItemNameFromUsable 返回
    //   "Placeable" → 给 BuildComponent(.obj) 发 OnSelect。BuildComponent.OnSelect/OnDeSelect
    //   均门控 playerNetwork.IsLocalPlayer(P2=false→不激活 BlockCreator/不设建造物→无幽灵)。
    //   这里临时把 P2 当本地玩家，让其原样跑：激活 BlockCreator + SetBlockTypeToBuild(可放置物)，
    //   之后由已 P2 化的 BlockCreator.Update 完成 幽灵预览 + 旋转(D-pad右/右摇杆) + RT 放置。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(BuildComponent), "OnSelect")]
    static class Patch_BuildComponent_OnSelect_P2
    {
        // 收束：P2OriginalScope.Build() 替手写 isLocalPlayer 翻转。
        static void Prefix(BuildComponent __instance, ref P2OriginalScope __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return;
            __state = P2OriginalScope.Build();
        }

        static void Postfix(P2OriginalScope __state) => __state?.Dispose();
        static Exception Finalizer(Exception __exception, P2OriginalScope __state) { __state?.Dispose(); return __exception; }
    }

    [HarmonyPatch(typeof(BuildComponent), "OnDeSelect")]
    static class Patch_BuildComponent_OnDeSelect_P2
    {
        static void Prefix(BuildComponent __instance, ref P2OriginalScope __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return;
            __state = P2OriginalScope.Build();
        }

        static void Postfix(P2OriginalScope __state)
        {
            // 离开可放置物时，把建造物重置为地基(非可放置物)。否则 selectedBuildableItem 仍是该可放置物，
            //  切到锤子后(P2 的 OnHammerSelect 因 buildMenu==null 不重选)会残留可放置物幽灵并能用 RT 放置。
            //  原版靠 OnHammerSelect→buildMenu.lastSelectedBuildabe 重置；P2 无 buildMenu，故在此显式重置。
            if (__state != null)
            {
                var bc = Main.GetP2BlockCreator();
                if (bc != null) bc.SetBlockTypeToBuild("Block_Foundation");
            }
            __state?.Dispose();
        }
        static Exception Finalizer(Exception __exception, P2OriginalScope __state) { __state?.Dispose(); return __exception; }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: CustomInputConfig 读输入 — P2 建造期间把建造按键重定向到 P2 手柄
    //   "LMB"=放置 → P2 RT。其余建造键(RMB/Rotate/...)阶段1先不接(返回 false)。
    //   仅在 _p2BuildActive 期间介入(即 P2 的 BlockCreator.Update 内)，不影响 P1。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(CustomInputConfig), "WasPressedThisFrame", new Type[] { typeof(InputAction), typeof(string) })]
    static class Patch_CIC_WasPressed_P2Build
    {
        static bool Prefix(string key, ref bool __result)
        {
            if (!(Main.P2BuildToolActive || (Main.p2UsingItemActive && key == "LMB"))) return true;
            var gp = Main.GetP2BoundGamepad();
            __result = gp != null && !Main.IsP2BuildMenuOpen && !Main.IsP2BackpackOpen &&  
                       ((key == "LMB" && gp.rightTrigger.wasPressedThisFrame)            // 放置/修理/加固/拆除/抛钩
                     || (key == "Rotate" && gp.dpad.right.wasPressedThisFrame)           // 旋转 = D-pad 右
                     || (key == "BlockPick" && gp.rightStickButton.wasPressedThisFrame) // 模块选择 = R3
                     || (key == "Remove" && gp.dpad.left.wasPressedThisFrame));          // 移除可放置物 = D-pad 左(原版)
            return false;
        }
    }

    [HarmonyPatch(typeof(CustomInputConfig), "IsPressed", new Type[] { typeof(InputAction), typeof(string) })]
    static class Patch_CIC_IsPressed_P2Build
    {
        static bool Prefix(string key, ref bool __result)
        {
            // P2BuildToolActive(锤/拆/移除) 或 Tool scope(P2ToolRunner 驱动的斧等)：把工具的 "LMB" 持按重定向到 RT。
            if (!(Main.P2BuildToolActive || Main.p2UsingItemActive)) return true;
            var gp = Main.GetP2BoundGamepad();
            __result = gp != null && !Main.IsP2BuildMenuOpen && !Main.IsP2BackpackOpen &&  
                       ((key == "LMB" && gp.rightTrigger.isPressed)
                     || (key == "Rotate" && gp.dpad.right.isPressed)
                     || (key == "Remove" && gp.dpad.left.isPressed));
            return false;
        }
    }

    [HarmonyPatch(typeof(CustomInputConfig), "WasReleasedThisFrame", new Type[] { typeof(InputAction), typeof(string) })]
    static class Patch_CIC_WasReleased_P2Build
    {
        static bool Prefix(string key, ref bool __result)
        {
            if (!(Main.P2BuildToolActive || (Main.p2UsingItemActive && key == "LMB"))) return true;
            var gp = Main.GetP2BoundGamepad();
            __result = gp != null && ((key == "LMB" && gp.rightTrigger.wasReleasedThisFrame)   // 抛钩松开/拉扯松开等
                                   || (key == "Rotate" && gp.dpad.right.wasReleasedThisFrame)
                                   || (key == "Remove" && gp.dpad.left.wasReleasedThisFrame));
            return false;
        }
    }

    // 自由旋转的 X 轴增量("Mouse X")→ P2 右摇杆 X(旋转时 P2 视角已被 SetMouseLookScripts 关闭)。
    [HarmonyPatch(typeof(CustomInputConfig), "GetXAxis", new Type[] { typeof(InputAction), typeof(string) })]
    static class Patch_CIC_GetXAxis_P2Build
    {
        static bool Prefix(string key, ref float __result)
        {
            if (!Main.P2BuildToolActive) return true;
            var gp = Main.GetP2BoundGamepad();
            __result = (gp != null && key == "Mouse X") ? gp.rightStick.ReadValue().x : 0f;
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: DisplayTextManager — P2 建造工具激活时【只屏蔽 P1 写入，不再转发成 P2 文本】。
    //   P2 的建造/工具提示现在由【字形提示条】(Main.P2BuildPrompt) 独占显示；若仍转发到
    //   _p2PromptText 会与字形条重复(用户反馈"提示文本有重复")。故这里一律 return false 仅屏蔽。
    // ══════════════════════════════════════════════════════════════════════
    // P2 持物提示(建造/装水/喝吃)→捕获到 P2 半屏；设备(射线)→交互提示；其余屏蔽。
    [HarmonyPatch(typeof(DisplayTextManager), "ShowText", new Type[] { typeof(string), typeof(string), typeof(KeyCode), typeof(int), typeof(int), typeof(bool) })]
    static class Patch_DTM_ShowText1_P2
    { static bool Prefix(string text) => P2DisplayTextRouter.RouteShowText(text); }

    [HarmonyPatch(typeof(DisplayTextManager), "ShowText", new Type[] { typeof(string), typeof(KeyCode), typeof(int), typeof(int), typeof(bool) })]
    static class Patch_DTM_ShowTextKey_P2
    { static bool Prefix(string text) => P2DisplayTextRouter.RouteShowText(text); }

    [HarmonyPatch(typeof(DisplayTextManager), "ShowText", new Type[] { typeof(string), typeof(string), typeof(string), typeof(KeyCode), typeof(int), typeof(int), typeof(bool) })]
    static class Patch_DTM_ShowText2_P2
    { static bool Prefix(string text) => P2DisplayTextRouter.RouteShowText(text); }

    [HarmonyPatch(typeof(DisplayTextManager), "ShowTextConsole", new Type[] { typeof(string), typeof(string), typeof(int), typeof(int), typeof(bool) })]
    static class Patch_DTM_ShowTextConsole1_P2
    { static bool Prefix(string text) => P2DisplayTextRouter.RouteShowText(text); }

    [HarmonyPatch(typeof(DisplayTextManager), "ShowTextConsole", new Type[] { typeof(string), typeof(string), typeof(string), typeof(int), typeof(int), typeof(bool) })]
    static class Patch_DTM_ShowTextConsole2_P2
    { static bool Prefix(string text) => P2DisplayTextRouter.RouteShowText(text); }

    [HarmonyPatch(typeof(DisplayTextManager), "HideDisplayTexts", new Type[] { typeof(int) })]
    static class Patch_DTM_Hide_P2
    {
        static bool Prefix()
        {
            if (Main.isProcessingP2Ray)
            {
                Main.ClearP2InteractPrompt();
                return false;
            }
            if (Main.IsP2OriginalInputActive)
            {
                Main.ClearP2Prompts();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(DisplayTextManager), "ShowText", new Type[] { typeof(string), typeof(int), typeof(bool), typeof(int) })]
    static class Patch_DTM_ShowText0_P2
    { static bool Prefix(string text) => P2DisplayTextRouter.RouteShowText(text); }

    [HarmonyPatch(typeof(DisplayTextManager), "HideDisplayTexts", new Type[] { })]
    static class Patch_DTM_HideAll_P2
    {
        static bool Prefix()
        {
            if (Main.isProcessingP2Ray)
            {
                Main.ClearP2InteractPrompt();
                return false;
            }
            if (Main.IsP2OriginalInputActive)
            {
                Main.ClearP2Prompts();
                return false;
            }
            return true;
        }
    }

    static class P2DisplayTextRouter
    {
        internal static bool RouteShowText(string text)
        {
            if (Main.isProcessingP2Ray)
            {
                Main.CaptureDevicePrompt(text);
                return false;
            }

            if (Main.IsP2OriginalInputActive)
            {
                Main.CaptureBuildPrompt(text);
                return false;
            }

            return true;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: ThirdPerson.Update — 让 P2 的第一/第三人称相机切换生效
    //   原版 ThirdPerson.Update + Start 都门控本地玩家：P2 的 cameraTransform/currentModel 未设、
    //   相机移动逻辑不跑 → 翻转 ThirdPersonState 无效。这里给 P2 的 ThirdPerson 补设相机字段，
    //   并在其 Update 期间临时把 P2 当本地玩家。(限制:第三人称视角旋转目前读 P1 的 Look 动作。)
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(ThirdPerson), "Update")]
    static class Patch_ThirdPerson_Update_P2
    {
        // 收束：P2OriginalScope.Movement() 替手写 isLocalPlayer 翻转。
        //  安全性：ThirdPerson.Update 只读自身 playerNetwork 字段(=P2)，不读 PlayerContext 路由的
        //  ComponentManager<Network_Player/Player/PlayerInventory>(那几个仅在 Start 读 Settings/CanvasHelper)，
        //  故附带的 PlayerContext=P2 不影响相机逻辑。Movement 模式无特殊读取者。
        static FieldInfo _fNet;

        static void Prefix(ThirdPerson __instance, ref P2OriginalScope __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            if (_fNet == null)
                _fNet = typeof(ThirdPerson).GetField("playerNetwork", BindingFlags.Instance | BindingFlags.NonPublic);
            var np = _fNet?.GetValue(__instance) as Network_Player;
            if (np == null || np != Main.player2) return;

            Main.EnsureP2ThirdPerson(__instance);   // 补建 cameraTransform/currentModel/cameraRotateTransform
            __state = P2OriginalScope.Movement();
        }

        static void Postfix(P2OriginalScope __state) => __state?.Dispose();
        static Exception Finalizer(Exception __exception, P2OriginalScope __state) { __state?.Dispose(); return __exception; }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: Hammer.Update — 让 P2 的【修理/加固】跑起来(临时本地玩家上下文)
    //   Hammer 与 BlockCreator 是同一把"建造锤"上的两个组件：持锤时两者同帧 Update。
    //   Hammer.HandleRepairingAndReinforcement 门控 IsLocalPlayer + 用 Helper.HitAllAtCursor
    //   (已由 AimRay/_p2HammerActive 改走 P2 相机) + 读 "LMB"(已重定向到 RT)。
    //   选中 修理工具/加固工具(走建造菜单)后，对着可修/可加固方块按住 RT 即生效。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Hammer), "Update")]
    static class Patch_Hammer_Update_P2
    {
        static readonly FieldInfo PlayerNetworkField =
            typeof(Hammer).GetField("playerNetwork", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo CanvasField =
            typeof(Hammer).GetField("canvas", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo BlockCreatorField =
            typeof(Hammer).GetField("blockCreator", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo WoodParticlesField =
            typeof(Hammer).GetField("woodParticles", BindingFlags.Instance | BindingFlags.NonPublic);
        static bool _reportedDeferredInitialization;
        // 收束：P2OriginalScope.Build() 替手写 isLocalPlayer 翻转(保留菜单内不接管的门控)。
        //  P2Mode=Build → Main._p2BuildActive 计算属性自动为真(修理/加固期与放置期同属 Build)。
        static bool Prefix(Hammer __instance, ref P2OriginalScope __state)
        {
            __state = null;
            if (Main.player2 == null) return true;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return true;

            // P2 tools can be invoked by the P2 runner before Unity has called
            // this component's Start. Mirror Hammer.Start's required references
            // and defer one frame if the cloned build components are not ready.
            if (PlayerNetworkField?.GetValue(__instance) == null)
                PlayerNetworkField?.SetValue(__instance, np);
            if (CanvasField?.GetValue(__instance) == null)
                CanvasField?.SetValue(__instance, ComponentManager<CanvasHelper>.Value);

            var blockCreator = BlockCreatorField?.GetValue(__instance) as BlockCreator;
            var canvas = CanvasField?.GetValue(__instance) as CanvasHelper;
            var particles = WoodParticlesField?.GetValue(__instance) as ParticleController;
            if (blockCreator == null || canvas == null || particles == null)
            {
                if (!_reportedDeferredInitialization)
                {
                    _reportedDeferredInitialization = true;
                    Main.ModEntry.Logger.Log("[P2Hammer] Deferred Hammer.Update until cloned build references initialize");
                }
                return false;
            }
            _reportedDeferredInitialization = false;
            Main.EnsureP2BuildInit(blockCreator);
            // 滑索中:跳过 P2 锤子整段 Update(否则 Hammer.Update 每帧 NRE)。用 P2ZiplineDriver 的稳健检测(直接找组件,不依赖可能为空的 ziplinePlayer 字段)。
            if (P2ZiplineDriver.IsAttached) return false;
            // P2 custom panels own input while open. Running Hammer.Update
            // unscoped here lets its global CanvasHelper path target P1.
            if (Main.IsP2BackpackOpen || Main.IsP2BuildMenuOpen) return false;

            __state = P2OriginalScope.Build();
            return true;
        }

        static void Postfix(P2OriginalScope __state) => __state?.Dispose();
        static Exception Finalizer(Exception __exception, P2OriginalScope __state) { __state?.Dispose(); return __exception; }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Axe.Update —— 已迁移到【通用 P2ToolRunner】(阶段7/8)。
    //   原 Patch_Axe_Update_P2 专用补丁已删除：现由 P2ToolRunner 每帧在 P2OriginalScope.Tool() 内
    //   反射调 P2 斧子的 Update()(scope 强制本地 → 原版拆除/准星逻辑整段跑通)。
    //   不可再保留强制本地的 Update 补丁，否则 Unity 原生那次 Update 也会真跑 → 双跑(双倍拆除/耐久)。
    //   配套：拆除期间的 P2 相机射线靠 AimRay 读 IsP2OriginalActive；进度环/耐久/拾取提示的
    //   门控已扩展为兼容 Tool scope(见下方 SetLoadCircle/RemoveDurability 及 ShowItem 补丁)。
    //   OnAxeHit(砍树动画事件，在 Update 之外触发)仍由 Patch_Axe_OnAxeHit_P2 处理(包 P2OriginalScope.Tool())。
    // ══════════════════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: Axe.OnAxeHit — 让 P2 能【砍树采集】(动画事件，在 Update 之外触发)。
    //   动画播到挥击帧时调 OnAxeHit；那一刻 isLocalPlayer 是 P2 的真实 false → 原版直接 return。
    //   做法(统一代理层)：整段包进 P2OriginalScope.Tool()(强制本地→过门控；IsP2OriginalActive→HitAtCursor 走
    //   P2 相机并跳过自身；p2UsingItemActive→耐久路由 P2)；并 SwapIn P2 背包使 Harvest 的 AddItem 落 P2。
    //   (原 _p2AxeActive 标志已并入 P2Mode：scope 内 IsP2OriginalActive/p2UsingItemActive 覆盖所有原读取点。)
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Axe), "OnAxeHit")]
    static class Patch_Axe_OnAxeHit_P2
    {
        static void Prefix(Axe __instance, ref HookP2State __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return;

            
            __state = new HookP2State { Ctx = P2FrameContext.Tool(routeInventory: true) };
        }

        static void Postfix(HookP2State __state) => Cleanup(__state);
        static Exception Finalizer(Exception __exception, HookP2State __state) { Cleanup(__state); return __exception; }

        static void Cleanup(HookP2State st)
        {
            if (st == null) return;
            st.Ctx?.Dispose(); st.Ctx = null;
        }
    }

    // P2 用工具(斧/其它)时，原版会扣【共享热栏=P1】的选中槽耐久 → 改为扣【P2 手持物】的耐久(对齐原版且不误伤 P1)。
    [HarmonyPatch(typeof(PlayerInventory), "RemoveDurabillityFromHotSlot")]
    static class Patch_PlayerInventory_RemoveDurability_P2
    {
        static bool Prefix(int durabilityStacksToRemove, ref bool __result)
        {
            if (!Main.p2UsingItemActive) return true;   
            var tv = GameModeValueManager.GetCurrentGameModeValue().toolVariables;
            if (!tv.areToolsIndestructible)
                Main.ConsumeP2HeldDurability((int)(durabilityStacksToRemove * tv.toolDurabilityLossMultiplier));
            __result = false;
            return false;   
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: CanvasHelper.SetLoadCircle — P2 斧拆除进度环原写 P1 HUD → 改【转发到 P2 半屏的进度环】。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(CanvasHelper), "SetLoadCircle", new Type[] { typeof(bool) })]
    static class Patch_CanvasHelper_SetLoadCircle_Bool_P2
    {
        static bool Prefix(bool state)
        { if (!(Main._p2RemoveActive || Main._p2FillWaterActive || Main.IsP2OriginalActive)) return true; Main.SetP2LoadCircle(state); return false; }
    }

    [HarmonyPatch(typeof(CanvasHelper), "SetLoadCircle", new Type[] { typeof(float) })]
    static class Patch_CanvasHelper_SetLoadCircle_Float_P2
    {
        static bool Prefix(float value)
        { if (!(Main._p2RemoveActive || Main._p2FillWaterActive || Main.IsP2OriginalActive)) return true; Main.SetP2LoadCircle(value); return false; }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: RemovePlaceables.Update — 让 P2 能【移除可放置物(箱子等)】(临时本地玩家上下文)
    //   原版 Update 门控 IsLocalPlayer + 用 Helper.HitAtCursor/HitAllAtCursor(→AimRay/_p2RemoveActive 走 P2 相机)
    //   + 读 "Remove"(→D-pad 下) + SetLoadCircle(→P2 进度环)。Postfix 据 showingText 显隐 P2 "移除"提示。
    // ══════════════════════════════════════════════════════════════════════
    sealed class RemoveP2State { public P2OriginalScope Scope; public bool Active; }

    [HarmonyPatch(typeof(RemovePlaceables), "Update")]
    static class Patch_RemovePlaceables_Update_P2
    {
        static FieldInfo _fNet, _fShowing;

        static void Prefix(RemovePlaceables __instance, ref RemoveP2State __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            if (_fNet == null)
            {
                _fNet     = typeof(RemovePlaceables).GetField("playerNetwork", BindingFlags.Instance | BindingFlags.NonPublic);
                _fShowing = typeof(RemovePlaceables).GetField("showingText", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            var np = _fNet?.GetValue(__instance) as Network_Player;
            if (np == null || np != Main.player2) return;   

            var st = new RemoveP2State();
            // 对 P2 的实例【始终】置 _p2RemoveActive：让它对共享 DisplayTextManager 的写/隐全部被屏蔽，
            //  否则其 ResetRemove→HideDisplayTexts() 会清掉【P1】在同一 index 的"移除"提示(共用 DTM)。
            Main._p2RemoveActive = true; st.Active = true;
            // 仅在非菜单时才用 scope 强制本地(真正执行移除/显示提示)；菜单内 body 会自行 reset(不移除)。
            //  收束:用 P2OriginalScope.Build() 替手写 isLocalPlayer 翻转(__state 保证恢复)。
            if (!Main.IsP2BackpackOpen && !Main.IsP2BuildMenuOpen)
                st.Scope = P2OriginalScope.Build();
            __state = st;
        }

        static void Postfix(RemovePlaceables __instance, RemoveP2State __state)
        {
            if (__state != null && __state.Active && _fShowing != null)
            {
                bool showing = (bool)_fShowing.GetValue(__instance);
                Main.SetP2RemovePrompt(showing);
            }
            Cleanup(__state);
        }
        static Exception Finalizer(Exception __exception, RemoveP2State __state) { Cleanup(__state); return __exception; }

        static void Cleanup(RemoveP2State st)
        {
            if (st == null || !st.Active) return;
            Main._p2RemoveActive = false;
            st.Scope?.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: RemovePlaceables.PickupBlock — 把【移除可放置物返还的物品】路由进 P2 背包。
    //   原版返还在 RemoveBlockCoroutine 里、门控 playerRemovingBlock.IsLocalPlayer(P2=false→跳过→物品凭空消失)。
    //   这里在移除前手动把"方块本体物品 + 箱子内容"加进 P2 背包(换入 + 只放背包格)。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(RemovePlaceables), "PickupBlock")]
    static class Patch_RemovePlaceables_PickupBlock_P2
    {
        static void Prefix(Block block)
        {
            if (!Main._p2RemoveActive || Main.player2 == null || block == null) return;
            // 原版 PickupBlock 先用 giveItems:false 判定能否拆(箱子被打开、奖杯正在编辑等会拒绝),通过才拆。
            // 这里必须走同一判定,否则方块没拆掉,P2 却每个读条周期白拿一份。
            try { if (!RemovePlaceables.ReturnItemsFromBlock(block, Main.player2, giveItems: false)) return; }
            catch (Exception) { return; }
            try
            {
                using (new P2InventoryScope())   // 换入 + RoutingPickup → 返还物进 P2 背包
                {
                    if (block.itemToReturnOnDestroy != null && block.itemToReturnOnDestroy.UniqueIndex != 257)
                        Main.player2.Inventory.AddItem(block.itemToReturnOnDestroy.UniqueName, 1);
                    RemovePlaceables.ReturnItemsFromBlock(block, Main.player2, giveItems: true);   // 箱子内容
                }
            }
            catch (Exception e) { Main.ModEntry.Logger.Log("[P2Remove] 返还物品异常: " + e.Message); }
        }
    }
}
