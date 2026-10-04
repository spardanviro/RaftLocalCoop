using System;
using UnityEngine;

namespace SplitScreen
{
    public static partial class Main
    {
        // ══════════════════════════════════════════════════════════════════
        //  P2 快捷栏（每槽一件 ItemInstance，可与背包互移）
        //
        //  数据：_p2Hotbar[10]（每槽一件，可空）。LB/RB 选择当前槽并手持其物品(StartUsing baseItem)。
        //  背包 UI 的虚拟光标可拖物到/出这些槽（见 Main.P2Inventory.cs）。视觉沿用已克隆的原版热栏。
        // ══════════════════════════════════════════════════════════════════
        internal const int P2HotbarSize = 10;
        internal static ItemInstance[] _p2Hotbar;       // P2 快捷栏每槽物品（与背包共享 ClickSlot 逻辑）
        static UseItemController _p2UseItemController;
        static bool _p2HeldInitialized;
        static int   _p2HeldWarmup;        // 初始化后预热重装步数（解决首次装备抓握/可用未生效）
        static float _p2HeldWarmupTime;    // 下次预热重装时间
        static int   _p2HeldDesiredIdx;    // 预热结束后要稳定停留的目标槽
        static int   _p2PlasticHideFrames; // 生成初期抑制"可收集塑料"持握模型的帧数

        static void EnsureP2Hotbar()
        {
            if (_p2Hotbar != null) return;
            _p2Hotbar = new ItemInstance[P2HotbarSize];
            // 初始物随游戏模式：与原版 PlayerInventory.GiveStartingItems 同一门控
            //  (playerSpecificVariables.giveStartingItems：创造=true→锤/斧/刷；其它模式=false→塑料钩)。
            bool giveTools = true;
            try { giveTools = GameModeValueManager.GetCurrentGameModeValue().playerSpecificVariables.giveStartingItems; }
            catch (Exception e) { LogV("[P2Hotbar] game mode starting items check ignored: " + e.Message); }
            var startNames = giveTools ? new[] { "Hammer", "Axe", "PaintBrush" } : new[] { "Hook_Plastic" };
            int idx = 0;
            foreach (var name in startNames)
            {
                var it = ItemManager.GetItemByName(name);
                if (it != null && idx < P2HotbarSize) _p2Hotbar[idx] = new ItemInstance(it, 1, it.MaxUses);
                idx++;
            }
            LogV($"[P2Hotbar] 初始化 P2 快捷栏(初始物={(giveTools ? "锤/斧/刷" : "塑料钩")})");
        }

        internal static int P2HotbarSlotCount() => P2HotbarSize;

        // 统计 P2 热栏里某物品(按 UniqueIndex)的总数 —— 建造资源判定用(P2 库存=背包+热栏)。
        internal static int CountP2HotbarItem(int uniqueIndex)
        {
            int total = 0;
            if (_p2Hotbar != null)
                foreach (var inst in _p2Hotbar)
                    if (inst != null && inst.baseItem != null && inst.baseItem.UniqueIndex == uniqueIndex)
                        total += inst.Amount;
            return total;
        }

        internal static int RemoveP2HotbarItem(int uniqueIndex, int amount)
        {
            if (amount <= 0 || _p2Hotbar == null) return 0;
            int removed = 0;
            for (int i = 0; i < _p2Hotbar.Length && amount > 0; i++)
            {
                var inst = _p2Hotbar[i];
                if (inst == null || inst.baseItem == null || inst.baseItem.UniqueIndex != uniqueIndex) continue;
                int take = Mathf.Min(amount, inst.Amount);
                inst.Amount -= take;
                amount -= take;
                removed += take;
                if (inst.Amount <= 0) _p2Hotbar[i] = null;
            }
            if (removed > 0)
            {
                UpdateP2Hotbar();
                _p2RefreshHeldNextFrame = true;
            }
            return removed;
        }

        // P2 当前"手持"槽的物品(设备读取 localPlayer.Inventory.GetSelectedHotbarSlot 时需临时注入它)。
        internal static ItemInstance GetP2HeldHotbarItem()
        {
            var r = SplitScreenRuntime.Instance?.P2;
            if (r == null || _p2Hotbar == null) return null;
            int idx = r.HotbarIndex;
            return (idx >= 0 && idx < _p2Hotbar.Length) ? _p2Hotbar[idx] : null;
        }

