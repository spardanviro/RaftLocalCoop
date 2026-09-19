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
        
        
        
        static void Prefix(BlockCreator __instance, ref P2OriginalScope __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return;        

            Main.EnsureP2BuildInit(__instance);
            __state = P2OriginalScope.Build();   
        }

        static void Postfix(P2OriginalScope __state) => __state?.Dispose();
        static Exception Finalizer(Exception __exception, P2OriginalScope __state) { __state?.Dispose(); return __exception; }
    }

    
    
    
    
    
    
    
    [HarmonyPatch(typeof(BlockCreator), "HasEnoughResourcesToBuild", new Type[] { typeof(CostMultiple[]) })]
    static class Patch_BlockCreator_HasEnough_P2
    {
        static void Postfix(BlockCreator __instance, CostMultiple[] buildCost, ref bool __result)
        {
            if (!Main._p2BuildActive || P2InventoryStore.IsSwapped) return;
            if (Main.player2 == null || __instance.GetComponentInParent<Network_Player>() != Main.player2) return;
            
            
            if (GameModeValueManager.GetCurrentGameModeValue().playerSpecificVariables.unlimitedResources) return;
            if (Cheat.UseGodMode) { __result = true; return; }
            if (buildCost == null) { __result = false; return; }
            for (int i = 0; i < buildCost.Length; i++)
            {
                var cm = buildCost[i];
                if (cm == null) continue;
                int have = 0;   
                if (cm.items != null)
                    foreach (var it in cm.items)
                        if (it != null)
                            have += P2InventoryStore.CountBackpackItem(it.UniqueIndex) + Main.CountP2HotbarItem(it.UniqueIndex);
                if (have < cm.amount) { __result = false; return; }
            }
            __result = true;
        }
    }

    
    
    
    
    
    
    
    
    
    
    
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
            finally { __state?.Dispose(); }   
        }

        static Exception Finalizer(Exception __exception, P2InventoryScope __state) { __state?.Dispose(); return __exception; }
    }

    
    
    
    
    
    
    
    
    [HarmonyPatch(typeof(BuildComponent), "OnSelect")]
    static class Patch_BuildComponent_OnSelect_P2
    {
        
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
            
            
            
            if (__state != null)
            {
                var bc = Main.GetP2BlockCreator();
                if (bc != null) bc.SetBlockTypeToBuild("Block_Foundation");
            }
            __state?.Dispose();
        }
        static Exception Finalizer(Exception __exception, P2OriginalScope __state) { __state?.Dispose(); return __exception; }
    }

    
    
    
    
    
    [HarmonyPatch(typeof(CustomInputConfig), "WasPressedThisFrame", new Type[] { typeof(InputAction), typeof(string) })]
    static class Patch_CIC_WasPressed_P2Build
    {
        static bool Prefix(string key, ref bool __result)
        {
            if (!(Main.P2BuildToolActive || (Main.p2UsingItemActive && key == "LMB"))) return true;
            var gp = Main.GetP2BoundGamepad();
            __result = gp != null && !Main.IsP2BuildMenuOpen && !Main.IsP2BackpackOpen &&  
                       ((key == "LMB" && gp.rightTrigger.wasPressedThisFrame)            
                     || (key == "Rotate" && gp.dpad.right.wasPressedThisFrame)           
                     || (key == "BlockPick" && gp.rightStickButton.wasPressedThisFrame) 
                     || (key == "Remove" && gp.dpad.left.wasPressedThisFrame));          
            return false;
        }
    }

    [HarmonyPatch(typeof(CustomInputConfig), "IsPressed", new Type[] { typeof(InputAction), typeof(string) })]
    static class Patch_CIC_IsPressed_P2Build
    {
        static bool Prefix(string key, ref bool __result)
        {
            
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
            __result = gp != null && ((key == "LMB" && gp.rightTrigger.wasReleasedThisFrame)   
                                   || (key == "Rotate" && gp.dpad.right.wasReleasedThisFrame)
                                   || (key == "Remove" && gp.dpad.left.wasReleasedThisFrame));
            return false;
        }
    }

    
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

    
    
    
    
    
    
    [HarmonyPatch(typeof(ThirdPerson), "Update")]
    static class Patch_ThirdPerson_Update_P2
    {
        
        
        
        
        static FieldInfo _fNet;

        static void Prefix(ThirdPerson __instance, ref P2OriginalScope __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            if (_fNet == null)
                _fNet = typeof(ThirdPerson).GetField("playerNetwork", BindingFlags.Instance | BindingFlags.NonPublic);
            var np = _fNet?.GetValue(__instance) as Network_Player;
            if (np == null || np != Main.player2) return;

            Main.EnsureP2ThirdPerson(__instance);   
            __state = P2OriginalScope.Movement();
        }

        static void Postfix(P2OriginalScope __state) => __state?.Dispose();
        static Exception Finalizer(Exception __exception, P2OriginalScope __state) { __state?.Dispose(); return __exception; }
    }

    
    
    
    
    
    
    
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
            
            
            Main._p2RemoveActive = true; st.Active = true;
            
            
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

    
    
    
    
    
    [HarmonyPatch(typeof(RemovePlaceables), "PickupBlock")]
    static class Patch_RemovePlaceables_PickupBlock_P2
    {
        static void Prefix(Block block)
        {
            if (!Main._p2RemoveActive || Main.player2 == null || block == null) return;
            try
            {
                using (new P2InventoryScope())   
                {
                    if (block.itemToReturnOnDestroy != null && block.itemToReturnOnDestroy.UniqueIndex != 257)
                        Main.player2.Inventory.AddItem(block.itemToReturnOnDestroy.UniqueName, 1);
                    RemovePlaceables.ReturnItemsFromBlock(block, Main.player2, giveItems: true);   
                }
            }
            catch (Exception e) { Main.ModEntry.Logger.Log("[P2Remove] 返还物品异常: " + e.Message); }
        }
    }
}
