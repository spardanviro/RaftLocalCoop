using UnityEngine;

namespace SplitScreen
{
    // World models stay on the proven TP animation path. These rigs contain only
    // the local FP arm mesh and its own original FP controller.
    internal static class FirstPersonVisualRig
    {
        sealed class Rig
        {
            internal Network_Player Player;
            internal CharacterModelModifications Source;
            internal SkinnedMeshRenderer SourceArm;
            internal GameObject Root;
            internal CharacterModelModifications Clone;
            internal SkinnedMeshRenderer Arm;
            internal Animator Animator;
            // 创建时的局部变换基准。AlignSelfViewToCamera 每帧直接写 rig 根的世界位姿,
            // 若不复位,离座后没人再写 -> 根永远停在被改歪的局部变换上 ->
            // 帽子(借挂 rig 的 hatParent)位置错乱、普通 FP 相机(取 holder.position)跟着偏高。
            internal Vector3 BaseLocalPos;
            internal Quaternion BaseLocalRot;
            // 就座时把 rig 按原版的层级方式挂到座位 FP 锚点上(见 MountRigToSeat)。
            internal Transform OriginalParent;
            internal Transform SeatYawNode;
            internal Transform SeatPitchNode;
            internal bool SeatMounted;
            internal int SelectedItemIndex = int.MinValue;
            // FP 自视图帽子:把真实装备的 localModel 借挂到本 rig 克隆的 hatParent(跟 FP 动画→相机相对稳定遮罩)。
            internal Transform MountedHat;
            internal Transform MountedHatOriginalParent;
            // 借挂期间被强开的渲染器 + 其原始 enabled,UnmountHat 时原样还回。
            internal Renderer[] MountedHatRenderers;
            internal bool[] MountedHatRendererStates;
            // 滑索期间帽子改挂到相机下的这个节点(见 SyncHatMountParent)。
            internal Transform HatPitchNode;
        }

        static Rig _p1;
        static Rig _p2;

        internal static void Tick()
        {
            Ensure(Main.player1, false);
            Ensure(Main.player2, true);
            Sync(_p1);
            Sync(_p2);
        }

        internal static SkinnedMeshRenderer GetArm(Network_Player player)
        {
            var rig = GetRig(player);
            return rig != null && rig.Arm != null ? rig.Arm : Main.GetArmMesh(player != null ? player.currentModel : null);
        }

        // 就座 FP 的原版眼位。
        //
        // vanilla(AttachPlayer.StartCarryingPlayer/Update):玩家根挂 firstPersonParent、
        // localPosition/localEuler 归零、全身坐姿置 None_0(站姿),相机挂在头骨 cameraHolder 上。
        // 即【眼位 = FP 锚点 + 站姿骨骼的头部偏移】,是纯层级结构算出来的不变量。
        //
        // 分屏里 P2 的身体必须钉在 tpParent(世界模型要给对方看正确坐姿),所以自视图眼位
        // 得自己按 fpParent 重算。曾经的写法是"站立时把眼位采样记下来,就座时套用",
        // 那引入了两个致命面:采样时机(躺浴缸时采到低眼位 -> 偏低)和反馈回路
        // (取不到座位时回退 holder.position,而 holder 正是 AlignSelfViewToCamera 每帧在搬的
        //  rig -> 相机抄 rig、rig 追相机 -> 乱飘)。
        //
        // 现在回到原版的结构式算法:偏移量取【rig 的 cameraHolder 相对 rig 根】。
        // rig 的 FullBodyIndex 已归零 = 站姿,而 holder 是 root 的后代,
        // root.InverseTransformPoint(holder.position) 只反映层级链上的累积局部偏移,
        // 与 root 被摆在世界何处、朝向如何【完全无关】-> 既不需要采样,也不可能形成反馈。
        internal static bool TryGetSeatedEyePosition(Network_Player player, AttachPlayer attach, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (player == null || attach == null || attach.firstPersonParent == null) return false;
            var rig = GetRig(player);
            if (rig == null || rig.Root == null || rig.Clone == null || rig.Clone.cameraHolder == null) return false;
            var local = rig.Root.transform.InverseTransformPoint(rig.Clone.cameraHolder.position);
            pos = attach.firstPersonParent.TransformPoint(local);
            return true;
        }

        internal static Transform GetCameraHolder(Network_Player player)
        {
            var rig = GetRig(player);
            return rig != null && rig.Clone != null ? rig.Clone.cameraHolder : null;
        }