        // 设备/装水交互后，手持槽物品可能变化(电池消耗→空、空杯→盐水杯) → 下一帧重装手持，刷新手持模型/动画。
        internal static bool _p2RefreshHeldNextFrame;

        // 物品签名(类型:数量:耐久)，用于判断设备/装水是否真的改了手持物，避免每帧无谓回写。
        internal static string ItemSig(ItemInstance it) => it == null ? "null" : it.UniqueIndex + ":" + it.Amount + ":" + it.Uses;

        // P2 使用工具时扣耐久(只扣 P2 手持物，不动 P1 共享热栏)。复刻 Slot.IncrementUses 的耗尽/换栈/损坏逻辑。
        internal static void ConsumeP2HeldDurability(int stacks)
        {
            if (stacks <= 0) return;
            var inst = GetP2HeldHotbarItem();
            if (inst == null || inst.baseItem == null || inst.baseItem.MaxUses <= 1) return;   // 不可损耗物跳过
            inst.Uses -= stacks;                          // Uses setter 已 clamp 到 [0, MaxUses]
            if (inst.Uses <= 0)
            {
                if (inst.Amount > 1) { inst.Amount -= 1; inst.SetUsesToMax(); }   // 用光一件 → 下一件满耐久
                else { SetP2HeldHotbarItem(null); return; }                       // 最后一件损坏 → 清空(SetP2Held 会触发重装手持=空手)
            }
            UpdateP2Hotbar();   // 刷新耐久条
        }

        internal static void SetP2HeldHotbarItem(ItemInstance it)
        {
            var r = SplitScreenRuntime.Instance?.P2;
            if (r == null || _p2Hotbar == null) return;
            int idx = r.HotbarIndex;
            if (idx < 0 || idx >= _p2Hotbar.Length) return;
            var before = _p2Hotbar[idx];
            var after = (it != null && it.Amount > 0) ? it : null;
            _p2Hotbar[idx] = after;
            UpdateP2Hotbar();
            // 物品本体/数量/耐久变化 → 标记重装手持(刷新手上模型；电池消耗后模型消失，盐水杯刷新水量)。
            bool changed = (before == null) != (after == null)
                || (before != null && after != null && (before.UniqueIndex != after.UniqueIndex || before.Amount != after.Amount || before.Uses != after.Uses));
            if (changed) _p2RefreshHeldNextFrame = true;
        }

        // ── 存档：导出/导入 P2 快捷栏(每槽 itemIndex/amount/uses) ─────────────
        internal static System.Collections.Generic.List<P2SaveEntry> GetP2HotbarEntries()
        {
            var list = new System.Collections.Generic.List<P2SaveEntry>();
            if (_p2Hotbar == null) return list;
            for (int i = 0; i < _p2Hotbar.Length; i++)
            {
                var it = _p2Hotbar[i];
                if (it == null || it.baseItem == null) continue;
                list.Add(new P2SaveEntry { slot = i, itemIndex = it.baseItem.UniqueIndex, amount = it.Amount, uses = it.Uses, exclusive = it.exclusiveString });
            }
            return list;
        }

        internal static void LoadP2HotbarEntries(System.Collections.Generic.List<P2SaveEntry> entries)
        {
            EnsureP2Hotbar();
            for (int i = 0; i < _p2Hotbar.Length; i++) _p2Hotbar[i] = null;   // 清空(覆盖默认锤/斧/刷)
            if (entries != null)
                foreach (var e in entries)
                {
                    if (e == null || e.slot < 0 || e.slot >= _p2Hotbar.Length) continue;
                    var item = ItemManager.GetItemByIndex(e.itemIndex);
                    if (item == null) continue;
                    _p2Hotbar[e.slot] = new ItemInstance(item, e.amount, e.uses) { exclusiveString = e.exclusive };
                }
            UpdateP2Hotbar();
        }

