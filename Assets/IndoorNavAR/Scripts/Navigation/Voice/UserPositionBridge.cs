// File: UserPositionBridge.cs
// ✅ v3 — Agrega UserForward e IsNoArMode (requeridos por NavigationVoiceGuide v4.5)
//
// ============================================================================
//  CAMBIOS v2 → v3
// ============================================================================
//
//  BUG — Error de compilación en NavigationVoiceGuide.cs:
//
//    private Vector3 UserFwd => _userBridge != null
//        ? _userBridge.UserForward   // ← NO existía en v2
//        : FlatFwd(...);
//
//    private bool IsFullARMode => _userBridge != null && !_userBridge.IsNoArMode;
//        // ← IsNoArMode NO existía en v2
//
//  FIX v3:
//    • Propiedad UserForward: dirección de la cámara XR proyectada en XZ (plana).
//      En NoAR usa el transform del agente. Siempre devuelve un vector normalizado
//      y nunca Vector3.zero (fallback a Vector3.forward).
//
//    • Propiedad IsNoArMode: inverso de IsFullARMode. Consistente con la
//      propiedad del mismo nombre en AROriginAligner.
//
//  TODOS LOS CAMPOS/PROPIEDADES DE v2 SE CONSERVAN SIN MODIFICAR.
//
// ============================================================================

using UnityEngine;
using UnityEngine.AI;
using Unity.XR.CoreUtils;
using IndoorNavAR.AR;

namespace IndoorNavAR.Navigation
{
    public class UserPositionBridge : MonoBehaviour
    {
        // ─── Singleton ────────────────────────────────────────────────────

        public static UserPositionBridge Instance { get; private set; }

        // ─── Inspector ────────────────────────────────────────────────────

        [Header("─── Referencias ─────────────────────────────────────────────")]
        [SerializeField] private XROrigin        _xrOrigin;
        [SerializeField] private NavigationAgent _navigationAgent;
        [SerializeField] private AROriginAligner _arOriginAligner;

        [Header("─── Suavizado de velocidad ──────────────────────────────────")]
        [Tooltip("Factor EMA para suavizar la velocidad (0=sin suavizado, 0.9=muy suavizado). " +
                 "Valor recomendado: 0.85 para evitar spikes de ARCore.")]
        [SerializeField] [Range(0f, 0.99f)] private float _speedSmoothing = 0.85f;

        [Tooltip("Velocidad máxima plausible (m/s). Valores mayores se descartan como " +
                 "artefactos del tracking. Valor recomendado: 3.0 (caminar rápido).")]
        [SerializeField] private float _maxPlausibleSpeed = 3.0f;

        [Header("─── Debug ────────────────────────────────────────────────────")]
        [SerializeField] private bool _logVerbose = false;

        // ─── Estado interno ───────────────────────────────────────────────

        private Vector3 _lastPosition  = Vector3.zero;
        private float   _smoothedSpeed = 0f;
        private bool    _isFullAR      = false;
        private bool    _initialized   = false;

        // ─── Propiedades públicas ─────────────────────────────────────────

        /// <summary>
        /// Posición del usuario en el mundo.
        /// FullAR: posición de la cámara XR (espacio físico real).
        /// NoAR:   posición del agente virtual.
        /// </summary>
        public Vector3 UserPosition
        {
            get
            {
                if (!_initialized) Initialize();

                if (_isFullAR && _xrOrigin?.Camera != null)
                    return _xrOrigin.Camera.transform.position;

                if (_navigationAgent != null)
                    return _navigationAgent.transform.position;

                if (Camera.main != null)
                    return Camera.main.transform.position;

                return Vector3.zero;
            }
        }

