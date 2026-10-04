using System.Collections.Generic;
using System.Reflection;
using FMODUnity;
using UnityEngine;

namespace SplitScreen
{
    public static partial class Main
    {
        // ══════════════════════════════════════════════════════════════════════
        //  P2 盛水（无桥版）—— P2 持液体容器(塑料杯/瓶/水壶)对海面按 X 装盐水。
        //
        //  旧实现 hook 原版 FillWaterComponent.Update，把 P2 手持物【注入 P1 共享选中槽】让原版读写、再回写
        //   _p2Hotbar。这座"注入/回写"桥对帧内时序、多 FWC 实例敏感(切换那一帧 HotbarIndex 中途变 → 残留容器的
        //   FWC 把自己的 empty item 回写进当前槽 → 清空手持)。
        //
        //  无桥实现：完全不碰 P1 选中槽，不复用原版 Update(原版对 P2 因 !IsLocalPlayer 自行 return)。
        //   每帧直接：P2 手持液体容器 + P2 相机准心对准水面 + 按 X → 把 _p2Hotbar 当前格在 Empty↔SaltWater 间转换/补满。
        //   saltWaterItem 从该容器模型自带的 FillWaterComponent(只读其配置字段，不依赖其 Update)取。
        // ══════════════════════════════════════════════════════════════════════
        static FieldInfo _fwcSaltItem;
        static FieldInfo _fwcEmptyItem;
        static Dictionary<string, FillWaterComponent> _p2Fwcs;
        static Network_Player _p2FwcsOwner;   // 缓存属于哪个 P2;换世界后 P2 是新对象,缓存必须重建
        static bool _p2FillPromptShown;

        // 盛水提示是否正占用准心提示 → 设备/拾取交互的 Show/Hide 据此让位(见 UiRouter)，避免每帧被清。
        internal static bool IsP2FillPromptActive => _p2FillPromptShown;

        // 容器物品名前缀 → 模型名(P2 子树下的 FillWaterComponent.gameObject.name)。
        static string ContainerModelName(string heldName)
        {
            if (string.IsNullOrEmpty(heldName)) return null;
            if (heldName.StartsWith("PlasticCup")) return "Plastic_Cup";
            if (heldName.StartsWith("PlasticBottle")) return "PlasticBottle";
            if (heldName.StartsWith("Canteen")) return "Canteen";
            return null;
        }

        // 找 P2 子树下某容器模型的 FillWaterComponent(缓存)。仅用于读其 saltWaterItem 配置。
        static FillWaterComponent FindP2ContainerFwc(string modelName)
        {
            if (player2 == null || modelName == null) return null;
            if (_p2Fwcs == null || !ReferenceEquals(_p2FwcsOwner, player2))
            {
                _p2Fwcs = new Dictionary<string, FillWaterComponent>();
                _p2FwcsOwner = player2;
                foreach (var f in player2.GetComponentsInChildren<FillWaterComponent>(true))
                    if (f != null && !_p2Fwcs.ContainsKey(f.name)) _p2Fwcs[f.name] = f;
            }
            return _p2Fwcs.TryGetValue(modelName, out var fwc) ? fwc : null;
        }

        // P2 准心(相机正前方)是否对准可取水的水面(复刻原版 AimingAtTarget 几何，用 P2 相机)。
        static bool P2AimingAtWater()
        {
            var camT = player2.Camera.transform;
            var ray = new Ray(camT.position, camT.forward);
            var waterPlane = new Plane(Vector3.up, Vector3.zero);
            // P2 恒第三人称(相机拉远)，Player.UseDistance 随 P1 视角变 → 用第三人称够到距离算 reach，防 P1 切一人称时够不到。
            float reach = Mathf.Max(Player.UseDistance, Player.UseDistanceThirdperson) * 1.5f;
            return waterPlane.Raycast(ray, out float enter) && enter < reach
                   && !Physics.Raycast(ray, enter, LayerMasks.MASK_Water_with_obstruction);
        }

        static void HideP2FillPrompt()
        {
            if (_p2FillPromptShown) { ClearP2InteractPrompt(); _p2FillPromptShown = false; }
        }

        // 每帧(TickP2Hotbar 末尾)调用。
        internal static void TickP2FillWater()
        {
            if (player2 == null || player2.Camera == null) { HideP2FillPrompt(); return; }
            if (IsP2BackpackOpen || IsP2BuildMenuOpen) { HideP2FillPrompt(); return; }

            var held = GetP2HeldHotbarItem();
            var consume = held?.settings_consumeable;
            if (consume == null || consume.FoodForm != FoodForm.Fluid) { HideP2FillPrompt(); return; }   // 非液体容器

            var fwc = FindP2ContainerFwc(ContainerModelName(held.UniqueName));
            if (fwc == null) { HideP2FillPrompt(); return; }
            if (_fwcSaltItem == null)
                _fwcSaltItem = typeof(FillWaterComponent).GetField("saltWaterItem", BindingFlags.Instance | BindingFlags.NonPublic);
            var salt = _fwcSaltItem?.GetValue(fwc) as Item_Base;
            if (salt == null) { HideP2FillPrompt(); return; }

            var foodType = consume.FoodType;

            // 倒水：持【有水】的容器(盐水/淡水) + 按 LT(P2 的 ActionContext，对应原版 RMB pour) → 倒空成空杯。
            //  盐水不能喝(RT 无喝行为)，只能倒掉或进净化器；倒空用容器自带的 emptyItem。
            if (foodType != FoodType.None && p2ActionContext != null && p2ActionContext.WasPressedThisFrame())
            {
                if (_fwcEmptyItem == null)
                    _fwcEmptyItem = typeof(FillWaterComponent).GetField("emptyItem", BindingFlags.Instance | BindingFlags.NonPublic);
                var empty = _fwcEmptyItem?.GetValue(fwc) as Item_Base;
                if (empty != null)
                {
                    SetP2HeldHotbarItem(new ItemInstance(empty, 1, empty.MaxUses));
                    FMODUnity.RuntimeManager.PlayOneShot(fwc.er_pourWater, player2.transform.position);   // 倒水音效
                    player2.Animator?.SetAnimation(PlayerAnimation.Trigger_Plant, triggering: true);      // 倒水动作(同原版)
                    HideP2FillPrompt();
                    return;
                }
            }

            bool isSalt = salt.settings_consumeable != null && foodType == salt.settings_consumeable.FoodType;
            // 可取水：空容器(None) 或 已是盐水但未满(补满)。其它(淡水等)不在海里取。
            bool fillable = foodType == FoodType.None || (isSalt && !held.HasMaxUses);
            if (!fillable || !P2AimingAtWater()) { HideP2FillPrompt(); return; }

            SetP2InteractPrompt("Interact", Helper.GetTerm("Game/FillSaltWater"));
            _p2FillPromptShown = true;

            var action = SplitScreenRuntime.Instance?.P2?.ActionInteract;
            if (action != null && action.WasPressedThisFrame())
            {
                if (foodType == FoodType.None)
                {
                    var inst = new ItemInstance(salt, 1, salt.MaxUses);   // 空杯 → 盐水杯(满)
                    inst.SetUsesToMax();
                    SetP2HeldHotbarItem(inst);
                }
                else
                {
                    held.SetUsesToMax();   // 盐水未满 → 补满
                    UpdateP2Hotbar();
                    _p2RefreshHeldNextFrame = true;
                }
                FMODUnity.RuntimeManager.PlayOneShot(fwc.er_fillWater, player2.transform.position);   // 取水音效
                HideP2FillPrompt();
            }
        }
    }
}