        // 放置一个可放置物后由 CreateBlock 的 Postfix 调用：从【当前手持的 P2 快捷栏槽】扣 1。
        //  返回 true 表示还有剩余(→ BlockCreator 重建幽灵继续放)；false 表示用光(→ 清空槽 + 下一帧取消手持)。
        static bool _p2PlaceableDepleted;
        internal static bool ConsumeP2HeldPlaceable(Item_Base placed)
        {
            var r = SplitScreenRuntime.Instance?.P2;
            if (r == null || _p2Hotbar == null || placed == null) return false;
            int idx = r.HotbarIndex;
            if (idx < 0 || idx >= _p2Hotbar.Length) return false;
            var inst = _p2Hotbar[idx];
            if (inst == null || inst.baseItem == null) return false;
            if (inst.baseItem.UniqueIndex != placed.UniqueIndex) return false;   // 手持的不是刚放下的那个 → 不扣(保险)
            int left = inst.Amount - 1;
            if (left > 0) { inst.Amount = left; UpdateP2Hotbar(); return true; }  // 还有剩
            _p2Hotbar[idx] = null;            // 用光 → 清空该槽
            UpdateP2Hotbar();
            _p2PlaceableDepleted = true;      // 下一帧 TickP2Hotbar 取消手持(空手、关幽灵)
            return false;
        }

        // P2 吃喝(由 UseItemController.Use 的 P2 消耗品补丁调用，调用时已在 P2OriginalScope.Tool() 内)。
        //  对 P2 应用食物 yield(Stats.Consume 内部门控 IsLocalPlayer，scope 内强制为真→作用于 P2)，并扣 P2 手持 1。
        //  绕开原版 UseItemController 消耗路径(其 ConsumeStacks 延迟协程会扣到 P1 共享选中槽)。带去抖防连发。
        static float _lastP2ConsumeTime = -10f;
        internal static void ConsumeP2Held(Item_Base food)
        {
            if (player2 == null || food == null || food.settings_consumeable == null) return;
            if (Time.time - _lastP2ConsumeTime < 0.4f) return;        // 去抖：防住扳机连吃
            var held = GetP2HeldHotbarItem();
            if (held == null || held.Amount <= 0) return;
            _lastP2ConsumeTime = Time.time;
            // 复刻 PlayerStats.Consume，但 normal 部分【强制 plain】(绕过 manipulator curve)：
            //  vanilla 无 BonusYield 的消耗(纯喝水)走 ConsumeWithManipulatorCurve → yield * curve.Evaluate(craving)。
            //  P2 是 Network_Player 克隆，其 stat 组件的 AnimationCurve(SerializeField)未随克隆深拷贝 → Evaluate 返回 0
            //  → 喝水 statTarget 不增 → thirst 不回。吃食物多带 BonusYield 走 plain Consume，不受影响，故 hunger 正常。
            //  这里对 P2 统一用 plain normalConsumable.Consume(直接 statTarget += yield)，回满 yield、不依赖 curve。
            try
            {
                var stats = player2.Stats;
                if (stats != null)
                {
                    var cc = food.settings_consumeable;
                    using (P2OriginalScope.Tool())
                    {
                        ApplyP2Stat(stats.stat_hunger, cc.HungerYield, cc.BonusHungerYield);
                        if (cc.ThirstYield != 0f || cc.BonusThirstYield != 0f)
                            ApplyP2Stat(stats.stat_thirst, cc.ThirstYield, cc.BonusThirstYield);
                        if (cc.OxygenYield > 0f && stats.stat_oxygen != null) stats.stat_oxygen.Value += cc.OxygenYield;
                    }
                    // vanilla Consume 末尾的进食音效(原 PlayerStats.Consume 内播放，这里手动补)。
                    if (!string.IsNullOrEmpty(cc.EventRef_ConsumeSound))
                        FMODUnity.RuntimeManager.PlayOneShotAttached(cc.EventRef_ConsumeSound, player2.gameObject);
                }
            }
            catch (System.Exception e) { ModEntry.Logger.Log("[P2Consume] 应用 yield 异常: " + e.Message); }
            // 喝/吃动作：原版由 UseItemController.Use 的 AnimationOnUse 触发，但 P2 喝吃走 ConsumeP2Held(跳过了原版 Use)→ 动作丢失。手动补。
            var su = food.settings_usable;
            if (su != null && player2.Animator != null && su.AnimationOnUse != PlayerAnimation.None)
                player2.Animator.SetAnimation(su.AnimationOnUse, su.ForceAnimationIndex, su.SetTriggering);
            ConsumeP2HeldUses(food);
        }

