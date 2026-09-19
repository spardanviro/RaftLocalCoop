using UnityEngine;

namespace SplitScreen
{
    public static partial class Main
    {
        
        
        //
        
        
        
        
        //
        
        
        
        
        
        
        
        
        
        static Network_Player _p2CarriedPlayer;
        static float _p2CarryCooldownUntil;
        const float CarryCooldown = 0.3f;

        // P2 当前搬着的物件 —— 直接读 vanilla 的【权威状态】,不再另存影子字段。
        // 旧实现存一份 _p2Carried,但它只在 HandleP2Carry 这一条路径上被赋值;而 P2 也可能经
        // 【设备射线】拿起搬运物 —— FindCarry 只在 ri.RaycastableObjects 里找 Carry,找不到的
        // 搬运物(如剧情爆炸桶)会落到 RunDeviceRayAsP2,由 vanilla Carry.OnIsRayed 以 P2 身份
        // 直接 OnStartCarry。那条路不写影子字段 → P2IsCarrying 恒 false,一个根因引出三个症状:
        //   ① TickP2Carry 每帧早退 → 搬起来了却【放不下】;
        //   ② InteractionRouter 不短路 P2 射线 → 搬着东西还能继续交互;
        //   ③ 手持不被锁 → 【切换工具时旧模型还在手上】。
        // CarryingComponent.CarriedObject 由 Carry.StartCarrying/StopCarrying 无条件维护
        // (不看 IsLocalPlayer),剧情脚本销毁搬运物(QuestInteractable_VarunaPillar.Interact →
        // OnPreDestroy → StopCarry)也会把它清掉 → 读它天然自愈,不可能与实际状态失同步。
        // 同 _aimSpritesResolved 的教训:能直接查权威状态,就不要另设一份会失同步的标志。
        internal static Carry P2CarriedObject
        {
            get
            {
                var cc = player2 != null ? player2.CarryingComponent : null;
                var obj = cc != null ? cc.CarriedObject : null;
                return (obj != null && obj.carryingPlayer == player2) ? obj : null;
            }
        }

        internal static bool P2IsCarrying => P2CarriedObject != null || _p2CarriedPlayer != null;

        
        internal static void HandleP2Carry(Carry carry, SplitScreenRuntime rt)
        {
            if (carry == null || player2 == null || rt == null) return;
            if (carry.carryingPlayer != null || !carry.AllowCarry) { rt.UI.HidePrompt(); return; }
            if (Time.time < _p2CarryCooldownUntil) { rt.UI.HidePrompt(); return; }   
            rt.UI.ShowPrompt("Interact", Helper.GetTerm("Game/CarryAnimal"));
            if (rt.P2.ActionInteract?.WasPressedThisFrame() == true)
            {
                ConsumeP2InteractThisFrame();
                try
                {
                    using (new PlayerItemBusyScope())        // 存还共享静态 IsBusy,防泄漏给 P1
                    using (P2OriginalScope.Interaction())
                        carry.OnStartCarry?.Invoke(player2);
                    HideP2HeldModels();   // 对齐 vanilla StartCarrying 的 HideItemInHand(P2 手持模型由 mod 自管)
                }
                catch (System.Exception e) { ModEntry.Logger.Log("[P2Carry] 搬起异常: " + e.Message); }
                _p2CarryCooldownUntil = Time.time + CarryCooldown;
                rt.UI.HidePrompt();
            }
        }

        
        internal static bool TickP2Carry(SplitScreenRuntime rt)
        {
            if (TickP2PlayerCarry(rt)) return true;
            var carried = P2CarriedObject;
            if (carried == null) return false;

            // 对齐 vanilla:搬着时常驻"放下"提示(原版在 Carry.Update 里显示,那条被 IsLocalPlayer 挡住)。
            if (rt != null) rt.UI.ShowPrompt("Cancel", Helper.GetTerm("Game/DropAnimal"));

            if (rt != null && Time.time >= _p2CarryCooldownUntil
                && rt.P2.ActionCancel?.WasPressedThisFrame() == true)   
            {
                DumpCarryDiag(carried);
                try
                {
                    using (new PlayerItemBusyScope())        // 同上
                    using (P2OriginalScope.Interaction())
                        carried.OnStopCarry?.Invoke(player2, false);
                }
                catch (System.Exception e) { ModEntry.Logger.Log("[P2Carry] 放下异常: " + e.Message); }
                SuppressP2CrouchOnExit();
                _p2CarryCooldownUntil = Time.time + CarryCooldown;
                RefreshP2HeldItem();   
            }
            return true;
        }

