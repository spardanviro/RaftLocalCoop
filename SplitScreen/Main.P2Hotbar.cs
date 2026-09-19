using System;
using UnityEngine;

namespace SplitScreen
{
    public static partial class Main
    {
        
        
        //
        
        
        
        internal const int P2HotbarSize = 10;
        internal static ItemInstance[] _p2Hotbar;       
        static UseItemController _p2UseItemController;
        static bool _p2HeldInitialized;
        static int   _p2HeldWarmup;        
        static float _p2HeldWarmupTime;    
        static int   _p2HeldDesiredIdx;    
        static int   _p2PlasticHideFrames; 

        static void EnsureP2Hotbar()
        {
            if (_p2Hotbar != null) return;
            _p2Hotbar = new ItemInstance[P2HotbarSize];
            
            
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

        
        internal static ItemInstance GetP2HeldHotbarItem()
        {
            var r = SplitScreenRuntime.Instance?.P2;
            if (r == null || _p2Hotbar == null) return null;
            int idx = r.HotbarIndex;
            return (idx >= 0 && idx < _p2Hotbar.Length) ? _p2Hotbar[idx] : null;
        }

        
        internal static bool _p2RefreshHeldNextFrame;

        
        internal static string ItemSig(ItemInstance it) => it == null ? "null" : it.UniqueIndex + ":" + it.Amount + ":" + it.Uses;

        
        internal static void ConsumeP2HeldDurability(int stacks)
        {
            if (stacks <= 0) return;
            var inst = GetP2HeldHotbarItem();
            if (inst == null || inst.baseItem == null || inst.baseItem.MaxUses <= 1) return;   
            inst.Uses -= stacks;                          
            if (inst.Uses <= 0)
            {
                if (inst.Amount > 1) { inst.Amount -= 1; inst.SetUsesToMax(); }   
                else { SetP2HeldHotbarItem(null); return; }                       
            }
            UpdateP2Hotbar();   
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
            
            bool changed = (before == null) != (after == null)
                || (before != null && after != null && (before.UniqueIndex != after.UniqueIndex || before.Amount != after.Amount || before.Uses != after.Uses));
            if (changed) _p2RefreshHeldNextFrame = true;
        }

        
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
            for (int i = 0; i < _p2Hotbar.Length; i++) _p2Hotbar[i] = null;   
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

        
        
        static bool _p2PlaceableDepleted;
        internal static bool ConsumeP2HeldPlaceable(Item_Base placed)
        {
            var r = SplitScreenRuntime.Instance?.P2;
            if (r == null || _p2Hotbar == null || placed == null) return false;
            int idx = r.HotbarIndex;
            if (idx < 0 || idx >= _p2Hotbar.Length) return false;
            var inst = _p2Hotbar[idx];
            if (inst == null || inst.baseItem == null) return false;
            if (inst.baseItem.UniqueIndex != placed.UniqueIndex) return false;   
            int left = inst.Amount - 1;
            if (left > 0) { inst.Amount = left; UpdateP2Hotbar(); return true; }  
            _p2Hotbar[idx] = null;            
            UpdateP2Hotbar();
            _p2PlaceableDepleted = true;      
            return false;
        }

        
        
        
        static float _lastP2ConsumeTime = -10f;
        internal static void ConsumeP2Held(Item_Base food)
        {
            if (player2 == null || food == null || food.settings_consumeable == null) return;
            if (Time.time - _lastP2ConsumeTime < 0.4f) return;        
            var held = GetP2HeldHotbarItem();
            if (held == null || held.Amount <= 0) return;
            _lastP2ConsumeTime = Time.time;
            
            
            
            
            
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
                    
                    if (!string.IsNullOrEmpty(cc.EventRef_ConsumeSound))
                        FMODUnity.RuntimeManager.PlayOneShotAttached(cc.EventRef_ConsumeSound, player2.gameObject);
                }
            }
            catch (System.Exception e) { ModEntry.Logger.Log("[P2Consume] 应用 yield 异常: " + e.Message); }
            
            var su = food.settings_usable;
            if (su != null && player2.Animator != null && su.AnimationOnUse != PlayerAnimation.None)
                player2.Animator.SetAnimation(su.AnimationOnUse, su.ForceAnimationIndex, su.SetTriggering);
            ConsumeP2HeldUses(food);
        }

        
        
        
        
        
        static void ApplyP2Stat(Stat_Bonus_Consumable stat, float yield, float bonusYield)
        {
            if (stat == null) return;
            if (yield != 0f && stat.normalConsumable != null) stat.normalConsumable.Value += yield;
            if (bonusYield != 0f && stat.bonusConsumable != null) stat.bonusConsumable.Value += bonusYield;
        }

        
        
        
        static void ConsumeP2HeldUses(Item_Base food)
        {
            var held = GetP2HeldHotbarItem();
            if (held == null) return;
            int useAmt = (food.settings_usable != null) ? Mathf.Max(1, food.settings_usable.ConsumeUseAmount) : 1;
            var after = food.settings_consumeable?.ItemAfterUse;
            bool hasAfter = after != null && after.item != null;

            held.Uses -= useAmt;                          
            if (held.Uses > 0) { UpdateP2Hotbar(); return; }   

            
            if (held.Amount > 1)
            {
                held.Amount -= 1;
                held.SetUsesToMax();
                UpdateP2Hotbar();
            }
            else
            {
                
                if (hasAfter)
                    SetP2HeldHotbarItem(new ItemInstance(after.item, Mathf.Max(1, after.amount), after.item.MaxUses));
                else
                    SetP2HeldHotbarItem(null);
            }
        }

        
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

        
        internal static void InitP2Held()
        {
            if (player2 == null) return;
            EnsureP2Hotbar();
            if (_p2UseItemController == null)
                _p2UseItemController = player2.GetComponentInChildren<UseItemController>(true);
            if (_p2UseItemController == null) return;     
            try { _p2UseItemController.Deselect(); }
            catch (Exception e) { LogV("[P2Hotbar] use item deselect ignored: " + e.Message); }
            var slot = SplitScreenRuntime.Instance?.P2;
            _p2HeldDesiredIdx = slot != null ? slot.HotbarIndex : 0;
            _p2HeldInitialized = true;
            EquipP2(_p2HeldDesiredIdx);
            _p2HeldWarmup = 1; _p2HeldWarmupTime = Time.time + 0.4f;
            _p2PlasticHideFrames = 90;   
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

            if (_p2PlasticHideFrames > 0) { _p2PlasticHideFrames--; HideP2HeldPlastic(); }   

            
            if (_p2PlaceableDepleted || _p2RefreshHeldNextFrame) { _p2PlaceableDepleted = false; _p2RefreshHeldNextFrame = false; EquipP2(slot.HotbarIndex); }

            if (!_p2HeldInitialized)
            {
                _p2HeldInitialized = true;
                _p2HeldDesiredIdx  = slot.HotbarIndex;
                EquipP2(_p2HeldDesiredIdx);
                _p2HeldWarmup      = 1;                   
                _p2HeldWarmupTime  = Time.time + 0.4f;
                if (_p2PlasticHideFrames <= 0) _p2PlasticHideFrames = 90;   
                return;
            }

            
            
            
            
            
            if (_p2HeldWarmup > 0 && Time.time >= _p2HeldWarmupTime)
            {
                
                
                EquipP2(_p2HeldDesiredIdx);
                ForceP2HeldAnimReselect();   
                _p2HeldWarmup--;
                _p2HeldWarmupTime = Time.time + 0.3f;
            }

            TickP2FillWater();   

            
            
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