        internal static void MountForFirstPerson(Network_Player player)
        {
            var rig = GetRig(player);
            if (rig == null || rig.Clone == null) return;
            Mount(player.leftHandParent, rig.Clone.leftHandItemHolder);
            Mount(player.rightHandParent, rig.Clone.rightHandItemHolder);
            SetSourceArmVisible(rig, false);
        }

        // 就座自视图挂载 —— 照【原版对身体做的事】做,只是对象换成 rig 克隆。
        //
        // vanilla 的链路是:玩家根(承载偏航,MouseLookX) -> CharacterPivot(承载俯仰,MouseLookY)
        //   -> Character/Animator -> 模型;相机挂在模型的 cameraHolder(头骨)上。
        // 就座时玩家根被 SetParent(firstPersonParent) 且局部位姿归零,于是眼位与环视
        // 全部由层级关系自然产生,原版自己一个坐标都不用算。
        //
        // 分屏里真实身体必须钉在 tpParent(世界模型要给对方看正确坐姿),所以这条链改由 rig 走:
        // SeatYawNode(相当于玩家根) -> SeatPitchNode(相当于 CharacterPivot) -> rig 根。
        // 两个节点的局部量都从【真实身体的静态层级】现读(playerPivot 的局部位置、
        // 世界模型相对 playerPivot 的位姿),不是采样保存的快照,也不被我们写过 -> 不可能漂移。
        //
        // 这样就没有"我们算的相机位置"这回事了:相机直接取 rig 的 cameraHolder。
        // 先前的 AlignSelfViewToCamera 用命令式每帧覆写 rig 世界位姿来模拟这个结果,
        // 因而需要记基准/判帧号/复位一整套辅助机制 —— 那套机制的存在本身就是设计不对的信号。
        internal static void MountRigToSeat(Network_Player player, AttachPlayer attach, float yaw, float pitch)
        {
            var rig = GetRig(player);
            if (rig == null || rig.Root == null || player == null) return;
            if (attach == null || attach.firstPersonParent == null) return;

            if (rig.SeatYawNode == null)
            {
                rig.SeatYawNode = new GameObject("FpRigSeatYaw").transform;
                rig.SeatPitchNode = new GameObject("FpRigSeatPitch").transform;
                rig.SeatPitchNode.SetParent(rig.SeatYawNode, false);
            }

            if (rig.SeatYawNode.parent != attach.firstPersonParent)
                rig.SeatYawNode.SetParent(attach.firstPersonParent, false);
            rig.SeatYawNode.localPosition = Vector3.zero;
            rig.SeatYawNode.localRotation = Quaternion.Euler(0f, yaw, 0f);

            var pivot = player.playerPivot;
            rig.SeatPitchNode.localPosition = pivot != null ? pivot.localPosition : Vector3.zero;
            rig.SeatPitchNode.localRotation = Quaternion.Euler(pitch, 0f, 0f);

            var root = rig.Root.transform;
            if (root.parent != rig.SeatPitchNode) root.SetParent(rig.SeatPitchNode, false);
            if (pivot != null && rig.Source != null)
            {
                root.localPosition = pivot.InverseTransformPoint(rig.Source.transform.position);
                root.localRotation = Quaternion.Inverse(pivot.rotation) * rig.Source.transform.rotation;
            }
            rig.SeatMounted = true;
        }

        // 离座:把 rig 还挂回原父级并恢复基准局部位姿(同 UnmountHands/UnmountHat 的还原义务)。
        internal static void UnmountRigFromSeat(Network_Player player)
        {
            var rig = GetRig(player);
            if (rig == null || !rig.SeatMounted || rig.Root == null) return;
            rig.SeatMounted = false;
            if (rig.OriginalParent != null) rig.Root.transform.SetParent(rig.OriginalParent, false);
            rig.Root.transform.localPosition = rig.BaseLocalPos;
            rig.Root.transform.localRotation = rig.BaseLocalRot;
        }

        internal static void MountForWorld(Network_Player player)
        {
            if (player == null || player.currentModel == null) return;
            Mount(player.leftHandParent, player.currentModel.leftHandItemHolder);
            Mount(player.rightHandParent, player.currentModel.rightHandItemHolder);
        }

