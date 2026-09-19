using System;
using System.Reflection;

namespace SplitScreen
{
    public sealed class P2InventoryGlobalScope : IDisposable
    {
        static readonly FieldInfo PickupInventoryField =
            typeof(Pickup).GetField("playerInventory", BindingFlags.Instance | BindingFlags.NonPublic);
        // 克隆背包/存储 UI(Instantiate 玩家/背包 GO)时,克隆体上的 Network_Player.Awake 会抢注
        // ComponentManager<Network_Player>.Value 的 backing 字段。本 scope 原先只还原 PlayerInventory,
        // 漏了 Network_Player → backing 长期停留在"停用的克隆" → 收音机等 Start 缓存 CM.Value 时抓到它
        // → TurnOffRadio 在 inactive 对象启协程报错。这里连 Network_Player 的 backing 一并保留/还原。
        static readonly FieldInfo NetworkPlayerBackingField =
            typeof(ComponentManager<Network_Player>).GetField("component", BindingFlags.Static | BindingFlags.NonPublic);

        readonly Network_Player _previousNetworkPlayer;
        readonly PlayerInventory _previousSingleton;
        readonly bool _restoreSingleton;
        readonly bool _previousRoutingPickup;
        readonly bool _restoreRoutingPickup;
        readonly Pickup _pickup;
        readonly PlayerInventory _previousPickupInventory;
        readonly bool _restorePickup;
        bool _disposed;

        public P2InventoryGlobalScope(PlayerInventory activeInventory, bool redirectP2Pickup)
            : this(activeInventory, redirectP2Pickup, setSingleton: true)
        {
        }

        public P2InventoryGlobalScope(PlayerInventory activeInventory, bool redirectP2Pickup, bool routePickupToast)
            : this(activeInventory, redirectP2Pickup, setSingleton: true, routePickupToast: routePickupToast)
        {
        }

        P2InventoryGlobalScope(PlayerInventory activeInventory, bool redirectP2Pickup, bool setSingleton, bool routePickupToast = false)
        {
            _previousSingleton = ComponentManager<PlayerInventory>.Value;
            _restoreSingleton = true;
            _previousNetworkPlayer = NetworkPlayerBackingField?.GetValue(null) as Network_Player;

            if (setSingleton && activeInventory != null)
                ComponentManager<PlayerInventory>.Value = activeInventory;

            _previousRoutingPickup = P2InventoryStore.RoutingPickup;
            if (routePickupToast)
            {
                P2InventoryStore.RoutingPickup = true;
                _restoreRoutingPickup = true;
            }

            if (!redirectP2Pickup || activeInventory == null || PickupInventoryField == null)
                return;

            var p2 = Main.player2;
            _pickup = p2 != null ? p2.PickupScript : null;
            if (_pickup == null) return;

            _previousPickupInventory = PickupInventoryField.GetValue(_pickup) as PlayerInventory;
            PickupInventoryField.SetValue(_pickup, activeInventory);
            _restorePickup = true;
        }

        public static P2InventoryGlobalScope PreserveCurrentSingleton()
        {
            return new P2InventoryGlobalScope(null, redirectP2Pickup: false, setSingleton: false);
        }

        public static void RestoreSingletonIfCurrent(PlayerInventory current, PlayerInventory restoreTo)
        {
            if (current != null && ReferenceEquals(ComponentManager<PlayerInventory>.Value, current))
                ComponentManager<PlayerInventory>.Value = restoreTo;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_restorePickup && _pickup != null && PickupInventoryField != null)
                PickupInventoryField.SetValue(_pickup, _previousPickupInventory);

            if (_restoreRoutingPickup)
                P2InventoryStore.RoutingPickup = _previousRoutingPickup;

            if (_restoreSingleton)
                ComponentManager<PlayerInventory>.Value = _previousSingleton;

            // 还原 Network_Player backing:优先真 P1(克隆抢注后必须踢回),否则回退到进入前的值。
            if (NetworkPlayerBackingField != null)
            {
                var restore = (UnityEngine.Object)Main.player1 != null ? Main.player1 : _previousNetworkPlayer;
                NetworkPlayerBackingField.SetValue(null, restore);
            }
        }
    }
}