        // 对 P2 的 hunger/thirst 应用 yield。
        //  vanilla 的 Consume 对【正 yield】只设 statTarget+Consuming，靠 Stat_Consumable.HandleValue 每帧 Lerp 渐进推进；
        //  但 P2(Network_Player 克隆)的 stat 渐进消化 HandleValue 不推进 → 正 yield(喝淡水/吃饭)永远落不到 Value 上。
        //  (反证:盐水负 yield 走 Consume 的 else 分支【立即】改 Value，所以"喝盐水"反而立刻生效。)
        //  故这里直接【立即】改 Value(正=回、负=扣)，绕过不可靠的渐进消化，符号也正确。
        static void ApplyP2Stat(Stat_Bonus_Consumable stat, float yield, float bonusYield)
        {
            if (stat == null) return;
            if (yield != 0f && stat.normalConsumable != null) stat.normalConsumable.Value += yield;
            if (bonusYield != 0f && stat.bonusConsumable != null) stat.bonusConsumable.Value += bonusYield;
        }

        // 复刻 Slot.IncrementUses(-ConsumeUseAmount) 的消耗：喝一口减 uses；uses 耗尽 → 减 1 个；
        //  耗尽时若该消耗品配了 ItemAfterUse(空容器, 如盐水杯→空杯)则换成它, 否则清空(普通食物吃完没了)。
        //  修复:盐水杯(数量1)直接走 ConsumeP2HeldOne 会被清空消失 → 应留空杯。
        static void ConsumeP2HeldUses(Item_Base food)
        {
            var held = GetP2HeldHotbarItem();
            if (held == null) return;
            int useAmt = (food.settings_usable != null) ? Mathf.Max(1, food.settings_usable.ConsumeUseAmount) : 1;
            var after = food.settings_consumeable?.ItemAfterUse;
            bool hasAfter = after != null && after.item != null;

            held.Uses -= useAmt;                          // Uses setter 已 clamp
            if (held.Uses > 0) { UpdateP2Hotbar(); return; }   // 还有口数 → 容器维持(水量条减少)

            // uses 耗尽 → 消耗一个
            if (held.Amount > 1)
            {
                held.Amount -= 1;
                held.SetUsesToMax();
                UpdateP2Hotbar();
            }
            else
            {
                // 最后一个：有 ItemAfterUse(空杯)→ 换成空杯；否则清空(食物吃完)。
                if (hasAfter)
                    SetP2HeldHotbarItem(new ItemInstance(after.item, Mathf.Max(1, after.amount), after.item.MaxUses));
                else
                    SetP2HeldHotbarItem(null);
            }
        }

        // 扣 P2 手持当前槽 1 个(用光清槽 + 下一帧重装手持)。供吃喝/单次消耗复用。
        internal static void ConsumeP2HeldOne()
        {
            var r = SplitScreenRuntime.Instance?.P2;
            if (r == null || _p2Hotbar == null) return;
            int idx = r.HotbarIndex;
            if (idx < 0 || idx >= _p2Hotbar.Length) return;
            var inst = _p2Hotbar[idx];
            if (inst == null) return;
            if (inst.Amount > 1) { inst.Amount -= 1; UpdateP2Hotbar(); }
            else { _p2Hotbar[idx] = null; UpdateP2Hotbar(); _p2RefreshHeldNextFrame = true; }
        }