        static System.Reflection.FieldInfo _localModelField;
        static System.Reflection.FieldInfo _localModelNameField;
        static readonly System.Collections.Generic.List<Equipment_Hat> _hatBuf = new System.Collections.Generic.List<Equipment_Hat>();
        static readonly System.Collections.Generic.List<Renderer> _hatRendBuf = new System.Collections.Generic.List<Renderer>();

        // 每帧从 EnforceMeshStates(P1)/EnforceP2FpArms(P2)调用,fp 为该玩家是否处于第一人称。
        // FP 时把当前装备的帽子 localModel 借挂到本 rig 克隆的 hatParent、放手部层、激活(vanilla 会
        // 偶发把 localModel 关掉,故每帧重申激活);非 FP/无帽/换帽时还原上一顶。
        internal static void SyncHat(Network_Player player, bool fp)
        {
            var rig = GetRig(player);
            if (rig == null || rig.Clone == null) return;

            Transform desired = fp ? GetEquippedHatLocalModel(player) : null;
            if (ReferenceEquals(desired, rig.MountedHat))
            {
                if (rig.MountedHat != null) EnsureHatEnforced(rig, player);
                return;
            }
            UnmountHat(rig);
            if (desired != null) MountHat(rig, player, desired);
            SyncHatMountParent(rig, player);
        }

        // 搬运中的动物/物件的握持偏移有 FP(carryPosOffset/carryRotOffset)与
        // TP(thirdPcarryPosOffset/thirdPcarryRotOffset)两套。vanilla 在
        // SetStartCarriedOffsets 里按 thirdPersonSettings.ThirdPersonState 二选一,并靠订阅
        // OnThirdpersonModelChange 在切视角时用私有 OnPerspectiveSwap(bool) 自愈。
        // 但 mod 强制两个玩家的世界模型恒为 TP、FP 是纯视觉伪装(从不改 ThirdPersonModel),
        // 那个事件永不触发 → 恒取 TP 偏移;而 FP 下动物实际挂在 rig 克隆的 FP 手上 → 朝向不对。
        // 故每帧按该玩家真实的 FP 状态主动调 vanilla 自己的 OnPerspectiveSwap(复用原版,不自算偏移)。
        static readonly System.Collections.Generic.Dictionary<System.Type, System.Reflection.MethodInfo> _swapCache =
            new System.Collections.Generic.Dictionary<System.Type, System.Reflection.MethodInfo>();
        static readonly object[] _swapArgs = new object[1];

        internal static void SyncCarriedPerspective(Network_Player player, bool fp)
        {
            var cc = player != null ? player.CarryingComponent : null;
            var carried = cc != null ? cc.CarriedObject : null;
            if (carried == null) return;

            // Carry 组件挂在被搬物体上,持有偏移的是其祖先(AI_NetworkBehaviour_Domestic / CarryNetworked)。
            Component owner = carried.GetComponentInParent<AI_NetworkBehaviour_Domestic>();
            if (owner == null) owner = carried.GetComponentInParent<CarryNetworked>();
            if (owner == null) return;

            var t = owner.GetType();
            System.Reflection.MethodInfo mi;
            if (!_swapCache.TryGetValue(t, out mi))
            {
                mi = HarmonyLib.AccessTools.Method(t, "OnPerspectiveSwap", new[] { typeof(bool) });   // 私有,可能在基类
                _swapCache[t] = mi;
            }
            if (mi == null) return;
            _swapArgs[0] = !fp;
            try { mi.Invoke(owner, _swapArgs); }
            catch (System.Exception e) { Main.LogV("[CarryPerspective] " + e.Message); }
        }

        // ---- 对方视角搬运动物位置(per-camera,复用 OnPerspectiveSwap) ----
        // 对方相机把 carrier 渲染为 TP 世界模型,但 carrier 处于 FP 时动物挂在 rig 克隆的 FP 手上+FP偏移
        // → 对方看着与 TP 身体脱节。故渲染对方相机前把动物临时移到 carrier 世界模型的 TP 手
        // (currentModel.rightHandItemHolder)+TP偏移,渲染后还原到自视角 FP 摆位。仅 carrier 处于 FP
        // 且正搬动物时介入;carrier 自视角相机 pass 不调用 → 自视角保持不变(TP 搬运本就两视角都对,不介入)。
        static Component GetCarryOwner(Carry carried)
        {
            if (carried == null) return null;
            Component owner = carried.GetComponentInParent<AI_NetworkBehaviour_Domestic>();
            if (owner == null) owner = carried.GetComponentInParent<CarryNetworked>();
            return owner;
        }

