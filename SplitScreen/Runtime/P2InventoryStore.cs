using System.Collections.Generic;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  P2InventoryStore — P2 独立背包数据（时间共享模型，仅背包槽）
    //
    //  Raft 的 PlayerInventory 是全局单例且自带 UI 槽位，无法同时存在两份。
    //  方案：复用那唯一的背包，P2 打开背包时把内容“换”成 P2 的：
    //    SwapInP2()  —— 保存 P1 当前背包槽(RGD 快照) → 载入 P2 的背包槽数据
    //    SwapOutP2() —— 把当前(=P2)背包槽存回 P2 数据 → 恢复 P1 背包槽
    //
    //  关键：只换【背包槽 backpackSlots】，绝不动【热栏 hotbar】。
    //   原因：P1 仍在游戏中，底部热栏必须保持稳定可见；且 P2 自有 LB/RB 热栏系统。
    //   （之前连热栏一起换 → P2 起始物会出现在 P1 热栏位置，造成“幽灵锤子”困惑。）
    //
    //  复用游戏自带的 RGD_Slot 序列化（无损：物品名/数量/耐久）。
    //  限制：同一时刻只能有一个玩家的背包是“活”的（P1、P2 不能同时开背包）。
    //  装备槽(护甲)暂不纳入，保持 P1 的；后续按需扩展。
    // ══════════════════════════════════════════════════════════════════════
    public static class P2InventoryStore
    {
        static RGD_Slot[] _p2Slots;     // P2 的背包内容（跨开关持久；后续接入存盘）
        static RGD_Slot[] _p1Backup;    // P2 开背包期间临时保存的 P1 背包内容
        static bool _swapped;
        static bool _swapToClone;
        static P2InventoryGlobalScope _globalScope;

        public static bool IsSwapped => _swapped;

        // ---- 视图同步指纹 ----
        // 换入/换出原本每次都整包拷贝(RestoreBackpack 逐格 SetItem + SnapshotBackpack 逐格 new RGD_Slot)。
        // P2 准星停在任何可交互物上,每帧都会走一次换入换出,而绝大多数时候背包根本没变。
        // 这里记下"视图与 _p2Slots 一致"那一刻视图内容的指纹:
        //   换入时指纹没变 -> 视图已经是 _p2Slots 的内容,跳过灌入;
        //   换出时指纹没变 -> 作用域内没人动过背包,跳过回写。
        // 语义与原来完全一致(含"作用域之外对视图的改动在下次换入时被 _p2Slots 覆盖"这一点),
        // 只是把无变化时的两次整包拷贝换成两次只读遍历。
        static PlayerInventory _fpView;
        static ulong _fp;
        static bool _fpValid;

        // 遍历口径必须与 SnapshotBackpack 一致:非手持栏格,按出现顺序编号。
        static ulong Fingerprint(PlayerInventory inv)
        {
            const ulong Prime = 1099511628211UL;
            ulong h = 14695981039346656037UL;
            var slots = inv != null ? inv.allSlots : null;
            if (slots == null) return h;
            int bpIdx = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s == null || s.slotType == SlotType.Hotbar) continue;
                var it = s.IsEmpty ? null : s.itemInstance;
                if (it != null)
                {
                    unchecked
                    {
                        h = (h ^ (ulong)(uint)bpIdx) * Prime;
                        h = (h ^ (ulong)(uint)it.UniqueIndex) * Prime;
                        h = (h ^ (ulong)(uint)it.Amount) * Prime;
                        h = (h ^ (ulong)(uint)it.Uses) * Prime;
                        h = (h ^ (ulong)(uint)(it.exclusiveString != null ? it.exclusiveString.GetHashCode() : 0)) * Prime;
                    }
                }
                bpIdx++;
            }
            return h;
        }

        // 声明:此刻 view 的内容与 _p2Slots 一致。
        static void MarkSynced(PlayerInventory view)
        {
            _fpView = view;
            _fp = Fingerprint(view);
            _fpValid = true;
        }

        static bool ViewMatchesSlots(PlayerInventory view)
        {
            return _fpValid && view != null && ReferenceEquals(_fpView, view) && Fingerprint(view) == _fp;
        }
        // P2 拾取/采集进行中：FindSuitableSlot 补丁据此把物品只放进【背包格(非热栏)】，
        // 否则可能落进 P1 的热栏空格(热栏不参与换入换出 → 物品丢失/污染 P1)。
        public static bool RoutingPickup;
        // 例行换入换出(每帧设备射线/工具采集)日志默认静音，避免刷屏；排障时置 true。
        public static bool VerboseLog;

        // 存档用：取 P2 背包当前内容(RGD_Slot[])。若此刻正换入(P2 背包是活的)→ 快照实时；否则用持久的 _p2Slots。
        public static RGD_Slot[] GetP2BackpackSnapshot()
        {
            if (_swapped)
            {
                var src = _swapToClone ? Main.P2ViewOrNull : Cm.Get<PlayerInventory>();
                if (src != null) return SnapshotBackpack(src);
            }
            // 背包视图开着时 _swapped=false,但真数据在视图里(_p2Slots 要到关包才回写)。
            // 存档必须读视图,并把光标上拿着的那一堆并进去,否则读档后表现为复制/丢失。
            if (Main.IsP2BackpackOpen)
            {
                var view = Main.P2ViewOrNull;
                if (view != null) return WithHeld(SnapshotBackpack(view), view, Main.P2HeldOrNull);
            }
            return _p2Slots;
        }

        // 读档用：直接设置 P2 背包数据(下次 SwapInP2 时显示)。
        public static void SetP2Backpack(RGD_Slot[] slots) { _p2Slots = slots; _fpValid = false; }

        public static void CaptureFrom(PlayerInventory inv)
        {
            _p2Slots = SnapshotBackpack(inv);
            if (inv != null && ReferenceEquals(inv, Main.P2ViewOrNull)) MarkSynced(inv); else _fpValid = false;
        }

        public static void RestoreInto(PlayerInventory inv)
        {
            RestoreBackpack(inv, _p2Slots);
            if (inv != null && ReferenceEquals(inv, Main.P2ViewOrNull)) MarkSynced(inv);
        }

        // 无副作用统计 P2 背包里某物品(按 UniqueIndex)的总数量。供 P2 建造的"材料是否足够"逐帧检查用：
        //  此时 P2 背包未换入(_swapped=false，数据在 _p2Slots)，单例里是 P1 内容 → 不能用 inventory.GetItemCount。
        //  仅读 _p2Slots(RGD_Slot.itemIndex == 物品 UniqueIndex；itemAmount 为数量)，不触发任何槽事件/换入换出。
        public static int CountBackpackItem(int uniqueIndex)
        {
            int total = 0;
            if (_p2Slots != null)
                foreach (var r in _p2Slots)
                    if (r != null && r.HasItem && r.itemIndex == uniqueIndex)
                        total += r.itemAmount;
            return total;
        }

        // P2 打开背包：换出 P1 背包，换入 P2 背包（仅 backpackSlots）。
        //  强语义:仅当【真正完成换入】(快照 P1 + 载入 P2 + _swapped=true)才返回 true;
        //  PlayerInventory 为空(边缘态)时返回 false,调用方据此【不开 RoutingPickup】,避免
        //  "RoutingPickup=true 但单例仍是 P1 数据" → P2 产出污染/丢失 P1。已换入态再调直接返回 true。
        public static bool TrySwapInP2()
        {
            if (_swapped) return true;
            var clone = Main.EnsureP2View();
            if (clone != null)
            {
                if (!ViewMatchesSlots(clone))
                {
                    RestoreBackpack(clone, _p2Slots);
                    MarkSynced(clone);
                }
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
                if (clone != null && !ViewMatchesSlots(clone))
                {
                    _p2Slots = SnapshotBackpack(clone);
                    MarkSynced(clone);
                }
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

        
        // 只放弃 swap/scope,保留 _p2Slots(倒地、强制关菜单用)。清数据只能走 Reset。
        public static void AbandonSwap()
        {
            RoutingPickup = false;
            if (_swapped)
            {
                if (_swapToClone)
                {
                    var clone = Main.P2ViewOrNull;
                    if (clone != null) { _p2Slots = SnapshotBackpack(clone); MarkSynced(clone); }
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
        }

        // 会话结束(离开世界):放弃 swap 并清空内存里的 P2 背包。
        public static void Reset()
        {
            AbandonSwap();
            _p2Slots = null;
            _fpValid = false;
            _fpView = null;
        }

        
        
        
        
        // 把光标上拿着的物品写进快照里第一个可用的普通空格(下标口径与 SnapshotBackpack 一致)。
        static RGD_Slot[] WithHeld(RGD_Slot[] snap, PlayerInventory view, ItemInstance held)
        {
            if (held == null || !held.Valid || view.allSlots == null) return snap;
            int bpIdx = 0;
            foreach (var s in view.allSlots)
            {
                if (s == null || s.slotType == SlotType.Hotbar) continue;
                if (s.IsEmpty && s.active && !(s is Slot_Equip))
                {
                    var list = new List<RGD_Slot>(snap ?? new RGD_Slot[0]);
                    list.Add(new RGD_Slot { slotIndex = (short)bpIdx, itemIndex = (short)held.UniqueIndex, itemAmount = (short)held.Amount, itemUses = (short)held.Uses, exclusiveString = held.exclusiveString });
                    return list.ToArray();
                }
                bpIdx++;
            }
            Main.ModEntry.Logger.Log("[P2Save] 背包已满,光标上拿着的物品未能写入存档快照");
            return snap;
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