        // 强制 P2 手持动画重新触发（无条件 ItemID+Switch），修复首次装备时丢失的转换。
        static System.Reflection.FieldInfo _fAnimatorR;
        static void ForceP2HeldAnimReselect()
        {
            try
            {
                if (_p2UseItemController == null) return;
                if (_fAnimatorR == null)
                    _fAnimatorR = typeof(UseItemController).GetField("playerAnimator",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var pa = _fAnimatorR?.GetValue(_p2UseItemController) as PlayerAnimator;
                if (pa != null) pa.ReselectAnimation();
            }
            catch (System.Exception e) { ModEntry.Logger.Log("[P2Hotbar] Reselect ex: " + e.Message); }
        }

        static void EquipP2(int index)
        {
            if (_p2UseItemController == null) return;
            index = ((index % P2HotbarSize) + P2HotbarSize) % P2HotbarSize;
            SplitScreenRuntime.Instance.P2.HotbarIndex = index;
            _p2HeldDesiredIdx = index;
            try
            {
                _p2UseItemController.Deselect();
                // 【A类根治】先关掉所有 item 持握模型，再由 StartUsing 激活当前那个。
                //  根因：克隆体(非本地玩家)跳过了本地专属的模型初始化 → 角色预制体里这些持握模型停在默认(很多 active)，
                //  且 P2 切换时 Deselect 只关上一个 → 残留累积(多个杯/瓶/壶同亮 → 取水互相覆盖)。
                //  统一清空 allConnections(覆盖所有 hotbar 物品)取代逐个黑名单，杜绝任何持握模型残留。
                foreach (var c in _p2UseItemController.allConnections)
                    if (c != null && c.obj != null) c.SetActive(false);
                var it = (_p2Hotbar != null && index < _p2Hotbar.Length) ? _p2Hotbar[index] : null;
                if (it != null && it.baseItem != null)
                {
                    _p2UseItemController.StartUsing(it.baseItem);
                    LogV($"[P2Hotbar] 手持[{index}] = {it.baseItem.UniqueName}");
                }
                else
                {
                    // 空手：Deselect 不会为 P2(非本地)复位手持动画 → 会卡在放置/持物姿势。手动回 Idle。
                    if (player2 != null && player2.Animator != null)
                        player2.Animator.SetAnimation(PlayerAnimation.Index_0_Idle);
                    LogV($"[P2Hotbar] 切到空槽[{index}] → 空手");
                }
            }
            catch (System.Exception e)
            {
                ModEntry.Logger.Log($"[P2Hotbar] 切换异常 [{index}]: {e.Message}");
            }
            HideP2HeldPlastic();   
        }

        // 关掉 P2 身上残留激活的【非 UseItemController 管理】的特殊持握模型(滑索轮/笔记本)。
        //  这些不在 allConnections 里(属 Equipment/特殊系统)，EquipP2 的"清空 allConnections"覆盖不到，故单独隐藏。
        //  (所有 hotbar 物品持握模型——杯/瓶/壶/锤/斧/钩…——已由 EquipP2 统一清空 + StartUsing 激活当前管理，不再列黑名单。)
        static readonly string[] _p2StrayNames = { "ZiplinePlayer", "NoteBookParent#RightHandParent" };
        static System.Collections.Generic.List<Transform> _p2StrayModels;
        static bool _p2HeldSuppressedByDeath;

        internal static void HideP2HeldPlastic()
        {
            if (player2 == null) return;
            if (_p2StrayModels == null)
            {
                _p2StrayModels = new System.Collections.Generic.List<Transform>();
                foreach (var t in player2.GetComponentsInChildren<Transform>(true))
                    foreach (var s in _p2StrayNames)
                        if (t.name == s) { _p2StrayModels.Add(t); break; }
            }
            foreach (var t in _p2StrayModels)
            {
                if (t != null && t.name == "ZiplinePlayer" && player2.ZiplinePlayer != null && player2.ZiplinePlayer.IsAttachedToZipline)
                    continue;
                if (t != null && t.gameObject.activeSelf) t.gameObject.SetActive(false);
            }
        }

        // 背包/热栏改动后重新手持当前槽（拖拽更换当前手持物时即时生效）
        internal static void RefreshP2HeldItem()
        {
            if (_p2UseItemController == null || player2 == null) return;
            if (P2IsDownedOrCarried) { SuppressP2HeldWhileDowned(); return; }
            var slot = SplitScreenRuntime.Instance?.P2;
            if (slot != null) EquipP2(slot.HotbarIndex);
        }

        internal static void ApplyP2DownedOriginalInputRestrictions()
        {
            if (player2 == null) return;
            if (_p2UseItemController == null)
                _p2UseItemController = player2.GetComponentInChildren<UseItemController>(true);
            SuppressP2HeldWhileDowned();
        }

        static void SuppressP2HeldWhileDowned()
        {
            if (player2 == null) return;
            try { player2.PlayerItemManager?.SelectUsable(null); }
            catch (Exception e) { LogV("[P2Hotbar] SelectUsable(null) ignored: " + e.Message); }
            try { player2.PlayerItemManager?.HideItemInHand(); }
            catch (Exception e) { LogV("[P2Hotbar] HideItemInHand ignored: " + e.Message); }
            try { _p2UseItemController?.Deselect(); }
            catch (Exception e) { LogV("[P2Hotbar] Deselect ignored: " + e.Message); }
            try
            {
                if (_p2UseItemController != null && _p2UseItemController.allConnections != null)
                    foreach (var c in _p2UseItemController.allConnections)
                        if (c != null && c.obj != null) c.SetActive(false);
            }
            catch (Exception e) { LogV("[P2Hotbar] forced equip refresh ignored: " + e.Message); }
            HideP2HeldPlastic();
            _p2HeldWarmup = 0;
            _p2RefreshHeldNextFrame = false;
            _p2PlaceableDepleted = false;
            _p2HeldSuppressedByDeath = true;
        }

        // 生成时立即让 P2 手持目标工具(锤子)，盖掉原版 GiveStartingItems 自动装备的 Hook_Plastic("塑料")一帧闪烁。
        internal static void InitP2Held()
        {
            if (player2 == null) return;
            EnsureP2Hotbar();
            if (_p2UseItemController == null)
                _p2UseItemController = player2.GetComponentInChildren<UseItemController>(true);
            if (_p2UseItemController == null) return;     // 此时还没好 → 退回 TickP2Hotbar 的首帧初始化
            try { _p2UseItemController.Deselect(); }
            catch (Exception e) { LogV("[P2Hotbar] use item deselect ignored: " + e.Message); }
            var slot = SplitScreenRuntime.Instance?.P2;
            _p2HeldDesiredIdx = slot != null ? slot.HotbarIndex : 0;
            _p2HeldInitialized = true;
            EquipP2(_p2HeldDesiredIdx);
            _p2HeldWarmup = 1; _p2HeldWarmupTime = Time.time + 0.4f;
            _p2PlasticHideFrames = 90;   // 生成后约 1.5 秒内持续压制塑料持握模型
            HideP2HeldPlastic();
            LogV("[P2Hotbar] 生成时立即手持(避免塑料闪烁)");
        }

        internal static void TickP2Hotbar()
        {
            if (player2 == null) return;
            var slot = SplitScreenRuntime.Instance?.P2;
            if (slot?.ActionHotbarNext == null || slot.ActionHotbarPrev == null) return;

            EnsureP2Hotbar();
            if (_p2UseItemController == null)
                _p2UseItemController = player2.GetComponentInChildren<UseItemController>(true);
            if (_p2UseItemController == null) return;

            // 搬运中(爆炸桶/动物):对齐 vanilla —— 拿着 Carry 物时 StartCarrying 置 IsBusy=true,
            // PlayerItemManager.CanSwitch 返回 !IsBusy,禁止切快捷栏。这里整段早退:既不切槽,
            // 也不跑 warmup 的 EquipP2(否则会把拿起时 HideP2HeldModels 收起的手持模型重新装回来)。
            // TickP2Carry 的放下(按 B)在本 Tick 之后,放下时 RefreshP2HeldItem 会重装手持。
            if (P2IsCarrying) return;

            if (P2IsDownedOrCarried)
            {
                SuppressP2HeldWhileDowned();
                return;
            }
            if (_p2HeldSuppressedByDeath)
            {
                _p2HeldSuppressedByDeath = false;
                _p2HeldInitialized = false;
                _p2HeldWarmup = 0;
                _p2RefreshHeldNextFrame = true;
            }

            if (_p2PlasticHideFrames > 0) { _p2PlasticHideFrames--; HideP2HeldPlastic(); }   // 压制残留持握模型

            // 可放置物用光 / 设备交互改变了手持物 → 重装手持(刷新手上模型；空槽=空手)。
            if (_p2PlaceableDepleted || _p2RefreshHeldNextFrame) { _p2PlaceableDepleted = false; _p2RefreshHeldNextFrame = false; EquipP2(slot.HotbarIndex); }

            if (!_p2HeldInitialized)
            {
                _p2HeldInitialized = true;
                _p2HeldDesiredIdx  = slot.HotbarIndex;
                EquipP2(_p2HeldDesiredIdx);
                _p2HeldWarmup      = 1;                   // 动画器就绪后强制重触发一次手持动画即可
                _p2HeldWarmupTime  = Time.time + 0.4f;
                if (_p2PlasticHideFrames <= 0) _p2PlasticHideFrames = 90;   // InitP2Held 没赶上时兜底
                return;
            }

            // 根因：首次装备时 SetAnimationIndex 设了 selectedIndex 并触发 "Switch"，但分屏初帧动画器未就绪→
            //  该 Switch 转换丢失；此后 selectedIndex 已等于目标 index，SetAnimationIndex 的
            //  `selectedIndex != index` 守卫使同 index 物品(锤/斧 同为 Index_4)再装备变成空操作 → 永远卡住，
            //  只有换不同 index(刷子 Index_10)才会重新触发。修复：动画器就绪后用 ReselectAnimation()
            //  无条件重发 ItemID+Switch，强制进入手持姿态(与物品无关，不需要切到刷子)。
            if (_p2HeldWarmup > 0 && Time.time >= _p2HeldWarmupTime)
            {
                // 完整重装(Deselect+StartUsing)：重跑工具 OnSelect → 重新激活被 BlockCreator.Start 关掉的
                //  建造器 GameObject(否则锤子拿在手上但建造器是 inactive，无幽灵/无法建造，需手动切工具)。
                EquipP2(_p2HeldDesiredIdx);
                ForceP2HeldAnimReselect();   // 再修抓握动画(同 index 重装不会自动重触发)
                _p2HeldWarmup--;
                _p2HeldWarmupTime = Time.time + 0.3f;
            }

            TickP2FillWater();   // P2 盛水(无桥)：手持液体容器对准水面按 X 装盐水(直接转换 _p2Hotbar 当前格)

            // P2 自己的菜单/背包打开时 LB/RB 留给 UI(标签切换/不切手持)。只看 P2 自己的旗标——
            //  不再用全局 CanvasHelper.ActiveMenu(P1 开背包会置 Inventory → 误冻结 P2 切换快捷栏)。
            if (IsP2BackpackOpen || IsP2BuildMenuOpen || IsP2MenuOpen) return;
            // 仿 vanilla:手持连接的可扔锚时 PlayerItemManager.CanSwitch()=>!IsBusy 禁止切换快捷栏
            if (IsP2AnchorBusy) return;

            if (slot.ActionHotbarNext.WasPressedThisFrame())      { _p2HeldWarmup = 0; EquipP2(slot.HotbarIndex + 1); }
            else if (slot.ActionHotbarPrev.WasPressedThisFrame()) { _p2HeldWarmup = 0; EquipP2(slot.HotbarIndex - 1); }
        }

        internal static void ResetP2Hotbar()
        {
            _p2UseItemController = null;
            _p2HeldInitialized   = false;
            _p2HeldWarmup        = 0;
            _p2HeldSuppressedByDeath = false;
            _p2StrayModels = null; _p2PlasticHideFrames = 0;
        }

        // A missing world save is a new world, not a continuation of the last
        // split-screen session.  Keep ResetP2Hotbar() lightweight for reloads,
        // and use this explicit reset only when P2SaveStore found no world file.
        internal static void ResetP2HotbarForFreshWorld()
        {
            ResetP2Hotbar();
            _p2Hotbar = null;
            var runtime = SplitScreenRuntime.Instance;
            if (runtime != null) runtime.P2.HotbarIndex = 0;
        }
    }
}
