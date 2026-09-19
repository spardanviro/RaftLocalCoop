using System;
using System.Collections.Generic;
using System.Reflection;

namespace SplitScreen
{
    internal sealed class P2InteractionContext : IDisposable
    {
        struct InstanceLocalPlayerOverride
        {
            internal FieldInfo Field;
            internal object Component;
            internal Network_Player OldValue;
        }

        static readonly Dictionary<Type, FieldInfo> s_instanceLocalPlayerFields = new Dictionary<Type, FieldInfo>();
        static readonly FieldInfo s_cookingStandDisplayTextManagerField =
            typeof(Block_CookingStand).GetField("displayTextManager", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_cookingStandPlayerNetworkField =
            typeof(Block_CookingStand).GetField("playerNetwork", BindingFlags.Instance | BindingFlags.NonPublic);
        // 取水按钮(InteractableButton_Tank)的 OnButtonPressed → Tank.HandleTakeFuel 用的是 Tank 的实例
        // localPlayer 字段。P2 瞄按钮时要连带把背后 Tank 的 localPlayer 也换成 P2,否则取水进 P1。
        static readonly FieldInfo s_tankLocalPlayerField =
            typeof(Tank).GetField("localPlayer", BindingFlags.Instance | BindingFlags.NonPublic);

        readonly Network_Player _p2;
        readonly bool _restoreProcessingRay;
        readonly bool _savedProcessingRay;
        readonly bool _savedBusy;
        readonly P2OriginalScope _originalScope;
        readonly P2InventoryScope _inventoryScope;
        readonly P2StaticLocalPlayerScope _staticLocalPlayerScope;
        readonly List<InstanceLocalPlayerOverride> _instanceLocalPlayerOverrides;
        readonly Slot _p1SelectedSlot;
        readonly ItemInstance _p1SelectedSlotItem;
        readonly string _p2HeldSignature;
        bool _disposed;

        public static P2InteractionContext InventoryOnly()
        {
            return new P2InteractionContext(null, routeRaycastables: false, mirrorHeldHotbar: false);
        }

        public static P2InteractionContext RaycastDevice(IEnumerable<IRaycastable> raycastables)
        {
            return new P2InteractionContext(raycastables, routeRaycastables: true, mirrorHeldHotbar: true);
        }

        P2InteractionContext(IEnumerable<IRaycastable> raycastables, bool routeRaycastables, bool mirrorHeldHotbar)
        {
            _p2 = Main.player2;
            _savedBusy = PlayerItemManager.IsBusy;
            _originalScope = P2OriginalScope.Interaction();
            _inventoryScope = new P2InventoryScope();

            if (routeRaycastables)
            {
                _savedProcessingRay = Main.isProcessingP2Ray;
                Main.isProcessingP2Ray = true;
                _restoreProcessingRay = true;
                EnsureCookingStandRaycastState(raycastables);
                _staticLocalPlayerScope = new P2StaticLocalPlayerScope(_p2, raycastables);
                _instanceLocalPlayerOverrides = OverrideInstanceLocalPlayers(_p2, raycastables);
            }

            if (mirrorHeldHotbar)
            {
                var inv = Cm.Get<PlayerInventory>();
                _p1SelectedSlot = inv != null ? inv.GetSelectedHotbarSlot() : null;
                _p1SelectedSlotItem = _p1SelectedSlot != null ? _p1SelectedSlot.itemInstance : null;
                var p2Held = Main.GetP2HeldHotbarItem();
                _p2HeldSignature = Main.ItemSig(p2Held);
                if (_p1SelectedSlot != null)
                    _p1SelectedSlot.itemInstance = p2Held != null ? p2Held.Clone() : null;
            }
        }

        static List<InstanceLocalPlayerOverride> OverrideInstanceLocalPlayers(Network_Player player, IEnumerable<IRaycastable> raycastables)
        {
            var result = new List<InstanceLocalPlayerOverride>();
            if (player == null || raycastables == null) return result;

            foreach (var obj in raycastables)
            {
                var comp = obj as UnityEngine.Component;
                if (comp == null) continue;

                var field = GetInstanceLocalPlayerField(comp.GetType());
                OverridePlayerField(result, comp, field, player);
                if (comp is Block_CookingStand)
                    OverridePlayerField(result, comp, s_cookingStandPlayerNetworkField, player);
                if (comp is InteractableButton_Tank tankButton && tankButton.tank != null)
                    OverridePlayerField(result, tankButton.tank, s_tankLocalPlayerField, player);
            }

            return result;
        }

        static void EnsureCookingStandRaycastState(IEnumerable<IRaycastable> raycastables)
        {
            if (raycastables == null || s_cookingStandDisplayTextManagerField == null) return;

            var displayTextManager = ComponentManager<DisplayTextManager>.Value;
            if (displayTextManager == null) return;

            foreach (var obj in raycastables)
            {
                var stand = obj as Block_CookingStand;
                if (stand == null) continue;

                if (s_cookingStandDisplayTextManagerField.GetValue(stand) == null)
                    s_cookingStandDisplayTextManagerField.SetValue(stand, displayTextManager);
            }
        }

        static void OverridePlayerField(List<InstanceLocalPlayerOverride> result, object component, FieldInfo field, Network_Player player)
        {
            if (result == null || component == null || field == null || player == null) return;
            if (field.FieldType != typeof(Network_Player)) return;

            var old = field.GetValue(component) as Network_Player;
            if (ReferenceEquals(old, player)) return;

            field.SetValue(component, player);
            result.Add(new InstanceLocalPlayerOverride
            {
                Field = field,
                Component = component,
                OldValue = old
            });
        }

        static FieldInfo GetInstanceLocalPlayerField(Type type)
        {
            if (type == null) return null;
            if (s_instanceLocalPlayerFields.TryGetValue(type, out var cached)) return cached;

            FieldInfo field = null;
            for (var t = type; t != null && t != typeof(UnityEngine.MonoBehaviour); t = t.BaseType)
            {
                field = t.GetField("localPlayer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null) break;
            }

            if (field != null && field.FieldType != typeof(Network_Player))
                field = null;

            // 少数设备(如 PickupChanneling 长按收集死亡动物/蜂巢)把"正在交互的本地玩家"缓存在名为
            // player 的字段而非 localPlayer。找不到 localPlayer 时回退找 player(取 Network_Player 类型),
            // 否则 P2 交互时该字段恒为 Start 缓存的 P1 → 采集进 P1 背包、P1 做采集动画。
            if (field == null)
            {
                for (var t = type; t != null && t != typeof(UnityEngine.MonoBehaviour); t = t.BaseType)
                {
                    var f = t.GetField("player", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null && f.FieldType == typeof(Network_Player)) { field = f; break; }
                }
            }

            s_instanceLocalPlayerFields[type] = field;
            return field;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_p1SelectedSlot != null)
            {
                if (Main.ItemSig(_p1SelectedSlot.itemInstance) != _p2HeldSignature)
                    Main.SetP2HeldHotbarItem(_p1SelectedSlot.itemInstance);
                _p1SelectedSlot.itemInstance = _p1SelectedSlotItem;
                _p1SelectedSlot.RefreshComponents();
            }

            _inventoryScope?.Dispose();
            _staticLocalPlayerScope?.Dispose();

            if (_instanceLocalPlayerOverrides != null)
            {
                for (int i = _instanceLocalPlayerOverrides.Count - 1; i >= 0; i--)
                {
                    var entry = _instanceLocalPlayerOverrides[i];
                    if (entry.Field != null && entry.Component != null)
                    {
                        var restore = entry.OldValue != null ? entry.OldValue : Main.player1;
                        entry.Field.SetValue(entry.Component, restore);
                    }
                }
            }

            _originalScope?.Dispose();

            if (_restoreProcessingRay)
                Main.isProcessingP2Ray = _savedProcessingRay;

            PlayerItemManager.IsBusy = _savedBusy;
        }
    }
}
