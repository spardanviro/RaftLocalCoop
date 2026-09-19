using System.Collections.Generic;
using System.Reflection;

namespace SplitScreen
{
    
    
    //
    
    
    
    //
    
    
    
    
    
    
    
    public static class P2EquipmentStore
    {
        static readonly Dictionary<EquipSlotType, ItemInstance> _equipped = new Dictionary<EquipSlotType, ItemInstance>();
        static readonly List<ItemInstance> _p1SlotBackup = new List<ItemInstance>();  
        static bool _displaySwapped;
        static FieldInfo _fEquipArray;

        
        public static bool HasEquipped(EquipSlotType type) =>
            type != EquipSlotType.None && _equipped.TryGetValue(type, out var v) && v != null && v.Valid;

        public static ItemInstance GetEquipped(EquipSlotType type) =>
            (_equipped.TryGetValue(type, out var v) && v != null && v.Valid) ? v : null;

        public static EquipSlotType TypeOf(ItemInstance it) =>
            (it != null && it.settings_equipment != null) ? it.settings_equipment.EquipType : EquipSlotType.None;

        
        public static bool IsEquippable(ItemInstance it) => TypeOf(it) != EquipSlotType.None;

        
        
        public static ItemInstance Equip(ItemInstance item)
        {
            var type = TypeOf(item);
            if (type == EquipSlotType.None) return item;   
            _equipped.TryGetValue(type, out var old);
            UnEquipAllOfType(type);
            _equipped[type] = item;
            ModelEquip(item.baseItem);
            return (old != null && old.Valid) ? old : null;
        }

        static void UnEquipAllOfType(EquipSlotType type)
        {
            var p2 = Main.player2; if (p2 == null || p2.PlayerEquipment == null) return;
            var arr = EquipArray(p2.PlayerEquipment);
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                var e = arr[i];
                if (e == null || !e.Equipped || e.equipableItem == null || e.equipableItem.settings_equipment == null) continue;
                if (e.equipableItem.settings_equipment.EquipType == type)
                    p2.PlayerEquipment.UnEquipItemNetwork(i);
            }
        }

        
        public static ItemInstance UnEquip(EquipSlotType type)
        {
            if (!_equipped.TryGetValue(type, out var it) || it == null) return null;
            _equipped.Remove(type);
            ModelUnEquip(it.baseItem);
            return it.Valid ? it : null;
        }

        
        public static List<ItemInstance> EquippedItems()
        {
            var list = new List<ItemInstance>();
            foreach (var kv in _equipped) if (kv.Value != null && kv.Value.Valid) list.Add(kv.Value);
            return list;
        }

        public static void Clear() { _equipped.Clear(); _p1SlotBackup.Clear(); _displaySwapped = false; }