        static bool TickP2PlayerCarry(SplitScreenRuntime rt)
        {
            if (player2 == null || player2.RessurectComponent == null) { _p2CarriedPlayer = null; return false; }

            if (_p2CarriedPlayer == null && player2.RessurectComponent.IsCarrying)
                _p2CarriedPlayer = player2.RessurectComponent.CarriedPlayer;

            if (_p2CarriedPlayer == null) return false;
            if (!player2.RessurectComponent.IsCarrying || player2.RessurectComponent.CarriedPlayer != _p2CarriedPlayer)
            {
                _p2CarriedPlayer = null;
                return false;
            }

            MaintainP2CarriedPlayerAnchor();
            var bed = FindP2AimedBed();
            if (rt != null)
            {
                if (bed != null && !bed.Busy)
                    rt.UI.ShowPrompt("Interact", Helper.GetTerm("Game/PlaceInBed", applyParameters: true));
                else
                    rt.UI.ShowPrompt("Cancel", Helper.GetTerm("Game/DropAnimal"));
            }

            if (rt != null && bed != null && !bed.Busy && rt.P2.ActionInteract?.WasPressedThisFrame() == true)
            {
                ConsumeP2InteractThisFrame();
                using (P2OriginalScope.Interaction())
                    player2.RessurectComponent.PlaceInBed(bed);
                _p2CarriedPlayer = null;
                _p2CarryCooldownUntil = Time.time + CarryCooldown;
                rt.UI.HidePrompt();
                return true;
            }

            if (rt != null && Time.time >= _p2CarryCooldownUntil && rt.P2.ActionCancel?.WasPressedThisFrame() == true)
            {
                using (P2OriginalScope.Interaction())
                    player2.RessurectComponent.StopCarryingPlayer(manipulatePosition: true);
                _p2CarriedPlayer = null;
                SuppressP2CrouchOnExit();
                _p2CarryCooldownUntil = Time.time + CarryCooldown;
                rt.UI.HidePrompt();
                RefreshP2HeldItem();
            }
            return true;
        }

