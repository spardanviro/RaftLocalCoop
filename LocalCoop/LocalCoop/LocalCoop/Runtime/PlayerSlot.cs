using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Users;
using UnityEngine.UI;

namespace SplitScreen
{
    
    
    //
    
    
    //
    
    
    
    
    
    public sealed class PlayerSlot
    {
        
        public Network_Player Player { get; internal set; }
        public bool           IsP2   { get; }

        
        public InputAction ActionMove     { get; internal set; }
        public InputAction ActionLook     { get; internal set; }
        public InputAction ActionRotate   { get; internal set; }
        public InputAction ActionJump     { get; internal set; }
        public InputAction ActionSprint   { get; internal set; }
        public InputAction ActionCrouch   { get; internal set; }
        public InputAction ActionInteract { get; internal set; }
        public InputAction ActionMenu     { get; internal set; }
        public InputAction ActionPause    { get; internal set; }
        public InputAction ActionFire        { get; internal set; }
        public InputAction ActionContext     { get; internal set; }   
        public InputAction ActionHotbarPrev  { get; internal set; }
        public InputAction ActionHotbarNext  { get; internal set; }
        public InputAction ActionSpecial     { get; internal set; }   
        public InputAction ActionCancel      { get; internal set; }   
        public InputAction ActionBlockPick   { get; internal set; }   
        public InputAction ActionTabLeft     { get; internal set; }   
        public InputAction ActionTabRight    { get; internal set; }   
        internal InputActionMap ActionMap { get; set; }

        
        public int HotbarIndex { get; internal set; } = 0;
        internal InputUser      InputUser { get; set; }

        
        
        public bool IsInPersonController { get; internal set; }
        
        public bool IsProcessingRay      { get; internal set; }
        
        public bool IsUsingItem          { get; internal set; }

        
        
        public bool IsUsingMenu { get; internal set; }
        
        public Storage_Small CurrentStorage { get; internal set; }

        
        public Text PromptText { get; internal set; }
        public Text DeathText  { get; internal set; }

        
        public SkinnedMeshRenderer ArmMesh        { get; internal set; }
        public int                 CamOriginalMask { get; internal set; } = -1;

        
        public bool  DeathHandled { get; internal set; }
        public float DeathTimer   { get; internal set; }

        
        public PlayerSlot(bool isP2) { IsP2 = isP2; }

        internal void AccumulateDeathTime(float dt) => DeathTimer += dt;

        internal void ResetDeathState()
        {
            DeathHandled = false;
            DeathTimer   = 0f;
        }
    }
}