        /// <summary>
        /// ✅ v3 NUEVO — Dirección de avance del usuario, proyectada en XZ (horizontal).
        ///
        /// FullAR: dirección de la cámara XR (a dónde apunta el usuario físico).
        /// NoAR:   dirección de avance del agente virtual.
        ///
        /// Siempre devuelve un vector normalizado no-zero. Si la cámara apunta
        /// exactamente arriba/abajo (caso extremo), devuelve Vector3.forward.
        /// </summary>
        public Vector3 UserForward
        {
            get
            {
                if (!_initialized) Initialize();

                Vector3 fwd;

                if (_isFullAR && _xrOrigin?.Camera != null)
                    fwd = _xrOrigin.Camera.transform.forward;
                else if (_navigationAgent != null)
                    fwd = _navigationAgent.transform.forward;
                else if (Camera.main != null)
                    fwd = Camera.main.transform.forward;
                else
                    return Vector3.forward;

                // Proyectar en plano XZ (ignorar componente Y)
                fwd.y = 0f;
                return fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;
            }
        }

        /// <summary>
        /// Posición del agente en el NavMesh. Puede diferir de UserPosition en FullAR
        /// mientras AROriginAligner sincroniza (lag de un frame es normal).
        /// </summary>
        public Vector3 AgentPosition => _navigationAgent != null
            ? _navigationAgent.transform.position
            : UserPosition;

        /// <summary>
        /// Velocidad de movimiento del usuario (m/s), suavizada con EMA.
        /// En FullAR refleja la velocidad real de la cámara XR.
        /// </summary>
        public float UserSpeed => _smoothedSpeed;

        /// <summary>
        /// Orientación completa de la cámara del usuario (incluye pitch/roll).
        /// Para dirección de avance plana usar UserForward.
        /// </summary>
        public Quaternion UserRotation
        {
            get
            {
                if (_xrOrigin?.Camera != null)
                    return _xrOrigin.Camera.transform.rotation;
                if (Camera.main != null)
                    return Camera.main.transform.rotation;
                return Quaternion.identity;
            }
        }

        /// <summary>
        /// ✅ v3 NUEVO — True si el sistema está en modo NoAR (sin ARCore).
        /// Inverso de IsFullARMode. Nombre consistente con AROriginAligner.IsNoArMode.
        ///
        /// Usado por NavigationVoiceGuide para decidir si EvalPos = UserPos o AgentPos:
        ///   IsFullARMode = !IsNoArMode → EvalPos = UserPos (cámara XR)
        ///   IsNoArMode   = true         → EvalPos = AgentPos (agente virtual)
        /// </summary>
        public bool IsNoArMode => !_isFullAR;

        /// <summary>
        /// True si el sistema está en modo FullAR (ARCore activo).
        /// Equivalente a !IsNoArMode.
        /// </summary>
        public bool IsFullARMode => _isFullAR;

        /// <summary>
        /// Índice del piso donde se encuentra el usuario según los StartPoints registrados.
        /// </summary>
        public int UserFloorLevel
        {
            get
            {
                var startPoints = NavigationStartPointManager.GetAllStartPoints();
                if (startPoints.Count == 0) return 0;

                float userY    = UserPosition.y;
                int   best     = 0;
                float bestDist = float.MaxValue;

                foreach (var pt in startPoints)
                {
                    if (pt == null) continue;
                    float dist = Mathf.Abs(pt.FloorHeight - userY);
                    if (dist < bestDist) { bestDist = dist; best = pt.Level; }
                }

                return best;
            }
        }

        /// <summary>
        /// True si la posición del usuario tiene NavMesh dentro de 1m.
        /// </summary>
        public bool IsOnNavMesh =>
            NavMesh.SamplePosition(UserPosition, out _, 1.0f, NavMesh.AllAreas);

        /// <summary>
        /// Distancia horizontal (XZ) desde el usuario hasta un punto.
        /// Ignora diferencias de altura — útil para comparaciones en el mismo piso.
        /// </summary>
        public float HorizontalDistanceTo(Vector3 point)
        {
            Vector3 userPos = UserPosition;
            return Vector2.Distance(
                new Vector2(userPos.x, userPos.z),
                new Vector2(point.x,   point.z));
        }

        /// <summary>
        /// Distancia 3D desde el usuario hasta un punto.
        /// </summary>
        public float DistanceTo(Vector3 point) =>
            Vector3.Distance(UserPosition, point);

        // ─── Lifecycle ────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()  => Initialize();
        private void Update() => UpdateSpeed();

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ─── Inicialización ───────────────────────────────────────────────

