using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    public static partial class Main
    {
        
        
        
        //
        
        
        
        
        
        
        static CraftingMenu _p2CraftMenu;
        static GameObject _p2CraftMenuGo;
        static GameObject _p2SelectedRecipeBoxGo;
        static int          _p2CraftCatIdx = 2;          
        static RecipeMenuSubItem _p2HoverSub;            
        static SkinMenuItem      _p2HoverSkin;           
        static BuildingUI_Costbox_Sub_Crafting _p2HoverCraftCost;
        static readonly FieldInfo P2QuickCraftButtonField =
            typeof(BuildingUI_Costbox_Sub_Crafting).GetField("quickCraftButton", BindingFlags.Instance | BindingFlags.NonPublic);
        const float P2CraftScrollSpeed = 1.5f;
        
        const float P2MagnetRadiusPx = 90f;              
        const float P2MagnetPullSpeed = 450f;            
        const float P2MagnetEscape = 2000f;              
        const float P2MagnetCenterThr = 8f;              

        internal static bool IsP2CraftingOpen => _p2CraftMenu != null && _p2CraftMenu.gameObject.activeInHierarchy;
        internal static bool IsP2CraftingMenu(CraftingMenu menu) => menu != null && _p2CraftMenu != null && menu == _p2CraftMenu;
        internal static bool IsP2SelectedRecipeBox(SelectedRecipeBox box) =>
            box != null && _p2CraftMenu != null && _p2CraftMenu.selectedRecipeBox == box;
        internal static bool IsP2CraftingCostCollection(CostCollection costs) =>
            costs != null && (( _p2CraftMenu != null && costs.transform.IsChildOf(_p2CraftMenu.transform))
                || (_p2SelectedRecipeBoxGo != null && costs.transform.IsChildOf(_p2SelectedRecipeBoxGo.transform)));
        internal static bool IsP2CraftingCostSub(BuildingUI_Costbox_Sub_Crafting sub) =>
            sub != null && (( _p2CraftMenu != null && sub.transform.IsChildOf(_p2CraftMenu.transform))
                || (_p2SelectedRecipeBoxGo != null && sub.transform.IsChildOf(_p2SelectedRecipeBoxGo.transform)));
        internal static bool IsP2CraftingUiComponent(Component component) =>
            component != null && (( _p2CraftMenu != null && component.transform.IsChildOf(_p2CraftMenu.transform))
                || (_p2SelectedRecipeBoxGo != null && component.transform.IsChildOf(_p2SelectedRecipeBoxGo.transform)));
        internal static bool IsP2HudClone(Component component) =>
            component != null && _p2HudCanvas != null && component.transform.IsChildOf(_p2HudCanvas.transform);
        internal static PlayerInventory ActiveP2CraftPlayerInventory() => ActiveP2BackpackInventory();

        internal static bool IsActiveP2CraftInventory(Inventory inventory)
        {
            if (!IsP2CraftingOpen || inventory == null) return false;
            var active = ActiveP2CraftPlayerInventory();
            return active != null && ReferenceEquals(inventory, active);
        }

        internal static int CountP2CraftHotbarItem(string uniqueName)
        {
            var item = ItemManager.GetItemByName(uniqueName);
            return item != null ? CountP2HotbarItem(item.UniqueIndex) : 0;
        }

        static int CountInventorySlotsRaw(Inventory inventory, string uniqueName)
        {
            if (inventory == null || inventory.allSlots == null) return 0;
            var item = ItemManager.GetItemByName(uniqueName);
            if (item == null) return 0;
            int total = 0;
            foreach (var slot in inventory.allSlots)
            {
                var inst = slot != null && !slot.IsEmpty ? slot.itemInstance : null;
                if (inst != null && inst.UniqueIndex == item.UniqueIndex)
                    total += inst.Amount;
            }
            return total;
        }

        internal static void RemoveP2CraftCostIncludingHotbar(CostMultiple[] costs, PlayerInventory inventory)
        {
            if (costs == null || inventory == null) return;

            foreach (var cost in costs)
            {
                if (cost == null || cost.items == null) continue;
                int remaining = cost.amount;

                foreach (var item in cost.items)
                {
                    if (item == null || remaining <= 0) continue;
                    string uniqueName = item.UniqueName;

                    int fromPrimary = Mathf.Min(remaining, CountInventorySlotsRaw(inventory, uniqueName));
                    if (fromPrimary > 0)
                    {
                        inventory.RemoveItem(uniqueName, fromPrimary);
                        remaining -= fromPrimary;
                    }

                    var second = inventory.secondInventory;
                    int fromSecond = Mathf.Min(remaining, CountInventorySlotsRaw(second, uniqueName));
                    if (fromSecond > 0)
                    {
                        second.RemoveItem(uniqueName, fromSecond);
                        remaining -= fromSecond;
                    }

                    int fromHotbar = Mathf.Min(remaining, CountP2HotbarItem(item.UniqueIndex));
                    if (fromHotbar > 0)
                    {
                        RemoveP2HotbarItem(item.UniqueIndex, fromHotbar);
                        remaining -= fromHotbar;
                    }

                    if (remaining <= 0) break;
                }
            }

            UpdateP2Hotbar();
            RefreshP2HeldItem();
        }

        static bool OpenP2CraftingClone()
        {
            var sourceConsole = ComponentManager<CraftingMenu>.ValueConsole;
            var sourcePc = ComponentManager<CraftingMenu>.Value;
            var source = sourceConsole ?? sourcePc;
            if (source == null || _p2HudCanvas == null) return false;

            if (_p2CraftMenu == null)
            {
                // Snapshot only after the host's vanilla world-mode setup has
                // populated the shared recipe state (creative learns all here).
                RestoreP1CraftingForCurrentWorld();
                var learnedSnapshot = SnapshotRecipeLearned();
                bool wasActive = source.gameObject.activeSelf;
                source.gameObject.SetActive(false);
                _p2CraftMenuGo = UnityEngine.Object.Instantiate(source.gameObject, _p2HudCanvas.transform, false);
                source.gameObject.SetActive(wasActive);
                _p2CraftMenu = _p2CraftMenuGo.GetComponent<CraftingMenu>();
                var sourceRecipeBox = source.selectedRecipeBox;
                if (_p2CraftMenu == null || sourceRecipeBox == null)
                {
                    if (_p2CraftMenuGo != null) UnityEngine.Object.Destroy(_p2CraftMenuGo);
                    _p2CraftMenuGo = null;
                    _p2CraftMenu = null;
                    return false;
                }

                // Parent under the P2 crafting menu (not the raw HUD canvas) and
                // copy P1's recipe-box RectTransform so it lands at the same spot
                // relative to the menu as P1's panel does (right of the category
                // list, top-aligned) instead of floating off in a screen corner.
                _p2SelectedRecipeBoxGo = UnityEngine.Object.Instantiate(sourceRecipeBox.gameObject, _p2CraftMenu.transform, false);
                _p2SelectedRecipeBoxGo.name = "P2_SelectedRecipeBox";
                var srcBoxRt = sourceRecipeBox.transform as RectTransform;
                var dstBoxRt = _p2SelectedRecipeBoxGo.transform as RectTransform;
                if (srcBoxRt != null && dstBoxRt != null)
                {
                    dstBoxRt.anchorMin = srcBoxRt.anchorMin;
                    dstBoxRt.anchorMax = srcBoxRt.anchorMax;
                    dstBoxRt.pivot = srcBoxRt.pivot;
                    dstBoxRt.anchoredPosition = srcBoxRt.anchoredPosition;
                    dstBoxRt.sizeDelta = srcBoxRt.sizeDelta;
                    dstBoxRt.localScale = srcBoxRt.localScale;
                }
                int p2UiLayer = LayerMask.NameToLayer("UI");
                if (p2UiLayer >= 0) SetLayerRecursively(_p2SelectedRecipeBoxGo.transform, p2UiLayer);
                var p2RecipeBox = _p2SelectedRecipeBoxGo.GetComponent<SelectedRecipeBox>();
                if (p2RecipeBox == null)
                {
                    UnityEngine.Object.Destroy(_p2SelectedRecipeBoxGo);
                    UnityEngine.Object.Destroy(_p2CraftMenuGo);
                    _p2SelectedRecipeBoxGo = null;
                    _p2CraftMenuGo = null;
                    _p2CraftMenu = null;
                    return false;
                }
                // The details panel shares the P2 HUD canvas with the opaque
                // backpack panel and otherwise draws behind it (SetAsLastSibling
                // is undone by per-frame sibling churn -> panel selects fine but
                // stays invisible). Give it its own override-sorting canvas so it
                // renders above the backpack, yet below the virtual cursor / held
                // icon / hints (29000-30001).
                var p2RecipeCanvas = _p2SelectedRecipeBoxGo.GetComponent<Canvas>();
                if (p2RecipeCanvas == null) p2RecipeCanvas = _p2SelectedRecipeBoxGo.AddComponent<Canvas>();
                p2RecipeCanvas.overrideSorting = true;
                p2RecipeCanvas.sortingOrder = 28000;
                p2RecipeBox.gameObject.SetActive(false);
                _p2CraftMenu.selectedRecipeBox = p2RecipeBox;
                // These components cache a single global PlayerInventory in the vanilla
                // implementation. P2 drives their visuals and action through its own
                // controller, so keep their lifecycle out of the P1-only static cache.
                DisableP2CraftingQuickCraftComponents();
                // An inactive source defers CraftingMenu.Awake until the clone is
                // first enabled. Run that destructive initialization now, before
                // restoring the shared P1 recipe snapshot.
                _p2CraftMenuGo.SetActive(true);
                _p2CraftMenuGo.name = "P2_CraftingMenu";
                SanitizeClonedUiRoot(_p2CraftMenuGo);
                RemoveP2CraftingOrphanRecipeItems();
                DisableP2CraftingQuickCraftComponents();
                ComponentManager<CraftingMenu>.ValueConsole = sourceConsole;
                ComponentManager<CraftingMenu>.Value = sourcePc;
                source.gameObject.SetActive(wasActive);
                RestoreRecipeLearned(learnedSnapshot);
                RestoreP1CraftingForCurrentWorld();
            }
            if (_p2CraftMenu == null) return false;

            var t = _p2CraftMenu.transform as RectTransform;
            if (t == null) return false;
            if (t.parent != _p2HudCanvas.transform) t.SetParent(_p2HudCanvas.transform, false);
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) SetLayerRecursively(t, uiLayer);
            _p2CraftMenu.gameObject.SetActive(true);
            SyncP2CreativeCategoryVisibility();
            SafeSelectCategory();
            LogV($"[P2Craft] opened cloned crafting menu (creative={IsCreativeMode()}, catIdx={_p2CraftCatIdx})");
            return true;
        }

        internal static void OpenP2Crafting()
        {
            if (OpenP2CraftingClone()) return;
            ModEntry.Logger.Log("[P2Craft] clone open failed");
        }

        internal static void CloseP2Crafting()
        {
            if (_p2CraftMenu == null) return;
            if (_p2CraftMenuGo != null && _p2CraftMenu.gameObject == _p2CraftMenuGo)
            {
                _p2CraftMenu.gameObject.SetActive(false);
                if (_p2SelectedRecipeBoxGo != null) _p2SelectedRecipeBoxGo.SetActive(false);
                _p2HoverSub = null;
                _p2HoverSkin = null;
                ClearP2CraftCostHover();
                return;
            }
            _p2CraftMenu = null; _p2HoverSub = null; _p2HoverSkin = null;
            ClearP2CraftCostHover();
        }

        static void SafeSelectCategory()
        {
            if (_p2CraftMenu == null || _p2CraftMenu.uiOrder == null || _p2CraftMenu.uiOrder.Length == 0) return;
            SyncP2CreativeCategoryVisibility();
            if (_p2CraftCatIdx < 0 || _p2CraftCatIdx >= _p2CraftMenu.uiOrder.Length || !P2CraftCategoryVisible(_p2CraftCatIdx))
                _p2CraftCatIdx = FirstVisibleP2CraftCategory();
            try { _p2CraftMenu.SelectCraftingCategory(_p2CraftMenu.uiOrder[_p2CraftCatIdx]); }
            catch (Exception e) { ModEntry.Logger.Log("[P2Craft] SelectCategory ex: " + e.Message); }
        }

        static void RemoveP2CraftingOrphanRecipeItems()
        {
            if (_p2CraftMenu == null) return;

            var field = typeof(CraftingMenu).GetField(
                "recipeMenuItems",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var managed = field != null
                ? field.GetValue(_p2CraftMenu) as System.Collections.Generic.List<RecipeMenuItem>
                : null;
            if (managed == null) return;

            var keep = new System.Collections.Generic.HashSet<RecipeMenuItem>(managed);
            foreach (var item in _p2CraftMenu.GetComponentsInChildren<RecipeMenuItem>(true))
            {
                if (item == null || keep.Contains(item)) continue;
                item.gameObject.SetActive(false);
                UnityEngine.Object.DestroyImmediate(item.gameObject);
            }
        }

        static void P2CraftCycleCategory(int dir)
        {
            if (_p2CraftMenu == null) return;
            SyncP2CreativeCategoryVisibility();
            int count = _p2CraftMenu.uiOrder != null ? _p2CraftMenu.uiOrder.Length : 0;
            if (count <= 0) return;
            for (int i = 0; i < count; i++)
            {
                _p2CraftCatIdx = ((_p2CraftCatIdx + dir) % count + count) % count;
                if (P2CraftCategoryVisible(_p2CraftCatIdx))
                {
                    SafeSelectCategory();
                    ComponentManager<SoundManager>.Value?.PlayUI_OpenMenu();
                    return;
                }
            }
        }

        internal static void ReapplyP2CraftCategoryAfterStart(CraftingMenu menu)
        {
            if (_p2CraftMenu == null || menu != _p2CraftMenu) return;
            SafeSelectCategory();
            LogV($"[P2Craft] Start 覆盖后重设分类 (creative={IsCreativeMode()}, catIdx={_p2CraftCatIdx})");
        }

        static bool IsCreativeMode()
        {
            try { return GameModeValueManager.GetCurrentGameModeValue().playerSpecificVariables.unlimitedResources; }
            catch { return false; }
        }

        // CraftingMenu.Awake resets the shared ItemInstance_Recipe.Learned flags.
        // A P2 UI clone must therefore restore the exact world-mode initialization
        // that the vanilla research table performs for P1 after a world is loaded.
        internal static void RestoreP1CraftingForCurrentWorld()
        {
            var values = GameModeValueManager.GetCurrentGameModeValue();
            if (values == null) return;

            if (values.playerSpecificVariables.learnAllRecipiesAtStart)
            {
                var research = ComponentManager<Inventory_ResearchTable>.Value;
                research?.LearnAllRecipesInstantly();
                RestoreCreativeRecipeFlagsFromVanillaCatalog();
            }

            bool showCreativeCategory = values.playerSpecificVariables.unlimitedResources;
            RestoreP1CreativeCategory(ComponentManager<CraftingMenu>.Value, showCreativeCategory);
            RestoreP1CreativeCategory(ComponentManager<CraftingMenu>.ValueConsole, showCreativeCategory);
        }

        static void RestoreCreativeRecipeFlagsFromVanillaCatalog()
        {
            var recipes = new System.Collections.Generic.HashSet<ItemInstance_Recipe>();
            AddVanillaMenuRecipes(ComponentManager<CraftingMenu>.Value, recipes);
            AddVanillaMenuRecipes(ComponentManager<CraftingMenu>.ValueConsole, recipes);

            // The normal and console menus are both absent only during an early
            // loading edge case. ItemManager is then the same catalog that the
            // vanilla CraftingMenu will initialize from on its next Awake.
            if (recipes.Count == 0)
            {
                var items = ItemManager.GetAllItems();
                if (items != null)
                    foreach (var item in items)
                        if (item != null && item.settings_recipe != null && item.settings_recipe.CraftingCategory != CraftingCategory.Nothing)
                            recipes.Add(item.settings_recipe);
            }

            foreach (var recipe in recipes)
                if (recipe != null) recipe.Learned = true;
        }

        static void AddVanillaMenuRecipes(CraftingMenu menu, System.Collections.Generic.HashSet<ItemInstance_Recipe> recipes)
        {
            if (menu == null || menu == _p2CraftMenu || recipes == null) return;
            var items = menu.AllRecipes;
            if (items == null) return;
            foreach (var item in items)
                if (item != null && item.settings_recipe != null)
                    recipes.Add(item.settings_recipe);
        }

        static void RestoreP1CreativeCategory(CraftingMenu menu, bool showCreativeCategory)
        {
            if (menu == null || menu == _p2CraftMenu || !showCreativeCategory || menu.creativeModeCategoryButton == null)
                return;

            menu.creativeModeCategoryButton.SetActive(true);
            if (menu.gameObject.activeInHierarchy)
                menu.ReselectCategory();
        }

        static System.Collections.Generic.Dictionary<ItemInstance_Recipe, bool> SnapshotRecipeLearned()
        {
            var snap = new System.Collections.Generic.Dictionary<ItemInstance_Recipe, bool>();
            var items = ItemManager.GetAllItems();
            if (items != null)
                foreach (var it in items)
                    if (it != null && it.settings_recipe != null) snap[it.settings_recipe] = it.settings_recipe.Learned;
            return snap;
        }

        static void RestoreRecipeLearned(System.Collections.Generic.Dictionary<ItemInstance_Recipe, bool> snap)
        {
            if (snap == null) return;
            foreach (var kv in snap) if (kv.Key != null) kv.Key.Learned = kv.Value;
        }

        static int P2CreativeCategoryIndex()
        {
            var order = _p2CraftMenu != null ? _p2CraftMenu.uiOrder : null;
            if (order == null) return -1;
            for (int i = 0; i < order.Length; i++)
                if (order[i] == CraftingCategory.CreativeMode) return i;
            return -1;
        }

        static void SyncP2CreativeCategoryVisibility()
        {
            if (_p2CraftMenu == null || _p2CraftMenu.creativeModeCategoryButton == null) return;
            bool creative = IsCreativeMode();
            if (_p2CraftMenu.creativeModeCategoryButton.activeSelf != creative)
                _p2CraftMenu.creativeModeCategoryButton.SetActive(creative);
        }

        static bool P2CraftCategoryVisible(int index)
        {
            if (_p2CraftMenu == null || _p2CraftMenu.uiOrder == null || index < 0 || index >= _p2CraftMenu.uiOrder.Length)
                return false;
            if (_p2CraftMenu.uiOrder[index] == CraftingCategory.CreativeMode)
                return _p2CraftMenu.creativeModeCategoryButton != null && _p2CraftMenu.creativeModeCategoryButton.activeSelf;
            return true;
        }

        static int FirstVisibleP2CraftCategory()
        {
            if (_p2CraftMenu == null || _p2CraftMenu.uiOrder == null) return 0;
            for (int i = 0; i < _p2CraftMenu.uiOrder.Length; i++)
                if (P2CraftCategoryVisible(i)) return i;
            return 0;
        }

        
        // Pin the details panel flush to the right edge of the crafting menu's
        // list panel, top-aligned — matching gamepad-mode layout ("紧挨着右面").
        // Corner-based so it is independent of anchors/pivot/canvas scale.
        static void PositionP2RecipeBox()
        {
            if (_p2SelectedRecipeBoxGo == null || !_p2SelectedRecipeBoxGo.activeInHierarchy || _p2CraftMenu == null) return;
            var boxRt = _p2SelectedRecipeBoxGo.transform as RectTransform;
            // Align to the whole crafting-menu panel (header + list), not just the
            // scroll list: the panel is the parent of the scroll view rect.
            RectTransform menuRt = null;
            if (_p2CraftMenu.scrollViewRect != null && _p2CraftMenu.scrollViewRect.parent is RectTransform panel)
                menuRt = panel;
            else if (_p2CraftMenu.scrollViewRect != null)
                menuRt = _p2CraftMenu.scrollViewRect;
            else
                menuRt = _p2CraftMenu.transform as RectTransform;
            if (boxRt == null || menuRt == null) return;

            var mc = new Vector3[4]; menuRt.GetWorldCorners(mc);   // 0=BL 1=TL 2=TR 3=BR
            var bc = new Vector3[4]; boxRt.GetWorldCorners(bc);
            // box top-left corner -> panel top-right corner (adjacent + top aligned)
            boxRt.position += (mc[2] - bc[1]);
        }

        // The details panel is a clone of P1's SelectedRecipeBox and inherits
        // P1's full-screen anchor, which lands it just past the right edge of the
        // half-width P2 viewport (selects fine, renders fine, but off-screen).
        // Nudge its anchoredPosition each frame so it stays fully inside P2's
        // camera rect. Resolution-independent; touches only the P2 clone.
        static void ClampP2RecipeBoxIntoView()
        {
            if (_p2SelectedRecipeBoxGo == null || !_p2SelectedRecipeBoxGo.activeInHierarchy) return;
            var rt = _p2SelectedRecipeBoxGo.transform as RectTransform;
            if (rt == null || _p2UiCamera == null) return;

            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Vector2 bl = RectTransformUtility.WorldToScreenPoint(_p2UiCamera, corners[0]);
            Vector2 tr = RectTransformUtility.WorldToScreenPoint(_p2UiCamera, corners[2]);
            float minX = Mathf.Min(bl.x, tr.x), maxX = Mathf.Max(bl.x, tr.x);
            float minY = Mathf.Min(bl.y, tr.y), maxY = Mathf.Max(bl.y, tr.y);

            var vp = _p2UiCamera.pixelRect;
            const float margin = 10f;
            float dx = 0f, dy = 0f;
            if (maxX > vp.xMax - margin) dx -= maxX - (vp.xMax - margin);
            if (minX < vp.xMin + margin) dx += (vp.xMin + margin) - minX;
            if (maxY > vp.yMax - margin) dy -= maxY - (vp.yMax - margin);
            if (minY < vp.yMin + margin) dy += (vp.yMin + margin) - minY;
            if (Mathf.Abs(dx) < 0.5f && Mathf.Abs(dy) < 0.5f) return;

            float scale = (_p2HudCanvas != null && _p2HudCanvas.scaleFactor > 0f) ? _p2HudCanvas.scaleFactor : 1f;
            rt.anchoredPosition += new Vector2(dx, dy) / scale;
        }

        static void TickP2Crafting(Gamepad gp, Vector2 cursorScreen)
        {
            if (_p2CraftMenu == null) return;
            DisableP2CraftingQuickCraftComponents();
            PositionP2RecipeBox();
            ClampP2RecipeBoxIntoView();

            if (gp.leftShoulder.wasPressedThisFrame)       P2CraftCycleCategory(-1);
            else if (gp.rightShoulder.wasPressedThisFrame) P2CraftCycleCategory(1);

            float ry = gp.rightStick.ReadValue().y;
            var sr = _p2CraftMenu.scrollRect;
            if (sr != null && Mathf.Abs(ry) > 0.15f)
                sr.verticalNormalizedPosition = Mathf.Clamp01(sr.verticalNormalizedPosition + ry * P2CraftScrollSpeed * Time.unscaledDeltaTime);

            
            
            _p2HoverSub = null;
            var parent = _p2CraftMenu.recipeMenuItemParent;
            var pr = _p2InvCursor.parent as RectTransform;
            var viewportRt = _p2CraftMenu.scrollRect != null
                ? (_p2CraftMenu.scrollRect.viewport != null ? _p2CraftMenu.scrollRect.viewport : _p2CraftMenu.scrollRect.transform as RectTransform)
                : null;
            if (parent != null && pr != null)
            {
                float scale = (_p2HudCanvas != null && _p2HudCanvas.scaleFactor > 0f) ? _p2HudCanvas.scaleFactor : 1f;
                float radius = P2MagnetRadiusPx * scale;
                RecipeMenuSubItem hit = null, nearest = null;
                float bestSq = radius * radius; Vector2 bestCenter = default, bestDiff = default;
                foreach (var sub in parent.GetComponentsInChildren<RecipeMenuSubItem>(false))
                {
                    if (sub.recipeItem == null) continue;
                    var rt = sub.transform as RectTransform;
                    if (rt == null) continue;
                    
                    var iconRt = (sub.item_bg != null) ? sub.item_bg.rectTransform : rt;
                    Vector2 c = RectTransformUtility.WorldToScreenPoint(_p2UiCamera, iconRt.TransformPoint(iconRt.rect.center));
                    if (viewportRt != null && !RectTransformUtility.RectangleContainsScreenPoint(viewportRt, c, _p2UiCamera)) continue;
                    if (RectTransformUtility.RectangleContainsScreenPoint(rt, cursorScreen, _p2UiCamera))
                    { hit = sub; bestCenter = c; bestDiff = c - cursorScreen; break; }
                    Vector2 diff = c - cursorScreen; float d = diff.sqrMagnitude;
                    if (d < bestSq) { bestSq = d; nearest = sub; bestCenter = c; bestDiff = diff; }
                }
                var chosen = hit ?? nearest;
                if (chosen != null)
                {
                    _p2HoverSub = chosen;                            
                    Vector2 stick = gp.leftStick.ReadValue();
                    float escape = P2MagnetEscape * scale;
                    if (bestDiff.sqrMagnitude >= (stick * escape).sqrMagnitude)   
                    {
                        float thr = P2MagnetCenterThr * scale;
                        Vector2 target = (bestDiff.sqrMagnitude > thr * thr)
                            ? cursorScreen + bestDiff.normalized * (P2MagnetPullSpeed * scale) * Time.unscaledDeltaTime
                            : bestCenter;
                        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(pr, target, _p2UiCamera, out var local))
                        { _p2InvCursorPos = local; _p2InvCursor.anchoredPosition = local; }
                    }
                }
            }

            
            _p2HoverSkin = null;
            var box = _p2CraftMenu.selectedRecipeBox;
            if (box != null && box.gameObject.activeInHierarchy)
                foreach (var sk in box.GetComponentsInChildren<SkinMenuItem>(false))
                {
                    var rt = sk.transform as RectTransform;
                    if (rt == null || !RectTransformUtility.RectangleContainsScreenPoint(rt, cursorScreen, _p2UiCamera)) continue;
                    _p2HoverSkin = sk;
                    Vector2 c = RectTransformUtility.WorldToScreenPoint(_p2UiCamera, rt.TransformPoint(rt.rect.center));
                    if (pr != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(pr, c, _p2UiCamera, out var sl))
                    { _p2InvCursorPos = sl; _p2InvCursor.anchoredPosition = sl; }
                    break;
                }

            var hoveredCost = FindP2CraftCostUnderCursor(box, cursorScreen);
            SetP2CraftCostHover(hoveredCost);
        }

        internal static void DisableP2CraftingQuickCraftComponents()
        {
            if (_p2CraftMenu == null) return;
            foreach (var box in _p2CraftMenu.GetComponentsInChildren<BuildingUI_CostBox_CraftingCost>(true))
                if (box != null) box.enabled = false;
            foreach (var sub in _p2CraftMenu.GetComponentsInChildren<BuildingUI_Costbox_Sub_Crafting>(true))
                if (sub != null) sub.enabled = false;
            if (_p2SelectedRecipeBoxGo == null) return;
            foreach (var box in _p2SelectedRecipeBoxGo.GetComponentsInChildren<BuildingUI_CostBox_CraftingCost>(true))
                if (box != null) box.enabled = false;
            foreach (var sub in _p2SelectedRecipeBoxGo.GetComponentsInChildren<BuildingUI_Costbox_Sub_Crafting>(true))
                if (sub != null) sub.enabled = false;
        }

        static BuildingUI_Costbox_Sub_Crafting FindP2CraftCostUnderCursor(SelectedRecipeBox box, Vector2 cursorScreen)
        {
            if (box == null || !box.gameObject.activeInHierarchy) return null;
            foreach (var cost in box.GetComponentsInChildren<BuildingUI_Costbox_Sub_Crafting>(false))
            {
                if (cost == null || cost.item == null || !cost.gameObject.activeInHierarchy) continue;
                var rect = cost.transform as RectTransform;
                if (rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, cursorScreen, _p2UiCamera))
                    return cost;
            }
            return null;
        }

        static void ClearP2CraftCostHover()
        {
            SetP2CraftCostHover(null);
        }

        static void SetP2CraftCostHover(BuildingUI_Costbox_Sub_Crafting hovered)
        {
            if (ReferenceEquals(hovered, _p2HoverCraftCost)) return;
            SetP2QuickCraftButtonVisible(_p2HoverCraftCost, false);
            _p2HoverCraftCost = hovered;
            SetP2QuickCraftButtonVisible(_p2HoverCraftCost, true);
        }

        static void SetP2QuickCraftButtonVisible(BuildingUI_Costbox_Sub_Crafting costSlot, bool visible)
        {
            if (costSlot == null) return;
            var button = P2QuickCraftButtonField?.GetValue(costSlot) as Button;
            if (button == null) return;
            button.gameObject.SetActive(visible);
            if (visible) button.interactable = CanP2QuickCraft(costSlot.item, ActiveP2CraftPlayerInventory());
        }

        
        static bool P2CraftTrySelectSkin()
        {
            if (_p2HoverSkin == null || _p2CraftMenu == null || _p2CraftMenu.selectedRecipeBox == null) return false;
            try
            {
                _p2CraftMenu.selectedRecipeBox.DisplaySkin(_p2HoverSkin.skinItem);
                _p2HoverSkin.SetAsActiveHighlight();
            }
            catch (Exception e) { ModEntry.Logger.Log("[P2Craft] skin ex: " + e.Message); }
            return true;
        }

        static bool P2CraftTryQuickCraftHovered()
        {
            var costSlot = _p2HoverCraftCost;
            var item = costSlot != null ? costSlot.item : null;
            var inventory = ActiveP2CraftPlayerInventory();
            if (!CanP2QuickCraft(item, inventory)) return false;
            bool unlimitedResources = GameModeValueManager.GetCurrentGameModeValue().playerSpecificVariables.unlimitedResources;

            if (!unlimitedResources)
                RemoveP2CraftCostIncludingHotbar(item.settings_recipe.NewCost, inventory);
            // 同 P2CraftTryCraft:提示要走 P2 分屏的路由(见那里的注释)。
            bool prevRoutingQ = P2InventoryStore.RoutingPickup;
            P2InventoryStore.RoutingPickup = true;
            try { inventory.AddItem(item.UniqueName, item.settings_recipe.AmountToCraft); }
            finally { P2InventoryStore.RoutingPickup = prevRoutingQ; }
            RefreshRealSlots(inventory);
            P2InventoryStore.CaptureFrom(inventory);
            return true;
        }

        static bool CanP2QuickCraft(Item_Base item, PlayerInventory inventory)
        {
            if (item == null || inventory == null || item.settings_recipe == null) return false;
            if (!item.settings_recipe.CanCraft || item.settings_recipe.NewCost == null || item.settings_recipe.NewCost.Length == 0)
                return false;
            return GameModeValueManager.GetCurrentGameModeValue().playerSpecificVariables.unlimitedResources ||
                   HasP2CraftCostIncludingHotbar(item.settings_recipe.NewCost, inventory);
        }

        static bool HasP2CraftCostIncludingHotbar(CostMultiple[] costs, PlayerInventory inventory)
        {
            if (costs == null || inventory == null) return false;
            foreach (var cost in costs)
            {
                if (cost == null || cost.items == null) continue;
                int available = 0;
                foreach (var item in cost.items)
                {
                    if (item == null) continue;
                    available += CountInventorySlotsRaw(inventory, item.UniqueName);
                    available += CountP2HotbarItem(item.UniqueIndex);
                }
                if (available < cost.amount) return false;
            }
            return true;
        }

        
        
        static bool P2CraftTrySelectHovered()
        {
            if (_p2CraftMenu == null || _p2HoverSub == null) return false;
            var recipe = _p2HoverSub.recipeItem;
            if (recipe == null) return true;
            try
            {
                var box = _p2CraftMenu.selectedRecipeBox;
                // Re-selecting the same recipe while the panel is hidden must still
                // reopen it; vanilla DisplayRecipe skips activation when unchanged.
                if (box != null)
                {
                    if (!box.gameObject.activeSelf) box.gameObject.SetActive(true);
                    box.selectedRecipeItem = null;
                }
                _p2CraftMenu.SelectRecipe(recipe, true);
            }
            catch (Exception e) { ModEntry.Logger.Log("[P2Craft] select ex: " + e.Message); }
            return true;
        }

        
        static void P2CraftTryCraft()
        {
            if (_p2CraftMenu == null) return;
            var box = _p2CraftMenu.selectedRecipeBox;
            if (box == null || box.craftButton == null || !box.craftButton.interactable) return;
            try
            {
                // 制造出的物品同样要把「+N 物品」提示送到 P2 分屏。该提示走 vanilla 的
                // InventoryPickup.ShowItem,mod 已有路由(Patch_InventoryPickup_ShowItem_P2),
                // 判据是 RoutingPickup / IsP2OriginalActive —— 但 P2 的制造是 mod 直接调菜单,
                // 不在任何 scope 内,两个判据都为假 → 提示落到了 P1 分屏。这里补上路由标志。
                bool prevRouting = P2InventoryStore.RoutingPickup;
                P2InventoryStore.RoutingPickup = true;
                try { _p2CraftMenu.CraftItem(); }
                finally { P2InventoryStore.RoutingPickup = prevRouting; }
                var p2Inv = ActiveP2CraftPlayerInventory();
                if (p2Inv != null)
                {
                    RefreshRealSlots(p2Inv);
                    P2InventoryStore.CaptureFrom(p2Inv);
                }
            }
            catch (Exception e) { ModEntry.Logger.Log("[P2Craft] craft ex: " + e.Message); }
        }
    }
}
