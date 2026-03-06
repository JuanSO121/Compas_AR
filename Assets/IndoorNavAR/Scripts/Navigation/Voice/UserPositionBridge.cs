// File: UserPositionBridge.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Navigation/Voice/
// v5 — Fix modo FullAR: expone AgentPosition para triggers de navegación
//
// ══════════════════════════════════════════════════════════════════════════════
// BUG CORREGIDO v4 → v5
// ══════════════════════════════════════════════════════════════════════════════
//
// SÍNTOMA:
//   En modo FullAR los triggers de instrucciones (giros, escaleras) nunca se
//   disparaban aunque el agente NPC avanzara correctamente por la ruta.
//
// CAUSA:
//   NavigationVoiceGuide.EvaluateInstructions() usaba UserPos (posición de la
//   cámara XR = usuario real) para evaluar distancias a los waypoints de la ruta.
//   En FullAR el NPC es quien recorre el NavMesh. Si el usuario real no está en
//   el mismo punto que el NPC, los triggers basados en distancia a waypoints
//   nunca se cumplían. Resultado: instrucciones mudas durante toda la navegación.
//
// FIX:
//   • Nueva propiedad pública AgentPosition que devuelve la posición del
//     _virtualAgentTransform independientemente del modo (FullAR o NoAR).
//   • NavigationVoiceGuide.EvalPos usa esta propiedad en modo FullAR para
//     evaluar los triggers de instrucciones de ruta.
//   • UserPosition sigue siendo la posición de la cámara XR en FullAR
//     (comportamiento correcto v4), usada para:
//       - Detección de llegada al destino (el usuario real debe llegar)
//       - Detección de parada (el usuario real se detiene)
//       - Velocidad del usuario (UserSpeed)
//   • En NoAR AgentPosition == UserPosition (sin cambio de comportamiento).
//
// RESUMEN DE FUENTES POR PROPIEDAD:
//   ┌──────────────────┬─────────────────────┬──────────────────────┐
//   │ Propiedad        │ FullAR              │ NoAR                 │
//   ├──────────────────┼─────────────────────┼──────────────────────┤
//   │ UserPosition     │ Cámara XR (usuario) │ Agente NPC           │
//   │ UserForward      │ Cámara XR (usuario) │ Agente NPC           │
//   │ UserSpeed        │ Cámara XR (usuario) │ Agente NPC           │
//   │ AgentPosition    │ Agente NPC          │ Agente NPC           │
//   └──────────────────┴─────────────────────┴──────────────────────┘
//
// ══════════════════════════════════════════════════════════════════════════════
// BUG CORREGIDO v3 → v4 (conservado íntegramente)
// ══════════════════════════════════════════════════════════════════════════════
//
// SÍNTOMA: En NoAR, UserPosition era la cámara estática → triggers rotos.
// FIX: En NoAR, GetActivePosition() devuelve _virtualAgentTransform.position.

using UnityEngine;
using Unity.XR.CoreUtils;
using IndoorNavAR.AR;
using IndoorNavAR.Agent;

namespace IndoorNavAR.Navigation.Voice
{
    public sealed class UserPositionBridge : MonoBehaviour
    {
        public static UserPositionBridge Instance { get; private set; }

        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Referencias AR")]
        [SerializeField] private XROrigin        _xrOrigin;
        [SerializeField] private AROriginAligner _arOriginAligner;

        [Header("Agente Virtual (NoAR y FullAR)")]
        [Tooltip("Transform del VirtualAssistant (agente NPC). " +
                 "En NoAR: UserPosition sigue este objeto. " +
                 "En FullAR: AgentPosition sigue este objeto (para triggers de ruta). " +
                 "Si se deja vacío, se busca automáticamente el componente ARGuideController.")]
        [SerializeField] private Transform _virtualAgentTransform;

        // ── Métricas públicas ─────────────────────────────────────────────────

