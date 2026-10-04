using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  Patch 17: Pickup.Update — P2 独立捡起 + 交互提示
    //
    //  全部逻辑移入 InteractionRouter.HandlePickupUpdate()；
    //  此处只是薄转发。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Pickup), "Update")]
    static class Patch_Pickup_Update
    {
        static bool Prefix(Pickup __instance) =>
            SplitScreenRuntime.Instance?.Interactions.HandlePickupUpdate(__instance) ?? true;

        static void Postfix(Pickup __instance)
        {
            if (Main.player1 == null) return;
            var player = __instance.GetComponent<Network_Player>();
            if (player == Main.player1) Main.ApplyP1HeldToolPromptPriority();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: InventoryPickup.ShowItem — P2 拾取时原版"捡到 X"弹在 P1 共享 HUD → 改弹到 P2 半屏右下角 toast。
    //   (P2 拾取靠 AddItem→PlayerInventory.AddItem 触发 inventoryPickup.ShowItem；P2 上下文时屏蔽 P1 弹窗并转发 P2。)
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(InventoryPickup), "ShowItem", new[] { typeof(string), typeof(int) })]
    static class Patch_InventoryPickup_ShowItem_P2
    {
        static bool Prefix(string uniqueItemName, int amount)
        {
            if (!(P2InventoryStore.RoutingPickup || Main.IsP2OriginalActive)) return true;  // P1 → 原版
            Main.ShowP2PickupToast(uniqueItemName, amount);   // P2 → 右下角 toast，屏蔽 P1 弹窗
            return false;
        }
    }

    // The clone shares the vanilla component type, but that component's Update
    // reads a P1-only static inventory. P2's controller supplies the equivalent
    // hover button state and invokes its own isolated craft path instead.
    [HarmonyPatch(typeof(BuildingUI_Costbox_Sub_Crafting), "Update")]
    static class Patch_BuildingUI_Costbox_Sub_Crafting_Update_P2
    {
        static bool Prefix(BuildingUI_Costbox_Sub_Crafting __instance)
        {
            return !Main.IsP2CraftingCostSub(__instance);
        }
    }

    // The cloned button retains the prefab's UnityEvent. P2's virtual cursor
    // uses Main.P2CraftTryQuickCraftHovered instead, so never let an EventSystem
    // click invoke the P1-oriented vanilla callback on a P2 clone.
    [HarmonyPatch(typeof(BuildingUI_Costbox_Sub_Crafting), "OnQuickCraft")]
    static class Patch_BuildingUI_Costbox_Sub_Crafting_OnQuickCraft_P2
    {
        static bool Prefix(BuildingUI_Costbox_Sub_Crafting __instance)
        {
            return !Main.IsP2CraftingCostSub(__instance);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: Inventory.FindSuitableSlot — P2 拾取/采集期间只用【背包格(非热栏)】
    //   原版按 allSlots 顺序返回第一个空槽，可能是 P1 热栏空格(热栏不参与 P2 换入换出→物品丢失)。
    //   RoutingPickup 期间改为：跳过 Hotbar 槽，只在非热栏(=已换入 P2 背包数据)里找空格/可叠加格。
    // ══════════════════════════════════════════════════════════════════════
    // ══════════════════════════════════════════════════════════════════════
    //  Patch: CanvasHelper.OpenMenu / OpenMenuCloseOther — 通用 P2 菜单宿主
    //   P2 设备上下文(isProcessingP2Ray)开任意菜单 → 搬到 P2 半屏(Main.OpenP2Menu)，跳过原版
    //   (原版会设全局 ActiveMenu/锁光标/切 P1 输入 → 冻结 P1)。P1 正常开菜单不受影响。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(CanvasHelper), "OpenMenu")]
    static class Patch_CanvasHelper_OpenMenu_P2
    {
        static bool Prefix(CanvasHelper __instance, MenuType menuType, ref bool __result)
        {
            if (Main.IsOpeningP2MenuInternally) return true;
            if (Main.SuppressVanillaP2PianoMenu(menuType))
            {
                __result = true;
                return false;
            }
            // P2 设备射线(isProcessingP2Ray) 或 P2 工具上下文(IsP2OriginalActive，如钓竿饵菜单/颜料刷调色菜单)开菜单 → 搬 P2 半屏。
            //  但仅限白名单(非全局/非专门通道菜单)，避免把 暂停/过场/建造菜单/背包 误塞到 P2 半屏。
            if (!(Main.isProcessingP2Ray || Main.IsP2OriginalActive) || !Main.ShouldHostP2Menu(menuType)) return true;
            __result = Main.OpenP2Menu(__instance, menuType);
            return false;
        }
    }

    [HarmonyPatch(typeof(CanvasHelper), "OpenMenuCloseOther")]
    static class Patch_CanvasHelper_OpenMenuCloseOther_P2
    {
        static bool Prefix(CanvasHelper __instance, MenuType menuType, ref bool __result)
        {
            if (Main.IsOpeningP2MenuInternally) return true;
            if (Main.SuppressVanillaP2PianoMenu(menuType))
            {
                __result = true;
                return false;
            }
            // P2 设备射线(isProcessingP2Ray) 或 P2 工具上下文(IsP2OriginalActive，如钓竿饵菜单/颜料刷调色菜单)开菜单 → 搬 P2 半屏。
            //  但仅限白名单(非全局/非专门通道菜单)，避免把 暂停/过场/建造菜单/背包 误塞到 P2 半屏。
            if (!(Main.isProcessingP2Ray || Main.IsP2OriginalActive) || !Main.ShouldHostP2Menu(menuType)) return true;
            __result = Main.OpenP2Menu(__instance, menuType);
            return false;
        }
    }

    [HarmonyPatch(typeof(CanvasHelper), "CloseMenu")]
    static class Patch_CanvasHelper_CloseMenu_P2Piano
    {
        static bool Prefix(MenuType menuType)
        {
            return !Main.SuppressVanillaP2PianoMenu(menuType);
        }
    }

    // P2 的容器不在 vanilla 视野里:加物品的目标是克隆的背包视图,而手持栏是 _p2Hotbar 数组。
    // 于是 vanilla 放不下时的两条退路(进手持栏 / 掉地面)对 P2 双双失效,物品静默消失。
    // AddItem 返回的是【没放下的数量】,拿它作判据把剩余交给 P2 自己的溢出处理。
    [HarmonyPatch(typeof(Inventory), "AddItem", new[] { typeof(string), typeof(int) })]
    static class Patch_Inventory_AddItem_P2Overflow
    {
        static void Postfix(string uniqueItemName, ref int __result)
        {
            if (__result <= 0 || !P2InventoryStore.RoutingPickup) return;
            Main.HandleP2AddOverflow(uniqueItemName, ref __result);
        }
    }

    [HarmonyPatch(typeof(Inventory), "FindSuitableSlot", new[] { typeof(Item_Base) })]
    static class Patch_Inventory_FindSuitableSlot_BackpackFirst
    {
        // 优先进背包(用户偏好，对两个玩家生效)：背包同类未满 > 背包空格 > 热栏同类未满 > 热栏空格。
        //  P2 拾取/制造上下文(RoutingPickup)绝不用热栏(P2 热栏是自定义数组，不在真实 allSlots 里)。
        //  仅作用于 PlayerInventory(箱子等其它库存不动)。
        static bool Prefix(Inventory __instance, Item_Base stackableItem, ref Slot __result)
        {
            if (!(__instance is PlayerInventory)) return true;
            bool p2 = P2InventoryStore.RoutingPickup;
            bool stack = stackableItem != null && stackableItem.settings_Inventory != null && stackableItem.settings_Inventory.Stackable;

            int p2BpCap = p2 ? P2EquipmentStore.P2BackpackExtraSlots() : 0;
            int p2BpSeen = 0;
            Slot bpStack = null, bpEmpty = null, hbStack = null, hbEmpty = null;
            foreach (var s in __instance.allSlots)
            {
                if (s == null) continue;
                bool isHotbar = s.slotType == SlotType.Hotbar;
                if (p2 && isHotbar) continue;                       // P2：绝不用真实热栏
                bool isBackpack = s.slotType == SlotType.Backpack;
                bool p2BpWithinCap = false;
                if (p2 && isBackpack) { p2BpWithinCap = p2BpSeen < p2BpCap; p2BpSeen++; }
                if (!s.active && !p2BpWithinCap) continue;
                if (s.IsEmpty)
                {
                    if (isHotbar) { if (hbEmpty == null) hbEmpty = s; }
                    else          { if (bpEmpty == null) bpEmpty = s; }
                    continue;
                }
                if (stack && !s.StackIsFull() && s.itemInstance != null && s.itemInstance.UniqueIndex == stackableItem.UniqueIndex)
                {
                    if (isHotbar) { if (hbStack == null) hbStack = s; }
                    else          { if (bpStack == null) bpStack = s; }
                }
            }
            __result = bpStack ?? bpEmpty ?? hbStack ?? hbEmpty;   // 背包优先；热栏兜底(P1 背包满时)
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  P2 背包满溢出 → 从 P2 身上掉落(而非 P1)
    //   背包满时 Inventory.AddItem 走 localPlayerInventory.DropItem(...)，原版用 hotbar.playerNetwork(=共享背包属主 P1)
    //   的位置/朝向掉落 → P2 拾取/制造溢出的物品从 P1 身上掉。RoutingPickup 期间(P2 拾取/制造)改用 P2 的位置/朝向。
    // ══════════════════════════════════════════════════════════════════════
    static class P2DropRedirect
    {
        internal static bool Active => Main.player2 != null && P2InventoryStore.RoutingPickup
                                        && Main.player2.CameraTransform != null && Main.player2.PersonController != null;
        internal static void Drop(ItemInstance instance)
        {
            var p2 = Main.player2;
            Helper.DropItem(instance, p2.transform.position, p2.CameraTransform.forward, p2.PersonController.HasRaftAsParent);
        }
    }

    [HarmonyPatch(typeof(PlayerInventory), "DropItem", new[] { typeof(ItemInstance) })]
    static class Patch_PlayerInventory_DropItem_Instance_P2
    {
        static bool Prefix(ItemInstance instance)
        {
            if (!P2DropRedirect.Active) return true;
            P2DropRedirect.Drop(instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInventory), "DropItem", new[] { typeof(Item_Base), typeof(int) })]
    static class Patch_PlayerInventory_DropItem_ItemAmount_P2
    {
        static bool Prefix(Item_Base item, int amount)
        {
            if (!P2DropRedirect.Active || item == null) return true;
            P2DropRedirect.Drop(new ItemInstance(item, amount, item.MaxUses));
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch 20: CraftingMenu.CraftItem — P2 制作时将 localPlayer 字段临时换成 P2
    //
    //  CraftItem IL 直接读 this.localPlayer 字段调 RemoveCostMultiple / AddItem，
    //  不经过 ComponentManager → 必须用 field-swap。
    // ══════════════════════════════════════════════════════════════════════
    // ══════════════════════════════════════════════════════════════════════
    //  Patch 20: CraftingMenu.CraftItem — P2 制作时将 localPlayer 字段临时换成 P2
    //
    //  还原策略：
    //    Postfix  = 正常路径（无异常）还原，同时清 module-level static。
    //    Finalizer= 异常路径还原（Postfix 未跑，static 仍有值）。
    //    Harmony 2.3.6 Finalizer 不支持 __state 参数，故用 module-level
    //    static 在 Prefix/Postfix/Finalizer 三者之间传递保存值。
    //    Unity 主线程单线程执行，不需要 [ThreadStatic]。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(CraftingMenu), "CraftItem")]
    static class Patch_CraftingMenu_CraftItem
    {
        static readonly FieldInfo craftSoundField =
            typeof(CraftingMenu).GetField("er_craftItem", BindingFlags.Instance | BindingFlags.NonPublic);

        static bool Prefix(CraftingMenu __instance)
        {
            if (!Main.IsP2CraftingMenu(__instance) || Main.player2 == null) return true;

            var craftInventory = Main.ActiveP2CraftPlayerInventory();
            if (craftInventory == null) return true;

            var itemToCraft = __instance.selectedRecipeBox != null
                ? __instance.selectedRecipeBox.ItemToCraft
                : null;
            if (itemToCraft == null) return false;

            var craftSound = craftSoundField?.GetValue(__instance) as string;
            if (!string.IsNullOrEmpty(craftSound))
                FMODUnity.RuntimeManager.PlayOneShotAttached(craftSound, Main.player2.gameObject);

            if (!GameModeValueManager.GetCurrentGameModeValue().playerSpecificVariables.unlimitedResources)
                Main.RemoveP2CraftCostIncludingHotbar(itemToCraft.settings_recipe.NewCost, craftInventory);

            craftInventory.AddItem(itemToCraft.UniqueName, itemToCraft.settings_recipe.AmountToCraft);
            Main.RefreshP2BackpackSlots();
            P2InventoryStore.CaptureFrom(craftInventory);
            return false;
        }
    }

    
    
    
    
    [HarmonyPatch(typeof(CraftingMenu), "Start")]
    static class Patch_CraftingMenu_Start_P2Category
    {
        static void Postfix(CraftingMenu __instance) => Main.ReapplyP2CraftCategoryAfterStart(__instance);
    }

    [HarmonyPatch(typeof(Inventory_ResearchTable), "CreateMenuItems")]
    static class Patch_ResearchCreateMenuItems_P2Clone
    {
        static void Prefix(Inventory_ResearchTable __instance, ref CraftingMenu craftingMenu)
        {
            if (!Main.IsP2ResearchClone(__instance) || craftingMenu != null) return;
            craftingMenu = Main.GetVanillaCraftingMenuForP2Research();
        }
    }

    // ResearchMenuItem.Start only caches ComponentManager<Network_Player>.Value.
    // During P2 construction that singleton briefly belongs to the player being
    // initialized. Do not let a pre-existing P1 research item cache that
    // transient object; its original local player is always P1.
    [HarmonyPatch(typeof(ResearchMenuItem), "Start")]
    static class Patch_ResearchMenuItem_Start_P1SpawnIsolation
    {
        static bool Prefix(ResearchMenuItem __instance)
        {
            if (!Main.isSpawningP2 || __instance == null || Main.player1 == null) return true;
            var root = __instance.transform.root;
            if (Main.P2HudCanvas != null && root != null && root.IsChildOf(Main.P2HudCanvas.transform)) return true;
            __instance.localPlayer = Main.player1;
            return false;
        }
    }

    // P2's independent recipe box must cache the P2 inventory.  P1 is untouched
    // and continues through the original SelectedRecipeBox initialization path.
    [HarmonyPatch(typeof(SelectedRecipeBox), "Initialize")]
    static class Patch_SelectedRecipeBox_Initialize_P2Inventory
    {
        static readonly FieldInfo PlayerInventoryField =
            typeof(SelectedRecipeBox).GetField("playerInventory", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo CostCollectionField =
            typeof(SelectedRecipeBox).GetField("costCollection", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo CostInventoryField =
            typeof(CostCollection).GetField("inventory", BindingFlags.Instance | BindingFlags.NonPublic);

        static void Postfix(SelectedRecipeBox __instance)
        {
            if (__instance == null) return;
            if (Main.IsP2SelectedRecipeBox(__instance))
            {
                var ownerInventory = Main.ActiveP2CraftPlayerInventory();
                if (ownerInventory == null) return;

                PlayerInventoryField?.SetValue(__instance, ownerInventory);
                var costs = CostCollectionField?.GetValue(__instance) as CostCollection;
                if (costs != null) CostInventoryField?.SetValue(costs, ownerInventory);
                return;
            }

            // P1's box can inherit a P2 inventory when the singleton is stale at
            // Initialize() time. Pin both refs to P1's real inventory so neither
            // the count display nor the Ctrl+C cheat leaks to P2's backpack.
            if (Main.player1 == null || Main.player1.Inventory == null) return;
            PlayerInventoryField?.SetValue(__instance, Main.player1.Inventory);
            var p1Costs = CostCollectionField?.GetValue(__instance) as CostCollection;
            if (p1Costs != null) CostInventoryField?.SetValue(p1Costs, Main.player1.Inventory);
        }
    }

    // PlayerInventory.Awake registers itself as the game's global inventory.
    // The P2 UI inventory is only a view clone, so it must never remain the
    // singleton used by P1's untouched vanilla menus.
    [HarmonyPatch(typeof(PlayerInventory), "Awake")]
    static class Patch_PlayerInventory_Awake_P2ViewRegistration
    {
        static void Postfix(PlayerInventory __instance)
        {
            if (!Main.IsP2HudClone(__instance) || Main.player1 == null || Main.player1.Inventory == null) return;
            ComponentManager<PlayerInventory>.Value = Main.player1.Inventory;
        }
    }

    // The original component stores its hover inventory in a static field.  Let
    // only P1 initialize that field; P2 handles cloned material hover/crafting
    // through Main.P2Crafting and must never overwrite the P1 singleton.
    [HarmonyPatch(typeof(BuildingUI_CostBox_CraftingCost), "Start")]
    static class Patch_BuildingUI_CostBox_CraftingCost_Start_P2
    {
        static bool Prefix(BuildingUI_CostBox_CraftingCost __instance) =>
            !Main.IsP2CraftingUiComponent(__instance);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch 25: Helper.LocalPlayerIsWithinDistance — P2 位置路由
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Helper), "LocalPlayerIsWithinDistance")]
    static class Patch25_Helper_LocalPlayerIsWithinDistance_P2
    {
        static bool Prefix(Vector3 position, float requiredDistance, ref bool __result)
        {
            if (!(Main.isProcessingP2Ray || Main._p2RemoveActive) || Main.player2 == null) return true;
            __result = Vector3.Distance(Main.player2.transform.position, position) <= requiredDistance;
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch 26: StorageManager.OpenStorage / CloseStorage — P2 背包链接
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(StorageManager), "OpenStorage")]
    static class Patch26a_StorageManager_OpenStorage_P2
    {
        static void Prefix(StorageManager __instance, Storage_Small storage)
        {
            if (Main.player2 == null || storage == null) return;
            var pn = __instance.GetComponentInParent<Network_Player>();
            if (pn != Main.player2) return;

            var inv = storage.GetInventoryReference();
            if (inv == null) return;
            Main.player2.Inventory.secondInventory = inv;
            inv.secondInventory = Main.player2.Inventory;
            Main.LogV("[Patch26a] P2 StorageManager.OpenStorage linked inventories");
        }
    }

    [HarmonyPatch(typeof(StorageManager), "CloseStorage")]
    static class Patch26b_StorageManager_CloseStorage_P2
    {
        static void Prefix(StorageManager __instance, Storage_Small storage)
        {
            if (Main.player2 == null || storage == null) return;
            var pn = __instance.GetComponentInParent<Network_Player>();
            if (pn != Main.player2) return;

            var inv = storage.GetInventoryReference();
            if (inv != null) inv.secondInventory = null;
            if (Main.player2.Inventory != null) Main.player2.Inventory.secondInventory = null;
            Main.LogV("[Patch26b] P2 StorageManager.CloseStorage cleared inventory links");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch 27: Storage_Small.Open / Close — P2 UI 显示
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Storage_Small), "Open")]
    static class Patch27a_Storage_Small_Open_P2
    {
        static void Postfix(Storage_Small __instance, Network_Player player)
        {
            if (Main.player2 == null || player != Main.player2) return;
            // P2 半屏箱子 UI（不调 OpenMenuCloseOther → 不设全局 ActiveMenu → 不冻结 P1）。
            Main.OpenP2Storage(__instance);
            Main.LogV("[Patch27a] P2 Storage_Small.Open -> P2 storage UI");
        }
    }

    [HarmonyPatch(typeof(Storage_Small), "Close")]
    static class Patch27b_Storage_Small_Close_P2
    {
        static void Postfix(Storage_Small __instance, Network_Player player)
        {
            if (Main.player2 == null || player != Main.player2) return;
            Main.CloseP2Storage();
            Main.LogV("[Patch27b] P2 Storage_Small.Close -> P2 storage UI close");
        }
    }
    [HarmonyPatch(typeof(Storage_Small), "Close")]
    static class Patch27c_Storage_Small_Close_KeepOpenForP2
    {
        static void Postfix(Storage_Small __instance)
        {
            if (!Main.IsP2UsingStorage(__instance)) return;
            if (__instance != null && __instance.anim != null)
                __instance.anim.SetBool("IsOpen", true);
        }
    }

    static class Patch_CostCollection_P2CraftInventory
    {
        static readonly FieldInfo inventoryField =
            typeof(CostCollection).GetField("inventory", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void Route(CostCollection __instance)
        {
            if (__instance == null || !Main.IsP2CraftingCostCollection(__instance)) return;
            var p2Inventory = Main.ActiveP2CraftPlayerInventory();
            if (p2Inventory != null) inventoryField?.SetValue(__instance, p2Inventory);
        }
    }

    // Root fix for the P1 recipe-panel material-count bleed: a vanilla P1 crafting
    // CostCollection caches ComponentManager<PlayerInventory>.Value in Start().
    // If that Start() ran while a P2 context was active, the getter resolved to
    // player2.Inventory (which MaintainP2InventoryField points at the P2 view
    // clone), so the P1 panel forever showed P2's counts. Pin P1's own crafting
    // cost collection to P1's real inventory once, at the exact caching moment —
    // no per-frame override, and P2's own clone is left to its routing patch.
    [HarmonyPatch(typeof(CostCollection), "Start")]
    static class Patch_CostCollection_Start_P1Pin
    {
        static readonly FieldInfo inventoryField =
            typeof(CostCollection).GetField("inventory", BindingFlags.Instance | BindingFlags.NonPublic);

        static void Postfix(CostCollection __instance)
        {
            if (__instance == null || Main.IsP2CraftingCostCollection(__instance)) return;
            if (Main.player1 == null || Main.player1.Inventory == null) return;
            if (__instance.GetComponentInParent<SelectedRecipeBox>() == null
                && __instance.GetComponentInParent<CraftingMenu>() == null) return;
            inventoryField?.SetValue(__instance, Main.player1.Inventory);
        }
    }

    [HarmonyPatch(typeof(CostCollection), "ShowCost", new[] { typeof(Cost) })]
    static class Patch_CostCollection_ShowCost_Cost_P2
    {
        static void Prefix(CostCollection __instance) => Patch_CostCollection_P2CraftInventory.Route(__instance);
    }

    [HarmonyPatch(typeof(CostCollection), "ShowCost", new[] { typeof(System.Collections.Generic.List<Cost>) })]
    static class Patch_CostCollection_ShowCost_List_P2
    {
        static void Prefix(CostCollection __instance) => Patch_CostCollection_P2CraftInventory.Route(__instance);
    }

    [HarmonyPatch(typeof(CostCollection), "ShowCost", new[] { typeof(CostMultiple[]) })]
    static class Patch_CostCollection_ShowCost_Multiple_P2
    {
        static void Prefix(CostCollection __instance, CostMultiple[] cost)
        {
            Patch_CostCollection_P2CraftInventory.Route(__instance);
        }
    }

    [HarmonyPatch(typeof(Inventory), "GetItemCount", new[] { typeof(string) })]
    static class Patch_Inventory_GetItemCount_P2CraftHotbar_String
    {
        static void Postfix(Inventory __instance, string uniqueItemName, ref int __result)
        {
            if (!Main.IsActiveP2CraftInventory(__instance)) return;
            __result += Main.CountP2CraftHotbarItem(uniqueItemName);
        }
    }

    [HarmonyPatch(typeof(Inventory), "GetItemCount", new[] { typeof(Item_Base) })]
    static class Patch_Inventory_GetItemCount_P2CraftHotbar_Item
    {
        static void Postfix(Inventory __instance, Item_Base item, ref int __result)
        {
            if (!Main.IsActiveP2CraftInventory(__instance) || item == null) return;
            __result += Main.CountP2HotbarItem(item.UniqueIndex);
        }
    }

    [HarmonyPatch]
    static class Patch_PlayerInventory_AddItem_P2Capture
    {
        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerInventory), "AddItem", new[] { typeof(string), typeof(int) });
            yield return AccessTools.Method(typeof(PlayerInventory), "AddItem", new[] { typeof(string), typeof(Slot), typeof(int) });
            yield return AccessTools.Method(typeof(PlayerInventory), "AddItem", new[] { typeof(ItemInstance), typeof(bool) });
        }
        static void Postfix(PlayerInventory __instance)
        {
            if ((object)__instance != null && ReferenceEquals(__instance, Main.P2ViewOrNull))
                P2InventoryStore.CaptureFrom(__instance);
        }
    }
}
