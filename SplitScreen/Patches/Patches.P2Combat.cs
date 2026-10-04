using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  P2 近战攻击 —— 让 P2 持近战武器(矛/剑/拳套等)能命中敌人。
    //
    //  原版 MeleeWeapon 的命中走【动画事件】(OnMeleeHit / OnMeleeWeaponSuccesfullRayHit[All])，
    //  在挥击动画帧触发，门控 playerNetwork.IsLocalPlayer，且内部射线用 Helper.HitAtCursor/
    //  SphereHitAtCursor(全屏中心)。动画事件在 P2ToolRunner 的 scope 之外触发，那一刻 P2 的真实
    //  isLocalPlayer=false → 原版直接 return，P2 打不到东西。
    //
    //  做法(复用统一代理层)：把这些命中方法整段包进 P2OriginalScope.Tool() ——
    //   scope 内 isLocalPlayer 被强制为 true(过门控)、IsP2OriginalActive=true(AimRay 用 P2 相机
    //   且跳过 P2 自身碰撞体)、p2UsingItemActive=true(耐久扣减路由到 P2 手持)。退出还原。
    //  仅当该武器属于 P2 时进入 scope；P1 的武器 np!=player2 → 不进 scope，原版照常。
    //  挥击动画本身由 UseItemController.Use(已 Tool scope, RT 扳机)触发，无需额外处理。
    //
    //  注：scope 可嵌套；OnMeleeHit→OnMeleeWeaponSuccesfullRayHit 都被包裹时为嵌套 scope，安全。
    // ══════════════════════════════════════════════════════════════════════

    static class P2CombatScope
    {
        static readonly FieldInfo fAttackRange = typeof(MeleeWeapon).GetField("attackRange", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo fDamage = typeof(MeleeWeapon).GetField("damage", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo fGoThroughInvurnability = typeof(MeleeWeapon).GetField("goThroughInvurnability", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo fUseSphereCast = typeof(MeleeWeapon).GetField("useSphereCast", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo fSphereRadius = typeof(MeleeWeapon).GetField("sphereRadius", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo fArrowDamage = typeof(Arrow).GetField("damage", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo fArrowIsExpended = typeof(Arrow).GetField("isExpended", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo fThrowableComponent = typeof(Throwable_Object).GetField("throwableComponent", BindingFlags.Instance | BindingFlags.NonPublic);

        static int s_originalHitFrame = -1;
        static Network_Player s_originalHitSource;
        static Network_Player s_originalHitTarget;
        static float s_originalHitHealth;
        static float s_originalHitBonusHealth;
        static int s_localDamageFrame = -1;
        static Network_Player s_localDamageSource;
        static Network_Player s_localDamageTarget;
        internal static bool LocalFriendlyFireRequested;
        
        internal static P2MeleeState Enter(UnityEngine.Component tool)
        {
            if (tool == null) return null;
            var np = tool.GetComponentInParent<Network_Player>();
            if (np == Main.player1)
                return new P2MeleeState { Source = Main.player1 };
            if (np == Main.player2)
                return new P2MeleeState { Source = Main.player2, Scope = P2OriginalScope.Tool() };

            if (Main.IsP2OriginalInputActive && Main.P2Mode == P2OriginalMode.Tool && Main.player2 != null)
                return new P2MeleeState { Source = Main.player2 };

            return null;
        }

        internal static void RecordOriginalHit(MeleeWeapon tool, Network_Entity entity)
        {
            var source = tool != null ? tool.GetComponentInParent<Network_Player>() : null;
            var target = PlayerFromEntity(entity);
            if (!IsLocalSplitPair(source, target)) return;

            s_originalHitFrame = Time.frameCount;
            s_originalHitSource = source;
            s_originalHitTarget = target;
            s_originalHitHealth = target != null && target.Stats != null && target.Stats.stat_health != null
                ? target.Stats.stat_health.Value
                : -1f;
            s_originalHitBonusHealth = target != null && target.Stats != null && target.Stats.stat_BonusHealth != null
                ? target.Stats.stat_BonusHealth.Value
                : -1f;
        }

        internal static void TryDamageLocalFriendlyTarget(MeleeWeapon tool, Network_Player source)
        {
            bool localFriendlyFire = IsLocalFriendlyFireEnabled();
            if (!localFriendlyFire || tool == null || Main.player1 == null || Main.player2 == null)
                return;

            if (source == null || (source != Main.player1 && source != Main.player2))
                return;
            if (source.PlayerScript != null && source.PlayerScript.IsDead)
                return;

            if (!TryHitOtherLocalPlayer(tool, source, out var hit, out var target))
            {
                if (!TryFindOtherLocalPlayerInMeleeCone(tool, source, out hit, out target))
                    return;
            }
            if (OriginalHitAlreadyDamagedTarget(source, target))
                return;
            if (LocalFriendlyTargetAlreadyDamaged(source, target))
                return;

            var stats = target.Stats;
            if (stats == null || stats.IsDead)
                return;

            bool goThroughInv = fGoThroughInvurnability != null && (bool)fGoThroughInvurnability.GetValue(tool);

            int damage = fDamage != null ? (int)fDamage.GetValue(tool) : 5;
            if (DamageLocalFriendlyTarget(stats, target, source, hit.transform, damage, hit.point, hit.normal, goThroughInv) &&
                !stats.IsInvurnerable && stats.removesDurabilityWhenHit && source.Inventory != null)
            {
                source.Inventory.RemoveDurabillityFromHotSlot();
            }
                // 注：设备内部的 ReselectCurrentSlot 已被 Patch_Hotbar_ReselectCurrentSlot_P2 抑制，P1 手持模型不受影响。
        }

        static bool TryHitOtherLocalPlayer(MeleeWeapon tool, Network_Player source, out RaycastHit hit, out Network_Player target)
        {
            hit = default;
            target = null;

            var cam = source.Camera != null ? source.Camera : Helper.MainCamera;
            if (cam == null)
                return false;

            float range = fAttackRange != null ? (float)fAttackRange.GetValue(tool) : 3f;
            bool useSphere = fUseSphereCast != null && (bool)fUseSphereCast.GetValue(tool);
            float radius = fSphereRadius != null ? (float)fSphereRadius.GetValue(tool) : 0.25f;

            int mask = 0;
            AddLayer(ref mask, "LocalPlayer");
            AddLayer(ref mask, "RemotePlayer");
            if (Main.LAYER_P1_BODY >= 0) mask |= 1 << Main.LAYER_P1_BODY;
            mask |= LayerMasks.MASK_Players.value;
            if (mask == 0)
                return false;

            var ray = new Ray(cam.transform.position, cam.transform.forward);
            RaycastHit[] hits = useSphere
                ? Physics.SphereCastAll(ray, radius, range, mask, QueryTriggerInteraction.Collide)
                : Physics.RaycastAll(ray, range, mask, QueryTriggerInteraction.Collide);
            if (hits == null || hits.Length == 0)
                return false;

            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                var tr = hits[i].transform;
                if (tr == null || tr.IsChildOf(source.transform)) continue;

                var np = tr.GetComponentInParent<Network_Player>();
                if (!IsLocalSplitPair(source, np)) continue;

                hit = hits[i];
                target = np;
                return true;
            }
            return false;
        }

        static void AddLayer(ref int mask, string layerName)
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer >= 0) mask |= 1 << layer;
        }

        static Network_Player PlayerFromEntity(Network_Entity entity)
        {
            if (entity == null) return null;
            var stats = entity as PlayerStats;
            if (stats != null) return stats.GetComponent<Network_Player>();
            return entity.GetComponent<Network_Player>();
        }

        static bool IsLocalSplitPair(Network_Player source, Network_Player target)
        {
            if (source == null || target == null || source == target) return false;
            return (source == Main.player1 && target == Main.player2) || (source == Main.player2 && target == Main.player1);
        }

        static bool OriginalHitAlreadyDamagedTarget(Network_Player source, Network_Player target)
        {
            if (s_originalHitFrame != Time.frameCount || s_originalHitSource != source || s_originalHitTarget != target)
                return false;

            var stats = target != null ? target.Stats : null;
            if (stats == null) return false;

            float health = stats.stat_health != null ? stats.stat_health.Value : s_originalHitHealth;
            float bonus = stats.stat_BonusHealth != null ? stats.stat_BonusHealth.Value : s_originalHitBonusHealth;
            return (s_originalHitHealth >= 0f && health < s_originalHitHealth) ||
                   (s_originalHitBonusHealth >= 0f && bonus < s_originalHitBonusHealth);
        }

        static bool LocalFriendlyTargetAlreadyDamaged(Network_Player source, Network_Player target)
        {
            return s_localDamageFrame == Time.frameCount &&
                   s_localDamageSource == source &&
                   s_localDamageTarget == target;
        }

        internal static void TryDamageLocalFriendlyTarget(Arrow arrow, Collision collision, Network_Entity originalEntity)
        {
            if (!IsLocalFriendlyFireEnabled() || arrow == null || collision == null || originalEntity != null) return;
            if (Main.player1 == null || Main.player2 == null) return;
            if (fArrowIsExpended != null && (bool)fArrowIsExpended.GetValue(arrow)) return;

            var throwable = fThrowableComponent?.GetValue(arrow) as ThrowableComponent;
            var source = throwable != null ? throwable.playerNetwork : null;
            if (source == null || (source != Main.player1 && source != Main.player2)) return;

            var target = collision.collider != null ? collision.collider.GetComponentInParent<Network_Player>() : null;
            if (!IsLocalSplitPair(source, target)) return;

            var stats = target.Stats;
            if (stats == null || stats.IsDead) return;

            float damage = fArrowDamage != null ? (float)fArrowDamage.GetValue(arrow) : 1f;
            var contact = collision.contactCount > 0 ? collision.GetContact(0) : default(ContactPoint);
            Vector3 point = collision.contactCount > 0 ? contact.point : arrow.transform.position;
            Vector3 normal = collision.contactCount > 0 ? contact.normal : -arrow.transform.forward;

            DamageLocalFriendlyTarget(stats, target, source, collision.transform, damage, point, normal, ignoreInvulnerability: false);
            fArrowIsExpended?.SetValue(arrow, true);
        }

        static bool DamageLocalFriendlyTarget(PlayerStats stats, Network_Player target, Network_Player source, Transform hitTransform, float damage, Vector3 hitPoint, Vector3 hitNormal, bool ignoreInvulnerability)
        {
            if (stats == null || target == null || source == null) return false;
            if (!IsLocalFriendlyFireEnabled()) return false;
            if (!CanDamageLocalFriendlyTarget(target, stats, ignoreInvulnerability)) return false;

            float finalDamage = damage;
            if (hitTransform != null)
            {
                if (hitTransform.CompareTag("Entity_Head"))
                    finalDamage *= 3f;
                else if (hitTransform.CompareTag("Entity_Chest"))
                    finalDamage *= 2f;
            }

            bool oldInvulnerable = stats.IsInvurnerable;
            bool oldTargetLocal = VanillaAccessors.GetIsLocalPlayer(target);
            if (ignoreInvulnerability && oldInvulnerable)
                stats.IsInvurnerable = false;
            try
            {
                // Vanilla online PvP damages a remote player representation. In local split-screen
                // P1 is still the real local player, so PlayerStats.Damage would otherwise return
                // early from GameModeValueManager.IsPlayerInvurnerable in creative/god-mode cases.
                if (oldTargetLocal && GameModeValueManager.IsPlayerInvurnerable)
                    VanillaAccessors.SetIsLocalPlayer(target, false);
                stats.Damage(finalDamage, hitPoint, hitNormal, EntityType.Player, isLocal: true);
            }
            finally
            {
                if (oldTargetLocal)
                    VanillaAccessors.SetIsLocalPlayer(target, true);
                if (ignoreInvulnerability && oldInvulnerable && !stats.IsDead)
                    stats.IsInvurnerable = oldInvulnerable;
            }

            if (stats.IsDead && target.PlayerScript != null && !target.PlayerScript.IsDead)
            {
                target.PlayerScript.Kill();
            }

            s_localDamageFrame = Time.frameCount;
            s_localDamageSource = source;
            s_localDamageTarget = target;
            return true;
        }

        static bool TryFindOtherLocalPlayerInMeleeCone(MeleeWeapon tool, Network_Player source, out RaycastHit hit, out Network_Player target)
        {
            hit = default;
            target = null;
            if (tool == null || source == null) return false;

            var other = source == Main.player1 ? Main.player2 : source == Main.player2 ? Main.player1 : null;
            if (!IsLocalSplitPair(source, other)) return false;
            if (other.PlayerScript != null && (other.PlayerScript.IsDead || other.PlayerScript.waitingForRespawn)) return false;
            if (other.RessurectComponent != null && other.RessurectComponent.BeingCarried) return false;
            if (other.BedComponent != null && other.BedComponent.Sleeping) return false;

            var cam = source.Camera != null ? source.Camera : Helper.MainCamera;
            if (cam == null)
                return false;

            float range = fAttackRange != null ? (float)fAttackRange.GetValue(tool) : 3f;
            Vector3 center = other.FeetPosition != Vector3.zero ? other.FeetPosition + Vector3.up : other.transform.position + Vector3.up;
            Vector3 delta = center - cam.transform.position;
            float dot = delta.sqrMagnitude > 0.0001f ? Vector3.Dot(cam.transform.forward, delta.normalized) : 1f;
            if (delta.sqrMagnitude > range * range) return false;
            if (dot < 0.72f) return false;

            hit = new RaycastHit();
            target = other;
            return true;
        }

        static bool CanDamageLocalFriendlyTarget(Network_Player target, PlayerStats stats, bool ignoreInvulnerability)
        {
            if (target == null || stats == null) return false;

            var ps = target.PlayerScript;
            bool protectedByActiveRespawn =
                ps != null && (ps.IsDead || ps.waitingForRespawn) ||
                target.BedComponent != null && target.BedComponent.Sleeping ||
                target.RessurectComponent != null && target.RessurectComponent.BeingCarried;

            if (protectedByActiveRespawn) return false;
            if (stats.IsInvurnerable && !ignoreInvulnerability) return false;
            return true;
        }

        internal static bool IsLocalFriendlyFireEnabled()
        {
            return GameManager.FriendlyFire || LocalFriendlyFireRequested;
        }
    }

    sealed class P2MeleeState
    {
        public P2OriginalScope Scope;
        public Network_Player Source;

        public void Dispose()
        {
            Scope?.Dispose();
            Scope = null;
        }
    }

    [HarmonyPatch(typeof(LoadGameBox), "Button_LoadGame")]
    static class Patch_LoadGameBox_ButtonLoadGame_CaptureFriendlyFire
    {
        static readonly FieldInfo fToggle = typeof(LoadGameBox).GetField("allowFriendlyFireToggle", BindingFlags.Instance | BindingFlags.NonPublic);

        static void Prefix(LoadGameBox __instance)
        {
            var toggle = fToggle?.GetValue(__instance) as UnityEngine.UI.Toggle;
            bool value = toggle != null && toggle.isOn;
            P2CombatScope.LocalFriendlyFireRequested = value;
            if (value) GameManager.FriendlyFire = true;
        }
    }

    [HarmonyPatch(typeof(NewGameBox), "Button_CreateNewGame")]
    static class Patch_NewGameBox_ButtonCreateNewGame_CaptureFriendlyFire
    {
        static readonly FieldInfo fToggle = typeof(NewGameBox).GetField("toggle_FriendlyFire", BindingFlags.Instance | BindingFlags.NonPublic);

        static void Prefix(NewGameBox __instance)
        {
            var toggle = fToggle?.GetValue(__instance) as UnityEngine.UI.Toggle;
            bool value = toggle != null && toggle.isOn;
            P2CombatScope.LocalFriendlyFireRequested = value;
            if (value) GameManager.FriendlyFire = true;
        }
    }

    [HarmonyPatch(typeof(MeleeWeapon), "OnMeleeHit")]
    static class Patch_MeleeWeapon_OnMeleeHit_P2
    {
        static void Prefix(MeleeWeapon __instance, ref P2MeleeState __state) => __state = P2CombatScope.Enter(__instance);
        static void Postfix(MeleeWeapon __instance, P2MeleeState __state)
        {
            P2CombatScope.TryDamageLocalFriendlyTarget(__instance, __state?.Source);
            __state?.Dispose();
        }
        static Exception Finalizer(Exception __exception, P2MeleeState __state) { __state?.Dispose(); return __exception; }
    }

    // 部分武器的动画事件直接调 OnMeleeWeaponSuccesfullRayHit / ...RayHitAll(不经 OnMeleeHit)。
    //  也包裹之；若由已包裹的 OnMeleeHit 调进来则为嵌套 scope(安全)。
    [HarmonyPatch(typeof(MeleeWeapon), "OnMeleeWeaponSuccesfullRayHit")]
    static class Patch_MeleeWeapon_RayHit_P2
    {
        static void Prefix(MeleeWeapon __instance, ref P2MeleeState __state) => __state = P2CombatScope.Enter(__instance);
        static void Postfix(MeleeWeapon __instance, P2MeleeState __state)
        {
            P2CombatScope.TryDamageLocalFriendlyTarget(__instance, __state?.Source);
            __state?.Dispose();
        }
        static Exception Finalizer(Exception __exception, P2MeleeState __state) { __state?.Dispose(); return __exception; }
    }

    [HarmonyPatch(typeof(MeleeWeapon), "OnMeleeWeaponSuccesfullRayHitAll")]
    static class Patch_MeleeWeapon_RayHitAll_P2
    {
        static void Prefix(MeleeWeapon __instance, ref P2MeleeState __state) => __state = P2CombatScope.Enter(__instance);
        static void Postfix(MeleeWeapon __instance, P2MeleeState __state)
        {
            P2CombatScope.TryDamageLocalFriendlyTarget(__instance, __state?.Source);
            __state?.Dispose();
        }
        static Exception Finalizer(Exception __exception, P2MeleeState __state) { __state?.Dispose(); return __exception; }
    }

    [HarmonyPatch(typeof(MeleeWeapon), "OnHitEntity")]
    static class Patch_MeleeWeapon_OnHitEntity_RecordLocalFriendly
    {
        static void Prefix(MeleeWeapon __instance, Network_Entity entity)
        {
            P2CombatScope.RecordOriginalHit(__instance, entity);
        }
    }

    [HarmonyPatch(typeof(Arrow), "OnCollisionEvent")]
    static class Patch_Arrow_OnCollisionEvent_LocalFriendly
    {
        static void Postfix(Arrow __instance, Collision collision, Network_Entity entity)
        {
            P2CombatScope.TryDamageLocalFriendlyTarget(__instance, collision, entity);
        }
    }

    
    
    
    
    
    
    
    
    sealed class ThrowableCompP2State { public P2FrameContext Ctx; public PlayerItemBusyScope Busy; }

    [HarmonyPatch(typeof(ThrowableComponent), "Update")]
    static class Patch_ThrowableComponent_Update_P2
    {
        static void Prefix(ThrowableComponent __instance, ref ThrowableCompP2State __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            if (Main.IsP2MenuOpen || Main.IsP2BackpackOpen || Main.IsP2BuildMenuOpen) return;   
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return;

            // 读/扣 P2 弹药 → routeInventory；蓄力期全局 IsBusy save/restore(独立于 Ctx)。
            __state = new ThrowableCompP2State
            {
                Busy = new PlayerItemBusyScope(),
                Ctx = P2FrameContext.Tool(routeInventory: true),
            };
        }

        static void Postfix(ThrowableCompP2State __state) => Cleanup(__state);
        static Exception Finalizer(Exception __exception, ThrowableCompP2State __state) { Cleanup(__state); return __exception; }

        static void Cleanup(ThrowableCompP2State st)
        {
            if (st == null) return;
            st.Ctx?.Dispose(); st.Ctx = null;
            st.Busy?.Dispose(); st.Busy = null;
        }
    }

    // 基类 HandleLocalClient 的蓄力 ReadValue<float>() → P2 右扳机模拟量(bow 子类 override 调 base，故补基类即可)。
    [HarmonyPatch(typeof(ThrowableComponent), "HandleLocalClient")]
    static class Patch_ThrowableComponent_HandleLocalClient_ReadValueP2
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => TranspilerGuard.Verify(instructions, TranspilerCore);

        static IEnumerable<CodeInstruction> TranspilerCore(IEnumerable<CodeInstruction> instructions)
            => HookReadValueTranspiler.Replace(instructions);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  P2 远程武器·真正发射(Throw) —— 修复"不消耗弹药 + 箭无碰撞穿过木筏"。
    //   Throw 由 StartThrow【协程】(WaitForSeconds 后)在【Update 的 scope 之外】调用，那一刻 P2 真实
    //   isLocalPlayer=false → 原版:
    //     · `if (IsLocalPlayer) Inventory.RemoveItem(ammo,1)` 跳过 → 不消耗箭；
    //     · `arrow.Initialize(..., colliderEnabled = IsLocalPlayer=false)` → 箭无碰撞体 → 穿过一切落水。
    //   修复：把 Throw 整段包进 P2OriginalScope.Tool()(强制 isLocalPlayer=true) + P2InventoryScope(与读弹药同一
    //   背包状态，RemoveItem 命中正确槽)。于是扣箭 + 箭开启碰撞体 → 命中后 HandleCollision 插住(host)并启用拾取碰撞体(可重复拾取)。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(ThrowableComponent), "Throw")]
    static class Patch_ThrowableComponent_Throw_P2
    {
        static void Prefix(ThrowableComponent __instance, ref ThrowableCompP2State __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            var np = __instance.playerNetwork != null ? __instance.playerNetwork : __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return;
            __state = new ThrowableCompP2State { Ctx = P2FrameContext.Tool(routeInventory: true), Busy = new PlayerItemBusyScope() };
        }

        static void Postfix(ThrowableCompP2State __state) => Cleanup(__state);
        static Exception Finalizer(Exception __exception, ThrowableCompP2State __state) { Cleanup(__state); return __exception; }

        static void Cleanup(ThrowableCompP2State st)
        {
            if (st == null) return;
            st.Ctx?.Dispose(); st.Ctx = null;
            st.Busy?.Dispose(); st.Busy = null;
        }
    }
}