        /// <summary>
        /// Posición del usuario real.
        /// FullAR: posición de la cámara XR (usuario físico con el dispositivo).
        /// NoAR:   posición del agente NPC (el agente representa al usuario).
        /// </summary>
        public Vector3 UserPosition { get; private set; }

        /// <summary>
        /// Orientación del usuario real (plana, sin componente Y).
        /// FullAR: forward de la cámara XR.
        /// NoAR:   forward del agente NPC.
        /// </summary>
        public Vector3 UserForward  { get; private set; } = Vector3.forward;

        /// <summary>
        /// Velocidad de movimiento del usuario real (m/s, suavizada).
        /// </summary>
        public float   UserSpeed    { get; private set; }

        /// <summary>
        /// ✅ v5 — Posición del agente NPC, independientemente del modo.
        ///
        /// Usada por NavigationVoiceGuide.EvalPos en FullAR para evaluar
        /// los triggers de instrucciones de ruta (giros, escaleras).
        ///
        /// En FullAR: posición del _virtualAgentTransform (NPC que recorre el NavMesh).
        /// En NoAR:   igual que UserPosition (el NPC ES el usuario en NoAR).
        ///
        /// Si _virtualAgentTransform no está disponible, devuelve UserPosition
        /// como fallback seguro.
        /// </summary>
        public Vector3 AgentPosition => _virtualAgentTransform != null
            ? _virtualAgentTransform.position
            : UserPosition;

        public bool IsNoArMode => _arOriginAligner != null && _arOriginAligner.IsNoArMode;

        // ── Privado ────────────────────────────────────────────────────────────

        private Vector3   _prevPosition;
        private Transform _cameraTransform;
        private const float SpeedLerpFactor = 0.18f;

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

            // Cache del transform de cámara XR (usado en FullAR para UserPosition)
            _cameraTransform = _xrOrigin?.Camera?.transform ?? Camera.main?.transform;

            if (_cameraTransform == null)
                Debug.LogWarning("[UserPositionBridge] ⚠️ Camera transform no encontrado.");

            if (_arOriginAligner == null)
                Debug.LogWarning("[UserPositionBridge] ⚠️ AROriginAligner no encontrado.");

            // Buscar el agente virtual automáticamente si no fue asignado en Inspector.
            // ✅ FindObjectsInactive.Include: el VirtualAssistant puede estar inactivo
            // al inicio si el NavMesh aún no está listo o si vive dentro del XR Origin.
            if (_virtualAgentTransform == null)
            {
                var guide = FindFirstObjectByType<ARGuideController>(FindObjectsInactive.Include);
                if (guide != null)
                {
                    _virtualAgentTransform = guide.transform;
                    Debug.Log($"[UserPositionBridge] ✅ Agente virtual encontrado: '{guide.gameObject.name}'");
                }
                else
                {
                    Debug.LogWarning("[UserPositionBridge] ⚠️ ARGuideController no encontrado " +
                                     "(ni activo ni inactivo). AgentPosition usará UserPosition como fallback.");
                }
            }

            _prevPosition = GetActivePosition();
            UserPosition  = _prevPosition;

            string mode   = IsNoArMode ? "NoAR" : "FullAR";
            string srcUser = IsNoArMode && _virtualAgentTransform != null
                ? $"agente '{_virtualAgentTransform.gameObject.name}'"
                : $"camara '{_cameraTransform?.gameObject.name ?? "NULL"}'";
            string srcAgent = _virtualAgentTransform != null
                ? $"agente '{_virtualAgentTransform.gameObject.name}'"
                : "fallback (UserPosition)";

            Debug.Log($"[UserPositionBridge] ✅ Iniciado." +
                      $"\n  Modo:           {mode}" +
                      $"\n  UserPosition ←  {srcUser}" +
                      $"\n  AgentPosition ← {srcAgent}");
        }