        public static void ResetForFreshWorld(Network_Player p2)
        {
            _equipped.Clear();
            _p1SlotBackup.Clear();
            _displaySwapped = false;

            if (p2 == null || p2.PlayerEquipment == null) return;
            var arr = EquipArray(p2.PlayerEquipment);
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] != null && arr[i].Equipped)
                    p2.PlayerEquipment.UnEquipItemNetwork(i);
        }

        
        
        
        
        public static int P2BackpackExtraSlots()
        {
            var bp = GetEquipped(EquipSlotType.Backpack);
            if (bp == null || bp.baseItem == null) return 0;
            var p2 = Main.player2; if (p2 == null || p2.PlayerEquipment == null) return 0;
            var arr = EquipArray(p2.PlayerEquipment);
            if (arr == null) return 0;
            foreach (var e in arr)
                if (e is Equipment_Backpack eb && e.equipableItem != null
                    && e.equipableItem.UniqueIndex == bp.baseItem.UniqueIndex)
                    return eb.extraBackpackSlots;
            return 0;
        }

        
        public static List<string> EquippedNames()
        {
            var list = new List<string>();
            foreach (var it in EquippedItems()) if (it != null && it.baseItem != null) list.Add(it.baseItem.UniqueName);
            return list;
        }

        
        public static void RestoreFromNames(System.Collections.Generic.IEnumerable<string> names)
        {
            if (names == null) return;
            foreach (var n in names)
            {
                var item = ItemManager.GetItemByName(n);
                if (item != null) Equip(new ItemInstance(item, 1, item.MaxUses));
            }
        }

        
        
        public static void RebuildFromModel(Network_Player p2)
        {
            _equipped.Clear();
            if (p2 == null || p2.PlayerEquipment == null) return;
            var arr = EquipArray(p2.PlayerEquipment);
            if (arr == null) return;
            foreach (var e in arr)
            {
                if (e == null || !e.Equipped || e.equipableItem == null) continue;
                var inst = new ItemInstance(e.equipableItem, 1, e.equipableItem.MaxUses);
                var type = inst.settings_equipment != null ? inst.settings_equipment.EquipType : EquipSlotType.None;
                if (type != EquipSlotType.None) _equipped[type] = inst;
            }
        }

        
        static Equipment[] EquipArray(PlayerEquipment pe)
        {
            if (pe == null) return null;
            if (_fEquipArray == null)
                _fEquipArray = typeof(PlayerEquipment).GetField("equipment", BindingFlags.Instance | BindingFlags.NonPublic);
            return _fEquipArray?.GetValue(pe) as Equipment[];
        }

        static int IndexOf(PlayerEquipment pe, Item_Base item)
        {
            var arr = EquipArray(pe);
            if (arr == null || item == null) return -1;
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] != null && arr[i].equipableItem != null && arr[i].equipableItem.UniqueIndex == item.UniqueIndex) return i;
            return -1;
        }

        static void ModelEquip(Item_Base item)
        {
            var p2 = Main.player2; if (p2 == null || p2.PlayerEquipment == null || item == null) return;
            int idx = IndexOf(p2.PlayerEquipment, item);
            if (idx >= 0) p2.PlayerEquipment.EquipItemNetwork(idx);
        }

        static void ModelUnEquip(Item_Base item)
        {
            var p2 = Main.player2; if (p2 == null || p2.PlayerEquipment == null || item == null) return;
            int idx = IndexOf(p2.PlayerEquipment, item);
            if (idx >= 0) p2.PlayerEquipment.UnEquipItemNetwork(idx);
        }

        
        
        public static void ShowInSlots()
        {
            var inv = ComponentManager<PlayerInventory>.Value;
            ShowInSlots(inv, backupP1: true);
        }

        public static void ShowInSlots(PlayerInventory inv)
        {
            ShowInSlots(inv, backupP1: false);
        }

        static void ShowInSlots(PlayerInventory inv, bool backupP1)
        {
            if (inv?.equipSlots == null) return;
            if (backupP1 && _displaySwapped) return;
            if (backupP1) _p1SlotBackup.Clear();
            var vals = EquippedItems();
            int vi = 0;
            foreach (var s in inv.equipSlots)
            {
                if (backupP1) _p1SlotBackup.Add(s != null ? s.itemInstance : null);
                if (s == null) continue;
                s.itemInstance = (vi < vals.Count) ? vals[vi++] : null;
                s.RefreshComponents();
            }
            _displaySwapped = backupP1;
        }

        
        public static void RestoreP1Slots()
        {
            var inv = ComponentManager<PlayerInventory>.Value;
            if (inv?.equipSlots == null || !_displaySwapped) return;
            for (int i = 0; i < inv.equipSlots.Count && i < _p1SlotBackup.Count; i++)
            {
                var s = inv.equipSlots[i];
                if (s == null) continue;
                s.itemInstance = _p1SlotBackup[i];
                s.RefreshComponents();
            }
            _p1SlotBackup.Clear();
            _displaySwapped = false;
        }
    }
}