        static void InvokeCarryPerspective(Component owner, bool toThirdPerson)
        {
            if (owner == null) return;
            var t = owner.GetType();
            System.Reflection.MethodInfo mi;
            if (!_swapCache.TryGetValue(t, out mi))
            {
                mi = HarmonyLib.AccessTools.Method(t, "OnPerspectiveSwap", new[] { typeof(bool) });
                _swapCache[t] = mi;
            }
            if (mi == null) return;
            _swapArgs[0] = toThirdPerson;
            try { mi.Invoke(owner, _swapArgs); }
            catch (System.Exception e) { Main.LogV("[CarryPerspective] " + e.Message); }
        }

        static Transform _repoAnimal, _repoSavedParent;
        static Vector3 _repoSavedPos;
        static Quaternion _repoSavedRot;
        static bool _repoActive;

        internal static void BeginCarriedAnimalTpForOtherView(Network_Player carrier)
        {
            if (_repoActive || carrier == null || carrier.currentModel == null) return;
            var cc = carrier.CarryingComponent;
            var carry = cc != null ? cc.CarriedObject : null;
            if (carry == null) return;
            var rhp = carrier.rightHandParent;
            var holder = carrier.currentModel.rightHandItemHolder;
            if (rhp == null || holder == null) return;
            // 动物根 = rightHandParent 下含 Carry 的那个直接子(vanilla SetStartCarriedOffsets 挂到此)。
            Transform root = carry.transform;
            while (root != null && root.parent != rhp) root = root.parent;
            if (root == null) return;   // 未按预期挂 rightHandParent 下(已TP/异常),不介入
            _repoAnimal = root;
            _repoSavedParent = root.parent;
            _repoSavedPos = root.localPosition;
            _repoSavedRot = root.localRotation;
            root.SetParent(holder, false);
            InvokeCarryPerspective(GetCarryOwner(carry), true);   // TP 偏移(相对 world hand)
            _repoActive = true;
        }

        internal static void RestoreCarriedAnimalTpForOtherView()
        {
            if (!_repoActive) return;
            _repoActive = false;
            if (_repoAnimal != null && _repoSavedParent != null)
            {
                _repoAnimal.SetParent(_repoSavedParent, false);
                _repoAnimal.localPosition = _repoSavedPos;
                _repoAnimal.localRotation = _repoSavedRot;
            }
            _repoAnimal = null;
            _repoSavedParent = null;
        }

