using System.Collections.Generic;
using System.Reflection;
using FMODUnity;
using UnityEngine;

namespace SplitScreen
{
    public static partial class Main
    {
        
        
        //
        
        
        
        //
        
        
        
        
        static FieldInfo _fwcSaltItem;
        static FieldInfo _fwcEmptyItem;
        static Dictionary<string, FillWaterComponent> _p2Fwcs;
        static bool _p2FillPromptShown;

        
        internal static bool IsP2FillPromptActive => _p2FillPromptShown;

        
        static string ContainerModelName(string heldName)
        {
            if (string.IsNullOrEmpty(heldName)) return null;
            if (heldName.StartsWith("PlasticCup")) return "Plastic_Cup";
            if (heldName.StartsWith("PlasticBottle")) return "PlasticBottle";
            if (heldName.StartsWith("Canteen")) return "Canteen";
            return null;
        }

        
        static FillWaterComponent FindP2ContainerFwc(string modelName)
        {
            if (player2 == null || modelName == null) return null;
            if (_p2Fwcs == null)
            {
                _p2Fwcs = new Dictionary<string, FillWaterComponent>();
                foreach (var f in player2.GetComponentsInChildren<FillWaterComponent>(true))
                    if (f != null && !_p2Fwcs.ContainsKey(f.name)) _p2Fwcs[f.name] = f;
            }
            return _p2Fwcs.TryGetValue(modelName, out var fwc) ? fwc : null;
        }

        
        static bool P2AimingAtWater()
        {
            var camT = player2.Camera.transform;
            var ray = new Ray(camT.position, camT.forward);
            var waterPlane = new Plane(Vector3.up, Vector3.zero);
            
            float reach = Mathf.Max(Player.UseDistance, Player.UseDistanceThirdperson) * 1.5f;
            return waterPlane.Raycast(ray, out float enter) && enter < reach
                   && !Physics.Raycast(ray, enter, LayerMasks.MASK_Water_with_obstruction);
        }

        static void HideP2FillPrompt()
        {
            if (_p2FillPromptShown) { ClearP2InteractPrompt(); _p2FillPromptShown = false; }
        }

        
        internal static void TickP2FillWater()
        {
            if (player2 == null || player2.Camera == null) { HideP2FillPrompt(); return; }
            if (IsP2BackpackOpen || IsP2BuildMenuOpen) { HideP2FillPrompt(); return; }

            var held = GetP2HeldHotbarItem();
            var consume = held?.settings_consumeable;
            if (consume == null || consume.FoodForm != FoodForm.Fluid) { HideP2FillPrompt(); return; }   

            var fwc = FindP2ContainerFwc(ContainerModelName(held.UniqueName));
            if (fwc == null) { HideP2FillPrompt(); return; }
            if (_fwcSaltItem == null)
                _fwcSaltItem = typeof(FillWaterComponent).GetField("saltWaterItem", BindingFlags.Instance | BindingFlags.NonPublic);
            var salt = _fwcSaltItem?.GetValue(fwc) as Item_Base;
            if (salt == null) { HideP2FillPrompt(); return; }

            var foodType = consume.FoodType;

            
            
            if (foodType != FoodType.None && p2ActionContext != null && p2ActionContext.WasPressedThisFrame())
            {
                if (_fwcEmptyItem == null)
                    _fwcEmptyItem = typeof(FillWaterComponent).GetField("emptyItem", BindingFlags.Instance | BindingFlags.NonPublic);
                var empty = _fwcEmptyItem?.GetValue(fwc) as Item_Base;
                if (empty != null)
                {
                    SetP2HeldHotbarItem(new ItemInstance(empty, 1, empty.MaxUses));
                    FMODUnity.RuntimeManager.PlayOneShot(fwc.er_pourWater, player2.transform.position);   
                    player2.Animator?.SetAnimation(PlayerAnimation.Trigger_Plant, triggering: true);      
                    HideP2FillPrompt();
                    return;
                }
            }

            bool isSalt = salt.settings_consumeable != null && foodType == salt.settings_consumeable.FoodType;
            
            bool fillable = foodType == FoodType.None || (isSalt && !held.HasMaxUses);
            if (!fillable || !P2AimingAtWater()) { HideP2FillPrompt(); return; }

            SetP2InteractPrompt("Interact", Helper.GetTerm("Game/FillSaltWater"));
            _p2FillPromptShown = true;

            var action = SplitScreenRuntime.Instance?.P2?.ActionInteract;
            if (action != null && action.WasPressedThisFrame())
            {
                if (foodType == FoodType.None)
                {
                    var inst = new ItemInstance(salt, 1, salt.MaxUses);   
                    inst.SetUsesToMax();
                    SetP2HeldHotbarItem(inst);
                }
                else
                {
                    held.SetUsesToMax();   
                    UpdateP2Hotbar();
                    _p2RefreshHeldNextFrame = true;
                }
                FMODUnity.RuntimeManager.PlayOneShot(fwc.er_fillWater, player2.transform.position);   
                HideP2FillPrompt();
            }
        }
    }
}