        private void Update()
        {
            Vector3 pos = GetActivePosition();

            float rawSpeed = Vector3.Distance(pos, _prevPosition) / (Time.deltaTime + 0.0001f);
            UserSpeed      = Mathf.Lerp(UserSpeed, rawSpeed, SpeedLerpFactor);

            UserPosition  = pos;
            UserForward   = GetActiveForward();
            _prevPosition = pos;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS PRIVADOS
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Posición del "usuario" para métricas de movimiento (parada, velocidad, llegada).
        ///
        /// FullAR: cámara XR (usuario real con el dispositivo en mano).
        ///         En FullAR el usuario se mueve físicamente → la cámara se mueve.
        ///
        /// NoAR:   agente NPC (sin cámara XR real, el agente representa al usuario).
        ///
        /// NOTA: Esta función NO se usa para los triggers de instrucciones de ruta.
        ///       Para eso, NavigationVoiceGuide usa AgentPosition (propiedad pública).
        /// </summary>
        private Vector3 GetActivePosition()
        {
            if (IsNoArMode && _virtualAgentTransform != null)
                return _virtualAgentTransform.position;

            return _cameraTransform != null ? _cameraTransform.position : Vector3.zero;
        }

        /// <summary>
        /// Orientación del "usuario" para instrucciones de giro recalculadas.
        ///
        /// FullAR: forward de la cámara XR (dirección a la que mira el usuario real).
        /// NoAR:   forward del agente NPC.
        /// </summary>
        private Vector3 GetActiveForward()
        {
            Transform src = (IsNoArMode && _virtualAgentTransform != null)
                ? _virtualAgentTransform
                : _cameraTransform;

            if (src == null) return Vector3.forward;
            Vector3 f = src.forward;
            f.y = 0f;
            return f.sqrMagnitude > 0.001f ? f.normalized : Vector3.forward;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  GIZMOS — solo en Editor
        // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying) return;

            // UserPosition: azul/amarillo según modo
            Gizmos.color = IsNoArMode ? Color.yellow : Color.cyan;
            Gizmos.DrawWireSphere(UserPosition, 0.25f);
            Gizmos.color = IsNoArMode ? new Color(1f, 0.8f, 0f) : Color.blue;
            Gizmos.DrawLine(UserPosition, UserPosition + UserForward * 0.7f);

            // AgentPosition: verde si difiere de UserPosition (FullAR con NPC separado)
            Vector3 agentPos = AgentPosition;
            if (Vector3.Distance(agentPos, UserPosition) > 0.1f)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireSphere(agentPos, 0.2f);
                Gizmos.DrawLine(UserPosition, agentPos);
            }
        }
#endif

        // ─────────────────────────────────────────────────────────────────────
        //  DEBUG
        // ─────────────────────────────────────────────────────────────────────

        [ContextMenu("Info Estado")]
        private void DebugInfo()
        {
            string srcUser = IsNoArMode && _virtualAgentTransform != null
                ? $"Agente '{_virtualAgentTransform.gameObject.name}'"
                : $"Camara '{_cameraTransform?.gameObject.name ?? "NULL"}'";

            string srcAgent = _virtualAgentTransform != null
                ? $"Agente '{_virtualAgentTransform.gameObject.name}'"
                : "Fallback (UserPosition)";

            Debug.Log($"[UserPositionBridge] ==================\n" +
                      $"  Modo:            {(IsNoArMode ? "NoAR" : "FullAR")}\n" +
                      $"  UserPosition ←   {srcUser}\n" +
                      $"  AgentPosition ←  {srcAgent}\n" +
                      $"  UserPosition:    {UserPosition:F2}\n" +
                      $"  AgentPosition:   {AgentPosition:F2}\n" +
                      $"  UserForward:     {UserForward:F2}\n" +
                      $"  UserSpeed:       {UserSpeed:F2} m/s\n" +
                      $"  Separación NPC:  {Vector3.Distance(UserPosition, AgentPosition):F2}m\n" +
                      $"==========================================");
        }
    }
}