        static Transform GetEquippedHatLocalModel(Network_Player player)
        {
            if (player == null || player.PlayerEquipment == null) return null;
            if (_localModelField == null)
                _localModelField = typeof(Equipment_Model).GetField("localModel",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (_localModelField == null) return null;

            _hatBuf.Clear();
            player.PlayerEquipment.GetComponentsInChildren(true, _hatBuf);
            for (int i = 0; i < _hatBuf.Count; i++)
            {
                var h = _hatBuf[i];
                if (h != null && h.Equipped)
                {
                    var lm = ResolveHatLocalModel(h, player);
                    if (lm != null) return lm;
                }
            }
            return null;
        }

        // P2 是非本地克隆:vanilla 只给远程玩家建 remoteModel,Equipment_Model.localModel(FP帽子)常为 null。
        // 仿 Equipment_Model.Initialize 的按名解析:用 localModelName 从 currentModel/hatParent 里 Find 出来并缓存。
        static Transform ResolveHatLocalModel(Equipment_Hat h, Network_Player player)
        {
            var lm = _localModelField.GetValue(h) as Transform;
            if (lm != null) return lm;
            if (player == null || player.currentModel == null) return null;
            if (_localModelNameField == null)
                _localModelNameField = typeof(Equipment_Model).GetField("localModelName",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            string name = _localModelNameField?.GetValue(h) as string;
            if (string.IsNullOrEmpty(name)) return null;
            var found = player.currentModel.transform.Find(name);
            if (found == null && player.currentModel.hatParent != null)
                found = player.currentModel.hatParent.Find(name);
            if (found != null) _localModelField.SetValue(h, found);   // 缓存回 Equipment_Model
            return found;
        }

        static int HatLayer(Network_Player player) =>
            player == Main.player2 ? Main.LAYER_P2_HAND : Main.LAYER_P1_HAND;

        static void MountHat(Rig rig, Network_Player player, Transform lm)
        {
            var hatParent = rig.Clone != null ? rig.Clone.hatParent : null;
            if (hatParent == null || lm == null) return;
            rig.MountedHat = lm;
            rig.MountedHatOriginalParent = lm.parent;
            // worldPositionStays=false 保留 localModel 相对 hatParent 的原始偏移(clone.hatParent 与
            // 世界 hatParent 语义一致)→ 摆位正确;克隆跑 firstPersonController → 跟 FP 姿势/相机固定。
            lm.SetParent(hatParent, false);
            SetLayerRecursively(lm, HatLayer(player));
            CacheAndEnableHatRenderers(rig, lm);
            if (!lm.gameObject.activeSelf) lm.gameObject.SetActive(true);
        }

        static bool OnZipline(Network_Player p) =>
            p != null && p.ZiplinePlayer != null && p.ZiplinePlayer.IsAttachedToZipline;

        // 滑索上相机的俯仰是 mod 显式算出来的(P2CameraController.SetFirstPersonCameraTransform),
        // 而 rig 播的悬挂动画头骨【不】跟着俯仰 → 帽子挂在 rig.hatParent 上就不跟镜头,定在一个方向。
        // 帽子在 FP 里本质是贴着视线的遮罩,应当跟相机走。
        // 做法(结构式,不采样):在相机下挂一个节点,每帧把「hatParent 相对 cameraHolder 的位姿」
        // 原样复制成它的局部位姿 —— 这两者都在 rig 上、我们从不写它们,故无反馈回路;
        // 帽子改挂到该节点 → 位置仍在头上的正确偏移,朝向随镜头俯仰。
        // 只对 P2 生效:P1 走 vanilla 相机,不能动。
        static void SyncHatMountParent(Rig rig, Network_Player player)
        {
            var lm = rig.MountedHat;
            if (lm == null || rig.Clone == null) return;
            var hatParent = rig.Clone.hatParent;
            if (hatParent == null) return;

            Transform want = hatParent;
            var holder = rig.Clone.cameraHolder;
            if (player == Main.player2 && OnZipline(player) && holder != null && player.CameraTransform != null)
            {
                var node = rig.HatPitchNode;
                if (node == null)
                {
                    node = new GameObject("FpHatPitchNode").transform;
                    rig.HatPitchNode = node;
                }
                var cam = player.CameraTransform;
                if (node.parent != cam) node.SetParent(cam, false);
                node.localPosition = holder.InverseTransformPoint(hatParent.position);
                node.localRotation = Quaternion.Inverse(holder.rotation) * hatParent.rotation;
                want = node;
            }
            if (lm.parent != want) lm.SetParent(want, false);
        }
        // P2 是非本地克隆:vanilla 不为远程玩家显示 FP 模型,localModel 子树的 Renderer 恒 disabled,
        // 只 SetActive 挂上去仍然看不见(实测:删掉本段则 P2 FP 看不到鲨鱼头)。挂载时收集一次并强开,
        // 之后每帧只遍历缓存重申(避免每帧 GetComponentsInChildren)。
        static void CacheAndEnableHatRenderers(Rig rig, Transform lm)
        {
            _hatRendBuf.Clear();
            lm.GetComponentsInChildren(true, _hatRendBuf);
            rig.MountedHatRenderers = _hatRendBuf.ToArray();
            rig.MountedHatRendererStates = new bool[rig.MountedHatRenderers.Length];
            for (int i = 0; i < rig.MountedHatRenderers.Length; i++)
            {
                var r = rig.MountedHatRenderers[i];
                if (r == null) continue;
                rig.MountedHatRendererStates[i] = r.enabled;
                if (!r.enabled) r.enabled = true;
            }
        }

        // vanilla SetModelState 会偶发关掉 localModel,故每帧重申(同 activeSelf/layer 那两行的理由)。
        static void ReEnableHatRenderers(Rig rig)
        {
            var rends = rig.MountedHatRenderers;
            if (rends == null) return;
            for (int i = 0; i < rends.Length; i++)
                if (rends[i] != null && !rends[i].enabled) rends[i].enabled = true;
        }

        static void EnsureHatEnforced(Rig rig, Network_Player player)
        {
            var lm = rig.MountedHat;
            if (lm == null) return;
            if (!lm.gameObject.activeSelf) lm.gameObject.SetActive(true);
            if (lm.gameObject.layer != HatLayer(player)) SetLayerRecursively(lm, HatLayer(player));
            ReEnableHatRenderers(rig);
            SyncHatMountParent(rig, player);
        }

        static void UnmountHat(Rig rig)
        {
            var lm = rig.MountedHat;
            rig.MountedHat = null;
            if (lm != null)
            {
                if (rig.MountedHatOriginalParent != null) lm.SetParent(rig.MountedHatOriginalParent, false);
                lm.gameObject.SetActive(false);   // 世界 TP 态 localModel 本就应关闭(mod 强制 TP)
            }
            var rends = rig.MountedHatRenderers;
            var states = rig.MountedHatRendererStates;
            if (rends != null && states != null)
                for (int i = 0; i < rends.Length && i < states.Length; i++)
                    if (rends[i] != null) rends[i].enabled = states[i];
            rig.MountedHatRenderers = null;
            rig.MountedHatRendererStates = null;
            rig.MountedHatOriginalParent = null;
        }

        internal static void SetArmVisible(Network_Player player, bool visible)
        {
            var rig = GetRig(player);
            if (rig == null || rig.Arm == null) return;
            if (rig.Arm.gameObject.activeSelf != visible) rig.Arm.gameObject.SetActive(visible);
            SetSourceArmVisible(rig, false);
        }

        internal static void ForwardAnimation(PlayerAnimator source, PlayerAnimation animation, bool overrideIndex, bool triggering)
        {
            var rig = FindByAnimator(source);
            if (rig == null || rig.Animator == null) return;

            string[] parts = animation.ToString().Split('_');
            if (parts.Length == 0) return;
            if (parts[0] == "Index" && parts.Length == 3)
            {
                int index;
                if (int.TryParse(parts[1], out index))
                {
                    // Match PlayerAnimator.SetAnimationIndex: Zipline's build
                    // preview asks for Index_1_Point every frame, but the source
                    // animator only switches when the selected index changes.
                    if (overrideIndex || rig.SelectedItemIndex != index)
                    {
                        rig.SelectedItemIndex = index;
                        rig.Animator.SetInteger("ItemID", index);
                        rig.Animator.SetTrigger("Switch");
                    }
                }
            }
            else if (parts[0] == "Trigger" && parts.Length == 2)
            {
                rig.Animator.SetTrigger(parts[1]);
                if (triggering) rig.Animator.SetBool("Triggering", true);
            }
        }

        internal static void ForwardReselect(PlayerAnimator source)
        {
            var rig = FindByAnimator(source);
            if (rig == null || rig.Animator == null) return;
            rig.SelectedItemIndex = source.selectedIndex;
            rig.Animator.SetInteger("ItemID", source.selectedIndex);
            rig.Animator.SetTrigger("Switch");
        }

        static void Ensure(Network_Player player, bool isP2)
        {
            if (player == null || player.currentModel == null) return;
            Rig rig = isP2 ? _p2 : _p1;
            if (rig != null && rig.Source == player.currentModel && rig.Root != null) return;

            Destroy(ref rig);
            if (isP2) _p2 = null; else _p1 = null;

            var source = player.currentModel;
            var root = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
            root.name = isP2 ? "P2_FirstPersonArms" : "P1_FirstPersonArms";
            root.transform.localPosition = source.transform.localPosition;
            root.transform.localRotation = source.transform.localRotation;
            root.transform.localScale = source.transform.localScale;
            root.SetActive(false);

            var clone = root.GetComponent<CharacterModelModifications>();
            if (clone == null)
            {
                UnityEngine.Object.Destroy(root);
                return;
            }
            var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
                if (behaviours[i] != null) behaviours[i].enabled = false;

            // This clone is a render-only FP arm rig.  Cloning the complete
            // character prefab also clones EntityCollider_* hitboxes; unlike the
            // original local-player path, they would otherwise remain invisible
            // physical colliders and collide with the raft.
            var colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                if (colliders[i] != null)
                {
                    colliders[i].enabled = false;
                    UnityEngine.Object.Destroy(colliders[i]);
                }

            // 同理:HatParent 下预挂着全部帽子模型,其中头灯(HeadLight/HeadLight_Advanced)自带 Light。
            // 它既不是 MonoBehaviour(上面的禁用管不到)也不是 Renderer(下面那句"只让胳膊网格 enabled"
            // 也管不到),于是克隆出的那盏灯永久常亮:不装备也有头灯效果,装备后还会与真灯叠加。
            // 而且原版卸下逻辑够不到它 —— Equipment_HeadLight 只认自己那份 lightSourceLight。
            // 【只在创建时销毁一次】:之后 SyncHat 借挂进来的是真帽子(连同真灯),不受影响。
            var lights = root.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; i++)
                if (lights[i] != null)
                {
                    lights[i].enabled = false;
                    UnityEngine.Object.Destroy(lights[i]);
                }

            // 同一类漏网(既非 MonoBehaviour 也非 Renderer,三道清理都不沾):
            // AudioSource 会变成跟着胳膊跑的幽灵音源,ParticleSystem 会是装备特效的第二份。
            // 与 Light 一样只在创建时清一次,不影响之后借挂进来的真装备。
            var audios = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < audios.Length; i++)
                if (audios[i] != null) { audios[i].Stop(); UnityEngine.Object.Destroy(audios[i]); }

            var particles = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < particles.Length; i++)
                if (particles[i] != null) UnityEngine.Object.Destroy(particles[i]);

