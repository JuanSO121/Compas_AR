// File: UserPositionBridge.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Navigation/Voice/
//
// ============================================================================
//  USER POSITION BRIDGE — IndoorNavAR
// ============================================================================
//
//  PROPÓSITO:
//    Expone la posición correcta del usuario en el espacio del modelo 3D
//    según el modo activo (FullAR / No-AR), para que NavigationVoiceGuide
//    y cualquier otro sistema consuman la posición sin saber en qué modo están.
//
//  LÓGICA POR MODO (derivada de AROriginAligner):
//
//  FullAR (AROriginAligner.IsNoArMode == false):
//    ARCore trackea la posición física del usuario. XROrigin.Camera.transform
//    se mueve con el usuario real en el espacio del modelo 3D alineado.
//    → UserPosition = XROrigin.Camera.transform.position
//    → UserForward  = XROrigin.Camera.transform.forward (XZ aplanado)
//
//  No-AR (AROriginAligner.IsNoArMode == true):
//    AROriginAligner.FollowAgent() mueve el XROrigin para que siga al NPC.
//    La cámara tiene la posición del NPC — no hay usuario físico real.
//    → UserPosition = XROrigin.Camera.transform.position (= posición del NPC)
//    → Resultado: el VoiceGuide se comporta igual que con el NPC, correcto.
//
//  DETECCIÓN DEL MODO:
//    Lee AROriginAligner.IsNoArMode (propiedad pública). No necesita ARSessionManager.
//    Requiere añadir en AROriginAligner: public bool IsNoArMode => _noArMode;
//
//  MÉTRICAS EXPUESTAS:
//    UserPosition  → posición en el espacio del modelo 3D
//    UserForward   → dirección de la cámara aplanada en XZ
//    UserSpeed     → velocidad de movimiento suavizada (m/s)
//    IsNoArMode    → true si el dispositivo no tiene ARCore

using UnityEngine;
using Unity.XR.CoreUtils;
using IndoorNavAR.AR;

namespace IndoorNavAR.Navigation.Voice
{
    public sealed class UserPositionBridge : MonoBehaviour
    {
        public static UserPositionBridge Instance { get; private set; }

        [Header("─── Referencias ─────────────────────────────────────────────")]
        [Tooltip("XROrigin de la escena (XR Origin Mobile AR). Auto-detectado.")]
        [SerializeField] private XROrigin         _xrOrigin;

        [Tooltip("AROriginAligner para saber si estamos en modo No-AR. Auto-detectado.")]
        [SerializeField] private AROriginAligner  _arOriginAligner;

        // ── Posición y orientación del usuario ────────────────────────────────

        /// <summary>
        /// Posición del usuario en el espacio del modelo 3D.
        /// FullAR: posición física real (ARCore tracking).
        /// No-AR:  posición del NPC (XROrigin sigue al agente).
        /// </summary>
        public Vector3 UserPosition { get; private set; }

        /// <summary>Dirección de la cámara en XZ, normalizada.</summary>
        public Vector3 UserForward  { get; private set; } = Vector3.forward;

        /// <summary>
        /// Velocidad de movimiento suavizada (m/s).
        /// Útil para distinguir quieto vs caminando en los escenarios de accesibilidad.
        /// </summary>
        public float UserSpeed { get; private set; }

        /// <summary>True si el dispositivo no tiene ARCore (modo No-AR activo).</summary>
        public bool IsNoArMode => _arOriginAligner != null && _arOriginAligner.IsNoArMode;

        // ── Privado ────────────────────────────────────────────────────────────
        private Vector3 _prevPosition;
        private float   _speedSmoothVelocity;

        // ─────────────────────────────────────────────────────────────────────
        //  LIFECYCLE
        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            if (_xrOrigin == null)
                _xrOrigin = FindFirstObjectByType<XROrigin>();

            if (_arOriginAligner == null)
                _arOriginAligner = FindFirstObjectByType<AROriginAligner>();

            if (_xrOrigin == null)
                Debug.LogWarning("[UserPositionBridge] ⚠️ XROrigin no encontrado.");

            if (_arOriginAligner == null)
                Debug.LogWarning("[UserPositionBridge] ⚠️ AROriginAligner no encontrado. " +
                                 "IsNoArMode siempre será false (FullAR asumido).");

            _prevPosition = GetCameraPosition();
            UserPosition  = _prevPosition;
        }

        private void Update()
        {
            Vector3 pos = GetCameraPosition();
            Vector3 fwd = GetCameraForward();

            // Velocidad suavizada para detectar si el usuario está quieto o caminando
            float rawSpeed = Vector3.Distance(pos, _prevPosition) / Mathf.Max(Time.deltaTime, 0.0001f);
            UserSpeed     = Mathf.SmoothDamp(UserSpeed, rawSpeed, ref _speedSmoothVelocity, 0.12f);

            UserPosition  = pos;
            UserForward   = fwd;
            _prevPosition = pos;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS PRIVADOS
        // ─────────────────────────────────────────────────────────────────────

        private Vector3 GetCameraPosition()
        {
            if (_xrOrigin != null && _xrOrigin.Camera != null)
                return _xrOrigin.Camera.transform.position;

            // Fallback: Camera.main (no debería llegar aquí si la escena está bien)
            return Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        }

        private Vector3 GetCameraForward()
        {
            Transform camT = _xrOrigin?.Camera?.transform ?? Camera.main?.transform;
            if (camT == null) return Vector3.forward;

            Vector3 f = camT.forward;
            f.y = 0f;
            return f.sqrMagnitude > 0.001f ? f.normalized : Vector3.forward;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  GIZMOS
        // ─────────────────────────────────────────────────────────────────────

        #if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying) return;
            Gizmos.color = IsNoArMode ? Color.yellow : Color.cyan;
            Gizmos.DrawWireSphere(UserPosition, 0.25f);
            Gizmos.color = Color.blue;
            Gizmos.DrawLine(UserPosition, UserPosition + UserForward * 0.7f);
        }
        #endif

        // ─────────────────────────────────────────────────────────────────────
        //  DEBUG
        // ─────────────────────────────────────────────────────────────────────

        [ContextMenu("ℹ️ Estado")]
        private void DebugInfo()
        {
            Debug.Log($"[UserPositionBridge] Mode={( IsNoArMode ? "NoAR" : "FullAR")}\n" +
                      $"UserPosition={UserPosition:F2} | UserForward={UserForward:F2}\n" +
                      $"UserSpeed={UserSpeed:F2}m/s\n" +
                      $"XROrigin='{_xrOrigin?.gameObject.name ?? "NULL"}'\n" +
                      $"AROriginAligner='{_arOriginAligner?.gameObject.name ?? "NULL"}'");
        }
    }
}