using System.Collections.Generic;
using System.Reflection;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  P2EquipmentStore — P2 独立装备(Phase A)
    //
    //  装备有两套表示(见调研):
    //   · PlayerInventory.equipSlots(UI 槽 Slot_Equip,共享单例=P1)→ 驱动 HasEquipmentOfTypeEquipped。
    //   · Network_Player.PlayerEquipment(equipment[] 模型,每玩家独立)→ 角色身上的装备模型。
    //
    //  本 store 让 P2 装备独立、且不污染 P1：
    //   · 功能:_equipped(EquipSlotType→ItemInstance)= P2 已装备集,功能门控(Phase B)读它。
    //   · 模型:p2.PlayerEquipment.EquipItemNetwork(idx)(模型上 P2 身,存档复用 GetEquipedIndexes)。
    //   · UI 显示:P2 开背包时把 _equipped "只改显示"写进单例 equipSlots(直接赋 itemInstance + RefreshComponents,
    //     绕过 Slot_Equip.SetItem→playerEquipment.Equip,故不会把 P2 装备穿到 P1 身上 / 不发网络);关背包还原 P1。
    //  绝不用背包那种逐帧 swap(会触发 Equip → P1 模型狂闪 + 网络刷屏)。
    // ══════════════════════════════════════════════════════════════════════
    public static class P2EquipmentStore
    {
        static readonly Dictionary<EquipSlotType, ItemInstance> _equipped = new Dictionary<EquipSlotType, ItemInstance>();
        static readonly List<ItemInstance> _p1SlotBackup = new List<ItemInstance>();  // 显示换入期间备份 P1 装备槽显示
        static bool _displaySwapped;
        static FieldInfo _fEquipArray;

        // ── 功能查询(Phase B 门控用) ──
        public static bool HasEquipped(EquipSlotType type) =>
            type != EquipSlotType.None && _equipped.TryGetValue(type, out var v) && v != null && v.Valid;

        public static ItemInstance GetEquipped(EquipSlotType type) =>
            (_equipped.TryGetValue(type, out var v) && v != null && v.Valid) ? v : null;

        public static EquipSlotType TypeOf(ItemInstance it) =>
            (it != null && it.settings_equipment != null) ? it.settings_equipment.EquipType : EquipSlotType.None;

        // 是装备件(类型有效;Backpack 也算装备槽,与原版一致)。
        public static bool IsEquippable(ItemInstance it) => TypeOf(it) != EquipSlotType.None;

        // ── 装/卸(模型上 P2 身) ──
        // 装备一件 → 返回被同类型顶下来的旧装备(可空,调用方放回手上/背包)。
        public static ItemInstance Equip(ItemInstance item)
        {
            var type = TypeOf(item);
            if (type == EquipSlotType.None) return item;   // 非装备件:原样返回,调用方不装
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

        // 卸下某类型 → 返回卸下的物品(可空)。
        public static ItemInstance UnEquip(EquipSlotType type)
        {
            if (!_equipped.TryGetValue(type, out var it) || it == null) return null;
            _equipped.Remove(type);
            ModelUnEquip(it.baseItem);
            return it.Valid ? it : null;
        }

        // 已装备物品快照(供存档/UI 顺序显示)。
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

        // P2 当前装备的背包能扩展多少格(无背包→0)。
        //  vanilla Equipment_Backpack.Equip 会 Inventory.SetBackpackActiveSlots(extraBackpackSlots),
        //  但只对 IsLocalPlayer 生效、且 P2 走 P2EquipmentStore.Equip(不跑该 override)→ 必须自己读这个值。
        //  extraBackpackSlots 是 Equipment_Backpack 组件字段;P2 的装备组件在 p2.PlayerEquipment.equipment[] 上。
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

        // 存档用:已装备物品的 UniqueName 列表(功能集=权威,含滑索工具等【非模型装备】,故比 PlayerEquipment.GetEquipedIndexes 全)。
        public static List<string> EquippedNames()
        {
            var list = new List<string>();
            foreach (var it in EquippedItems()) if (it != null && it.baseItem != null) list.Add(it.baseItem.UniqueName);
            return list;
        }

        // 读档用:按 UniqueName 逐件装备(功能集 + 模型;模型装备会经 EquipItemNetwork 上 P2 身)。
        public static void RestoreFromNames(System.Collections.Generic.IEnumerable<string> names)
        {
            if (names == null) return;
            foreach (var n in names)
            {
                var item = ItemManager.GetItemByName(n);
                if (item != null) Equip(new ItemInstance(item, 1, item.MaxUses));
            }
        }

        // 读档后调:从 P2 模型(已 EquipItemNetwork 的装备)重建功能集 _equipped。
        //  否则读档只恢复了模型,_equipped 为空 → 装备栏显示空、HasEquipped 恒假。
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

        // ── 模型(P2 身):EquipItemNetwork/UnEquipItemNetwork by index ──
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

        // ── UI 显示(单例 equipSlots,只改显示不触发 Equip) ──
        // P2 开背包后调:备份 P1 装备槽显示 → 把 P2 已装备件按顺序写进槽(直接赋字段)。
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

        // P2 关背包前调:还原 P1 装备槽显示。
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
