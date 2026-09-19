using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SplitScreen
{
    public static partial class Main
    {
        
        
        //
        
        
        
        
        
        
        
        //
        
        
        
        
        
        const int PH_OFF = 0, PH_IN_UP = 1, PH_IN_DOWN = 2, PH_ON = 3, PH_OUT_UP = 4, PH_OUT_DOWN = 5;
        const float ZoomStep = 9f;        

        static Binoculars   _p2Binoc;       
        static MeshRenderer _binocModel;    
        static GameObject   _p2BinocOverlay;
        static CanvasGroup  _p2BinocFade;   
        static int   _binocPhase;           
        static bool  _binocHidesArms;       
        static bool  _p2BinocPrevFP;        
        static float _binocFadeAlpha;
        static float _binocEnterFov;        
        static float _binocActiveFov, _binocTargetFov, _binocMinFov, _binocMaxFov, _binocZoomSpeed, _binocFadeSpeed = 1f;

        
        static bool _p1BinocActive, _p1BinocWasTP;
        internal static bool P1BinocActive => _p1BinocActive;

        
        internal static bool P2BinocActive    => _binocPhase != PH_OFF;
        
        internal static bool P2BinocHidesArms => _binocHidesArms;

        
        static FieldInfo _fBinActiveFOV, _fBinMinFOV, _fBinZoomSpeed, _fBinFadeSpeed, _fBinModel, _fBinActive, _fBinocActiveObj;

        internal static void ResetP2Binoc()
        {
            ForceOffImmediate();
            _p2Binoc = null; _binocModel = null;
            _p2BinocOverlay = null; _p2BinocFade = null;   
            _p1BinocActive = false; _p1BinocWasTP = false;
        }

        
        internal static void TickP2Binoculars()
        {
            if (player2 == null || _p2HudCanvas == null) { if (_binocPhase != PH_OFF) ForceOffImmediate(); _p2Binoc = null; return; }

            
            bool blocked = IsP2BackpackOpen || IsP2MenuOpen || IsP2BuildMenuOpen
                           || P2IsSeated || P2ZiplineDriver.IsAttached
                           || (player2.BedComponent != null && player2.BedComponent.Sleeping);

            var bino = blocked ? null : ResolveP2Binoculars();
            if (bino == null) { if (_binocPhase != PH_OFF) ForceOffImmediate(); _p2Binoc = null; return; }
            _p2Binoc = bino;

            bool want = p2ActionFire != null && p2ActionFire.IsPressed();   
            float dt = Time.deltaTime;
            float sp = Mathf.Max(_binocFadeSpeed, 0.5f);

            switch (_binocPhase)
            {
                case PH_OFF:
                    if (want) { EnterBegin(bino); _binocPhase = PH_IN_UP; }
                    break;
                case PH_IN_UP:                                   
                    _binocFadeAlpha = Mathf.MoveTowards(_binocFadeAlpha, 1f, dt * sp); SetFade(_binocFadeAlpha);
                    if (_binocFadeAlpha >= 1f) { ApplyBinocVisual(true); _binocPhase = PH_IN_DOWN; }
                    break;
                case PH_IN_DOWN:                                 
                    _binocFadeAlpha = Mathf.MoveTowards(_binocFadeAlpha, 0f, dt * sp); SetFade(_binocFadeAlpha);
                    if (_binocFadeAlpha <= 0f) _binocPhase = PH_ON;
                    break;
                case PH_ON:                                      
                    DriveP2BinocZoom();
                    if (!want) _binocPhase = PH_OUT_UP;
                    break;
                case PH_OUT_UP:                                  
                    _binocFadeAlpha = Mathf.MoveTowards(_binocFadeAlpha, 1f, dt * sp * 2f); SetFade(_binocFadeAlpha);
                    if (_binocFadeAlpha >= 1f) { ApplyBinocVisual(false); _binocPhase = PH_OUT_DOWN; }
                    break;
                case PH_OUT_DOWN:                                
                    _binocFadeAlpha = Mathf.MoveTowards(_binocFadeAlpha, 0f, dt * sp * 2f); SetFade(_binocFadeAlpha);
                    if (_binocFadeAlpha <= 0f) _binocPhase = PH_OFF;
                    break;
            }
        }

        
        static void ApplyBinocVisual(bool state)
        {
            if (state)
            {
                EnsureP2BinocOverlay();
                if (_p2BinocOverlay != null) _p2BinocOverlay.SetActive(true);
                _binocHidesArms = true;
                p2FirstPerson = true;          
                HideP2Arms(true);
                if (player2 != null && player2.Camera != null) player2.Camera.fieldOfView = _binocActiveFov;
            }
            else
            {
                if (_p2BinocOverlay != null) _p2BinocOverlay.SetActive(false);
                _binocHidesArms = false;
                HideP2Arms(false);   
                if (player2 != null && player2.Camera != null && _binocEnterFov > 0f)
                    player2.Camera.fieldOfView = _binocEnterFov;
                p2FirstPerson = _p2BinocPrevFP;   
            }
        }

        
        static void ForceOffImmediate()
        {
            if (_binocPhase != PH_OFF) ApplyBinocVisual(false);   
            _binocHidesArms = false;
            _binocFadeAlpha = 0f; SetFade(0f);
            _binocPhase = PH_OFF;
        }

        static void DriveP2BinocZoom()
        {
            if (player2 == null || player2.Camera == null) return;
            p2FirstPerson = true;   
            var gp = GetP2Gamepad();
            if (gp != null)
            {
                if (gp.dpad.up.wasPressedThisFrame)        _binocTargetFov -= ZoomStep;  
                else if (gp.dpad.down.wasPressedThisFrame) _binocTargetFov += ZoomStep;  
            }
            _binocTargetFov = Mathf.Clamp(_binocTargetFov, _binocMinFov, _binocMaxFov);
            _binocActiveFov = Mathf.Lerp(_binocActiveFov, _binocTargetFov, Time.deltaTime * Mathf.Max(_binocZoomSpeed, 1f));
            player2.Camera.fieldOfView = _binocActiveFov;
        }

        
        static void EnterBegin(Binoculars bino)
        {
            if (player2 == null || player2.Camera == null) return;
            EnsureBinocFields();
            EnsureP2BinocOverlay();
            _binocModel = _fBinModel?.GetValue(bino) as MeshRenderer;
            _p2BinocPrevFP = p2FirstPerson; p2FirstPerson = true;   
            _binocEnterFov  = player2.Camera.fieldOfView;
            float activeFov = _fBinActiveFOV != null ? (float)_fBinActiveFOV.GetValue(bino) : 30f;
            _binocMinFov    = _fBinMinFOV    != null ? (float)_fBinMinFOV.GetValue(bino)    : 10f;
            _binocZoomSpeed = _fBinZoomSpeed != null ? (float)_fBinZoomSpeed.GetValue(bino) : 5f;
            _binocFadeSpeed = _fBinFadeSpeed != null ? (float)_fBinFadeSpeed.GetValue(bino) : 1f;
            _binocMaxFov    = Mathf.Clamp(_binocEnterFov - 10f, _binocMinFov, _binocEnterFov);
            _binocActiveFov = Mathf.Clamp(activeFov, _binocMinFov, _binocMaxFov);
            _binocTargetFov = _binocActiveFov;
        }

        
        static void HideP2Arms(bool hide)
        {
            if (player2ArmMesh != null && player2ArmMesh.gameObject.activeSelf == hide)
                player2ArmMesh.gameObject.SetActive(!hide);
            if (_binocModel != null) _binocModel.enabled = !hide;
        }

        static Binoculars ResolveP2Binoculars() => ResolveHeldBinoculars(player2);

        
        static Binoculars ResolveHeldBinoculars(Network_Player np)
        {
            if (np == null) return null;
            var uic = np.GetComponentInChildren<UseItemController>(true);
            if (uic == null) return null;
            if (_fBinocActiveObj == null)
                _fBinocActiveObj = typeof(UseItemController).GetField("activeObject", BindingFlags.Instance | BindingFlags.NonPublic);
            var conn = _fBinocActiveObj?.GetValue(uic) as ItemConnection;
            var obj = conn?.obj;
            if (obj == null || !obj.activeInHierarchy) return null;
            return obj.GetComponent<Binoculars>() ?? obj.GetComponentInChildren<Binoculars>(true);
        }

        static void EnsureBinocFields()
        {
            if (_fBinActiveFOV != null) return;
            var t = typeof(Binoculars);
            var bf = BindingFlags.Instance | BindingFlags.NonPublic;
            _fBinActiveFOV = t.GetField("activeFOV", bf);
            _fBinMinFOV    = t.GetField("minFOV", bf);
            _fBinZoomSpeed = t.GetField("zoomSpeed", bf);
            _fBinFadeSpeed = t.GetField("fadeSpeed", bf);
            _fBinModel     = t.GetField("binocularModel", bf);
            _fBinActive    = t.GetField("active", bf);
        }

        
        
        
        
        internal static void EnforceP1Binoculars(bool p1IsTP)
        {
            if (player1 == null) return;
            var b = ResolveHeldBinoculars(player1);
            bool active = b != null && ReadBinocActive(b);
            if (active && !_p1BinocActive)
            {
                _p1BinocWasTP = p1IsTP;
                if (p1IsTP) ForceP1ThirdPerson(false);   
            }
            else if (!active && _p1BinocActive)
            {
                if (_p1BinocWasTP) ForceP1ThirdPerson(true);   
                _p1BinocWasTP = false;
            }
            _p1BinocActive = active;
        }

        static bool ReadBinocActive(Binoculars b)
        {
            EnsureBinocFields();
            return _fBinActive != null && b != null && (bool)_fBinActive.GetValue(b);
        }

        static void ForceP1ThirdPerson(bool toTP)
        {
            var tp = player1 != null ? player1.GetComponentInChildren<ThirdPerson>() : null;
            if (tp != null) tp.ForceThirdPersonState(toTP);   
        }

        
        static void EnsureP2BinocOverlay()
        {
            if (_p2BinocOverlay != null || _p2HudCanvas == null) return;
            var ch = ComponentManager<CanvasHelper>.Value;
            if (ch == null || ch.binocularImage == null) return;

            _p2BinocOverlay = Object.Instantiate(ch.binocularImage, _p2HudCanvas.transform);
            _p2BinocOverlay.name = "P2_BinocularOverlay";
            FillParent(_p2BinocOverlay.GetComponent<RectTransform>());
            var cv = _p2BinocOverlay.GetComponent<Canvas>() ?? _p2BinocOverlay.AddComponent<Canvas>();
            cv.overrideSorting = true; cv.sortingOrder = 25000;   
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) SetLayerRecursively(_p2BinocOverlay.transform, uiLayer);
            _p2BinocOverlay.SetActive(false);
        }

        
        static void EnsureP2BinocFade()
        {
            if (_p2BinocFade != null || _p2HudCanvas == null) return;
            var go = new GameObject("P2_BinocularFade", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
            FillParent(go.GetComponent<RectTransform>());
            var img = go.GetComponent<Image>(); img.color = Color.black; img.raycastTarget = false;
            var cv = go.AddComponent<Canvas>(); cv.overrideSorting = true; cv.sortingOrder = 29000;  
            _p2BinocFade = go.GetComponent<CanvasGroup>(); _p2BinocFade.alpha = 0f; _p2BinocFade.blocksRaycasts = false;
            int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) go.layer = uiLayer;
        }

        static void SetFade(float a)
        {
            if (a > 0f) EnsureP2BinocFade();
            if (_p2BinocFade != null) _p2BinocFade.alpha = a;
        }

        static void FillParent(RectTransform rt)
        {
            if (rt == null) return;
            if (rt.parent == null && _p2HudCanvas != null) rt.SetParent(_p2HudCanvas.transform, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        static Gamepad GetP2Gamepad()
        {
            return GetP2BoundGamepad();
        }
    }
}
