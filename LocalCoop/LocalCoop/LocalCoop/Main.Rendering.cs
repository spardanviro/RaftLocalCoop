using System.Collections.Generic;
using System.Reflection;
using Object = UnityEngine.Object;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.PostProcessing;

namespace SplitScreen
{
    public static partial class Main
    {
        
        static FieldInfo armMeshField;

        public static SkinnedMeshRenderer GetArmMesh(CharacterModelModifications cmm)
        {
            if (cmm == null) return null;

            if (armMeshField == null)
                armMeshField = typeof(CharacterModelModifications).GetField(
                    "armMesh", BindingFlags.Instance | BindingFlags.NonPublic);
            var result = armMeshField?.GetValue(cmm) as SkinnedMeshRenderer;
            if (result != null) return result;

            foreach (var field in typeof(CharacterModelModifications)
                         .GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (field.FieldType != typeof(SkinnedMeshRenderer)) continue;
                result = field.GetValue(cmm) as SkinnedMeshRenderer;
                if (result != null)
                {
                    armMeshField = field;
                    LogV($"[GetArmMesh] fallback field: '{field.Name}'");
                    return result;
                }
            }
            ModEntry.Logger.Log("[GetArmMesh] WARNING: no SkinnedMeshRenderer field found");
            return null;
        }

        
        static bool AllocateLayers()
        {
            var free = new List<int>();
            for (int i = 3; i <= 31; i++)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                    free.Add(i);

            var namedLayers = new List<string>();
            for (int i = 0; i <= 31; i++)
            {
                string n = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(n)) namedLayers.Add($"{i}:{n}");
            }
            LogV($"[Layer] Named: {string.Join(", ", namedLayers)}");
            LogV($"[Layer] Free({free.Count}): {string.Join(", ", free)}");

            if (free.Count < 4)
            {
                ModEntry.Logger.Log(
                    $"[Layer] ERROR: 需要 4 个空闲层，仅找到 {free.Count} 个。" +
                    "请检查 Project Settings → Tags & Layers 是否所有自定义层都已命名。分屏无法启用。");
                return false;
            }

            LAYER_P1_HAND = free[0];
            LAYER_P1_TOOL = free[1];
            LAYER_P2_HAND = free[2];
            LAYER_P1_BODY = free[3];
            LAYER_P1_NOTEBOOK = LAYER_P1_TOOL;
            p1HandCameraLayer = LayerMask.NameToLayer("HandCamera"); 
            LogV(
                $"[Layer] Allocated — P1_HAND={LAYER_P1_HAND} P1_TOOL={LAYER_P1_TOOL} " +
                $"P2_HAND={LAYER_P2_HAND} P1_BODY={LAYER_P1_BODY} P1_NOTEBOOK(alias P1_TOOL)={LAYER_P1_NOTEBOOK}");
            return true;
        }

        
        internal static void ApplySplitViewport(Camera cam, bool isP2)
        {
            if (cam == null) return;
            if (ActiveSplitMode == SplitMode.DualMonitor)
            {
                cam.rect = new Rect(0f, 0f, 1f, 1f);
                cam.targetDisplay = isP2 ? 1 : 0;
            }
            else
            {
                cam.rect = isP2 ? new Rect(0.5f, 0f, 0.5f, 1f) : new Rect(0f, 0f, 0.5f, 1f);
                cam.targetDisplay = 0;
            }
        }

        // 第二显示器上的占位相机。Unity 只在【有相机渲染】时才刷新一块 Display:
        // 退回主菜单时 P2 连同它的相机一起被销毁,display[1] 上没有任何相机 ->
        // 那块屏的后备缓冲无人覆盖,就一直定格在退出前的最后一帧(既没销毁也没清屏)。
        // 原版没有这个问题:它从不点亮第二块屏。所以由点亮它的人负责收场 ->
        // 离开世界时留一台只清黑、什么都不拍的相机占住它,重新分屏时再撤掉。
        static Camera _blankDisplayCam;

        // Unity 没有 Display.Deactivate():Activate() 之后那块屏撤不回来,
        // 所以从窗口层收场 —— Unity 给第二块 Display 开的是一个同进程、同窗口类
        // (UnityWndClass)的独立顶层窗口,把它 ShowWindow(HIDE) 就等于回到点亮之前的样子。
        // 隐藏失败时(非 Windows / 找不到窗口)退回占位黑屏相机,至少不定格最后一帧。
        const int SW_HIDE = 0, SW_SHOW = 5;

        delegate bool EnumWindowsProc(System.IntPtr hWnd, System.IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc cb, System.IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool IsWindowVisible(System.IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern int GetClassName(System.IntPtr hWnd, System.Text.StringBuilder buf, int max);

        // 认哪个窗口是 P2 的:直接看它落在哪块显示器上。
        // 试过的两种做法都不可靠 —— ①排除 Process.MainWindowHandle:那个属性挑的是进程里
        // 碰到的第一个可见顶层窗口,双窗口下挑中哪个不确定(实测挑了 P2 的,退出时把 P1 主窗口关了);
        // ②取 Activate 前后新增的窗口:新增的不止一个,会认到 Unity 的隐藏辅助窗口
        // (隐藏它成功但屏幕纹丝不动)。
        // 现在按系统的主/副屏判定:P1 恒在主屏,落在【非主屏】上的那个 Unity 窗口就是 P2 的。
        // 这是每次现场查的结构事实,不用记句柄,也就没有热重载后名单失效的问题。
        const uint MONITOR_DEFAULTTONEAREST = 2;
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct POINT { public int X, Y; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern System.IntPtr MonitorFromWindow(System.IntPtr hWnd, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern System.IntPtr MonitorFromPoint(POINT pt, uint flags);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetWindowRect(System.IntPtr hWnd, out RECT rect);

        // 主屏的左上角在虚拟桌面坐标系里恒为 (0,0)。
        static System.IntPtr PrimaryMonitor() =>
            MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTONEAREST);

        // 本进程所有 Unity 顶层窗口(含已隐藏的 —— 恢复时要找的正是藏起来那个)。
        static System.Collections.Generic.List<System.IntPtr> EnumUnityWindows()
        {
            var found = new System.Collections.Generic.List<System.IntPtr>();
            try
            {
                uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                var sb = new System.Text.StringBuilder(256);
                EnumWindows((h, l) =>
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid != myPid) return true;
                    sb.Length = 0;
                    GetClassName(h, sb, sb.Capacity);
                    if (sb.ToString().IndexOf("Unity", System.StringComparison.OrdinalIgnoreCase) < 0) return true;
                    found.Add(h);
                    return true;
                }, System.IntPtr.Zero);
            }
            catch (System.Exception e) { ModEntry.Logger.Log("[DualMonitor] 枚举窗口失败: " + e.Message); }
            return found;
        }

        // Unity 给第二块屏开的是【普通带边框窗口】(实测 rect=-1080,-301 1080x1920),
        // 所以任务栏会压在它上面。改成无边框弹出窗口 + 铺满整块显示器的 rcMonitor
        // (rcMonitor 是含任务栏区域的完整边界,rcWork 才是扣掉任务栏的),
        // 视觉上就与原版全屏一致。只在样式还没改过时做一次,避免每次抬升都 FRAMECHANGED 闪一下。
        const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000,
                  WS_MAXIMIZEBOX = 0x00010000, WS_SYSMENU = 0x00080000, WS_BORDER = 0x00800000,
                  WS_DLGFRAME = 0x00400000, WS_POPUP = unchecked((int)0x80000000);
        const int WS_EX_DLGMODALFRAME = 0x00000001, WS_EX_CLIENTEDGE = 0x00000200, WS_EX_WINDOWEDGE = 0x00000100,
                  WS_EX_STATICEDGE = 0x00020000;
        const uint SWP_FRAMECHANGED = 0x0020;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern bool GetMonitorInfo(System.IntPtr hMonitor, ref MONITORINFO lpmi);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        static extern System.IntPtr GetWindowLongPtr64(System.IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        static extern System.IntPtr SetWindowLongPtr64(System.IntPtr hWnd, int nIndex, System.IntPtr dwNewLong);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        static extern int GetWindowLong32(System.IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        static extern int SetWindowLong32(System.IntPtr hWnd, int nIndex, int dwNewLong);

        // 64 位进程要走 ...Ptr 版,32 位只有普通版 —— 按指针宽度分流。
        static int GetStyle(System.IntPtr h, int idx) =>
            System.IntPtr.Size == 8 ? (int)GetWindowLongPtr64(h, idx).ToInt64() : GetWindowLong32(h, idx);
        static void SetStyle(System.IntPtr h, int idx, int v)
        {
            if (System.IntPtr.Size == 8) SetWindowLongPtr64(h, idx, new System.IntPtr(v));
            else SetWindowLong32(h, idx, v);
        }

        static System.IntPtr _borderlessAppliedTo = System.IntPtr.Zero;

        static void MakeP2WindowBorderlessFullscreen(System.IntPtr wnd)
        {
            if (wnd == System.IntPtr.Zero || wnd == _borderlessAppliedTo) return;
            try
            {
                int style = GetStyle(wnd, GWL_STYLE);
                style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU | WS_BORDER | WS_DLGFRAME);
                style |= WS_POPUP;
                SetStyle(wnd, GWL_STYLE, style);
                int ex = GetStyle(wnd, GWL_EXSTYLE);
                ex &= ~(WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_WINDOWEDGE | WS_EX_STATICEDGE);
                SetStyle(wnd, GWL_EXSTYLE, ex);

                var mi = new MONITORINFO();
                mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO));
                var mon = MonitorFromWindow(wnd, MONITOR_DEFAULTTONEAREST);
                if (!GetMonitorInfo(mon, ref mi)) return;
                var r = mi.rcMonitor;
                SetWindowPos(wnd, HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                             SWP_FRAMECHANGED | SWP_NOACTIVATE);
                SetWindowPos(wnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                _borderlessAppliedTo = wnd;
                ModEntry.Logger.Log("[DualMonitor] P2 窗口已无边框全屏 rect=" + r.Left + "," + r.Top
                    + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top));
            }
            catch (System.Exception e) { ModEntry.Logger.Log("[DualMonitor] 全屏化失败: " + e.Message); }
        }

