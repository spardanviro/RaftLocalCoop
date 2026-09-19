using Object = UnityEngine.Object;
using UnityEngine;

namespace SplitScreen
{
    public sealed class SplitScreenRuntime
    {
        public static SplitScreenRuntime Instance { get; private set; }

        public static void Create()
        {
            if (Instance != null) return;
            Instance = new SplitScreenRuntime();
        }

        public static void Destroy()
        {
            if (Instance == null) return;
            Instance.Shutdown();
            Instance = null;
        }

        void Shutdown()
        {
            SplitFramePacing.Restore();
            try { UI.CloseCurrentStorageIfAny(); } catch (System.Exception e) { Main.ModEntry.Logger.Log("[Runtime] CloseStorage ignored: " + e.Message); }
            try { Main.RestoreP2InventoryField(); } catch (System.Exception e) { Main.ModEntry.Logger.Log("[Runtime] RestoreP2InventoryField ignored: " + e.Message); }
            if (P2.IsUsingMenu)
            {
                try { Main.ForceResetP2Backpack(); } catch (System.Exception e) { Main.ModEntry.Logger.Log("[Runtime] ForceResetP2Backpack ignored: " + e.Message); }
                P2.IsUsingMenu = false;
            }

            if (P2.PromptText != null)
            {
                Object.Destroy(P2.PromptText.gameObject);
                P2.PromptText = null;
            }
            if (P2.DeathText != null)
            {
                Object.Destroy(P2.DeathText.gameObject);
                P2.DeathText = null;
            }

            var p1 = P1.Player;
            if (p1 != null)
            {
                if (p1.Camera     != null) { p1.Camera.rect     = new Rect(0f, 0f, 1f, 1f); p1.Camera.depth     = 0; p1.Camera.targetDisplay = 0; }
                if (p1.HandCamera != null) { p1.HandCamera.rect = new Rect(0f, 0f, 1f, 1f); p1.HandCamera.depth = 1; p1.HandCamera.targetDisplay = 0; }

                if (P1.CamOriginalMask >= 0 && p1.Camera != null)
                    p1.Camera.cullingMask = P1.CamOriginalMask;
            }

            Input.Dispose();

            Main.ModEntry.Logger.Log("[Runtime] Shutdown complete");
        }

        public PlayerSlot P1 { get; } = new PlayerSlot(isP2: false);
        public PlayerSlot P2 { get; } = new PlayerSlot(isP2: true);

        public InputRouter       Input        { get; }
        public InteractionRouter Interactions { get; }
        public UiRouter          UI           { get; }

        public bool IsSpawningP2 { get; internal set; }

        public Network_Player Player1  => P1.Player;
        public Network_Player Player2  => P2.Player;
        public bool           P2Active => P2.Player != null;

        SplitScreenRuntime()
        {
            Input        = new InputRouter(this);
            Interactions = new InteractionRouter(this);
            UI           = new UiRouter(this);
        }

        public void Tick(float dt)
        {
            if (!P2Active) return;
            P2ZiplineDriver.EnsureActiveIfAttached();   // 挂滑索时每帧保活GO(早于所有早退;修TP卡住)
            Input.Tick();
            Main.MaintainLocalPlayerCM();
            Main.MaintainP2InventoryField();
            Main.UpdateSplitWellBeingFactors();
            Main.EnforceMeshStates();
            Main.ReconcileP2LoadCircle();   // 蓄力读条圈停刷即隐藏,防卡满值
            SplitScreenDeathFlow.TickDeathMenuGate();
            SplitScreenDeathFlow.TickP1DownedCamera();
            P1PianoViewToggle.Tick();   // P1 在钢琴前也能按 V 切视角(绕过原版的 ActiveMenu 门控)

            if (P2.Player?.PlayerScript != null && P2.Player.PlayerScript.IsDead)
            {
                Main.ApplyP2DownedOriginalInputRestrictions();
                Main.TickP2StatHud();
                TickDeath(dt);
                return;
            }

            Main.TickP2Hotbar();
            Main.TickP2StatHud();

            Main.TickP2SleepFade();
            Main.TickP2Binoculars();

            if (Main.TickP2Bed()) { TickDeath(dt); return; }

            if (Main.TickP2Seat()) { TickDeath(dt); return; }

            // 雪橇车就座 → 早退(同椅子:就座期间不跑工具/建造/背包 tick),下车由 TickP2Snowmobile 检测
            if (Main.TickP2Snowmobile()) { TickDeath(dt); return; }

            if (Main.TickP2Carry(this)) { TickDeath(dt); return; }

            if (Main.IsP2MenuOpen) { Main.TickP2Menu(); TickDeath(dt); return; }
            Main.TickP2Backpack();

            if (P2ZiplineDriver.IsAttached) { P2ZiplineDriver.Tick(); UI.TickMenu(); TickDeath(dt); return; }
            P2ToolRunner.Tick();
            Main.TickP2Build();
            Main.TickP2BuildMenu();
            UI.TickMenu();
            TickDeath(dt);
        }

        void TickDeath(float dt)
        {
            var ps = P2.Player?.PlayerScript;
            if (ps == null) return;

            if (ps.IsDead)
            {
                if (!P2.DeathHandled)
                {
                    P2.DeathHandled = true;
                    P2.DeathTimer   = 0f;
                    Main.ModEntry.Logger.Log("[P2 Death] P2 downed; waiting for rescue or all-dead death menu");
                    if (P2.DeathText != null)
                    {
                        P2.DeathText.text = "P2 downed\nWaiting for rescue";
                        P2.DeathText.gameObject.SetActive(true);
                    }
                }

                P2.AccumulateDeathTime(dt);

                if (P2.DeathText != null && P2.DeathText.gameObject.activeSelf)
                    P2.DeathText.text = "P2 downed\nWaiting for rescue";
            }
            else if (P2.DeathHandled)
            {
                P2.DeathHandled = false;
                if (P2.DeathText != null && P2.DeathText.gameObject.activeSelf)
                    P2.DeathText.gameObject.SetActive(false);
            }
        }
    }
}
