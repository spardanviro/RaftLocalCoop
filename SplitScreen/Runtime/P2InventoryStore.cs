using System.Collections.Generic;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    //
    
    
    
    //
    
    
    
    
    public static class P2InventoryStore
    {
        static RGD_Slot[] _p2Slots;     
        static RGD_Slot[] _p1Backup;    
        static bool _swapped;
        static bool _swapToClone;
        static P2InventoryGlobalScope _globalScope;

        public static bool IsSwapped => _swapped;
        
        
        public static bool RoutingPickup;
        
        public static bool VerboseLog;

        
        public static RGD_Slot[] GetP2BackpackSnapshot()
        {
            if (_swapped)
            {
                var src = _swapToClone ? Main.P2ViewOrNull : Cm.Get<PlayerInventory>();
                if (src != null) return SnapshotBackpack(src);
            }
            return _p2Slots;
        }

        
        public static void SetP2Backpack(RGD_Slot[] slots) => _p2Slots = slots;

        public static void CaptureFrom(PlayerInventory inv)
        {
            _p2Slots = SnapshotBackpack(inv);
        }

        public static void RestoreInto(PlayerInventory inv)
        {
            RestoreBackpack(inv, _p2Slots);
        }

        
        
        
        public static int CountBackpackItem(int uniqueIndex)
        {
            int total = 0;
            if (_p2Slots != null)
                foreach (var r in _p2Slots)
                    if (r != null && r.HasItem && r.itemIndex == uniqueIndex)
                        total += r.itemAmount;
            return total;
        }

        
        
        
        
        public static bool TrySwapInP2()
        {
            if (_swapped) return true;
            var clone = Main.EnsureP2View();
            if (clone != null)
            {
                RestoreBackpack(clone, _p2Slots);
                clone.SetBackpackActiveSlots(P2EquipmentStore.P2BackpackExtraSlots());
                _globalScope = new P2InventoryGlobalScope(clone, redirectP2Pickup: true);
                _swapToClone = true;
                _swapped = true;
                return true;
            }
            var inv = Cm.Get<PlayerInventory>();
            if (inv == null) { Main.ModEntry.Logger.Log("[P2Inv] SwapIn 跳过：PlayerInventory 为空"); return false; }
            _p1Backup = SnapshotBackpack(inv);
            RestoreBackpack(inv, _p2Slots);
            _swapToClone = false;
            _swapped = true;
            return true;
        }

        public static void SwapOutP2()
        {
            if (!_swapped) return;
            if (_swapToClone)
            {
                var clone = Main.P2ViewOrNull;
                if (clone != null) _p2Slots = SnapshotBackpack(clone);
                _globalScope?.Dispose();
                _globalScope = null;
                _swapToClone = false;
                _swapped = false;
                return;
            }
            var inv = Cm.Get<PlayerInventory>();
            if (inv == null) { _swapped = false; return; }
            _p2Slots = SnapshotBackpack(inv);
            RestoreBackpack(inv, _p1Backup);
            _p1Backup = null;
            _swapped  = false;
        }

        
        public static void Reset()
        {
            RoutingPickup = false;
            if (_swapped)
            {
                if (_swapToClone)
                {
                    var clone = Main.P2ViewOrNull;
                    if (clone != null) _p2Slots = SnapshotBackpack(clone);
                    _swapToClone = false;
                }
                else
                {
                    var inv = Cm.Get<PlayerInventory>();
                    if (inv != null) RestoreBackpack(inv, _p1Backup);
                }
                _p1Backup = null;
                _globalScope?.Dispose();
                _globalScope = null;
                _swapped = false;
            }
            _globalScope?.Dispose();
            _globalScope = null;
            _p2Slots = null;
        }

        
        
        
        
        static RGD_Slot[] SnapshotBackpack(PlayerInventory inv)
        {
            var slots = inv.allSlots;
            if (slots == null) return null;
            var list = new List<RGD_Slot>();
            int bpIdx = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s == null || s.slotType == SlotType.Hotbar) continue;   
                if (!s.IsEmpty) list.Add(new RGD_Slot(s, bpIdx));
                bpIdx++;
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        static void RestoreBackpack(PlayerInventory inv, RGD_Slot[] snap)
        {
            var slots = inv.allSlots;
            if (slots == null) return;
            int bpIdx = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s == null || s.slotType == SlotType.Hotbar) continue;
                bool found = false;
                if (snap != null)
                    foreach (var r in snap)
                        if (r != null && r.slotIndex == bpIdx) { r.RestoreSlot(s); found = true; break; }
                if (!found) s.SetItem(null);
                bpIdx++;
            }
        }
    }
}