        // 落在非主屏、且有实际尺寸的那个 Unity 窗口 = P2 的窗口。多个就取最大的。
        static System.IntPtr FindP2DisplayWindow()
        {
            var primary = PrimaryMonitor();
            System.IntPtr best = System.IntPtr.Zero;
            long bestArea = 0;
            foreach (var h in EnumUnityWindows())
            {
                if (!GetWindowRect(h, out RECT r)) continue;
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area < 64 * 64) continue;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) == primary) continue;
                if (area > bestArea) { bestArea = area; best = h; }
            }
            return best;
        }

        // 第二块屏的窗口是 Unity 现开的,创建时不抢前台 —— 那台显示器上若已开着别的窗口,
        // 它就被压在下面,要手点一下才看得见。这里把它提到最上层但【不夺取输入焦点】:
        // 置 TOPMOST 再立刻退回 NOTOPMOST,是抬升 Z 序而不 SetForegroundWindow 的标准做法
        // (真去抢前台会把键鼠焦点从主窗口拽走,P1 就没法操作了)。
        // 窗口可能在 Activate 后隔几帧才出现,所以连抬若干帧。
        static readonly System.IntPtr HWND_TOPMOST = new System.IntPtr(-1);
        static readonly System.IntPtr HWND_NOTOPMOST = new System.IntPtr(-2);
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        static int _raiseDisplayFrames;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(System.IntPtr hWnd, System.IntPtr hWndInsertAfter,
                                        int X, int Y, int cx, int cy, uint flags);

        internal static void RequestRaiseSecondDisplayWindow() { _raiseDisplayFrames = 120; }

        internal static void TickRaiseSecondDisplayWindow()
        {
            if (_raiseDisplayFrames <= 0) return;
            _raiseDisplayFrames--;
            if (_raiseDisplayFrames % 20 != 0) return;   // 每 20 帧抬一次就够,别每帧调 user32
            if (ActiveSplitMode != SplitMode.DualMonitor) { _raiseDisplayFrames = 0; return; }
            var wnd = FindP2DisplayWindow();
            if (wnd == System.IntPtr.Zero) return;
            // ⚠ 不能带 SWP_SHOWWINDOW:那个标志会顺带把窗口显示出来 —— 退出世界时若抬升计时
            // 还没走完,就会把刚隐藏的窗口又拉回来(实测现象:P2 窗口先消失、随即重新出现)。
            // 抬升只该管 Z 序;窗口该不该可见由 Hide/Show 决定。已隐藏就直接不管。
            if (!IsWindowVisible(wnd)) { _raiseDisplayFrames = 0; return; }
            MakeP2WindowBorderlessFullscreen(wnd);
            SetWindowPos(wnd, HWND_TOPMOST,   0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetWindowPos(wnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        internal static void HideSecondDisplayWindow()
        {
            if (ActiveSplitMode != SplitMode.DualMonitor) return;
            if (Display.displays.Length <= 1) return;
            _raiseDisplayFrames = 0;   // 取消未走完的抬升,免得它把要收起来的窗口又拉回来

            // 只动落在副屏上的那个窗口。找不到宁可不动:猜错就是把 P1 的主窗口关掉。
            var wnd = FindP2DisplayWindow();
            if (wnd != System.IntPtr.Zero && IsWindowVisible(wnd) && ShowWindow(wnd, SW_HIDE))
                ModEntry.Logger.Log("[DualMonitor] 已隐藏第二显示器窗口 hwnd=" + wnd.ToInt64());
            else
            {
                ModEntry.Logger.Log("[DualMonitor] 未认得第二显示器窗口,退回占位黑屏相机");
                EnsureBlankSecondDisplayCam();
            }
        }

        // 每次现场按副屏找,不记任何句柄 —— 窗口比 mod 的静态活得久,记名单必然在热重载后失效
        // (踩过:重载后名单没了、窗口还藏着 -> display[1] 是 active 但不可见 -> P2 被画回 display0)。
        // 隐藏的窗口仍保留位置,MonitorFromWindow 照样能认出它在副屏上。
        internal static void ShowSecondDisplayWindow()
        {
            if (Display.displays.Length <= 1) return;
            // 只在 display[1] 已经被 Activate 过时才恢复。Unity 会为每块屏预先建好窗口并
            // 保持隐藏,点亮前它一直藏着 —— 无差别地 ShowWindow 等于替 Unity 提前点亮那块屏,
            // 结果就是刚加载 mod、还没进世界,第二块屏就变成一片黑(没有任何相机渲染它)。
            if (!Display.displays[1].active) return;
            // 还要看【本次是不是双显示器模式】:display[1] 一旦点亮就撤不回来,
            // 只凭它判断的话,上一局是双屏、这一局选左右分屏时也会把那个窗口显示出来,
            // 而左右分屏没有相机渲染它 -> 又是一屏退出前的旧画面。模式不对就反过来收好。
            var wnd = FindP2DisplayWindow();
            if (wnd == System.IntPtr.Zero) return;
            if (ActiveSplitMode != SplitMode.DualMonitor)
            {
                if (IsWindowVisible(wnd)) ShowWindow(wnd, SW_HIDE);
                return;
            }
            if (!IsWindowVisible(wnd)) ShowWindow(wnd, SW_SHOW);
        }

        internal static void EnsureBlankSecondDisplayCam()
        {
            if (ActiveSplitMode != SplitMode.DualMonitor) return;
            if (Display.displays.Length <= 1) return;
            _raiseDisplayFrames = 0;   // 取消未走完的抬升,免得它把要收起来的窗口又拉回来

            if (_blankDisplayCam != null) return;
            var go = new GameObject("P2_BlankDisplayCam");
            Object.DontDestroyOnLoad(go);   // 回主菜单是场景切换,不留下来就白做
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = 0;
            cam.depth = -100f;
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            cam.targetDisplay = 1;
            cam.useOcclusionCulling = false;
            cam.allowHDR = false; cam.allowMSAA = false;
            _blankDisplayCam = cam;
            ModEntry.Logger.Log("[DualMonitor] 已在 display[1] 留占位黑屏相机");
        }

        internal static void DestroyBlankSecondDisplayCam()
        {
            if (_blankDisplayCam == null) return;
            Object.Destroy(_blankDisplayCam.gameObject);
            _blankDisplayCam = null;
        }

        internal static void ActivateSecondDisplayIfNeeded()
        {
            if (ActiveSplitMode != SplitMode.DualMonitor) return;
            if (Display.displays.Length > 1 && !Display.displays[1].active)
            {
                Display.displays[1].Activate();
                ModEntry.Logger.Log("[DualMonitor] 已激活第二显示器 display[1]");
            }
            else if (Display.displays.Length <= 1)
                ModEntry.Logger.Log("[DualMonitor] 警告:只检测到 1 块显示器,双显示器模式将回退到同屏(P2 视口在 display0)");
        }

        public static void SetCameraRects()
        {
            if (p1LocalPlayerLayer < 0)
                p1LocalPlayerLayer = LayerMask.NameToLayer("LocalPlayer");

            ActiveSplitMode = PendingSplitMode;
            ActivateSecondDisplayIfNeeded();
            DestroyBlankSecondDisplayCam();   // 真正的 P2 相机接管这块屏了
            ShowSecondDisplayWindow();        // 上次离开世界时隐藏过就恢复回来
            RequestRaiseSecondDisplayWindow();// 别被那台显示器上已有的窗口压住

            if (player1.Camera     != null) { ApplySplitViewport(player1.Camera, false);     player1.Camera.depth     = 0; }
            if (player1.HandCamera != null) { ApplySplitViewport(player1.HandCamera, false); player1.HandCamera.depth = 1; }
            if (player2.Camera     != null) { ApplySplitViewport(player2.Camera, true);      player2.Camera.depth     = 2; }
            if (player2.HandCamera != null)
            {
                
                
                ApplySplitViewport(player2.HandCamera, true); player2.HandCamera.depth = 3;
                player2.HandCamera.clearFlags = CameraClearFlags.Depth;
                if (!P2CameraBootstrapActive && !player2.HandCamera.enabled) player2.HandCamera.enabled = true;
                if (!player2.HandCamera.gameObject.activeSelf) player2.HandCamera.gameObject.SetActive(true);
            }
            LogV($"Split rects set | LocalPlayerLayer={p1LocalPlayerLayer} | depths P1={player1.Camera?.depth}/{player1.HandCamera?.depth} P2={player2.Camera?.depth}/{player2.HandCamera?.depth}");
        }

        
        public static void ConfigureRendering()
        {
            var log = ModEntry.Logger;
            log.Log("ConfigureRendering start");

            player1ArmMesh = GetArmMesh(player1.currentModel);
            player2ArmMesh = GetArmMesh(player2.currentModel);
            log.Log($"  armMesh: P1={player1ArmMesh != null} P2={player2ArmMesh != null}");

            ConfigureP1Layers(player1ArmMesh);

            FirstPersonVisualRig.Tick();
            player1ArmMesh = FirstPersonVisualRig.GetArm(player1);
            player2ArmMesh = FirstPersonVisualRig.GetArm(player2);

            player1ThirdPerson = player1.GetComponentInChildren<ThirdPerson>();

            if (player1.currentModel != null)
            {
                player1.currentModel.ActivateHairStyle(HairStyle.Full, true);
                ApplyEquippedHeadVisuals(player1);
                log.Log("  P1: ActivateHairStyle(Full) restored for P2 view");
            }

            int modLayerMask = SplitLightingLayerMask();

            int lightCount = 0;
            foreach (var lt in Object.FindObjectsOfType<Light>())
            {
                if ((lt.cullingMask & modLayerMask) != modLayerMask)
                {
                    lt.cullingMask |= modLayerMask;
                    lightCount++;
                }
            }
            log.Log($"  Lights patched: {lightCount} lights updated with custom layers");

            int probeCount = 0;
            foreach (var rp in Object.FindObjectsOfType<ReflectionProbe>())
            {
                if ((rp.cullingMask & modLayerMask) != modLayerMask)
                {
                    rp.cullingMask |= modLayerMask;
                    probeCount++;
                }
            }
            log.Log($"  ReflectionProbes patched: {probeCount} probes updated with custom layers");

            SetupSplitPostFxProfiles();

            log.Log("ConfigureRendering done");
        }

        internal static int SplitLightingLayerMask()
        {
            int mask = (1 << LAYER_P1_HAND) | (1 << LAYER_P1_TOOL) | (1 << LAYER_P1_BODY) | (1 << LAYER_P2_HAND);
            int remotePlayerLayer = LayerMask.NameToLayer("RemotePlayer");
            if (remotePlayerLayer >= 0) mask |= (1 << remotePlayerLayer);
            return mask;
        }

        internal static void EnsureSplitLightingMasks()
        {
            int mask = SplitLightingLayerMask();
            foreach (var lt in Object.FindObjectsOfType<Light>())
                if ((lt.cullingMask & mask) != mask)
                    lt.cullingMask |= mask;

            foreach (var rp in Object.FindObjectsOfType<ReflectionProbe>())
                if ((rp.cullingMask & mask) != mask)
                    rp.cullingMask |= mask;
        }

        static void ConfigureP1Layers(SkinnedMeshRenderer arm)
        {
            var cmm = player1.currentModel;
            if (cmm == null) { LogV("P1.currentModel null, skip"); return; }

            if (cmm.fullBodyMesh != null)
                cmm.fullBodyMesh.gameObject.SetActive(true);

            int count = 0;
            foreach (var r in cmm.GetComponentsInChildren<Renderer>(true))
            {
                if (arm != null && r.transform.IsChildOf(arm.transform)) continue;
                r.gameObject.layer = LAYER_P1_BODY;
                count++;
            }
            LogV($"P1: {count} Renderers -> layer={LAYER_P1_BODY}");

            bool isTPNow = player1.GetComponentInChildren<ThirdPerson>()?.ThirdPersonState ?? false;

            if (arm != null)
            {
                
                
                arm.gameObject.SetActive(!isTPNow);
                if (!isTPNow)
                    SetLayerRecursively(arm.transform, LAYER_P1_HAND);
                LogV($"P1: armMesh active={!isTPNow} -> layer={LAYER_P1_HAND}");
            }

            
            int initialHandLayer = isTPNow ? LAYER_P1_BODY : p1HandCameraLayer;
            if (player1.leftHandParent != null)
            {
                SetLayerRecursivelyKeepColliders(player1.leftHandParent, initialHandLayer, null);
                LogV($"P1: leftHandParent -> layer={initialHandLayer}");
            }
            if (player1.rightHandParent != null)
            {
                SetLayerRecursivelyKeepColliders(player1.rightHandParent, initialHandLayer, null);
                LogV($"P1: rightHandParent -> layer={initialHandLayer}");
            }
        }

        
        static readonly System.Collections.Generic.List<Renderer> _p1ToolRendBuf = new System.Collections.Generic.List<Renderer>();
        static void EnsureP1ToolLightProbes(Transform handParent)
        {
            if (handParent == null) return;
            _p1ToolRendBuf.Clear();
            handParent.GetComponentsInChildren(true, _p1ToolRendBuf);
            for (int i = 0; i < _p1ToolRendBuf.Count; i++)
            {
                var r = _p1ToolRendBuf[i];
                if (r == null) continue;
                if (r.lightProbeUsage == LightProbeUsage.Off)
                    r.lightProbeUsage = LightProbeUsage.BlendProbes;
                if (r.reflectionProbeUsage == ReflectionProbeUsage.Off)
                    r.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            }
        }

        public static void SetLayerRecursively(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursively(t.GetChild(i), layer);
        }

        
        
        static void SetLayerRecursivelyExcept(Transform t, int layer, Transform except)
        {
            if (except != null && t == except) return;     
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursivelyExcept(t.GetChild(i), layer, except);
        }

        static bool AnyChildLayerDiffers(Transform t, int layer, Transform except)
        {
            if (t == null) return false;
            if (except != null && t == except) return false;
            if (t.gameObject.layer != layer) return true;
            for (int i = 0; i < t.childCount; i++)
                if (AnyChildLayerDiffers(t.GetChild(i), layer, except))
                    return true;
            return false;
        }

        static void SetLayerRecursivelyKeepColliders(Transform t, int layer, Transform except)
        {
            if (except != null && t == except) return;
            if (t.GetComponent<Collider>() == null)
                t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursivelyKeepColliders(t.GetChild(i), layer, except);
        }

        static bool AnyChildLayerDiffersKeepColliders(Transform t, int layer, Transform except)
        {
            if (t == null) return false;
            if (except != null && t == except) return false;
            if (t.GetComponent<Collider>() == null && t.gameObject.layer != layer) return true;
            for (int i = 0; i < t.childCount; i++)
                if (AnyChildLayerDiffersKeepColliders(t.GetChild(i), layer, except))
                    return true;
            return false;
        }

        
        
        
        
        static Transform GetCarriedParentedTransform(Network_Player p)
        {
            if (p == null || p.rightHandParent == null) return null;
            var cc = p.CarryingComponent;
            if (cc == null || !cc.IsCarrying || cc.CarriedObject == null) return null;
            var rhp = p.rightHandParent;
            var t = cc.CarriedObject.transform;
            while (t != null && t.parent != rhp) t = t.parent;   
            return t;
        }

        
        
        
        
        static FieldInfo s_tpCarryPos, s_tpCarryRot;
        static void ForceP1CarryTPOffset(Transform p1Carried)
        {
            if (p1Carried == null || player1 == null || player1.CarryingComponent == null) return;
            var carry = player1.CarryingComponent.CarriedObject;
            if (carry == null) return;
            var dom = carry.GetComponentInParent<AI_NetworkBehaviour_Domestic>();
            if (dom == null) return;
            if (s_tpCarryPos == null)
            {
                var bf = BindingFlags.Instance | BindingFlags.NonPublic;
                s_tpCarryPos = typeof(AI_NetworkBehaviour_Domestic).GetField("thirdPcarryPosOffset", bf);
                s_tpCarryRot = typeof(AI_NetworkBehaviour_Domestic).GetField("thirdPcarryRotOffset", bf);
            }
            if (s_tpCarryPos == null) return;
            p1Carried.localPosition    = (Vector3)s_tpCarryPos.GetValue(dom);
            p1Carried.localEulerAngles = (Vector3)s_tpCarryRot.GetValue(dom);
        }

        
        internal static void EnforceMeshStates()
        {
            if (player1.currentModel == null) return;

            FirstPersonVisualRig.Tick();
            player1ArmMesh = FirstPersonVisualRig.GetArm(player1);
            player2ArmMesh = FirstPersonVisualRig.GetArm(player2);

            
            
            
            Transform p1Carried = GetCarriedParentedTransform(player1);
            ForceP1CarryTPOffset(p1Carried);
            RefreshRendererCachesIfNeeded();

            bool refreshP1BodyLayers = Time.frameCount >= _nextP1BodyLayerRefreshFrame
                                       || _p1BodyCarriedCache != p1Carried;
            if (refreshP1BodyLayers)
            {
                _nextP1BodyLayerRefreshFrame = Time.frameCount + 30;
                _p1BodyCarriedCache = p1Carried;
                var p1LeftHand = player1.leftHandParent;
                var p1RightHand = player1.rightHandParent;
                var p1ModelRoot = player1.currentModel != null ? player1.currentModel.transform : null;
                foreach (var r in _p1ModelRenderers)
                {
                    if (r == null) continue;
                    if (p1ModelRoot != null && !r.transform.IsChildOf(p1ModelRoot)) continue;
                    if (player1ArmMesh != null && r.transform.IsChildOf(player1ArmMesh.transform))
                        continue;
                    if (p1LeftHand != null && r.transform.IsChildOf(p1LeftHand))
                        continue;
                    if (p1RightHand != null && r.transform.IsChildOf(p1RightHand))
                        continue;
                    if (p1Carried != null && r.transform.IsChildOf(p1Carried))
                        continue;
                    r.gameObject.layer = LAYER_P1_BODY;
                }
            }

            if (player1ThirdPerson == null)
                player1ThirdPerson = player1.GetComponentInChildren<ThirdPerson>();
            bool p1IsTP = player1ThirdPerson != null && player1ThirdPerson.ThirdPersonState;

            
            
            
            EnforceP1Binoculars(p1IsTP);
            bool p1Fp = !p1IsTP && !P1BinocActive;
            if (p1Fp) FirstPersonVisualRig.MountForFirstPerson(player1);
            else FirstPersonVisualRig.MountForWorld(player1);
            FirstPersonVisualRig.SyncHat(player1, p1Fp);   // FP 自视图帽子并入 FP rig(相机相对稳定遮罩)
            FirstPersonVisualRig.SyncCarriedPerspective(player1, p1Fp);   // 搬运物握持偏移跟随真实FP状态
            if (player1ArmMesh != null)
            {
                bool armActive = !p1IsTP && !P1BinocActive;
                FirstPersonVisualRig.SetArmVisible(player1, armActive);
            }

            int handParentLayer = p1IsTP ? LAYER_P1_BODY : p1HandCameraLayer;
            bool p1NotebookOpen = IsAnyNotebookDisplayed();
            bool periodicP1HandRefresh = Time.frameCount >= _nextP1HandLayerRefreshFrame;
            bool p1HandLayerDrifted = periodicP1HandRefresh &&
                !p1NotebookOpen &&
                ((player1ArmMesh != null && AnyChildLayerDiffers(player1ArmMesh.transform, LAYER_P1_HAND, null))
                || (player1.leftHandParent != null && AnyChildLayerDiffersKeepColliders(player1.leftHandParent, handParentLayer, p1Carried))
                || (player1.rightHandParent != null && AnyChildLayerDiffersKeepColliders(player1.rightHandParent, handParentLayer, p1Carried)));
            bool refreshP1Hands = !p1NotebookOpen &&
                                  (periodicP1HandRefresh
                                  || _p1HandLayerCache != handParentLayer
                                  || _p1HandCarriedCache != p1Carried
                                  || _p1ArmLayerCache != LAYER_P1_HAND
                                  || p1HandLayerDrifted);
            if (refreshP1Hands)
            {
                _nextP1HandLayerRefreshFrame = Time.frameCount + 30;
                _p1HandLayerCache = handParentLayer;
                _p1HandCarriedCache = p1Carried;
                _p1ArmLayerCache = LAYER_P1_HAND;

                if (player1ArmMesh != null)
                    SetLayerRecursively(player1ArmMesh.transform, LAYER_P1_HAND);
                if (player1.leftHandParent != null)
                    SetLayerRecursivelyKeepColliders(player1.leftHandParent, handParentLayer, p1Carried);
                if (player1.rightHandParent != null)
                    SetLayerRecursivelyKeepColliders(player1.rightHandParent, handParentLayer, p1Carried);

                EnsureP1ToolLightProbes(player1.leftHandParent);
                EnsureP1ToolLightProbes(player1.rightHandParent);
            }

            if (Time.frameCount >= _nextP1BodyCacheRefreshFrame)
            {
                _nextP1BodyCacheRefreshFrame = Time.frameCount + 30;
                p1CachedBodyRenderers.Clear();
                var p1LeftHand = player1.leftHandParent;
                var p1RightHand = player1.rightHandParent;
                var p1ModelRoot = player1.currentModel != null ? player1.currentModel.transform : null;
                foreach (var r in _p1ModelRenderers)
                {
                    if (r == null) continue;
                    if (p1ModelRoot != null && !r.transform.IsChildOf(p1ModelRoot)) continue;
                    if (player1ArmMesh != null && r.transform.IsChildOf(player1ArmMesh.transform)) continue;
                    if (p1LeftHand != null && r.transform.IsChildOf(p1LeftHand)) continue;
                    if (p1RightHand != null && r.transform.IsChildOf(p1RightHand)) continue;
                    p1CachedBodyRenderers.Add(r);
                }
            }

            
            
            
            bool refreshP2BodyList = Time.frameCount >= _nextP2BodyListRefreshFrame
                                     || _p2BodyLeftHandCache != (player2 != null ? player2.leftHandParent : null)
                                     || _p2BodyRightHandCache != (player2 != null ? player2.rightHandParent : null);
            if (refreshP2BodyList)
            {
                _nextP2BodyListRefreshFrame = Time.frameCount + 30;
                _p2BodyLeftHandCache = player2 != null ? player2.leftHandParent : null;
                _p2BodyRightHandCache = player2 != null ? player2.rightHandParent : null;
                _p2BodyRenderers.Clear();
                if (player2 != null && player2.currentModel != null)
                {
                    var lhp = player2.leftHandParent;  var rhp = player2.rightHandParent;
                    foreach (var r in _p2ModelRenderers)
                    {
                        if (r == null) continue;
                        if (player2ArmMesh != null && r.transform.IsChildOf(player2ArmMesh.transform)) continue;  
                        if (lhp != null && r.transform.IsChildOf(lhp)) continue;   
                        if (rhp != null && r.transform.IsChildOf(rhp)) continue;   
                        _p2BodyRenderers.Add(r);
                    }
                }
            }

            EnforceP2FpArms();
        }
        static readonly System.Collections.Generic.List<Renderer> _p2BodyRenderers = new System.Collections.Generic.List<Renderer>();
        
        static readonly System.Collections.Generic.List<Renderer> _p1RendBuf = new System.Collections.Generic.List<Renderer>();
        static readonly System.Collections.Generic.List<Renderer> _p2RendBuf = new System.Collections.Generic.List<Renderer>();
        static readonly System.Collections.Generic.List<Renderer> _p1ModelRenderers = new System.Collections.Generic.List<Renderer>();
        static readonly System.Collections.Generic.List<Renderer> _p2ModelRenderers = new System.Collections.Generic.List<Renderer>();
        static CharacterModelModifications _p1RendererCacheModel;
        static CharacterModelModifications _p2RendererCacheModel;
        static int _nextRendererCacheRefreshFrame;
        static int _p2LeftHandOrigLayer = -1, _p2RightHandOrigLayer = -1;
        static FieldInfo _equipmentRemoteModelField;
        static FieldInfo _equipmentLocalModelField;
        static int _nextP1HandLayerRefreshFrame;
        static int _p1HandLayerCache = int.MinValue;
        static int _p1ArmLayerCache = int.MinValue;
        static Transform _p1HandCarriedCache;
        static int _nextP2ToolLayerRefreshFrame;
        static int _p2ToolLayerCache = int.MinValue;
        static int _p2ToolLeftLayerCache = int.MinValue;
        static int _p2ToolRightLayerCache = int.MinValue;
        static Transform _p2ToolCarriedCache;
        static int _nextP1BodyLayerRefreshFrame;
        static Transform _p1BodyCarriedCache;
        static int _nextP1BodyCacheRefreshFrame;
        static int _nextP2BodyListRefreshFrame;
        static Transform _p2BodyLeftHandCache;
        static Transform _p2BodyRightHandCache;
        static void RefreshRendererCachesIfNeeded()
        {
            bool modelChanged = _p1RendererCacheModel != player1.currentModel
                                || _p2RendererCacheModel != (player2 != null ? player2.currentModel : null);
            if (!modelChanged && Time.frameCount < _nextRendererCacheRefreshFrame) return;

            _nextRendererCacheRefreshFrame = Time.frameCount + 30;
            _p1RendererCacheModel = player1.currentModel;
            _p2RendererCacheModel = player2 != null ? player2.currentModel : null;

            _p1ModelRenderers.Clear();
            _p1RendBuf.Clear();
            if (_p1RendererCacheModel != null)
            {
                _p1RendererCacheModel.GetComponentsInChildren(true, _p1RendBuf);
                for (int i = 0; i < _p1RendBuf.Count; i++)
                    if (_p1RendBuf[i] != null) _p1ModelRenderers.Add(_p1RendBuf[i]);
            }

            _p2ModelRenderers.Clear();
            _p2RendBuf.Clear();
            if (_p2RendererCacheModel != null)
            {
                _p2RendererCacheModel.GetComponentsInChildren(true, _p2RendBuf);
                for (int i = 0; i < _p2RendBuf.Count; i++)
                    if (_p2RendBuf[i] != null) _p2ModelRenderers.Add(_p2RendBuf[i]);
            }
        }

        struct P1EquipmentVisualState
        {
            internal Transform Model;
            internal bool Active;
            internal Renderer[] Renderers;
            internal int[] Layers;
        }
        struct P1EquipmentRenderCache
        {
            internal Equipment_Model Equipment;
            internal Transform Model;
            internal Transform LocalModel;
            internal Renderer[] Renderers;
        }
        static readonly List<P1EquipmentVisualState> _p1EquipmentVisualStates = new List<P1EquipmentVisualState>();
        static readonly List<P1EquipmentRenderCache> _p1EquipmentRenderCache = new List<P1EquipmentRenderCache>();
        static int _nextP1EquipmentCacheRefreshFrame;

        struct HairConnectionVisualState
        {
            internal HairStyleConnection Connection;
            internal bool[] ModelActive;
            internal float BlendShapeWeight;
        }
        struct RemoteVisualState
        {
            internal bool Active;
            internal CharacterModelModifications Model;
            internal bool FullBodyActive;
            internal bool FullBodyHazmatActive;
            internal bool HatParentValid;
            internal Transform HatParent;
            internal Transform HatOriginalParent;
            internal Vector3 HatLocalPosition;
            internal Quaternion HatLocalRotation;
            internal Vector3 HatLocalScale;
            internal List<HairConnectionVisualState> HairStates;
        }
        static RemoteVisualState _p1RemoteVisualForP2;
        static RemoteVisualState _p2RemoteVisualForP1;
        static FieldInfo _fullBodyHazmatField;

        
        
        
        
        static void EnforceP2FpArms()
        {
            if (player2 == null || player2.currentModel == null) return;
            var lhp = player2.leftHandParent;  var rhp = player2.rightHandParent;
            if (lhp != null && _p2LeftHandOrigLayer  < 0) _p2LeftHandOrigLayer  = lhp.gameObject.layer;
            if (rhp != null && _p2RightHandOrigLayer < 0) _p2RightHandOrigLayer = rhp.gameObject.layer;

            
            bool p2Downed = P2IsDownedOrCarried;
            bool fp = p2FirstPerson && !P2BinocHidesArms && !p2Downed;
            if (fp) FirstPersonVisualRig.MountForFirstPerson(player2);
            else FirstPersonVisualRig.MountForWorld(player2);
            FirstPersonVisualRig.SyncHat(player2, fp);   // P2 FP 自视图帽子并入 P2 rig(独立,不污染 P1)
            FirstPersonVisualRig.SyncCarriedPerspective(player2, fp);   // 同上(P2 独立)
            if (p2Downed)
            {
                EnforceP2DownedWorldVisual();
                return;
            }
            if (player2.HandCamera != null)
            {
                if (!player2.HandCamera.enabled)
                    player2.HandCamera.enabled = true;
            }
            if (player2ArmMesh != null)
            {
                if (player2ArmMesh.gameObject.activeSelf != fp) player2ArmMesh.gameObject.SetActive(fp);
                if (fp)
                {
                    SetP2ArmRenderersEnabled(player2ArmMesh.transform, true);
                }
                if (fp && Time.frameCount >= _nextP2ToolLayerRefreshFrame)
                    SetLayerRecursively(player2ArmMesh.transform, LAYER_P2_HAND);
            }
             
             
            SetP2ToolLayer(fp ? LAYER_P2_HAND : -1);

            
            
            
            
            if (player1 != null)
            {
                if (!P2BinocActive && player1.Camera != null && player2.Camera != null
                    && !Mathf.Approximately(player2.Camera.fieldOfView, player1.Camera.fieldOfView))
                    player2.Camera.fieldOfView = player1.Camera.fieldOfView;
                if (player1.HandCamera != null && player2.HandCamera != null
                    && !Mathf.Approximately(player2.HandCamera.fieldOfView, player1.HandCamera.fieldOfView))
                    player2.HandCamera.fieldOfView = player1.HandCamera.fieldOfView;
            }
        }

        
        internal static readonly System.Collections.Generic.List<Transform> P2FishingExtra = new System.Collections.Generic.List<Transform>();
        
        internal static readonly System.Collections.Generic.List<Transform> P1FishingExtra = new System.Collections.Generic.List<Transform>();
        internal static void SetP1FishingLayer(int layer)
        {
            for (int i = 0; i < P1FishingExtra.Count; i++)
                if (P1FishingExtra[i] != null) SetLayerRecursively(P1FishingExtra[i], layer);
        }

        
        internal static void SetP2ToolLayer(int layer)
        {
            if (player2 == null) return;
            var lhp = player2.leftHandParent;  var rhp = player2.rightHandParent;
            int lLayer = layer >= 0 ? layer : (_p2LeftHandOrigLayer  >= 0 ? _p2LeftHandOrigLayer  : (lhp != null ? lhp.gameObject.layer : 0));
            int rLayer = layer >= 0 ? layer : (_p2RightHandOrigLayer >= 0 ? _p2RightHandOrigLayer : (rhp != null ? rhp.gameObject.layer : 0));
            
            Transform p2Carried = GetCarriedParentedTransform(player2);
            bool refresh = Time.frameCount >= _nextP2ToolLayerRefreshFrame
                           || _p2ToolLayerCache != layer
                           || _p2ToolLeftLayerCache != lLayer
                           || _p2ToolRightLayerCache != rLayer
                           || _p2ToolCarriedCache != p2Carried;
            if (!refresh) return;

            _nextP2ToolLayerRefreshFrame = Time.frameCount + 30;
            _p2ToolLayerCache = layer;
            _p2ToolLeftLayerCache = lLayer;
            _p2ToolRightLayerCache = rLayer;
            _p2ToolCarriedCache = p2Carried;

            if (lhp != null) SetLayerRecursivelyKeepColliders(lhp, lLayer, p2Carried);
            if (rhp != null) SetLayerRecursivelyKeepColliders(rhp, rLayer, p2Carried);
            
            for (int i = 0; i < P2FishingExtra.Count; i++)
                if (P2FishingExtra[i] != null) SetLayerRecursively(P2FishingExtra[i], lLayer);
        }

        
        internal static void SetP2BodyVisible(bool visible)
        {
            for (int i = 0; i < _p2BodyRenderers.Count; i++)
                if (_p2BodyRenderers[i] != null) _p2BodyRenderers[i].enabled = visible;
        }

        static bool _p2BodyHiddenForP1CarryFp;
        static bool _p1BodyHiddenForP2CarryFp;
        static bool _p2FpArmsHiddenForP1WorldView;

        static bool ShouldHideP2CarriedBodyForP1Fp()
        {
            if (player1 == null || player2 == null) return false;
            if (player1.RessurectComponent == null || !player1.RessurectComponent.IsCarrying) return false;
            if (player1.RessurectComponent.CarriedPlayer != player2) return false;

            if (player1ThirdPerson == null)
                player1ThirdPerson = player1.GetComponentInChildren<ThirdPerson>();
            return player1ThirdPerson == null || !player1ThirdPerson.ThirdPersonState;
        }

        static bool ShouldHideP1CarriedBodyForP2Fp()
        {
            if (player1 == null || player2 == null) return false;
            if (player2.RessurectComponent == null || !player2.RessurectComponent.IsCarrying) return false;
            if (player2.RessurectComponent.CarriedPlayer != player1) return false;
            return p2FirstPerson;
        }

        static void HideP2FirstPersonArmsForWorldView()
        {
            if (player2ArmMesh != null && player2ArmMesh.gameObject.activeSelf)
                player2ArmMesh.gameObject.SetActive(false);

            var lhp = player2 != null ? player2.leftHandParent : null;
            var rhp = player2 != null ? player2.rightHandParent : null;
            SetP2ArmRenderersEnabled(lhp, false);
            SetP2ArmRenderersEnabled(rhp, false);
        }

        static void BeginHideP2FirstPersonArmsForP1WorldView()
        {
            if (!p2FirstPerson || P2IsDownedOrCarried) return;
            FirstPersonVisualRig.MountForWorld(player2);
            HideP2FirstPersonArmsForWorldView();
            _p2FpArmsHiddenForP1WorldView = true;
        }

        static void RestoreP2FirstPersonArmsAfterP1WorldView()
        {
            if (!_p2FpArmsHiddenForP1WorldView) return;
            _p2FpArmsHiddenForP1WorldView = false;
            FirstPersonVisualRig.MountForFirstPerson(player2);
            EnforceP2FpArms();
        }

        static void SetP2ArmRenderersEnabled(Transform root, bool enabled)
        {
            if (root == null) return;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                if (player2ArmMesh != null && r.transform.IsChildOf(player2ArmMesh.transform))
                    r.enabled = enabled;
            }
        }

        internal static void EnforceP2DownedWorldVisual()
        {
            if (player2 == null || !P2IsDownedOrCarried) return;
            int remotePlayerLayer = RemotePlayerLayer();
            if (remotePlayerLayer < 0 || player2.currentModel == null) return;

            if (!player2.currentModel.gameObject.activeSelf)
                player2.currentModel.gameObject.SetActive(true);

            try
            {
                player2.currentModel.SetFullBodyMeshState(true);
                player2.currentModel.SetArmMeshState(false);
                player2.currentModel.ActivateHairStyle(HairStyle.Full, useAsCurrentHairStyle: false);
                ApplyEquippedHeadVisuals(player2);
            }
            catch (System.Exception e) { LogV("[P2Rendering] RestoreP2WorldVisualState model setup ignored: " + e.Message); }

            HideP2FirstPersonArmsForWorldView();

            var lhp = player2.leftHandParent;
            var rhp = player2.rightHandParent;
            _p2BodyRenderers.Clear();
            _p2RendBuf.Clear();
            player2.currentModel.GetComponentsInChildren(true, _p2RendBuf);
            for (int i = 0; i < _p2RendBuf.Count; i++)
            {
                var r = _p2RendBuf[i];
                if (r == null) continue;
                if (player2ArmMesh != null && r.transform.IsChildOf(player2ArmMesh.transform)) continue;
                if (lhp != null && r.transform.IsChildOf(lhp)) continue;
                if (rhp != null && r.transform.IsChildOf(rhp)) continue;
                r.gameObject.layer = remotePlayerLayer;
                r.enabled = true;
                r.shadowCastingMode = ShadowCastingMode.On;
                if (r.lightProbeUsage == LightProbeUsage.Off)
                    r.lightProbeUsage = LightProbeUsage.BlendProbes;
                if (r.reflectionProbeUsage == ReflectionProbeUsage.Off)
                    r.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                _p2BodyRenderers.Add(r);
            }

            if (player2.HandCamera != null && player2.HandCamera.enabled)
                player2.HandCamera.enabled = false;
        }

        static void BeginP1EquipmentForP2View()
        {
            RestoreP1EquipmentForP2View();
            if (player1 == null || player1.PlayerEquipment == null || p1LocalPlayerLayer < 0) return;

            RefreshP1EquipmentRenderCacheIfNeeded();

            for (int i = 0; i < _p1EquipmentRenderCache.Count; i++)
            {
                var e = _p1EquipmentRenderCache[i].Equipment;
                if (e == null || !e.Equipped) continue;
                var model = _p1EquipmentRenderCache[i].Model;
                if (model == null) continue;

                var renderers = _p1EquipmentRenderCache[i].Renderers;
                if (renderers == null) continue;
                var layers = new int[renderers.Length];
                for (int r = 0; r < renderers.Length; r++)
                    layers[r] = renderers[r] != null ? renderers[r].gameObject.layer : -1;

                _p1EquipmentVisualStates.Add(new P1EquipmentVisualState
                {
                    Model = model,
                    Active = model.gameObject.activeSelf,
                    Renderers = renderers,
                    Layers = layers
                });

                // P1 在 FP 时 vanilla 开着 FP localModel(SetP1BodyRenderLayer 会把它扫到 P2 可见层),
                // 同帧 remoteModel 又被强开 → P2 看到两个头。呈现给 P2 即 vanilla TP 状态:remoteModel 开、
                // localModel 关。快照后隐藏 localModel,RestoreP1EquipmentForP2View 在 P2 渲染后原路还原,不影响 P1 自视图。
                var localModel = _p1EquipmentRenderCache[i].LocalModel;
                if (localModel != null && localModel.gameObject.activeSelf)
                {
                    _p1EquipmentVisualStates.Add(new P1EquipmentVisualState { Model = localModel, Active = true, Renderers = null, Layers = null });
                    localModel.gameObject.SetActive(false);
                }
                if (!model.gameObject.activeSelf) model.gameObject.SetActive(true);
                for (int r = 0; r < renderers.Length; r++)
                    if (renderers[r] != null) renderers[r].gameObject.layer = p1LocalPlayerLayer;
            }
        }

        static void RefreshP1EquipmentRenderCacheIfNeeded()
        {
            if (Time.frameCount < _nextP1EquipmentCacheRefreshFrame) return;
            _nextP1EquipmentCacheRefreshFrame = Time.frameCount + 15;

            _p1EquipmentRenderCache.Clear();
            if (player1 == null || player1.PlayerEquipment == null) return;
            if (_equipmentRemoteModelField == null)
                _equipmentRemoteModelField = typeof(Equipment_Model).GetField("remoteModel", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_equipmentRemoteModelField == null) return;
            if (_equipmentLocalModelField == null)
                _equipmentLocalModelField = typeof(Equipment_Model).GetField("localModel", BindingFlags.Instance | BindingFlags.NonPublic);

            var equipment = player1.PlayerEquipment.GetComponentsInChildren<Equipment_Model>(true);
            for (int i = 0; i < equipment.Length; i++)
            {
                var e = equipment[i];
                if (e == null) continue;
                var model = _equipmentRemoteModelField.GetValue(e) as Transform;
                if (model == null) continue;
                _p1EquipmentRenderCache.Add(new P1EquipmentRenderCache
                {
                    Equipment = e,
                    Model = model,
                    LocalModel = _equipmentLocalModelField != null ? _equipmentLocalModelField.GetValue(e) as Transform : null,
                    Renderers = model.GetComponentsInChildren<Renderer>(true)
                });
            }
        }

        static void RestoreP1EquipmentForP2View()
        {
            for (int i = 0; i < _p1EquipmentVisualStates.Count; i++)
            {
                var st = _p1EquipmentVisualStates[i];
                if (st.Renderers != null && st.Layers != null)
                {
                    for (int r = 0; r < st.Renderers.Length && r < st.Layers.Length; r++)
                        if (st.Renderers[r] != null && st.Layers[r] >= 0)
                            st.Renderers[r].gameObject.layer = st.Layers[r];
                }
                if (st.Model != null && st.Model.gameObject.activeSelf != st.Active)
                    st.Model.gameObject.SetActive(st.Active);
            }
            _p1EquipmentVisualStates.Clear();
        }

        static GameObject GetP1FullBodyHazmatObject(CharacterModelModifications model)
        {
            if (model == null) return null;
            if (_fullBodyHazmatField == null)
                _fullBodyHazmatField = typeof(CharacterModelModifications).GetField(
                    "fullBodyMeshHazmat", BindingFlags.Instance | BindingFlags.NonPublic);
            return _fullBodyHazmatField?.GetValue(model) as GameObject;
        }

        static void BeginP1RemoteVisualForP2View()
        {
            BeginRemoteVisual(player1, ref _p1RemoteVisualForP2);
        }

        static void BeginP2RemoteVisualForP1View()
        {
            BeginRemoteVisual(player2, ref _p2RemoteVisualForP1);
        }

        static void BeginRemoteVisual(Network_Player player, ref RemoteVisualState remoteState)
        {
            RestoreRemoteVisual(player, ref remoteState);
            if (player == null || player.currentModel == null) return;

            FirstPersonVisualRig.MountForWorld(player);

            var model = player.currentModel;
            var state = new RemoteVisualState
            {
                Active = true,
                Model = model,
                FullBodyActive = model.fullBodyMesh != null && model.fullBodyMesh.gameObject.activeSelf,
                HairStates = new List<HairConnectionVisualState>()
            };

            var hazmat = GetP1FullBodyHazmatObject(model);
            state.FullBodyHazmatActive = hazmat != null && hazmat.activeSelf;

            if (model.hatParent != null)
            {
                state.HatParentValid = true;
                state.HatParent = model.hatParent;
                state.HatOriginalParent = model.hatParent.parent;
                state.HatLocalPosition = model.hatParent.localPosition;
                state.HatLocalRotation = model.hatParent.localRotation;
                state.HatLocalScale = model.hatParent.localScale;
            }

            if (model.hairStyles != null)
            {
                for (int i = 0; i < model.hairStyles.Length; i++)
                {
                    var h = model.hairStyles[i];
                    if (h == null) continue;
                    bool[] active = null;
                    if (h.models != null)
                    {
                        active = new bool[h.models.Length];
                        for (int m = 0; m < h.models.Length; m++)
                            active[m] = h.models[m] != null && h.models[m].activeSelf;
                    }
                    float weight = 0f;
                    if (h.blendShapeRenderer != null)
                        weight = h.blendShapeRenderer.GetBlendShapeWeight(h.blendShapeIndex);
                    state.HairStates.Add(new HairConnectionVisualState
                    {
                        Connection = h,
                        ModelActive = active,
                        BlendShapeWeight = weight
                    });
                }
            }

            remoteState = state;

            if (model.fullBodyMesh != null && !model.fullBodyMesh.gameObject.activeSelf)
                model.fullBodyMesh.gameObject.SetActive(true);
            if (hazmat != null && player.HazmatSuit != null && player.HazmatSuit.Equipped && !hazmat.activeSelf)
                hazmat.SetActive(true);

            bool forcedLocal = false;
            try
            {
                if (VanillaAccessors.GetIsLocalPlayer(player))
                {
                    VanillaAccessors.SetIsLocalPlayer(player, false);
                    forcedLocal = true;
                }
                model.ActivateHairStyle(model.CurrentHairStyle, useAsCurrentHairStyle: false);
            }
            catch (System.Exception e)
            {
                LogV("[RemoteVisual] hair scope failed: " + e.Message);
            }
            finally
            {
                if (forcedLocal && player != null)
                    VanillaAccessors.SetIsLocalPlayer(player, true);
            }

            if (model.hatParent != null && model.headBone != null)
            {
                model.hatParent.SetParent(model.headBone, false);
                model.hatParent.localPosition = Vector3.zero;
                model.hatParent.localRotation = Quaternion.identity;
            }
        }

        static void RestoreP1RemoteVisualForP2View()
        {
            RestoreRemoteVisual(player1, ref _p1RemoteVisualForP2);
        }

        static void RestoreP2RemoteVisualForP1View()
        {
            RestoreRemoteVisual(player2, ref _p2RemoteVisualForP1);
        }

        static void RestoreRemoteVisual(Network_Player player, ref RemoteVisualState remoteState)
        {
            if (!remoteState.Active) return;

            var st = remoteState;
            remoteState = default(RemoteVisualState);

            var model = st.Model;
            if (model != null)
            {
                if (model.fullBodyMesh != null && model.fullBodyMesh.gameObject.activeSelf != st.FullBodyActive)
                    model.fullBodyMesh.gameObject.SetActive(st.FullBodyActive);

                var hazmat = GetP1FullBodyHazmatObject(model);
                if (hazmat != null && hazmat.activeSelf != st.FullBodyHazmatActive)
                    hazmat.SetActive(st.FullBodyHazmatActive);

                if (st.HairStates != null)
                {
                    for (int i = 0; i < st.HairStates.Count; i++)
                    {
                        var hst = st.HairStates[i];
                        var h = hst.Connection;
                        if (h == null) continue;
                        if (h.models != null && hst.ModelActive != null)
                        {
                            int count = Mathf.Min(h.models.Length, hst.ModelActive.Length);
                            for (int m = 0; m < count; m++)
                                if (h.models[m] != null && h.models[m].activeSelf != hst.ModelActive[m])
                                    h.models[m].SetActive(hst.ModelActive[m]);
                        }
                        if (h.blendShapeRenderer != null)
                            h.blendShapeRenderer.SetBlendShapeWeight(h.blendShapeIndex, hst.BlendShapeWeight);
                    }
                }
            }

            if (st.HatParentValid && st.HatParent != null)
            {
                st.HatParent.SetParent(st.HatOriginalParent, false);
                st.HatParent.localPosition = st.HatLocalPosition;
                st.HatParent.localRotation = st.HatLocalRotation;
                st.HatParent.localScale = st.HatLocalScale;
            }

            var thirdPerson = player != null ? player.GetComponentInChildren<ThirdPerson>() : null;
            if (thirdPerson == null || !thirdPerson.ThirdPersonState)
                FirstPersonVisualRig.MountForFirstPerson(player);

        }

        internal static void RestoreP2WorldVisualState()
        {
            if (player2 == null) return;

            int remotePlayerLayer = LayerMask.NameToLayer("RemotePlayer");
            if (remotePlayerLayer >= 0 && player2.currentModel != null)
            {
                if (!player2.currentModel.gameObject.activeSelf)
                    player2.currentModel.gameObject.SetActive(true);
                try
                {
                    player2.currentModel.SetFullBodyMeshState(true);
                    player2.currentModel.SetArmMeshState(false);
                    player2.currentModel.ActivateHairStyle(HairStyle.Full, useAsCurrentHairStyle: false);
                    ApplyEquippedHeadVisuals(player2);
                }
                catch (System.Exception e)
                {
                    LogV("[P2VisualRestore] model state restore failed: " + e.Message);
                }

                var lhp = player2.leftHandParent;
                var rhp = player2.rightHandParent;
                _p2BodyRenderers.Clear();
                _p2RendBuf.Clear();
                player2.currentModel.GetComponentsInChildren(true, _p2RendBuf);
                foreach (var r in _p2RendBuf)
                {
                    if (r == null) continue;
                    if (player2ArmMesh != null && r.transform.IsChildOf(player2ArmMesh.transform)) continue;
                    if (lhp != null && r.transform.IsChildOf(lhp)) continue;
                    if (rhp != null && r.transform.IsChildOf(rhp)) continue;
                    r.gameObject.layer = remotePlayerLayer;
                    r.enabled = true;
                    r.shadowCastingMode = ShadowCastingMode.On;
                    if (r.lightProbeUsage == LightProbeUsage.Off)
                        r.lightProbeUsage = LightProbeUsage.BlendProbes;
                    if (r.reflectionProbeUsage == ReflectionProbeUsage.Off)
                        r.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                    _p2BodyRenderers.Add(r);
                }
            }

            if (player2ArmMesh != null && player2ArmMesh.gameObject.activeSelf)
                player2ArmMesh.gameObject.SetActive(false);

            if (player2.HandCamera != null)
            {
                bool p2Sleeping = player2.BedComponent != null && player2.BedComponent.Sleeping;
                bool p2HandCameraActive = !P2IsDownedOrCarried && !p2Sleeping && !SplitScreenDeathFlow.P2PostBedExitRestoring;
                if (player2.HandCamera.enabled != p2HandCameraActive)
                    player2.HandCamera.enabled = p2HandCameraActive;
            }

            if (!P2IsDownedOrCarried && p2FirstPerson && !SplitScreenDeathFlow.P2PostBedExitRestoring)
                SetP2ToolLayer(LAYER_P2_HAND);
            HideP2FirstPersonArmsForWorldView();
            SetP2BodyVisible(true);
            EnsureSplitLightingMasks();
        }

        internal static void ApplyEquippedHeadVisuals(Network_Player np)
        {
            if (np == null || np.PlayerEquipment == null) return;
            var equipment = np.PlayerEquipment.GetComponentsInChildren<Equipment_Model>(true);
            for (int i = 0; i < equipment.Length; i++)
            {
                var model = equipment[i];
                if (model == null || !model.Equipped) continue;
                if (model is Equipment_Hat || model is Equipment_Helmet)
                    model.SetModelState(true);
            }
        }

        
        // 触发全屏用「任意 NoteBookUI 正在显示」而非 player1.NoteBookUI —— 分屏下 P1 对象上
        // 克隆增殖出多个 NoteBookUI(UE 实测 4 个),player1.NoteBookUI 引用的那个未必是按 T 打开的那本,
        // 盯单一引用会漏触发。缓存实例列表(每 120 帧刷一次),每帧只遍历 isDisplayed(廉价)。
        static NoteBookUI[] _nbCache;
        static int _nbCacheFrame = -999;
        internal static bool IsAnyNotebookDisplayed()
        {
            if (_nbCache == null || Time.frameCount - _nbCacheFrame > 120)
            {
                _nbCache = Resources.FindObjectsOfTypeAll<NoteBookUI>();
                _nbCacheFrame = Time.frameCount;
            }
            var arr = _nbCache;
            if (arr == null) return false;
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] != null && arr[i].isDisplayed) return true;
            return false;
        }

        // 全屏期间被关闭的克隆/P2 相机,退出时精确恢复。
        static readonly System.Collections.Generic.List<Camera> _nbDisabledCams = new System.Collections.Generic.List<Camera>();

        static void EnterNotebookFullscreen()
        {
            _nbFullscreenActive = true;
            if (player1?.Camera != null) { _p1CamRectSaved = player1.Camera.rect; _p1CamFovSaved = player1.Camera.fieldOfView; }
            if (player1?.HandCamera != null) _p1HandCamRectSaved = player1.HandCamera.rect;
            // 双显示器:P1 本就独占 display0、P2 在 display1 继续游戏 -> 只做笔记本渲染,不改 UI 全屏、不关 P2。
            bool sameScreen = ActiveSplitMode != SplitMode.DualMonitor;
            if (_p1UiCamera != null) { _p1UiCamRectSaved = _p1UiCamera.rect; if (sameScreen) _p1UiCamera.rect = new Rect(0f, 0f, 1f, 1f); }
            if (sameScreen && _p2UiCamera != null) _p2UiCamera.enabled = false;
            // 同屏左右分屏下,克隆增殖出多台 P2/残留 PlayerCamera+HandCamera 仍渲染左右半
            // (UE 实测:除 player1.Camera 外另有一台 PlayerCamera 占左半 + 一台 HandCamera 又渲染笔记本层
            //  = 一本书被两台相机渲染)。只关 player2.Camera 一个引用漏掉克隆。改为全屏期间关闭【所有】
            // 游戏 Player/Hand 相机、只留 player1.Camera(手相机在下方 mask 置 0)。UE/水效/反射相机按名字排除。
            DisableAllNonP1PlayerCameras();
        }

        static void DisableAllNonP1PlayerCameras()
        {
            _nbDisabledCams.Clear();
            var all = Resources.FindObjectsOfTypeAll<Camera>();
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null || !c.gameObject.scene.IsValid() || !c.enabled) continue;
                if (c == player1?.Camera) continue;   // 只留 P1 主相机;P1 手相机也关掉(否则其左半 depth-clear = 中缝)
                if (c.name != "PlayerCamera" && c.name != "HandCamera") continue;   // 只碰游戏主/手相机,放过 UE/水效/反射
                if (ActiveSplitMode == SplitMode.DualMonitor && c.targetDisplay == 1) continue;   // 双显示器:P2 在第二块屏,保留它继续游戏
                _nbDisabledCams.Add(c);
                c.enabled = false;
            }
        }

        static void ExitNotebookFullscreen()
        {
            _nbFullscreenActive = false;
            UnfreezeP1Animator();
            if (player1?.Camera != null && _p1CamRectSaved.width > 0f) player1.Camera.rect = _p1CamRectSaved;
            if (player1?.Camera != null && _p1CamFovSaved > 0f) { player1.Camera.fieldOfView = _p1CamFovSaved; _p1CamFovSaved = -1f; }
            if (player1?.HandCamera != null && _p1HandCamRectSaved.width > 0f) player1.HandCamera.rect = _p1HandCamRectSaved;
            if (_p1UiCamera != null && _p1UiCamRectSaved.width > 0f) _p1UiCamera.rect = _p1UiCamRectSaved;
            if (_p2UiCamera != null) _p2UiCamera.enabled = true;
            for (int i = 0; i < _nbDisabledCams.Count; i++)
                if (_nbDisabledCams[i] != null) _nbDisabledCams[i].enabled = true;
            _nbDisabledCams.Clear();
        }

        static void OnCameraPreCull(Camera cam)
        {
            if (player1 == null || player2 == null) return;
            if (cam == player2.Camera && p2FirstPerson && !P2IsDownedOrCarried)
            {
                P2CameraController.EnforceFirstPersonCamera();
            }

            bool p1NbOpenFs = IsAnyNotebookDisplayed();
            if (cam == player1.Camera)
            {
                if (p1NbOpenFs && !_nbFullscreenActive) EnterNotebookFullscreen();
                else if (!p1NbOpenFs && _nbFullscreenActive) ExitNotebookFullscreen();
            }
            if (_nbFullscreenActive)
            {
                // 每帧复位:防某系统(如 SetCameraRects)中途把克隆相机重新点亮 -> 第二本又冒出来。
                for (int i = 0; i < _nbDisabledCams.Count; i++)
                    if (_nbDisabledCams[i] != null && _nbDisabledCams[i].enabled) _nbDisabledCams[i].enabled = false;
                int nbBit = p1HandCameraLayer >= 0 ? (1 << p1HandCameraLayer) : 0;
                if (cam == player1.Camera)
                {
                    EnforceP1CameraPitch();
                    EnforceP1HandCameraVisible();
                    if (p1CamOriginalMask == -1) p1CamOriginalMask = cam.cullingMask;
                    cam.rect = new Rect(0f, 0f, 1f, 1f);
                    // 笔记本 fov(NotebookMainFov):38 太窄=书过大且裁切,自然 fov(~70)太宽=书太小;50 居中适中。
                    cam.fieldOfView = NotebookMainFov;
                    cam.cullingMask = p1CamOriginalMask | nbBit | (1 << LAYER_P1_HAND) | (1 << LAYER_P1_TOOL);
                    SetNotebookCanvasEventCamera(cam);
                    return;
                }
                if (cam == player1.HandCamera)
                {
                    cam.cullingMask = 0;
                    return;
                }
                // 同屏:其它相机(P2)已禁用 -> return。双显示器:P2 相机要正常渲染,放行到下方常规处理。
                if (ActiveSplitMode != SplitMode.DualMonitor) return;
            }

            if (cam == player1.HandCamera)
            {
                bool p1Dead = player1.PlayerScript != null && player1.PlayerScript.IsDead;
                bool p1ThirdPerson = player1ThirdPerson != null && player1ThirdPerson.ThirdPersonState;
                if (p1Dead || p1ThirdPerson)
                {
                    cam.cullingMask = 0;
                    return;
                }

                EnforceP1HandCameraVisible();
                cam.cullingMask = (1 << LAYER_P1_HAND)
                                  | (1 << LAYER_P1_TOOL)
                                  | (p1HandCameraLayer >= 0 ? (1 << p1HandCameraLayer) : 0);
                return;
            }

            if (cam == player2.HandCamera)
            {
                bool p2SleepingForHandCam = player2 != null && player2.BedComponent != null && player2.BedComponent.Sleeping;
                bool p2RestoringForHandCam = SplitScreenDeathFlow.P2PostBedExitRestoring;
                bool renderP2Hands = p2FirstPerson && !P2IsDownedOrCarried && !p2SleepingForHandCam && !p2RestoringForHandCam;
                cam.cullingMask = renderP2Hands ? (1 << LAYER_P2_HAND) : 0;
                return;
            }

            if (cam == player1.Camera)
            {
                EnforceP1HandCameraVisible();
                SetupSplitPostFxProfiles();
                bool p2SleepingForP1 = player2 != null && player2.BedComponent != null && player2.BedComponent.Sleeping;
                if (p2SleepingForP1 || SplitScreenDeathFlow.P2PostBedExitRestoring)
                    RestoreP2WorldVisualState();
                if (!P2IsDownedOrCarried)
                    BeginP2RemoteVisualForP1View();
                if (p2FirstPerson) FirstPersonVisualRig.BeginCarriedAnimalTpForOtherView(player2);   // 对方视角搬运动物按TP摆
                BeginHideP2FirstPersonArmsForP1WorldView();
                if (P2IsDownedOrCarried) EnforceP2DownedWorldVisual();
                SetP2BodyVisible(true);   
                if (ShouldHideP2CarriedBodyForP1Fp())
                {
                    SetP2BodyVisible(false);
                    _p2BodyHiddenForP1CarryFp = true;
                }
                SetP1BodyRenderLayer(LAYER_P1_BODY);
                
                
                if (!P2IsDownedOrCarried && p2FirstPerson)
                {
                    if (P2ZiplineDriver.IsAttached) SetP2ToolLayer(RemotePlayerLayer());
                    else SetP2ToolLayer(-1);
                    BendP2SpineForP1View();
                }   
                if (P2ZiplineDriver.IsAttached && player2.ZiplinePlayer != null)
                    ZiplineRenderFix.SyncToolTransforms(player2.ZiplinePlayer, RemotePlayerLayer());
                if (P2ZiplineDriver.IsAttached)
                    ZiplineRenderFix.BeginRemoteVisual(player2, RemotePlayerLayer(), out _p2RemoteZiplineVisual);

                
                EnforceP1CameraPitch();
                RecalculateP1FreeLookSeatTpCameraBeforeRender();

                if (p1CamOriginalMask == -1) p1CamOriginalMask = cam.cullingMask;

                if (player1ThirdPerson == null)
                    player1ThirdPerson = player1.GetComponentInChildren<ThirdPerson>();

                bool p1IsTP = player1ThirdPerson != null && player1ThirdPerson.ThirdPersonState;
                bool p1DownedTp = SplitScreenDeathFlow.P1DownedThirdPerson;
                bool p1DeadFp = player1.PlayerScript != null && player1.PlayerScript.IsDead && !p1DownedTp;
                if (p1DownedTp)
                    SplitScreenDeathFlow.ApplyP1DownedThirdPersonCameraForRender(cam);

                // P1 只要在 TP 就按 TP 呈现(显示全身)。原先附着座位时只认"自由视角座位",
                // 于是 P1 在雪橇车等普通座位上切 TP 既拿不到轨道相机也不显示身体。
                // vanilla 的 TP 呈现本就与是否坐着无关,这里还原之。
                if (p1IsTP || p1DownedTp)
                {
                    SetP1BodyRenderLayer(p1LocalPlayerLayer);

                    
                    
                    
                    int p1BodyVisibleBit = p1LocalPlayerLayer >= 0 ? (1 << p1LocalPlayerLayer) : (1 << LAYER_P1_BODY);
                    int baseMask = p1DownedTp ? -1 : p1CamOriginalMask;
                    int uiLayer = LayerMask.NameToLayer("UI");
                    cam.cullingMask = (baseMask | p1BodyVisibleBit | (1 << LAYER_P1_BODY) | RemotePlayerMaskBit())
                                      & ~(1 << LAYER_P1_HAND)
                                      & ~(1 << LAYER_P1_TOOL)
                                      & ~(LAYER_P1_NOTEBOOK >= 0 ? (1 << LAYER_P1_NOTEBOOK) : 0)
                                      & ~(1 << LAYER_P2_HAND)
                                      & ~(uiLayer >= 0 ? (1 << uiLayer) : 0);
                }
                else
                {
                    if (p1DeadFp)
                    {
                        SetP1BodyRenderEnabled(false);
                        _p1BodyHiddenForP1Camera = true;
                    }
                    cam.cullingMask = (p1CamOriginalMask | RemotePlayerMaskBit())
                                      & ~(1 << LAYER_P1_HAND)
                                      & ~(1 << LAYER_P1_TOOL)
                                      & ~(1 << LAYER_P1_BODY)
                                      & ~(LAYER_P1_NOTEBOOK >= 0 ? (1 << LAYER_P1_NOTEBOOK) : 0)
                                      & ~(1 << LAYER_P2_HAND);
                }
                return;
            }

            if (cam == player2.Camera)
            {
                SetupSplitPostFxProfiles();
                bool p2Downed = P2IsDownedOrCarried;
                bool p2Sleeping = player2.BedComponent != null && player2.BedComponent.Sleeping;
                bool p2BedRestoring = SplitScreenDeathFlow.P2PostBedExitRestoring;
                if (p2Sleeping || p2BedRestoring)
                    RestoreP2WorldVisualState();
                if (p2Downed) EnforceP2DownedWorldVisual();
                SetP2BodyVisible(!p2FirstPerson);   
                
                
                if (p2Sleeping) SetP2ToolLayer(-1);
                else if (!p2Downed && p2FirstPerson) SetP2ToolLayer(LAYER_P2_HAND);

                if (p2CamOriginalMask == -1) p2CamOriginalMask = cam.cullingMask;

                BeginP1RemoteVisualForP2View();
                if (p1LocalPlayerLayer >= 0)
                {
                    SetP1BodyRenderLayer(p1LocalPlayerLayer);
                    SetP1FishingLayer(p1LocalPlayerLayer);
                }
                BeginP1EquipmentForP2View();
                if (player1ThirdPerson != null && !player1ThirdPerson.ThirdPersonState) FirstPersonVisualRig.BeginCarriedAnimalTpForOtherView(player1);   // 对方视角搬运动物按TP摆
                if (P1OnZipline && player1.ZiplinePlayer != null)
                    ZiplineRenderFix.SyncToolTransforms(player1.ZiplinePlayer, p1LocalPlayerLayer);
                if (ShouldHideP1CarriedBodyForP2Fp())
                {
                    SetP1BodyRenderEnabled(false);
                    _p1BodyHiddenForP2CarryFp = true;
                }

                
                bool p1ThirdPersonForP2 = player1ThirdPerson != null && player1ThirdPerson.ThirdPersonState;
                bool p1NbOpenForP2 = IsAnyNotebookDisplayed();
                int p2ToolBit = (p1HandCameraLayer >= 0 && !p1NbOpenForP2) ? (1 << p1HandCameraLayer) : 0;
                int p1TpHandBit = p1ThirdPersonForP2 ? (1 << LAYER_P1_BODY) : 0;
                cam.cullingMask = (p2CamOriginalMask | p2ToolBit | p1TpHandBit | RemotePlayerMaskBit())
                                  & ~(1 << LAYER_P1_HAND)
                                  & ~(LAYER_P1_NOTEBOOK >= 0 ? (1 << LAYER_P1_NOTEBOOK) : 0)
                                  & ~(1 << LAYER_P2_HAND);
                if (!p1ThirdPersonForP2)
                    cam.cullingMask &= ~(1 << LAYER_P1_BODY);
                if (p1LocalPlayerLayer >= 0)
                    cam.cullingMask |= (1 << p1LocalPlayerLayer);

                if (player1.playerPivot != null && !(player1.BedComponent != null && player1.BedComponent.Sleeping))
                {
                    var e = player1.playerPivot.localEulerAngles;
                    p1PivotXSaved = e.x;
                    float bodyYaw = e.y;
                    
                    
                    
                    if (P1Attached && !P1OnZipline)
                    {
                        p1SeatPivotYSaved = e.y;
                        p1SeatPivoted = true;
                        bodyYaw = -player1.transform.localEulerAngles.y;
                    }
                    else if (P1OnZipline)
                    {
                        p1SeatPivotYSaved = e.y;
                        p1SeatPivoted = true;
                        bodyYaw = e.y;
                    }
                    player1.playerPivot.localEulerAngles = new Vector3(0f, bodyYaw, e.z);
                }
                if (P1OnZipline)
                    ZiplineRenderFix.BeginRemoteVisual(player1, p1LocalPlayerLayer, out _p1RemoteZiplineVisual);
            }
        }

        static int RemotePlayerMaskBit()
        {
            int layer = LayerMask.NameToLayer("RemotePlayer");
            return layer >= 0 ? (1 << layer) : 0;
        }

        static int RemotePlayerLayer()
        {
            return LayerMask.NameToLayer("RemotePlayer");
        }

        static void EnforceP1HandCameraVisible()
        {
            if (player1 == null || player1.HandCamera == null) return;
            var handCam = player1.HandCamera;
            if (!handCam.gameObject.activeSelf)
                handCam.gameObject.SetActive(true);
            ApplySplitViewport(handCam, false);
            handCam.depth = player1.Camera != null ? player1.Camera.depth + 1f : 1f;
            handCam.clearFlags = CameraClearFlags.Depth;
            if (handCam.targetTexture != null)
                handCam.targetTexture = null;

            bool p1Sleeping = player1.BedComponent != null && player1.BedComponent.Sleeping;
            bool p1Dead = player1.PlayerScript != null && player1.PlayerScript.IsDead;
            bool p1InNormalFp = player1ThirdPerson == null || !player1ThirdPerson.ThirdPersonState;
            if (!p1Sleeping && !p1Dead && p1InNormalFp && !handCam.enabled)
                handCam.enabled = true;
        }

        static void SetP1BodyRenderLayer(int layer)
        {
            if (layer < 0) return;
            for (int i = 0; i < p1CachedBodyRenderers.Count; i++)
            {
                var r = p1CachedBodyRenderers[i];
                if (r != null) r.gameObject.layer = layer;
            }
        }

        static bool _p1BodyHiddenForP1Camera;
        static void SetP1BodyRenderEnabled(bool enabled)
        {
            for (int i = 0; i < p1CachedBodyRenderers.Count; i++)
            {
                var r = p1CachedBodyRenderers[i];
                if (r != null) r.enabled = enabled;
            }
        }

        
        static bool P1Attached =>
            player1 != null && player1.PlayerNetworkManager != null && player1.PlayerNetworkManager.IsAttached;
        static bool P1OnZipline =>
            player1 != null && player1.ZiplinePlayer != null && player1.ZiplinePlayer.IsAttachedToZipline;
        static float p1SeatPivotYSaved;
        static bool  p1SeatPivoted;
        static bool  _p1SeatLookLocked;   
        static ZiplineRenderFix.RemoteVisualState _p1RemoteZiplineVisual;
        static ZiplineRenderFix.RemoteVisualState _p2RemoteZiplineVisual;
        static MethodInfo _p1ThirdPersonHandleMethod;

        static void RecalculateP1FreeLookSeatTpCameraBeforeRender()
        {
            if (player1 == null || player1.Camera == null || player1ThirdPerson == null) return;
            if (!player1ThirdPerson.ThirdPersonState || !player1ThirdPerson.ThirdPersonModel) return;
            if (!P1Attached || P1OnZipline) return;

            var attach = FindFreeLookAttachForPlayer(player1);
            if (attach == null) return;

            Patch_AttachPlayer_Update_P1SeatPose.ForceFreeLookSeatVisibleBodyParent(attach, player1);

            if (_p1ThirdPersonHandleMethod == null)
                _p1ThirdPersonHandleMethod = typeof(ThirdPerson).GetMethod("HandleThirdPerson", BindingFlags.Instance | BindingFlags.NonPublic);

            try
            {
                _p1ThirdPersonHandleMethod?.Invoke(player1ThirdPerson, null);
            }
            catch (System.Exception e)
            {
                ModEntry.Logger.Log("[P1SeatTP] camera before render ex: " + e.Message);
            }
        }

        
        static void OnCameraPostRender(Camera cam)
        {
            if (player1 == null || player2 == null) return;

            if (cam == player1.HandCamera)
            {
                return;
            }

            if (cam == player1.Camera)
            {
                ZiplineRenderFix.EndRemoteVisual(ref _p2RemoteZiplineVisual);
                if (_p2BodyHiddenForP1CarryFp)
                {
                    SetP2BodyVisible(true);
                    _p2BodyHiddenForP1CarryFp = false;
                }
                RestoreP2FirstPersonArmsAfterP1WorldView();
                RestoreP2RemoteVisualForP1View();
                FirstPersonVisualRig.RestoreCarriedAnimalTpForOtherView();
                if (p2FirstPerson) RestoreP2SpineForP1View();   
                if (p1LocalPlayerLayer >= 0)
                {
                    bool p1IsTP = player1ThirdPerson != null && player1ThirdPerson.ThirdPersonState;
                    if (p1IsTP || SplitScreenDeathFlow.P1DownedThirdPerson || _p1BodyHiddenForP1Camera)
                        SetP1BodyRenderLayer(LAYER_P1_BODY);
                }
                if (_p1BodyHiddenForP1Camera)
                {
                    SetP1BodyRenderEnabled(true);
                    _p1BodyHiddenForP1Camera = false;
                }
                return;
            }

            if (cam != player2.Camera) return;

            ZiplineRenderFix.EndRemoteVisual(ref _p1RemoteZiplineVisual);
            if (_p1BodyHiddenForP2CarryFp)
            {
                SetP1BodyRenderEnabled(true);
                _p1BodyHiddenForP2CarryFp = false;
            }

            if (P2IsDownedOrCarried || SplitScreenDeathFlow.P2PostBedExitRestoring)
                SetP2BodyVisible(true);

            if (p1LocalPlayerLayer >= 0)
            {
                SetP1BodyRenderLayer(LAYER_P1_BODY);
                SetP1FishingLayer(LAYER_P1_HAND);   
            }
            RestoreP1EquipmentForP2View();
            FirstPersonVisualRig.RestoreCarriedAnimalTpForOtherView();
            RestoreP1RemoteVisualForP2View();

            if (player1.playerPivot != null && !(player1.BedComponent != null && player1.BedComponent.Sleeping))
            {
                var e = player1.playerPivot.localEulerAngles;
                
                float restY = p1SeatPivoted ? p1SeatPivotYSaved : e.y;
                p1SeatPivoted = false;
                player1.playerPivot.localEulerAngles = new Vector3(p1PivotXSaved, restY, e.z);
            }
        }

        
        
        
        
        
        
        
        
        static PostProcessingProfile _survivalFx;     
        static PostProcessingProfile _p1Profile;      
        static PostProcessingProfile _p2Profile;      
        
        static MethodInfo _miHandleUIFeedback;
        static FieldInfo _fiPostEffects;

        static PostProcessingProfile GetSurvivalFx()
        {
            if (_survivalFx == null)
            {
                var s = ComponentManager<Settings>.Value;
                if (s != null && s.graphicsBox != null) _survivalFx = s.graphicsBox.postEffects;
            }
            return _survivalFx;
        }

        static void SetupSplitPostFxProfiles()
        {
            var fx = GetSurvivalFx();
            if (fx == null) return;

            if (_p1Profile == null)
                _p1Profile = Object.Instantiate(fx);
            if (_p2Profile == null)
            {
                _p2Profile = Object.Instantiate(fx);
                ResetSurvivalFeedback(_p2Profile);
            }

            SetCameraProfile(player1 != null ? player1.Camera : null, _p1Profile);
            SetCameraProfile(player2 != null ? player2.Camera : null, _p2Profile);
            SetStatsProfile(player1 != null ? player1.Stats : null, _p1Profile);
            SetStatsProfile(player2 != null ? player2.Stats : null, _p2Profile);
        }

        internal static void ResetSplitPostFxProfiles()
        {
            var fx = GetSurvivalFx();
            SetCameraProfile(player1 != null ? player1.Camera : null, fx);
            SetCameraProfile(player2 != null ? player2.Camera : null, fx);
            SetStatsProfile(player1 != null ? player1.Stats : null, fx);
            SetStatsProfile(player2 != null ? player2.Stats : null, fx);

            if (_p1Profile != null) Object.Destroy(_p1Profile);
            if (_p2Profile != null) Object.Destroy(_p2Profile);
            _p1Profile = null;
            _p2Profile = null;
            _survivalFx = null;
        }

        
        static void SetupP2PostFxProfile()
        {
            if (player2 == null) return;
            SetupSplitPostFxProfiles();

            
            
            SetCameraProfile(player2.Camera, _p2Profile);

            if (_p2Stats == null) _p2Stats = player2.Stats;
            SetStatsProfile(_p2Stats, _p2Profile);
        }

        
        static bool SetCameraProfile(Camera cam, PostProcessingProfile p)
        {
            if (cam == null) return false;
            var b = cam.GetComponent<PostProcessingBehaviour>();
            if (b == null) return false;
            if (b.profile == p) return true;
            b.profile = p;
            return true;
        }

        static void SetStatsProfile(PlayerStats stats, PostProcessingProfile p)
        {
            if (stats == null) return;
            if (_fiPostEffects == null)
                _fiPostEffects = typeof(PlayerStats).GetField("postEffects", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_fiPostEffects == null) return;
            var current = _fiPostEffects.GetValue(stats) as PostProcessingProfile;
            if (!ReferenceEquals(current, p))
                _fiPostEffects.SetValue(stats, p);
        }

        static void ResetSurvivalFeedback(PostProcessingProfile p)
        {
            if (p == null) return;
            if (p.vignette != null) p.vignette.enabled = false;
            if (p.chromaticAberration != null) p.chromaticAberration.enabled = false;
            if (p.colorGrading != null)
            {
                var cg = p.colorGrading.settings;
                cg.basic.saturation = 1f;
                p.colorGrading.settings = cg;
            }
        }

        
        internal static void DriveP2SurvivalFx(PlayerStats p2Stats)
        {
            if (p2Stats == null) return;
            _p2Stats = p2Stats;
            SetupP2PostFxProfile();   
            if (_miHandleUIFeedback == null)
                _miHandleUIFeedback = typeof(PlayerStats).GetMethod("HandleUIFeedback", BindingFlags.Instance | BindingFlags.NonPublic);
            try { _miHandleUIFeedback?.Invoke(p2Stats, null); }
            catch (System.Exception e) { LogV("[P2PostFx] HandleUIFeedback ignored: " + e.Message); }
        }

    }
}
