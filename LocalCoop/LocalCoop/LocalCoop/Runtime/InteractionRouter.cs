using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;   // GetBindingDisplayString 扩展方法 + PlayerInput

namespace SplitScreen
{
    
    
    //
    
    //    static bool Prefix(Pickup __instance) =>
    //        SplitScreenRuntime.Instance?.Interactions.HandlePickupUpdate(__instance) ?? true;
    //
    
    
    
    
    
    
    
    //
    
    
    public sealed class InteractionRouter
    {
        readonly SplitScreenRuntime _rt;

        static readonly FieldInfo s_playerNetworkField =
            typeof(Pickup).GetField("playerNetwork", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_incapacitatedPlayerField =
            typeof(RessurectComponent).GetField("incapacitatedPlayerAtCursor", BindingFlags.Instance | BindingFlags.NonPublic);
        // Network_Player.isLocalPlayer 走 VanillaAccessors(高频跨文件,集中收口)。

        readonly List<IRaycastable> _p2Raycastables = new List<IRaycastable>();

        public InteractionRouter(SplitScreenRuntime rt) { _rt = rt; }

        
        /// <summary>
        
        
        /// </summary>
        public bool HandlePickupUpdate(Pickup instance)
        {
            if (!_rt.P2Active) return true;

            var np = s_playerNetworkField?.GetValue(instance) as Network_Player;
            if (np != _rt.P2.Player) return true; // 不是 P2 的 Pickup，放行

            Main.IsP2Steering = false;   // 每帧默认非转向;仅在下方对准方向盘且按住 D-pad 右时置真(冻结右摇杆看视角)
            Main.IsP2HoveringPickup = false;   // 每帧默认未悬停拾取物;仅在下方命中可拾取物时置真(供 P2 工具瞄准 abort 判定)

            // ── 以下完整替换 P2 的 Pickup.Update ──────────────────────
            if (_rt.P2.Player.Camera == null) return false;

            if (Main.P2ConsumedInteractThisFrame)
            { ClearRaycastables(); _rt.UI.HidePrompt(); return false; }

            // P2 睡觉中：由 Main.TickP2Bed 独占「X 站起」提示,这里不射线、不碰提示(否则每帧 HidePrompt 抢掉)。
            if (_rt.P2.Player.BedComponent != null && _rt.P2.Player.BedComponent.Sleeping)
            { ClearRaycastables(); return false; }

            // P2 坐着(椅子)中：由 Main.TickP2Seat 独占「X 起身」提示,这里不射线、不碰提示(同床)。
            if (Main.P2IsSeated)
            { ClearRaycastables(); return false; }

            
            // 就座雪橇车期间短路 P2 交互射线,防重复检测与重复上车(仿 P2IsSeated)
            if (Main.P2InSnowmobile)
            { ClearRaycastables(); return false; }

            if (Main.P2IsCarrying)
            { ClearRaycastables(); return false; }

            // P2 滑索中：禁用与物品/设备的交互(否则能和滑索杆等交互);脱离按键由骑乘 Update 处理。
            if (P2ZiplineDriver.IsAttached)
            { ClearRaycastables(); _rt.UI.HidePrompt(); return false; }

            
            
            if (Main.GlobalBlocksP2)
            {
                ClearRaycastables();
                _rt.UI.HidePrompt();
                return false;
            }

            // P2 自有 UI 打开时(背包/箱子/建造菜单/设备菜单)：不再对世界射线检测/显示交互提示。
            if (Main.IsP2BackpackOpen || Main.IsP2BuildMenuOpen || Main.IsP2MenuOpen)
            {
                ClearRaycastables();
                _rt.UI.HidePrompt();
                return false;
            }

            if (TryHandleP2DownedPlayerCarryPrompt())
            {
                ClearRaycastables();
                return false;
            }

            // 用与建造/工具一致的【已验证】相机与图层：CameraTransform(=瞄准方向) + 预定义 MASK_RaycastInteractable。
            // (之前用 Camera.transform + 1<<NameToLayer("RaycastInteractable")，后者名字解析不到→-1→1<<-1=图层31→永远打空。)
            Transform camT    = _rt.P2.Player.CameraTransform != null ? _rt.P2.Player.CameraTransform : _rt.P2.Player.Camera.transform;
            int layerMask     = LayerMasks.MASK_RaycastInteractable;
            // Player.UseDistance 是全局静态,由"最后切换视角的那个玩家"写(vanilla SetThirdPersonState:
            // FP=UseDistanceDefault 2.5 / TP=UseDistanceThirdperson 7.5)。P1 在 FP 时它就是 2.5,
            // 而 P2 的 TP 相机在身后约 3m —— 射线从相机出发,够到目标的实际距离被身后那段吃掉,
            // 稍远一点的目标(雪橇车)就完全射不到 → P2 在 TP 下看向车没有任何交互提示。
            // 按 P2 自己的视角取 vanilla 对应值(只读不写全局,P1 不受影响)。
            float reach       = Main.IsP2FirstPerson ? Player.UseDistanceDefault : Player.UseDistanceThirdperson;

            bool hit = Physics.Raycast(camT.position, camT.forward,
                                       out RaycastHit hitInfo, reach, layerMask,
                                       QueryTriggerInteraction.Collide);

            if (hit)
            {
                var ri = hitInfo.collider.GetComponent<RaycastInteractable>()
                         ?? hitInfo.collider.GetComponentInParent<RaycastInteractable>();
                if (ri == null)
                {
                    // 动物等用 RaycastInteractable_Redirect(碰撞体指向别处的 RaycastInteractable);
                    //  vanilla Helper.FindInteractable 处理了它,P2 之前没处理 → 打不到动物(搬运失效)。
                    var redir = hitInfo.collider.GetComponent<RaycastInteractable_Redirect>()
                                ?? hitInfo.collider.GetComponentInParent<RaycastInteractable_Redirect>();
                    if (redir != null) ri = redir.RaycastInteractable;
                }

                if (ri != null)
                {
                    var p2ZiplineBase = FindZiplineBase(ri);
                    if (p2ZiplineBase != null)
                    {
                        UpdateRaycastables(null);
                        P2ZiplineRopeBuilder.HandleTarget(_rt, p2ZiplineBase);
                        return false;
                    }
                    if (P2ZiplineRopeBuilder.IsBuilding)
                    {
                        UpdateRaycastables(null);
                        P2ZiplineRopeBuilder.HandleNoTarget(_rt);
                        return false;
                    }

                    // 雪橇车(Snowmobile)P2 上车:仿 zipline 独立 early-return,不掺进下方 null 级联
                    var p2Snow = FindSnowmobile(ri);
                    if (p2Snow != null)
                    {
                        UpdateRaycastables(null);
                        HandleP2Snowmobile(p2Snow);
                        return false;
                    }

                    var p2Storage  = hitInfo.collider.GetComponentInParent<Storage_Small>();
                    var p2Research = p2Storage == null ? FindResearchTable(ri) : null;
                    var p2Wheel    = (p2Storage == null && p2Research == null) ? FindSteeringWheel(ri) : null;
                    var p2Sail     = (p2Storage == null && p2Research == null && p2Wheel == null) ? FindSail(ri) : null;
                    var p2Net      = (p2Storage == null && p2Research == null && p2Wheel == null && p2Sail == null) ? ri.transform.GetComponent<ItemNet>() : null;
                    var p2Seat     = (p2Storage == null && p2Research == null && p2Wheel == null && p2Sail == null && p2Net == null) ? FindPlayerSeat(ri) : null;
                    // 爆炸桶=CarryNetworked,其 Carry(carryScript)与 collider/PickupItem/RaycastInteractable
                    // 分属根下不同子分支 → 从 collider 或 ri 向上找 Carry 会漏(Carry 在兄弟分支);而桶堆里
                    // pickupItem(下方 ri.transform 上的 PickupItem)却命中 → 误判"捡起"并与搬运提示互闪。
                    // 改从父链必经的 CarryNetworked 根取 carryScript:命中任一子碰撞体向上都经过它,故爆炸桶
                    // 稳定识别为搬运(canPickup 因 p2Carry!=null 关闭)。动物无 CarryNetworked,仍走 FindCarry。
                    var p2CarryNet = hitInfo.collider.GetComponentInParent<CarryNetworked>() ?? ri.GetComponentInParent<CarryNetworked>();
                    var p2Carry    = (p2Storage == null && p2Research == null && p2Wheel == null && p2Sail == null && p2Net == null && p2Seat == null)
                                     ? (FindCarry(ri) ?? (p2CarryNet != null ? p2CarryNet.carryScript : null) ?? hitInfo.collider.GetComponentInParent<Carry>()) : null;
                    var p2Wardrobe = (p2Storage == null && p2Research == null && p2Wheel == null && p2Sail == null && p2Net == null && p2Seat == null && p2Carry == null) ? FindWardrobe(ri) : null;
                    // 爆炸桶堆=Container(玩家取一个 Carriable_Barrel 搬走)。P2 准心多命中这个容器,
                    // 提示走 vanilla OnRayEnter(P1 半屏)→ P2 无稳定提示。独立接管见 Main.HandleP2Container。
                    var p2Container = (p2Storage == null && p2Research == null && p2Wheel == null && p2Sail == null && p2Net == null && p2Seat == null && p2Carry == null && p2Wardrobe == null)
                                      ? (FindContainer(ri) ?? ri.transform.GetComponent<Container>() ?? hitInfo.collider.GetComponentInParent<Container>()) : null;
                    // 交易站(TradingPost, IRaycastable):独立接管 → 克隆面板到 P2 半屏,不开共享菜单(P1 可同时用)。
                    var p2Trading = (p2Storage == null && p2Research == null && p2Wheel == null && p2Sail == null && p2Net == null && p2Seat == null && p2Carry == null && p2Wardrobe == null && p2Container == null)
                                    ? FindTradingPost(ri) : null;

                    if (p2Wardrobe != null)
                    {
                        UpdateRaycastables(null);
                        if (!Main.IsP2MenuOpen)
                        {
                            _rt.UI.ShowPrompt("Interact", Helper.GetTerm("Game/Open"));
                            if (_rt.P2.ActionInteract?.WasPressedThisFrame() == true)
                                Main.OpenP2Wardrobe(p2Wardrobe);
                        }
                        else _rt.UI.HidePrompt();
                    }
                    else if (p2Wheel != null)
                    {
                        // ── 方向盘:不跑 vanilla OnIsRayed(它读 P1 手柄/锁 P1 视角),由 HandleP2Steering 用 P2 输入驱动 ──
                        UpdateRaycastables(null);
                        HandleP2Steering(p2Wheel);
                    }
                    else if (p2Sail != null)
                    {
                        UpdateRaycastables(null);
                        HandleP2Sail(p2Sail);
                    }
                    else if (p2Net != null)
                    {
                        // ── 收集网:ItemNet 本身是 PickupItem → 通用拾取分支会对空网误显"按X捡到物品"。
                        //  vanilla 用 pickupItemType != ItemNet 排除通用提示,改由 ItemNet.OnIsRayed 仅在 Count>0 时提示。
                        //  这里同理:不跑 vanilla OnIsRayed(它读 P1 距离/写 P1 画布),由 HandleP2ItemNet 仅在有物品时提示+收集。
                        UpdateRaycastables(null);
                        HandleP2ItemNet(p2Net, instance);
                    }
                    else if (p2Research != null)
                    {
                        
                        
                        UpdateRaycastables(null);   
                        if (!Main.IsP2ResearchOpen) _rt.UI.ShowPrompt("Interact", Helper.GetTerm("Game/Open"));   
                        else _rt.UI.HidePrompt();
                        if (!Main.IsP2ResearchOpen && _rt.P2.ActionInteract?.WasPressedThisFrame() == true)
                            Main.OpenP2ResearchTable(p2Research);
                    }
                    else if (p2Storage != null)
                    {
                        // ── Storage 路径：直接走 UiRouter ────────────────
                        //  仅当箱子【未被占用】才提示+打开;已被打开(P1 或他人,storage.IsOpen=true)→ 不提示、不操作。
                        //  (P2 自己的箱子打开时 IsP2BackpackOpen=true → 上面 line76 已 return,到不了这里;故 IsOpen 必是别人开的,
                        //   绝不能替别人关箱。P2 关自己的箱走 TickMenu 的 B。)
                        //  【不跑 vanilla Storage.OnIsRayed】(同座椅):它在 P1 上下文读 P1 键盘 Interact +
                        //   写 P1 的 DTM 提示 → P1 按交互键(如搬动物)时会被它吃掉去开/闪 P2 看的箱子 + 置全局 IsBusy
                        //   → P1 搬运被卡。P2 开箱由下方 _rt.UI.OpenStorage(用 P2 自己的 StorageManager)独立完成,不需 OnIsRayed。
                        UpdateRaycastables(null);
                        if (_rt.P2.CurrentStorage != p2Storage)
                        {
                            _rt.UI.ShowPrompt("Interact", Helper.GetTerm("Game/Open"));   
                            if (_rt.P2.ActionInteract?.WasPressedThisFrame() == true)
                                _rt.UI.OpenStorage(p2Storage);
                        }
                        else _rt.UI.HidePrompt();
                    }
                    else if (p2Seat != null)
                    {
                        
                        
                        UpdateRaycastables(null);   
                        HandleP2Seat(p2Seat);
                    }
                    else if (p2Carry != null)
                    {
                        
                        
                        UpdateRaycastables(null);
                        Main.HandleP2Carry(p2Carry, _rt);
                    }
                    else if (p2Container != null)
                    {
                        UpdateRaycastables(null);
                        Main.HandleP2Container(p2Container, _rt);
                    }
                    else if (p2Trading != null)
                    {
                        UpdateRaycastables(null);
                        if (!Main.IsP2TradingOpen) _rt.UI.ShowPrompt("Interact", Helper.GetTerm("Game/Open"));
                        else _rt.UI.HidePrompt();
                        if (!Main.IsP2TradingOpen && _rt.P2.ActionInteract?.WasPressedThisFrame() == true)
                            Main.OpenP2TradingPost(p2Trading);
                    }
                    else
                    {
                        // ── 非 Storage 设备：完整 P2 上下文跑 OnIsRayed ──────────────
                        //  许多设备(电池座/研究台…)在 Start 缓存 localPlayer=ComponentManager<Network_Player>.Value(=P1)，
                        //  并读 localPlayer.Inventory.GetSelectedHotbarSlot() 判断手持物。P2 手持物在自定义 _p2Hotbar，
                        //  共享热栏选中槽是 P1 的 → 设备看不到 P2 手持物(电池/水瓶)，无提示、装不上。
                        //  故这里临时：①设备缓存的 localPlayer 字段 → P2；②把 P2 手持物注入共享选中热栏槽(用完同步回扣减)；
                        //  ③强制 P2 本地玩家；④换入 P2 背包(供 take/add 落到 P2)。结束全部还原。
                        RunDeviceRayAsP2(ri);
                    }

                    // ── 捡起逻辑（仅非箱子；箱子的提示已在上面 Storage 路径处理，勿在此 HidePrompt 覆盖）─────
                    var pickupItem       = ri.transform.GetComponent<PickupItem>();
                    var pickupChanneling = ri.transform.GetComponent<PickupChanneling>();
                    var p2HarvestPlant = (p2Storage == null && p2Research == null && p2Wheel == null && p2Sail == null && p2Net == null && p2Seat == null && p2Carry == null && p2Wardrobe == null && p2Trading == null) ? hitInfo.collider.GetComponentInParent<Plant>() : null;
                    bool canHarvest = p2HarvestPlant != null && p2HarvestPlant.FullyGrown() && p2HarvestPlant.harvestable && p2HarvestPlant.playerCanHarvest;

                    bool canPickup = p2Storage == null && p2Research == null && p2Wheel == null && p2Sail == null && p2Net == null && p2Seat == null && p2Carry == null && p2Container == null && p2Trading == null && pickupItem != null
                                     && pickupItem.canBePickedUp
                                     && p2Wardrobe == null && !pickupItem.CompareTag("NotPickupable");
                    bool hasYield  = p2Storage == null && p2Research == null && p2Wheel == null && p2Sail == null && p2Net == null && p2Seat == null && p2Carry == null && p2Wardrobe == null && p2Trading == null && pickupChanneling != null && pickupChanneling.HasYield;

                    if (canPickup || hasYield || canHarvest)
                    {
                        Main.IsP2HoveringPickup = true;   // P2 准心悬停可拾取物 → P2 工具瞄准应让位(对齐 vanilla abortOnItemHover)
                        string term;
                        if (canHarvest) term = Helper.GetTerm("Game/Harvest");
                        else
                        {
                            LocalizationParameters.itemX = pickupItem?.PickupName ?? "";
                            term = hasYield ? Helper.GetTerm("Game/HoldToPickupX", applyParameters: true) : Helper.GetTerm("Game/PickUpX", applyParameters: true);
                        }
                        _rt.UI.ShowPrompt("Interact", term);   
                    }
                    else if (p2Storage == null && p2Research == null && p2Wheel == null && p2Sail == null && p2Net == null && p2Seat == null && p2Carry == null && p2Wardrobe == null && p2Container == null && p2Trading == null && !Main.IsP2DevicePromptThisFrame)
                    {
                        // 设备(烹饪/熔炉…)在 OnIsRayed 里通过 DTM 写了交互提示 → 已被捕获到 P2 半屏，勿清。
                        //  椅子(p2Seat)排除:HandleP2Seat 已设「X 坐下」提示,勿在此清掉(否则瞄椅子无提示)。
                        _rt.UI.HidePrompt();
                    }

                    if (canPickup && _rt.P2.ActionInteract?.WasPressedThisFrame() == true)
                    {
                        
                        
                        
                        using (P2InteractionContext.InventoryOnly())
                            instance.PickupItemByType(pickupItem, false);
                    }

                    if (canHarvest && _rt.P2.ActionInteract?.WasPressedThisFrame() == true)
                        HarvestPlantAsP2(p2HarvestPlant);
                }
                else
                {
                    if (P2ZiplineRopeBuilder.HandleNoTarget(_rt))
                        return false;
                    ClearRaycastables();
                    _rt.UI.HidePrompt();
                }
            }
            else
            {
                if (P2ZiplineRopeBuilder.HandleNoTarget(_rt))
                    return false;
                ClearRaycastables();
                _rt.UI.HidePrompt();
            }

            return false;
        }

        bool TryHandleP2DownedPlayerCarryPrompt()
        {
            var carrier = _rt.P2.Player;
            var rc = carrier != null ? carrier.RessurectComponent : null;
            if (carrier == null || rc == null || rc.IsCarrying) return false;

            Transform camT = carrier.CameraTransform != null ? carrier.CameraTransform : carrier.Camera != null ? carrier.Camera.transform : null;
            if (camT == null) return false;

            if (!Physics.Raycast(camT.position, camT.forward, out RaycastHit hit, Player.UseDistance, LayerMasks.MASK_RemotePlayer, QueryTriggerInteraction.Collide))
                return false;

            var target = hit.transform != null ? hit.transform.GetComponentInParent<Network_Player>() : null;
            if (target == null || target == carrier || !rc.CanCarryPlayer(target)) return false;

            s_incapacitatedPlayerField?.SetValue(rc, target);
            _rt.UI.ShowPrompt("Interact", Helper.GetTerm("Game/Carry", applyParameters: true));

            if (_rt.P2.ActionInteract?.WasPressedThisFrame() == true && !Main.P2ConsumedInteractThisFrame)
            {
                Main.ConsumeP2InteractThisFrame();
                using (P2OriginalScope.Interaction())
                    rc.StartCarryingPlayer(target);
                _rt.UI.HidePrompt();
            }

            return true;
        }


        void HarvestPlantAsP2(Plant plant)
        {
            var np2 = _rt.P2.Player;
            var pm  = np2 != null ? np2.PlantManager : null;
            if (pm == null || plant == null) return;

            using (P2InteractionContext.InventoryOnly())
            {
                var message = new Message_HarvestPlant(Messages.PlantManager_HarvestPlant, pm, plant, giveYield: true);
                if (Raft_Network.IsHost)
                {
                    np2.Network.RPC(message, Target.Other, Steamworks.EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                    pm.Harvest(plant, giveYield: true);
                }
                else
                {
                    np2.SendP2P(message, Steamworks.EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                }
            }
        }

        static ResearchTable FindResearchTable(RaycastInteractable ri)
        {
            var objs = ri.RaycastableObjects;
            if (objs != null)
                foreach (var o in objs)
                    if (o is ResearchTable rt) return rt;
            return null;
        }

        static MeshPathBase FindZiplineBase(RaycastInteractable ri)
        {
            if (ri == null) return null;
            var objects = ri.RaycastableObjects;
            if (objects != null)
                foreach (var obj in objects)
                    if (obj is MeshPathBase ziplineBase) return ziplineBase;
            return ri.GetComponentInParent<MeshPathBase>();
        }

        // 同上:在 IRaycastable 列表里找方向盘(SteeringWheel 实现 IRaycastable)。
        static SteeringWheel FindSteeringWheel(RaycastInteractable ri)
        {
            var objs = ri.RaycastableObjects;
            if (objs != null)
                foreach (var o in objs)
                    if (o is SteeringWheel sw) return sw;
            return null;
        }

        static Sail FindSail(RaycastInteractable ri)
        {
            var objs = ri.RaycastableObjects;
            if (objs != null)
                foreach (var o in objs)
                    if (o is Sail s) return s;
            return null;
        }

        
        static Snowmobile FindSnowmobile(RaycastInteractable ri)
        {
            if (ri == null) return null;
            var objs = ri.RaycastableObjects;
            if (objs != null)
                foreach (var o in objs)
                {
                    if (o is Snowmobile sm) return sm;
                    var comp = o as Component;
                    if (comp != null)
                    {
                        var fromParent = comp.GetComponentInParent<Snowmobile>();
                        if (fromParent != null) return fromParent;
                    }
                }
            return ri.GetComponentInParent<Snowmobile>();
        }

        static PlayerSeat FindPlayerSeat(RaycastInteractable ri)
        {
            var objs = ri.RaycastableObjects;
            if (objs != null)
                foreach (var o in objs)
                {
                    if (o is PlayerSeat ps) return ps;
                    var comp = o as Component;
                    if (comp != null)
                    {
                        var fromParent = comp.GetComponentInParent<PlayerSeat>();
                        if (fromParent != null) return fromParent;
                    }
                }
            return ri.GetComponentInParent<PlayerSeat>();
        }

        // 同上:在 IRaycastable 列表里找动物搬运组件(Carry/Domestic_Carry 实现 IRaycastable)。
        static Carry FindCarry(RaycastInteractable ri)
        {
            var objs = ri.RaycastableObjects;
            if (objs != null)
                foreach (var o in objs)
                    if (o is Carry c) return c;
            return null;
        }

        // Container(爆炸桶堆)是 ri.RaycastableObjects 里的 IRaycastable 引用,不在 ri.transform 上,
        // 故 GetComponent 找不到 → 和 FindCarry 一样从列表里找。
        static Container FindContainer(RaycastInteractable ri)
        {
            var objs = ri.RaycastableObjects;
            if (objs != null)
                foreach (var o in objs)
                    if (o is Container c) return c;
            return null;
        }

        // 交易站在 ri.RaycastableObjects 里(同 ResearchTable),先从列表找,兜底父链。
        static TradingPost FindTradingPost(RaycastInteractable ri)
        {
            var objs = ri.RaycastableObjects;
            if (objs != null)
                foreach (var o in objs)
                    if (o is TradingPost tp) return tp;
            return ri.GetComponentInParent<TradingPost>();
        }

        static Block_Wardrobe FindWardrobe(RaycastInteractable ri)
        {
            var objs = ri.RaycastableObjects;
            if (objs != null)
                foreach (var o in objs)
                {
                    if (o is Block_Wardrobe w) return w;
                    var comp = o as Component;
                    if (comp != null)
                    {
                        var fromParent = comp.GetComponentInParent<Block_Wardrobe>();
                        if (fromParent != null) return fromParent;
                    }
                }
            return ri.GetComponentInParent<Block_Wardrobe>();
        }

        
        // 雪橇车上车(仿 HandleP2Seat):瞄准显示使用,P2 按 Interact 后 TakeP2Snowmobile
        void HandleP2Snowmobile(Snowmobile sm)
        {
            if (!Main.SnowmobileHasFree(sm)) { _rt.UI.HidePrompt(); return; }
            _rt.UI.ShowPrompt("Interact", Helper.GetTerm("Game/Use"));
            if (_rt.P2.ActionInteract?.WasPressedThisFrame() == true)
            {
                Main.TakeP2Snowmobile(sm);
                _rt.UI.HidePrompt();
            }
        }

        void HandleP2Seat(PlayerSeat seat)
        {
            if (!Main.SeatHasFree(seat)) { _rt.UI.HidePrompt(); return; }   // 满座:不提示
            _rt.UI.ShowPrompt("Interact", Helper.GetTerm("Game/Use"));
            if (_rt.P2.ActionInteract?.WasPressedThisFrame() == true)
            {
                Main.TakeP2Seat(seat);
                _rt.UI.HidePrompt();
            }
        }

        // ── P2 方向盘:按住 D-pad 右 + 右摇杆左右 → 转向(复用 vanilla 私有 Rotate,含 clamp±80 + host RPC) ──
        //  不跑 vanilla OnIsRayed(它用 GetPlayerByIndex(0)=P1 手柄并 SetLockMouseLook 锁 P1 视角)。
        //  转速对齐 vanilla:其手柄 GetXAxis(Look,"Mouse X") 即 rightStick.x,这里读 p2ActionLook.x 等价。
        //  视角冻结:置 Main.IsP2Steering=true,FP(TickP2View)/TP(HandleThirdPerson) 据此停止右摇杆看视角累加。
        static readonly MethodInfo s_wheelRotate =
            typeof(SteeringWheel).GetMethod("Rotate", BindingFlags.Instance | BindingFlags.NonPublic);

        void HandleP2Steering(SteeringWheel wheel)
        {
            // 提示:准心上方双字形([D-pad右][右摇杆]) + "长按来轻轻旋转",复刻 vanilla。
            _rt.UI.HidePrompt();
            Main.SetP2SteerPrompt("Rotate", "RotateAxis", Helper.GetTerm("Game/RotateSmooth2"));

            var gp = Main.GetP2BoundGamepad();
            bool held = gp != null && gp.dpad.right.isPressed;
            Main.IsP2Steering = held;
            if (!held) return;

            float x = Main.p2ActionLook != null ? Main.p2ActionLook.ReadValue<Vector2>().x : 0f;
            if (Mathf.Abs(x) > 0.0001f)
                s_wheelRotate?.Invoke(wheel, new object[] { x });
        }



        static readonly MethodInfo s_sailRotate =
            typeof(Sail).GetMethod("Rotate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly MethodInfo s_sailOpenM =
            typeof(Sail).GetMethod("Open", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly MethodInfo s_sailCloseM =
            typeof(Sail).GetMethod("Close", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);




        void HandleP2Sail(Sail sail)
        {
            bool open = sail.open;


            _rt.UI.ShowPrompt("Interact", Helper.GetTerm(open ? "Game/Close" : "Game/Open"));
            if (open)
                Main.SetP2SteerPrompt("Rotate", "RotateAxis", Helper.GetTerm("Game/RotateSmooth2"));

            var gp = Main.GetP2BoundGamepad();


            if (_rt.P2.ActionInteract?.WasPressedThisFrame() == true)
            {
                try { if (open) s_sailCloseM?.Invoke(sail, null); else s_sailOpenM?.Invoke(sail, null); }
                catch (System.Exception e) { Main.ModEntry.Logger.Log("[P2Sail] 升降异常: " + e.Message); }
            }


            bool held = open && gp != null && gp.dpad.right.isPressed;
            Main.IsP2Steering = held;
            if (!held) return;
            float x = Main.p2ActionLook != null ? Main.p2ActionLook.ReadValue<Vector2>().x : 0f;
            if (Mathf.Abs(x) > 0.0001f)
            {
                try { s_sailRotate?.Invoke(sail, new object[] { x }); }
                catch (System.Exception e) { Main.ModEntry.Logger.Log("[P2Sail] Rotate 异常: " + e.Message); }
            }
        }

        // ── P2 收集网:仅在网内有物品时提示+收集(对齐 vanilla;空网不提示) ──
        //  收集走 vanilla Pickup.PickupItemByType→PickupItemNet→AddCollectedItemsToPlayer。其内部
        //  RemovePickupItem 有 IsLocalPlayer 门控(P2=false),故沿用普通拾取的 forcedLocal+SwapInP2 路由,
        //  使物品真正落到 P2 背包。PickupItemNet 自身已 guard Count>0,但提示文案/显隐这里据 Count 控制。
        void HandleP2ItemNet(ItemNet net, Pickup instance)
        {
            int count = (net != null && net.itemCollector != null) ? net.itemCollector.collectedItems.Count : 0;
            if (count <= 0) { _rt.UI.HidePrompt(); return; }   // 空网:不提示(对齐 vanilla)

            LocalizationParameters.itemCount = count;
            _rt.UI.ShowPrompt("Interact", Helper.GetTerm("Game/CollectItemCount", applyParameters: true));   

            if (_rt.P2.ActionInteract?.WasPressedThisFrame() != true) return;

            using (P2InteractionContext.InventoryOnly())
                instance.PickupItemByType(net, false);
        }

        
        
        static bool _loggedRayError;

        static string _rotateGlyphKey;
        static string RotateGlyphKey()
        {
            return _rotateGlyphKey ?? (_rotateGlyphKey = "Rotate");
        }

        // ── IRaycastable 通知（维护帧间 enter / exit） ────────────────────
        void UpdateRaycastables(List<IRaycastable> newList)
        {
            // 离开旧列表中不再命中的对象
            foreach (var prev in _p2Raycastables)
                if (prev != null && (newList == null || !newList.Contains(prev)))
                    prev.OnRayExit();

            // 进入 / 持续命中新列表
            if (newList != null)
                foreach (var item in newList)
                {
                    if (item == null) continue;
                    // 防御:单个设备的 OnRayEnter/OnIsRayed 抛异常(常见于 vanilla 门控读 P2 克隆的空
                    // 子组件)不得打断 P2 整个交互循环,否则一个坏设备会连带其它设备/提示全废。
                    try
                    {
                        if (!_p2Raycastables.Contains(item)) item.OnRayEnter();
                        item.OnIsRayed();
                    }
                    catch (System.Exception e)
                    {
                        if (!_loggedRayError)
                        {
                            _loggedRayError = true;
                            Main.ModEntry.Logger.Log($"[InteractionRouter] P2 OnIsRayed 异常({item.GetType().Name}): {e.Message}");
                        }
                    }
                }

            _p2Raycastables.Clear();
            if (newList != null) _p2Raycastables.AddRange(newList);
        }

        void ClearRaycastables()
        {
            foreach (var prev in _p2Raycastables)
                prev?.OnRayExit();
            _p2Raycastables.Clear();
        }

        
        void RunDeviceRayAsP2(RaycastInteractable ri)
        {
            var np2 = _rt.P2.Player;
            var objs = ri.RaycastableObjects;
            if (np2 == null)
            {
                _rt.P2.IsProcessingRay = true;
                try { using (PlayerContext.AsP2()) UpdateRaycastables(objs); }
                finally { _rt.P2.IsProcessingRay = false; }
                return;
            }

            using (P2InteractionContext.RaycastDevice(objs))
            using (Main.BeginP2DevicePromptScope(objs))
            {
                using (PlayerContext.AsP2())
                    UpdateRaycastables(objs);
            }
        }
    }
}