        static void MaintainP2CarriedPlayerAnchor()
        {
            if (player2 == null || _p2CarriedPlayer == null || player2.RessurectComponent == null) return;
            var field = typeof(RessurectComponent).GetField("thirdPersonCarryTransform", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var anchor = field?.GetValue(player2.RessurectComponent) as Transform;
            if (anchor == null) return;

            if (_p2CarriedPlayer.transform.parent != anchor)
                _p2CarriedPlayer.transform.SetParent(anchor);
            _p2CarriedPlayer.transform.localPosition = Vector3.zero;
            _p2CarriedPlayer.transform.localEulerAngles = Vector3.zero;
            if (_p2CarriedPlayer.playerPivot != null)
                _p2CarriedPlayer.playerPivot.localEulerAngles = Vector3.zero;
        }

        static Bed FindP2AimedBed()
        {
            if (player2 == null || player2.CameraTransform == null) return null;
            if (!Physics.Raycast(player2.CameraTransform.position, player2.CameraTransform.forward, out var hit, Player.UseDistance, LayerMasks.MASK_Block, QueryTriggerInteraction.Collide))
                return null;
            if (hit.transform == null || !hit.transform.CompareTag("Bed")) return null;
            return hit.transform.GetComponent<Bed>() ?? hit.transform.GetComponentInParent<Bed>();
        }

        // [诊断] P2放下动物 native 闪退定位用,查完删除。
        static string ParentChain(Transform t, int depth)
        {
            if (t == null) return "null";
            var sb = new System.Text.StringBuilder();
            var cur = t;
            for (int i = 0; i < depth && cur != null; i++)
            {
                if (i > 0) sb.Append(" < ");
                sb.Append(cur.name);
                cur = cur.parent;
            }
            return sb.ToString();
        }

        static void DumpCarryDiag(Carry carry)
        {
            var at = carry != null ? carry.transform : null;
            var p2t = player2 != null ? player2.transform : null;
            // vanilla SetStopCarriedOffsets 会 animal.SetParent(player2.transform.parent);
            // 若目标父级本身是动物的子孙 → Transform 成环 → Unity 主线程死循环/native 崩。
            string cyc = "n/a";
            if (at != null && p2t != null && p2t.parent != null) cyc = p2t.parent.IsChildOf(at).ToString();
            ModEntry.Logger.Log("[CarryDiag] drop animal=" + (carry != null ? carry.gameObject.name : "null")
                + " type=" + (carry != null ? carry.carryObjectType.ToString() : "-")
                + " animalChain=" + ParentChain(at, 7)
                + " animalPos=" + (at != null ? at.position.ToString("F2") : "-")
                + " animalLossy=" + (at != null ? at.lossyScale.ToString("F3") : "-")
                + " p2Chain=" + ParentChain(p2t, 4)
                + " p2Pos=" + (p2t != null ? p2t.position.ToString("F2") : "-")
                + " p2Lossy=" + (p2t != null ? p2t.lossyScale.ToString("F3") : "-")
                + " WOULD_CYCLE=" + cyc);
        }

        // ── P2 与爆炸桶堆(Container)交互 ──
        // 桶堆是 vanilla Container(IRaycastable):提示在 OnRayEnter(displayText 单例=P1 半屏),
        // 交互在 OnIsRayed 里按 Interact → SpawnObjectNetworked 生成一个 Carriable_Barrel 到
        // localPlayer 手上。P2 走 RunDeviceRayAsP2 只驱动 OnIsRayed(能 spawn),但提示在 OnRayEnter、
        // 且用 P1 的 DisplayTextManager → P2 半屏无稳定提示(只随准心进出抖动偶尔闪)。
        // 这里独立接管:用 rt.UI 在 P2 半屏稳定显示提示,并以 P2 身份 spawn 桶(临时把 Container.localPlayer
        // 覆盖成 P2、在 P2OriginalScope 内调 SpawnObjectNetworked,spawn 完还原,不污染 P1 的交互)。
        static System.Reflection.FieldInfo _fContainerLocalPlayer, _fContainerObjectToSpawn;
        static System.Reflection.MethodInfo _miContainerSpawn;

        static void EnsureContainerReflection()
        {
            if (_fContainerLocalPlayer != null) return;
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var t = typeof(Container);
            _fContainerLocalPlayer   = t.GetField("localPlayer", bf);
            _fContainerObjectToSpawn = t.GetField("objectToSpawn", bf);
            _miContainerSpawn        = t.GetMethod("SpawnObjectNetworked", bf);
        }

        internal static void HandleP2Container(Container container, SplitScreenRuntime rt)
        {
            if (container == null || player2 == null || rt == null) return;
            // vanilla 门控:手上没搬东西才能取(判 P2 自己)
            if (player2.CarryingComponent != null && player2.CarryingComponent.IsCarrying) { rt.UI.HidePrompt(); return; }
            if (Time.time < _p2CarryCooldownUntil) { rt.UI.HidePrompt(); return; }

            rt.UI.ShowPrompt("Interact", Helper.GetTerm("Game/PickUp"));

            if (rt.P2.ActionInteract?.WasPressedThisFrame() == true)
            {
                ConsumeP2InteractThisFrame();
                EnsureContainerReflection();
                try
                {
                    var prevLocal = _fContainerLocalPlayer?.GetValue(container) as Network_Player;
                    _fContainerLocalPlayer?.SetValue(container, player2);
                    var toSpawn = _fContainerObjectToSpawn?.GetValue(container) as CarryNetworked;
                    using (new PlayerItemBusyScope())        // 存还共享静态 IsBusy,防泄漏给 P1
                    using (P2OriginalScope.Interaction())    // 强制 P2 为本地:StartCarry 的 IsLocalPlayer 分支作用于 P2
                    {
                        if (toSpawn != null && _miContainerSpawn != null)
                            _miContainerSpawn.Invoke(container, new object[] { toSpawn.gameObject });
                    }
                    _fContainerLocalPlayer?.SetValue(container, prevLocal);   // 还原,不污染 P1 交互
                    HideP2HeldModels();   // 搬起后收起 P2 手持工具模型(对齐 HandleP2Carry)
                }
                catch (System.Exception e) { ModEntry.Logger.Log("[P2Container] spawn 异常: " + e.Message); }
                _p2CarryCooldownUntil = Time.time + CarryCooldown;
                rt.UI.HidePrompt();
            }
        }

        internal static void ClearP2Carry()
        {
            _p2CarriedPlayer = null;
        }
    }
}