            var animator = root.AddComponent<Animator>();
            var sourceAnimator = source.GetComponentInParent<Animator>();
            animator.avatar = sourceAnimator != null ? sourceAnimator.avatar : null;
            animator.runtimeAnimatorController = source.firstPersonController;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var arm = Main.GetArmMesh(clone);
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;
                renderer.enabled = arm != null && renderer.transform.IsChildOf(arm.transform);
            }
            if (clone.fullBodyMesh != null) clone.fullBodyMesh.gameObject.SetActive(false);
            if (arm != null) arm.gameObject.SetActive(true);
            root.SetActive(true);
            animator.Rebind();

            rig = new Rig
            {
                Player = player,
                Source = source,
                SourceArm = Main.GetArmMesh(source),
                Root = root,
                Clone = clone,
                Arm = arm,
                Animator = animator,
                OriginalParent = root.transform.parent,
                BaseLocalPos = root.transform.localPosition,
                BaseLocalRot = root.transform.localRotation
            };
            SetSourceArmVisible(rig, false);
            SetLayerRecursively(root.transform, isP2 ? Main.LAYER_P2_HAND : Main.LAYER_P1_HAND);
            if (isP2) _p2 = rig; else _p1 = rig;
        }

        static void Sync(Rig rig)
        {
            if (rig == null || rig.Source == null || rig.Animator == null) return;
            var sourceAnimator = rig.Source.GetComponentInParent<Animator>();
            if (sourceAnimator == null) return;
            if (rig.Source.thirdPersonController != null && sourceAnimator.runtimeAnimatorController != rig.Source.thirdPersonController)
            {
                sourceAnimator.runtimeAnimatorController = rig.Source.thirdPersonController;
                sourceAnimator.applyRootMotion = false;
            }

            var parameters = sourceAnimator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                try
                {
                    if (p.type == AnimatorControllerParameterType.Float) rig.Animator.SetFloat(p.nameHash, sourceAnimator.GetFloat(p.nameHash));
                    else if (p.type == AnimatorControllerParameterType.Int) rig.Animator.SetInteger(p.nameHash, sourceAnimator.GetInteger(p.nameHash));
                    else if (p.type == AnimatorControllerParameterType.Bool) rig.Animator.SetBool(p.nameHash, sourceAnimator.GetBool(p.nameHash));
                }
                catch { }
            }

            // 全身坐姿动画只写一个整数参数 FullBodyIndex(PlayerAnimator.SetAnimation(PlayerFullBodyAnimation)),
            // 上面的参数拷贝会把它一并抄给克隆 -> 自视图 rig 也播坐姿。
            // 但 vanilla 的本地 FP 玩家【从不】播全身坐姿:AttachPlayer.Update 对本地 FP 置 None_0。
            // 本 rig 正是"玩家看自己"的那一份,必须照 vanilla 归零,否则相机(挂 rig 的 cameraHolder)
            // 会骑到坐着的头骨上 -> 视点偏低偏后、且转视角时绕身体旋转中心而非眼睛画弧。
            // 世界模型不受影响,继续保留坐姿供【对方】观看 —— 这正是分屏需要的解耦:
            // 一具身体两个观众,对方要坐姿、自己要原版站姿眼位。
            // 睡觉例外:睡眠相机就是贴着 rig 的 holder 定位的,归零会挪动它。
            // 例外:全身动画本身就是"自视图该看到的姿势"的场合,不能归零。
            //   睡觉 —— 睡眠相机贴着 rig 的 holder 定位,归零会把它挪走;
             //   滑索 —— 悬挂姿势决定了手/手持工具/帽子的位置。归零后 rig 播站姿而身体在悬挂,
            //           表现为工具浮在身前、帽子不跟俯仰(椅子浴缸马桶无此问题,
            //           因为那几处的自视图本来就该是站姿眼位)。
            bool sleeping = rig.Player != null && rig.Player.BedComponent != null && rig.Player.BedComponent.Sleeping;
            bool onZipline = rig.Player != null && rig.Player.ZiplinePlayer != null
                             && rig.Player.ZiplinePlayer.IsAttachedToZipline;
            if (!sleeping && !onZipline) rig.Animator.SetInteger("FullBodyIndex", 0);

            SetSourceArmVisible(rig, false);

            // 【不要在这里每帧复位 rig 根】。复位只在离座那一次做(见 UnmountRigFromSeat)。
            // 每帧复位会和其他每帧调整 rig 相关变换的系统对着干 —— 实测滑索(ZiplineRenderFix)
            // 下工具位置错乱、帽子不跟俯仰,就是这么来的。就座期间由 MountRigToSeat 用层级接管,
            // 非就座期间 rig 本就跟着身体走,无需任何人每帧去写它。
        }

        static Rig GetRig(Network_Player player)
        {
            if (player == Main.player1) return _p1;
            if (player == Main.player2) return _p2;
            return null;
        }

        static Rig FindByAnimator(PlayerAnimator animator)
        {
            if (_p1 != null && _p1.Player != null && _p1.Player.Animator == animator) return _p1;
            if (_p2 != null && _p2.Player != null && _p2.Player.Animator == animator) return _p2;
            return null;
        }

        static void Mount(Transform handParent, Transform holder)
        {
            if (handParent == null || holder == null) return;
            if (handParent.parent != holder) handParent.SetParent(holder, false);
            handParent.localPosition = Vector3.zero;
            handParent.localRotation = Quaternion.identity;
            if (!handParent.gameObject.activeSelf) handParent.gameObject.SetActive(true);
        }

        static void SetSourceArmVisible(Rig rig, bool visible)
        {
            if (rig != null && rig.SourceArm != null && rig.SourceArm.gameObject.activeSelf != visible)
                rig.SourceArm.gameObject.SetActive(visible);
        }

        static void SetLayerRecursively(Transform root, int layer)
        {
            if (root == null || layer < 0) return;
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++) SetLayerRecursively(root.GetChild(i), layer);
        }

        // MountForFirstPerson 把玩家的 leftHandParent/rightHandParent 借挂到本 rig 克隆的
        // handItemHolder 下。销毁克隆前必须还回世界模型,否则手部父物体(连同挂在手上的
        // 东西,如正被搬运的动物)随克隆一起被永久销毁 —— 同 UnmountHat 的理由。
        static void UnmountHands(Rig rig)
        {
            if (rig == null || rig.Player == null || rig.Root == null) return;
            var rootT = rig.Root.transform;
            var model = rig.Player.currentModel;
            ReturnHand(rig.Player.leftHandParent, rootT, model != null ? model.leftHandItemHolder : null, rig.Player.transform);
            ReturnHand(rig.Player.rightHandParent, rootT, model != null ? model.rightHandItemHolder : null, rig.Player.transform);
        }

        static void ReturnHand(Transform hand, Transform rigRoot, Transform worldHolder, Transform fallback)
        {
            if (hand == null || rigRoot == null || !hand.IsChildOf(rigRoot)) return;   // 没借出去就不动
            if (worldHolder != null) Mount(hand, worldHolder);
            else if (fallback != null) hand.SetParent(fallback, false);   // 世界模型也没了:至少不随克隆陪葬
        }

        static void Destroy(ref Rig rig)
        {
            if (rig != null)
            {
                UnmountHat(rig);   // 关键:先把借用的帽子还回世界模型,否则随克隆一起被销毁(永久丢失)
                UnmountRigFromSeat(rig.Player);   // 先把 rig 从座位链摘回,再销毁临时节点
                if (rig.SeatYawNode != null) UnityEngine.Object.Destroy(rig.SeatYawNode.gameObject);
                UnmountHands(rig);   // 同上:手部父物体(及其挂载物,如搬运中的动物)
                if (rig.Root != null) UnityEngine.Object.Destroy(rig.Root);
            }
            rig = null;
        }
    }
}