        private void Initialize()
        {
            if (_initialized) return;

            if (_xrOrigin == null)
                _xrOrigin = FindFirstObjectByType<XROrigin>();

            if (_navigationAgent == null)
                _navigationAgent = FindFirstObjectByType<NavigationAgent>(
                    FindObjectsInactive.Include);

            if (_arOriginAligner == null)
                _arOriginAligner = FindFirstObjectByType<AROriginAligner>(
                    FindObjectsInactive.Include);

            // Determinar modo: consultar AROriginAligner si está disponible
            if (_arOriginAligner != null)
                _isFullAR = _arOriginAligner.IsFullARMode;
            else
                _isFullAR = _xrOrigin != null; // Heurística: XR Origin presente = FullAR

            // ✅ FIX StackOverflow: _initialized = true ANTES de leer UserPosition.
            // UserPosition tiene guard "if (!_initialized) Initialize()" → bucle infinito
            // si se llama desde aquí antes de que _initialized sea true.
            // Calculamos _lastPosition directamente sin pasar por la propiedad.
            _initialized = true;
            _lastPosition = _isFullAR && _xrOrigin?.Camera != null
                ? _xrOrigin.Camera.transform.position
                : _navigationAgent != null
                    ? _navigationAgent.transform.position
                    : Camera.main != null ? Camera.main.transform.position : Vector3.zero;

            if (_logVerbose)
                Debug.Log($"[UserPositionBridge] ✅ v3 Inicializado.\n" +
                          $"  Modo: {(_isFullAR ? "FullAR" : "NoAR")} | " +
                          $"  XROrigin: {(_xrOrigin != null ? "✓" : "✗")} | " +
                          $"  Agent: {(_navigationAgent != null ? "✓" : "✗")} | " +
                          $"  AROriginAligner: {(_arOriginAligner != null ? "✓" : "✗")}");
        }

        // ─── Cálculo de velocidad ─────────────────────────────────────────

        private void UpdateSpeed()
        {
            if (!_initialized) return;

            Vector3 currentPos = UserPosition;
            float   rawSpeed   = Time.deltaTime > 0f
                ? Vector3.Distance(currentPos, _lastPosition) / Time.deltaTime
                : 0f;

            // Descartar spikes de tracking (teleports de ARCore, etc.)
            if (rawSpeed > _maxPlausibleSpeed)
                rawSpeed = _smoothedSpeed; // mantener valor anterior

            // EMA: smoothed = alpha * smoothed + (1 - alpha) * raw
            _smoothedSpeed = _speedSmoothing * _smoothedSpeed
                           + (1f - _speedSmoothing) * rawSpeed;

            _lastPosition = currentPos;
        }

        // ─── API para actualización de modo ──────────────────────────────

        /// <summary>
        /// Llamar cuando el modo AR cambia en runtime (ej. ARCapabilityDetector resuelve).
        /// AROriginAligner llama esto después de InitializeCapabilityRoutine().
        /// </summary>
        public void RefreshMode()
        {
            if (_arOriginAligner != null)
                _isFullAR = _arOriginAligner.IsFullARMode;

            if (_logVerbose)
                Debug.Log($"[UserPositionBridge] 🔄 Modo refrescado: " +
                          $"{(_isFullAR ? "FullAR" : "NoAR")}");
        }

        // ─── Debug ────────────────────────────────────────────────────────

        [ContextMenu("ℹ️ Estado actual")]
        private void DebugStatus()
        {
            Debug.Log(
                $"[UserPositionBridge] v3\n" +
                $"  Modo:          {(_isFullAR ? "FullAR" : "NoAR")} (IsNoArMode={IsNoArMode})\n" +
                $"  UserPosition:  {UserPosition:F3}\n" +
                $"  UserForward:   {UserForward:F3}\n" +
                $"  AgentPosition: {AgentPosition:F3}\n" +
                $"  UserFloor:     {UserFloorLevel}\n" +
                $"  UserSpeed:     {UserSpeed:F2} m/s\n" +
                $"  IsOnNavMesh:   {IsOnNavMesh}\n" +
                $"  UserRotation:  {UserRotation.eulerAngles:F1}°");
        }
    }
}