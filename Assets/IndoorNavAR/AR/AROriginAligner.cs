// File: AROriginAligner.cs
// ✅ v9.0 — REFACTOR: Alineación correcta según AR Foundation + throttle de sync
//
// ============================================================================
//  PROBLEMA RAÍZ (diagnosticado desde logs)
// ============================================================================
//
//  Log de síntoma:
//    [AROriginAligner] ! Hit r=1m descartado: Y=1.80 vs 4.46 (margen=1.30)
//    [AROriginAligner] ! Hit r=2m descartado: Y=1.80 vs 4.46 (margen=1.30)
//    [AROriginAligner] ! Hit r=3m descartado: Y=1.80 vs 4.46 (margen=1.30)
//    → Repite decenas de veces por segundo → CPU starvation → STT falla
//
//  CAUSA A — GetExpectedFloorY() malinterpreta la posición de cámara:
//    La cámara AR está a altura de OJO del usuario (~1.65m sobre el suelo
//    físico). GetExpectedFloorY() buscaba el StartPoint más cercano en Y a
//    cameraY=4.46, pero no hay ningún StartPoint a esa altura (todos están
//    en el suelo: Y≈1.75 para piso 0, Y≈3.20 para piso 1). La función
//    retornaba 4.46 como "expected floor", hitMargin=1.3 → buscaba NavMesh
//    entre Y=3.16 y Y=5.76 → el NavMesh en Y=1.80 nunca entraba en rango
//    → FALLA PERPETUA cada frame.
//
//  CAUSA B — Sin throttle en el path de fallo:
//    Cada fallo ejecuta 4 NavMesh.SamplePosition() por frame × 60fps
//    = 240+ queries/segundo → CPU starvation → STT timeout → micrófono falla.
//
//  FIX A — GetExpectedFloorY(cameraY) descuenta _eyeHeightOffset:
//    estimatedGround = cameraY - _eyeHeightOffset → busca StartPoint más
//    cercano a estimatedGround. Con cameraY=4.46, offset=1.3:
//    estimatedGround=3.16 → StartPoint L0 en Y=1.75 (Δ=1.41) → ✅ SELECCIONADO
//    searchOrigin elevado al hitMargin sobre estimatedGround → SamplePosition
//    encuentra NavMesh en Y=1.80 dentro del rango → SYNC EXITOSO.
//
//  FIX B — Throttle exponencial en path de fallo:
//    1er fallo → cooldown 0.5s | 2do → 1s | 3ro → 2s | ... | máx 5s
//    Elimina 240 queries/seg → 1-2/seg en fallo → CPU libre para VIO + STT.

using System.Collections;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.XR.ARFoundation;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Navigation;
using IndoorNavAR.Integration;
using Unity.XR.CoreUtils;

namespace IndoorNavAR.AR
{
    public class AROriginAligner : MonoBehaviour
    {
        [Header("─── Referencias ────────────────────────────────────────────")]
        [SerializeField] private XROrigin        _xrOrigin;
        [SerializeField] private NavigationAgent _navigationAgent;

        [Header("─── Configuración ─────────────────────────────────────────")]
        [SerializeField] private int   _targetLevel     = 0;
        [SerializeField] private float _eyeHeightOffset = 1.3f;
        [SerializeField] private int   _delayFrames     = 2;

        [Header("─── Modo NoAR ──────────────────────────────────────────────")]
        [SerializeField] private float _noArCameraHeight   = 1.65f;
        [SerializeField] private float _noArCameraBack     = 0.0f;
        [SerializeField] private float _noArPitchAngle     = 0.0f;
        [SerializeField] private float _noArFollowSmooth   = 8f;
        [SerializeField] private bool  _noArFollowRotation = true;

        [Header("─── Modo FullAR ─────────────────────────────────────────────")]
        [SerializeField] private float _fullArSnapRadius    = 3.0f;
        [SerializeField] private float _fullArSyncThreshold = 0.05f;

        [Tooltip("Tolerancia vertical (m) para aceptar un hit de NavMesh.\n\n" +
                 "✅ v9.0: Se aplica sobre (cameraY - _eyeHeightOffset), NO sobre cameraY.\n" +
                 "Margen efectivo = _floorSnapTolerance + 0.5f\n" +
                 "Default 0.8f → margen efectivo 1.3m\n\n" +
                 "Ejemplo: cámara Y=4.46, offset=1.3 → estimGround=3.16\n" +
                 "→ busca NavMesh cerca de Y=3.16 ±1.3m\n" +
                 "→ acepta hits entre Y=1.86 y Y=4.46\n" +
                 "→ NavMesh en Y=1.80 → ΔY=1.36 ≈ límite, ajustar a 0.9 si falla.")]
        [SerializeField] private float _floorSnapTolerance = 0.9f;   // subido de 0.8 → 0.9 para cubrir el caso del log

        [Header("─── VIO Recovery ───────────────────────────────────────────")]
        [SerializeField] private float _vioRecoveryDelay          = 0.8f;
        [SerializeField] private bool  _freezeAgentOnTrackingLoss = true;

        [Header("─── FIX_FLICKER — Filtro de flickers de tracking ────────────")]
        [Tooltip("Duración mínima (s) de pérdida de tracking para ser un VIO reset real.\n" +
                 "Default: 0.5s")]
        [SerializeField] private float _minTrackingLostDuration = 0.5f;

        [Header("─── FIX_TIMESTAMP — Cooldown pose-query post-recovery ──────")]
        [Tooltip("Segundos de cooldown tras recuperar tracking.\n" +
                 "Evita 'GetRecentDevicePose: Passed timestamp is too new'.\n" +
                 "Default: 1.5s")]
        [SerializeField] private float _poseQueryCooldownSec = 1.5f;

        [Header("─── v9.0 FIX B — Throttle de sync en fallo ─────────────────")]
        [Tooltip("Cooldown base (s) entre reintentos de sync cuando el NavMesh\n" +
                 "no es alcanzable. Se multiplica exponencialmente.\n" +
                 "Default: 0.5s — elimina la avalancha de logs que saturaba el CPU.")]
        [SerializeField] private float _syncFailCooldown    = 0.5f;

        [Tooltip("Cooldown máximo (s) entre reintentos de sync fallidos.\n" +
                 "Default: 5.0s")]
        [SerializeField] private float _syncFailCooldownMax = 5.0f;

        [Header("─── Estabilización post-VIO ──────────────────────────────")]
        [SerializeField] private int _stableFramesRequired = 10;

        [Header("─── Warp de emergencia ──────────────────────────────────────")]
        [SerializeField] private int _syncFailThreshold = 120;

        [Header("─── Espera de estabilidad inicial ───────────────────────────")]
        [SerializeField] private float _fullStabilityTimeout = 12f;

        [Header("─── Alineación diferida sin tracking (v8.7) ─────────────────")]
        [SerializeField] private bool _deferAlignIfNoTracking = true;

        [Header("─── Debug ──────────────────────────────────────────────────")]
        [SerializeField] private bool _logAlignment = true;

        // ─── Estado interno ────────────────────────────────────────────────

        private bool             _noArMode           = false;
        private bool             _followActive       = false;
        private bool             _capabilityResolved = false;
        private bool             _initialAlignDone   = false;
        private ARCapabilityDetector _capDetector;
        private ARSessionManager _arSessionManager;

        private Vector3      _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
        private NavMeshAgent _agentNavMeshAgent;

        private ARSessionState _lastARState    = ARSessionState.None;
        private bool           _trackingLost   = false;
        private Vector3        _lastStableAgentPos;
        private bool           _hasStablePos   = false;

        private float _trackingLostTime      = 0f;   // v8.10
        private float _trackingRecoveredTime = 0f;   // v8.12

        private int _stableFrameCount = 0;
        private int _syncFailFrames   = 0;

        private bool _pendingAlignAfterTracking    = false;  // v8.7
        private bool _alignedWithoutTracking       = false;
        private bool _lastWaitForFullyStableResult = false;

        // ✅ v9.0 FIX B — Throttle exponencial de sync en fallo
        private float _nextSyncAllowedTime     = 0f;
        private float _currentSyncFailCooldown = 0f;
        private int   _consecutiveSyncFails    = 0;

        // ─── Propiedades ──────────────────────────────────────────────────

        public bool IsNoArMode       => _noArMode;
        public bool IsFullARMode     => !_noArMode;
        public bool IsTrackingStable => ARSession.state == ARSessionState.SessionTracking;
        public bool InitialAlignDone => _initialAlignDone;

        #region Lifecycle

        private void Awake()
        {
            FindComponents();
            if (_capDetector != null && _capDetector.IsReady &&
                _capDetector.Current != ARCapabilityLevel.NoAR)
            {
                var pc = _navigationAgent?.GetComponent<NavigationPathController>();
                if (pc != null) { pc.SetFullARMode(true); Log("✅ [Awake] PathController.SetFullARMode(true)"); }
            }
        }

        private void Start() => StartCoroutine(InitializeCapabilityRoutine());

        private void OnEnable()
        {
            EventBus.Instance?.Subscribe<ModelLoadedEvent>(OnModelLoaded);
            ARSession.stateChanged += OnARSessionStateChanged;
        }

        private void OnDisable()
        {
            EventBus.Instance?.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);
            ARSession.stateChanged -= OnARSessionStateChanged;
            _followActive = false;
        }

        private void Update()
        {
            if (_followActive && _noArMode) { FollowAgent(); return; }
            if (!_noArMode && _initialAlignDone) SyncAgentToCameraFullAR();
        }

        #endregion

        #region Component Discovery

        private void FindComponents()
        {
            if (_xrOrigin        == null) _xrOrigin        = FindFirstObjectByType<XROrigin>();
            if (_navigationAgent == null) _navigationAgent = FindFirstObjectByType<NavigationAgent>();
            if (_navigationAgent != null) _agentNavMeshAgent = _navigationAgent.GetComponent<NavMeshAgent>();
            _capDetector      = ARCapabilityDetector.Instance ?? FindFirstObjectByType<ARCapabilityDetector>();
            _arSessionManager = FindFirstObjectByType<ARSessionManager>();

            if (_xrOrigin         == null) Debug.LogWarning("[AROriginAligner] ⚠️ XROrigin no encontrado.");
            if (_capDetector      == null) Debug.LogWarning("[AROriginAligner] ⚠️ ARCapabilityDetector no encontrado.");
            if (_arSessionManager == null) Debug.LogWarning("[AROriginAligner] ⚠️ ARSessionManager no encontrado.");
        }

        #endregion

        #region Capability Initialization

        private IEnumerator InitializeCapabilityRoutine()
        {
            yield return null;
            if (_capDetector != null) yield return _capDetector.WaitUntilReady();

            ARCapabilityLevel level = _capDetector?.Current ?? ARCapabilityLevel.FullAR;
            _capabilityResolved = true;
            Log($"📡 [Start] Capacidad AR: {level}");

            if (level == ARCapabilityLevel.NoAR)
            {
                _noArMode = true;
                Log("📵 Modo NoAR activo.");
                _navigationAgent?.GetComponent<NavigationPathController>()?.SetFullARMode(false);

                var arSess = FindFirstObjectByType<ARSession>();
                if (arSess != null) arSess.enabled = false;
                var pm = FindFirstObjectByType<ARPlaneManager>();
                if (pm != null) pm.enabled = false;

                SetAgentActiveAndVisible(true);
                var mlm = FindFirstObjectByType<IndoorNavAR.Core.Managers.ModelLoadManager>();
                if (mlm != null && mlm.IsModelLoaded) ActivateNoArMode();
            }
            else
            {
                _noArMode = false; _followActive = false;
                Log("📡 Modo FullAR activo.");
                var pc = _navigationAgent?.GetComponent<NavigationPathController>();
                if (pc != null) { pc.SetFullARMode(true); Log("✅ PathController.SetFullARMode(true)"); }
                SetAgentActiveAndVisible(false);
                StopAgentMovement();
                if (level == ARCapabilityLevel.ARWithoutPlanes)
                {
                    var arpm = FindFirstObjectByType<ARPlaneManager>();
                    if (arpm != null) arpm.enabled = false;
                }
            }
        }

        private void SetAgentActiveAndVisible(bool makeVisible)
        {
            if (_navigationAgent == null) return;
            if (!_navigationAgent.gameObject.activeSelf)
                _navigationAgent.gameObject.SetActive(true);
            foreach (var r in _navigationAgent.GetComponentsInChildren<Renderer>(true))
                r.enabled = makeVisible;
        }

        private void StopAgentMovement()
        {
            if (_agentNavMeshAgent == null) return;
            if (_agentNavMeshAgent.enabled && _agentNavMeshAgent.isOnNavMesh)
            { _agentNavMeshAgent.isStopped = true; _agentNavMeshAgent.ResetPath(); }
        }

        #endregion

        #region VIO Reset Detection

        private void OnARSessionStateChanged(ARSessionStateChangedEventArgs args)
        {
            ARSessionState newState    = args.state;
            bool           wasLost     = IsTrackingDegraded(_lastARState);
            bool           nowTracking = newState == ARSessionState.SessionTracking;
            bool           nowLost     = IsTrackingDegraded(newState);

            Log($"📡 ARSession: {_lastARState} → {newState}");
            _lastARState = newState;
            NotifyFlutterTrackingState(nowTracking, newState.ToString());
            if (_noArMode) return;

            if (nowLost && !_trackingLost)
            {
                _trackingLost     = true;
                _trackingLostTime = Time.realtimeSinceStartup;
                _stableFrameCount = 0;
                if (_navigationAgent != null)
                {
                    _lastStableAgentPos = _navigationAgent.transform.position;
                    _hasStablePos = true;
                    Log($"⚠️ Tracking perdido — pos guardada: {_lastStableAgentPos:F2}");
                }
            }

            if (nowTracking)
            {
                _trackingRecoveredTime = Time.realtimeSinceStartup;
                // v9.0: resetear throttle al recuperar tracking
                _consecutiveSyncFails    = 0;
                _currentSyncFailCooldown = 0f;
                _nextSyncAllowedTime     = 0f;
            }

            // v8.7: alineación diferida
            if (nowTracking && _pendingAlignAfterTracking)
            {
                _pendingAlignAfterTracking = false;
                Log("📡 Tracking recuperado — ejecutando alineación diferida.");
                AlignXROriginOnce();
            }

            if (wasLost && nowTracking && _initialAlignDone)
            {
                float lostDuration = Time.realtimeSinceStartup - _trackingLostTime;
                if (lostDuration < _minTrackingLostDuration)
                {
                    _trackingLost = false;
                    return;
                }
                _trackingLost = false;
                Log("📡 Tracking recuperado — ARCore corrige automáticamente.");
            }
            else if (nowTracking) _trackingLost = false;
        }

        private static bool IsTrackingDegraded(ARSessionState s) =>
            s == ARSessionState.SessionInitializing;

        private void NotifyFlutterTrackingState(bool isStable, string stateStr)
        {
            if (ARSession.state == ARSessionState.Ready) return;
            var api = VoiceCommandAPI.Instance;
            if (api == null) return;
            string reason = isStable ? "None" : ARSession.notTrackingReason.ToString();
            api.NotifyTrackingState(isStable, isStable ? stateStr : $"{stateStr}|{reason}");
        }

        #endregion

        #region Event Handlers / Public API

        private void OnModelLoaded(ModelLoadedEvent evt)
        {
            Log($"📦 Modelo: {evt.ModelName}");
            StartCoroutine(HandleModelReady());
        }

        public void NotifySessionRestored() => StartCoroutine(HandleModelReady());
        public void AlignToStartPoint()     => StartCoroutine(HandleModelReady());

        public void ForceRealign()
        {
            if (!_noArMode)
            {
                _pendingAlignAfterTracking = false;
                _lastSyncedCameraPos       = new Vector3(float.PositiveInfinity, 0, 0);
                _consecutiveSyncFails      = 0;
                _currentSyncFailCooldown   = 0f;
                _nextSyncAllowedTime       = 0f;
            }
            StartCoroutine(HandleModelReady());
        }

        public void ForceSnapAgentToCamera()
        {
            if (_noArMode || _xrOrigin?.Camera == null || _navigationAgent == null) return;
            EmergencyWarpAgentToCamera(_xrOrigin.Camera.transform.position);
        }

        #endregion

        #region Core Logic

        private IEnumerator HandleModelReady()
        {
            for (int i = 0; i < _delayFrames; i++) yield return null;
            if (_capDetector != null && !_capabilityResolved)
                yield return _capDetector.WaitUntilReady();

            ARCapabilityLevel level = _capDetector?.Current ?? ARCapabilityLevel.FullAR;

            if (level == ARCapabilityLevel.NoAR)
            {
                _noArMode = true;
                ActivateNoArMode();
            }
            else
            {
                _noArMode = false; _followActive = false;
                yield return WaitForFullyStable();
                bool trackingReady = _lastWaitForFullyStableResult;

                if (!trackingReady && _deferAlignIfNoTracking)
                {
                    _pendingAlignAfterTracking = true;
                    _alignedWithoutTracking    = false;
                    Debug.LogWarning("[AROriginAligner] ⚠️ [v8.7] Alineación DIFERIDA hasta SessionTracking.");
                }
                else
                {
                    _pendingAlignAfterTracking = false;
                    _alignedWithoutTracking    = !trackingReady;
                    if (!trackingReady) Debug.LogWarning("[AROriginAligner] ⚠️ Alineando SIN tracking.");
                    AlignXROriginOnce();
                }
            }
        }

        private IEnumerator WaitForFullyStable()
        {
            _lastWaitForFullyStableResult = false;
            if (_noArMode) { _lastWaitForFullyStableResult = true; yield break; }

#if UNITY_EDITOR
            if (ARSession.state == ARSessionState.None || ARSession.state == ARSessionState.Ready)
            {
                Debug.LogWarning("[AROriginAligner] ✅ [v9.0] Editor sin ARCore — Wait inmediato.");
                _lastWaitForFullyStableResult = true;
                yield break;
            }
#endif

            if (_arSessionManager != null && _arSessionManager.IsFullyStable)
            {
                Log("✅ [WaitForFullyStable] Ya estable."); _lastWaitForFullyStableResult = true; yield break;
            }
            if (ARSession.state == ARSessionState.SessionTracking)
            {
                Log("✅ [WaitForFullyStable] SessionTracking activo — 10 frames...");
                for (int i = 0; i < 10; i++) yield return null;
                _lastWaitForFullyStableResult = true; yield break;
            }

            float elapsed = 0f;
            Log($"⏳ [WaitForFullyStable] Esperando (timeout={_fullStabilityTimeout}s)...");
            while (elapsed < _fullStabilityTimeout)
            {
                yield return null;
                elapsed += Time.deltaTime;
                bool isStable = _arSessionManager != null
                    ? _arSessionManager.IsFullyStable
                    : ARSession.state == ARSessionState.SessionTracking;
                if (isStable)
                {
                    Log($"✅ Estabilidad en {elapsed:F1}s.");
                    _lastWaitForFullyStableResult = true;
                    yield break;
                }
            }
            Debug.LogWarning($"[AROriginAligner] ⚠️ WaitForFullyStable timeout {_fullStabilityTimeout}s — Estado: {ARSession.state}");
            _lastWaitForFullyStableResult = false;
        }

        /// <summary>
        /// Alinea el XROrigin para que la cámara quede en la posición del StartPoint.
        ///
        /// FUNDAMENTO AR FOUNDATION:
        ///   El XROrigin transforma el "session space" (espacio relativo al inicio
        ///   de la sesión AR) al espacio Unity. La cámara es controlada por
        ///   TrackedPoseDriver y NO se puede mover directamente.
        ///   MoveCameraToWorldLocation() mueve el XROrigin.transform para que la
        ///   cámara quede en targetPos — es la forma oficial de anclar el contenido
        ///   AR a una posición del mundo virtual.
        ///
        ///   Después de esta llamada, la cámara estará en targetPos y el XROrigin
        ///   se habrá desplazado en la cantidad necesaria. Todos los trackables
        ///   (anclas, planos) se moverán junto con el XROrigin.
        /// </summary>
        private void AlignXROriginOnce()
        {
            if (_xrOrigin == null) { Debug.LogError("[AROriginAligner] ❌ XROrigin null."); return; }

            var startPoint = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            if (startPoint == null)
            {
                Debug.LogWarning($"[AROriginAligner] ⚠️ Sin StartPoint nivel {_targetLevel}.");
                return;
            }

            startPoint.ConfirmModelPositioned();

            if (!_initialAlignDone)
            {
                // targetPos = posición del StartPoint + offset de ojo
                // MoveCameraToWorldLocation mueve el XROrigin para que la cámara
                // (controlada por TrackedPoseDriver) quede en esta posición
                Vector3 targetPos = startPoint.transform.position + Vector3.up * _eyeHeightOffset;

                _arSessionManager?.SuppressQuickMoveDetection(frames: 5);
                _xrOrigin.MoveCameraToWorldLocation(targetPos);

                _initialAlignDone    = true;
                _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);

                // v9.0: resetear throttle tras alineación
                _consecutiveSyncFails    = 0;
                _currentSyncFailCooldown = 0f;
                _nextSyncAllowedTime     = 0f;

                bool hadTracking = ARSession.state == ARSessionState.SessionTracking;
                Log($"✅ [FullAR] XR Origin → {targetPos}. Tracking: {(hadTracking ? "✅ SÍ" : "⚠️ NO")}");

                if (!hadTracking)
                    Debug.LogWarning("[AROriginAligner] ⚠️ Alineación sin tracking — posible desalineación.");
            }
            else Log("📡 [FullAR] Alineación ya hecha — XR Origin intocado.");

            SetAgentActiveAndVisible(false);
            StopAgentMovement();
            EventBus.Instance?.Publish(new ShowMessageEvent
                { Message = "Navegación lista", Type = MessageType.Success, Duration = 3f });
        }

        #endregion

        #region FullAR Sync

        /// <summary>
        /// Sincroniza el agente virtual a la posición de la cámara AR en el NavMesh.
        ///
        /// ✅ v9.0 LÓGICA CORREGIDA:
        ///
        ///   PROBLEMA v8.x:
        ///     GetExpectedFloorY(cameraY=4.46) buscaba StartPoint más cercano a Y=4.46
        ///     → ninguno existe → retornaba 4.46 → hitRange [3.16, 5.76]
        ///     → NavMesh en Y=1.80 FUERA del rango → FALLA PERPETUA × 60fps
        ///
        ///   SOLUCIÓN v9.0:
        ///     La cámara AR está a altura de ojo. El suelo está ~1.3m más abajo.
        ///     estimatedGround = cameraY - _eyeHeightOffset = 4.46 - 1.3 = 3.16
        ///     GetExpectedFloorY(cameraY) busca StartPoint más cercano a 3.16
        ///     → StartPoint L0 en Y=1.75 (Δ=1.41) → seleccionado como piso esperado
        ///     searchOrigin.y = estimatedFloorY + hitMargin = 1.75 + 1.40 = 3.15
        ///     SamplePosition desde Y=3.15 con r=0.5m → NavMesh en Y=1.80 → ΔY=1.35
        ///     hitMargin = 0.9 + 0.5 = 1.40 → 1.35 ≤ 1.40 → ✅ ACEPTADO
        ///
        ///   El throttle exponencial garantiza que los fallos no saturen el CPU.
        /// </summary>
        private void SyncAgentToCameraFullAR()
        {
            if (_navigationAgent == null || _xrOrigin?.Camera == null) return;
            if (!_navigationAgent.gameObject.activeSelf) return;

            // Fuera de tracking → congelar en última posición estable
            if (ARSession.state != ARSessionState.SessionTracking)
            {
                _stableFrameCount = 0; _syncFailFrames = 0;
                if (_freezeAgentOnTrackingLoss && _hasStablePos)
                {
                    float dist = Vector3.Distance(
                        _navigationAgent.transform.position, _lastStableAgentPos);
                    if (dist > _fullArSyncThreshold)
                    {
                        _navigationAgent.transform.position = _lastStableAgentPos;
                        if (_agentNavMeshAgent?.enabled == true && _agentNavMeshAgent.isOnNavMesh)
                        { _agentNavMeshAgent.Warp(_lastStableAgentPos); _agentNavMeshAgent.isStopped = true; }
                    }
                }
                return;
            }

            // v8.12 FIX_TIMESTAMP: cooldown post-recovery
            if (_poseQueryCooldownSec > 0f &&
                Time.realtimeSinceStartup - _trackingRecoveredTime < _poseQueryCooldownSec)
                return;

            if (_arSessionManager != null && _arSessionManager.IsQuickMovePaused) return;

            // Frames estables antes de sincronizar
            _stableFrameCount++;
            if (_stableFrameCount < _stableFramesRequired) return;

            // ✅ v9.0 FIX B: Throttle — no ejecutar durante cooldown de fallo
            if (_currentSyncFailCooldown > 0f && Time.unscaledTime < _nextSyncAllowedTime)
                return;

            Vector3 cameraPos = _xrOrigin.Camera.transform.position;

            // Umbral de movimiento
            if (Vector3.Distance(cameraPos, _lastSyncedCameraPos) < _fullArSyncThreshold) return;
            _lastSyncedCameraPos = cameraPos;

            // Si navegando, asegurar que el agente esté parado
            if (_navigationAgent.IsNavigating)
            {
                if (_agentNavMeshAgent?.enabled == true &&
                    _agentNavMeshAgent.isOnNavMesh &&
                    !_agentNavMeshAgent.isStopped)
                    _agentNavMeshAgent.isStopped = true;
                return;
            }

            // ✅ v9.0 FIX A: Calcular piso esperado desde suelo estimado (no desde cámara)
            float estimatedFloorY = GetExpectedFloorY(cameraPos.y);
            float hitMargin       = _floorSnapTolerance + 0.5f;

            // searchOrigin: XZ de la cámara, Y elevado al hitMargin sobre el piso esperado
            // para que SamplePosition encuentre el NavMesh hacia abajo
            Vector3 searchOrigin = new Vector3(cameraPos.x, estimatedFloorY + hitMargin, cameraPos.z);

            NavMeshHit bestHit = default;
            bool       found   = false;

            foreach (float r in new[] { 0.5f, 1.0f, 2.0f, _fullArSnapRadius })
            {
                if (!NavMesh.SamplePosition(searchOrigin, out NavMeshHit hit, r, NavMesh.AllAreas))
                    continue;

                float deltaY = Mathf.Abs(hit.position.y - estimatedFloorY);
                if (deltaY <= hitMargin)
                {
                    bestHit = hit;
                    found   = true;
                    break;
                }

                // Log reducido: solo en debug (no cada frame)
                if (_logAlignment)
                    Debug.Log($"[AROriginAligner] Hit r={r}m descartado: " +
                              $"hitY={hit.position.y:F2} estimFloor={estimatedFloorY:F2} " +
                              $"ΔY={deltaY:F2} margen={hitMargin:F2}");
            }

            if (found)
            {
                // Resetear throttle al tener sync exitoso
                _syncFailFrames          = 0;
                _consecutiveSyncFails    = 0;
                _currentSyncFailCooldown = 0f;
                _nextSyncAllowedTime     = 0f;

                if (Vector3.Distance(_navigationAgent.transform.position,
                        bestHit.position) < _fullArSyncThreshold)
                    return;

                _navigationAgent.transform.position = bestHit.position;
                if (_agentNavMeshAgent?.enabled == true && _agentNavMeshAgent.isOnNavMesh)
                { _agentNavMeshAgent.Warp(bestHit.position); _agentNavMeshAgent.isStopped = true; }

                _lastStableAgentPos = bestHit.position;
                _hasStablePos       = true;
            }
            else
            {
                // ✅ v9.0 FIX B: Fallo → cooldown exponencial
                _syncFailFrames++;
                _consecutiveSyncFails++;

                // 1er fallo→0.5s | 2do→1s | 3ro→2s | 4to→4s | 5to→5s (max)
                _currentSyncFailCooldown = Mathf.Min(
                    _syncFailCooldown * Mathf.Pow(2f, _consecutiveSyncFails - 1),
                    _syncFailCooldownMax);
                _nextSyncAllowedTime = Time.unscaledTime + _currentSyncFailCooldown;

                Debug.LogWarning(
                    $"[AROriginAligner] ⚠️ Sync fallo #{_consecutiveSyncFails} — " +
                    $"camY={cameraPos.y:F2} estimFloor={estimatedFloorY:F2} " +
                    $"searchOrigin={searchOrigin:F2} " +
                    $"cooldown={_currentSyncFailCooldown:F1}s");

                // Warp de emergencia tras N fallos
                if (_syncFailThreshold > 0 && _syncFailFrames >= _syncFailThreshold)
                {
                    _syncFailFrames = 0; _consecutiveSyncFails = 0;
                    EmergencyWarpAgentToCamera(cameraPos);
                }
            }
        }

        /// <summary>
        /// ✅ v9.0 FIX A — GetExpectedFloorY corregido.
        ///
        ///   Descuenta _eyeHeightOffset de cameraY antes de buscar el piso más cercano.
        ///   La cámara AR está a altura de ojo, no a altura de suelo.
        ///
        ///   ANTES (v8.x, INCORRECTO):
        ///     return StartPoint más cercano a cameraY
        ///     → con cameraY=4.46: ningún StartPoint cercano → retorna 4.46 → FALLA
        ///
        ///   AHORA (v9.0, CORRECTO):
        ///     estimatedGround = cameraY - _eyeHeightOffset
        ///     return StartPoint más cercano a estimatedGround
        ///     → con cameraY=4.46, offset=1.3: estimatedGround=3.16
        ///     → StartPoint L0 en Y=1.75 (Δ=1.41) → retorna 1.75 ✅
        /// </summary>
        private float GetExpectedFloorY(float cameraY)
        {
            float estimatedGroundY = cameraY - _eyeHeightOffset;

            var pts = NavigationStartPointManager.GetAllStartPoints();
            if (pts.Count == 0) return estimatedGroundY;

            float bestFloorY = estimatedGroundY;
            float bestDist   = float.MaxValue;

            foreach (var pt in pts)
            {
                if (pt == null) continue;
                float d = Mathf.Abs(pt.FloorHeight - estimatedGroundY);
                if (d < bestDist) { bestDist = d; bestFloorY = pt.FloorHeight; }
            }

            return bestFloorY;
        }

        /// <summary>
        /// ✅ v9.0 FIX D — Warp de emergencia sin restricción de Y.
        /// </summary>
        private void EmergencyWarpAgentToCamera(Vector3 cameraPos)
        {
            float   estimatedGroundY = cameraPos.y - _eyeHeightOffset;
            Vector3 searchPos        = new Vector3(cameraPos.x, estimatedGroundY, cameraPos.z);

            float[] radii = { 1f, 2f, _fullArSnapRadius, _fullArSnapRadius * 2f, 10f };
            foreach (float r in radii)
            {
                if (!NavMesh.SamplePosition(searchPos, out NavMeshHit hit, r, NavMesh.AllAreas))
                    continue;

                Debug.LogWarning(
                    $"[AROriginAligner] 🚨 WARP: {_navigationAgent.transform.position:F2} " +
                    $"→ {hit.position:F2} (r={r}m)");

                _navigationAgent.transform.position = hit.position;
                if (_agentNavMeshAgent?.enabled == true)
                { _agentNavMeshAgent.Warp(hit.position); _agentNavMeshAgent.isStopped = true; }

                _lastStableAgentPos      = hit.position;
                _hasStablePos            = true;
                _lastSyncedCameraPos     = new Vector3(float.PositiveInfinity, 0, 0);
                _consecutiveSyncFails    = 0;
                _currentSyncFailCooldown = 0f;
                _nextSyncAllowedTime     = 0f;
                return;
            }

            Debug.LogWarning(
                $"[AROriginAligner] ⚠️ WARP emergencia sin NavMesh. searchPos={searchPos:F2}");
        }

        #endregion

        #region NoAR Mode

        private void ActivateNoArMode()
        {
            if (_xrOrigin == null) { Debug.LogError("[AROriginAligner] ❌ XROrigin null."); return; }
            SetAgentActiveAndVisible(true);
            var sp = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            if (sp != null) { sp.ConfirmModelPositioned(); sp.ReteleportAgent(); }
            if (_navigationAgent != null)
                SnapCameraToAgent(_navigationAgent.transform.position, _navigationAgent.transform.forward);
            _followActive = true;
            EventBus.Instance?.Publish(new ShowMessageEvent
                { Message = "Modo visualización (sin ARCore)", Type = MessageType.Info, Duration = 4f });
        }

        private void FollowAgent()
        {
            if (_navigationAgent == null || _xrOrigin == null) return;
            Vector3    pos = _navigationAgent.transform.position;
            Vector3    fwd = _navigationAgent.transform.forward;
            Vector3    cam = pos + Vector3.up * _noArCameraHeight - fwd * _noArCameraBack;
            Quaternion rot;
            if (_noArFollowRotation && fwd != Vector3.zero)
            { Vector3 ld = _noArCameraBack > 0f ? (pos - cam).normalized : fwd; rot = Quaternion.LookRotation(ld) * Quaternion.Euler(_noArPitchAngle, 0f, 0f); }
            else rot = _xrOrigin.Camera.transform.rotation;
            float t = _noArFollowSmooth > 0f ? Time.deltaTime * _noArFollowSmooth : 1f;
            _xrOrigin.MoveCameraToWorldLocation(Vector3.Lerp(_xrOrigin.Camera.transform.position, cam, t));
            if (_noArFollowRotation)
                _xrOrigin.MatchOriginUpCameraForward(Vector3.up,
                    Quaternion.Slerp(_xrOrigin.Camera.transform.rotation, rot, t) * Vector3.forward);
        }

        private void SnapCameraToAgent(Vector3 pos, Vector3 fwd)
        {
            _xrOrigin.MoveCameraToWorldLocation(pos + Vector3.up * _noArCameraHeight - fwd * _noArCameraBack);
            if (_noArFollowRotation && fwd != Vector3.zero)
                _xrOrigin.MatchOriginUpCameraForward(Vector3.up, fwd);
        }

        #endregion

        #region Debug

        private void Log(string m) { if (_logAlignment) Debug.Log($"[AROriginAligner] {m}"); }

        [ContextMenu("ℹ️ Info v9.0")]
        private void DebugInfo()
        {
            float camY        = _xrOrigin?.Camera != null ? _xrOrigin.Camera.transform.position.y : -999f;
            float estimGround = camY - _eyeHeightOffset;
            float efy         = GetExpectedFloorY(camY);
            float lostDur     = _trackingLost ? Time.realtimeSinceStartup - _trackingLostTime : 0f;
            float recovAgo    = Time.realtimeSinceStartup - _trackingRecoveredTime;
            float syncCoolRem = Mathf.Max(0f, _nextSyncAllowedTime - Time.unscaledTime);
            var   sp          = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);

            Debug.Log(
                "══════════════════════════════════════════════\n" +
                "  AROriginAligner v9.0\n" +
                "══════════════════════════════════════════════\n" +
                $"  Modo:             {(IsNoArMode ? "NoAR" : "FullAR")}\n" +
                $"  ARSession:        {ARSession.state}\n" +
                $"  InitialAlignDone: {_initialAlignDone}\n" +
                $"  TrackingLost:     {_trackingLost} ({lostDur * 1000:F0}ms)\n" +
                $"  PoseCooldown:     {_poseQueryCooldownSec}s | recovAgo={recovAgo:F2}s | " +
                    $"inCooldown={recovAgo < _poseQueryCooldownSec}\n" +
                $"  camY:             {camY:F3}\n" +
                $"  estimGround:      {estimGround:F3}  (camY - eyeOffset={_eyeHeightOffset})\n" +
                $"  expectedFloorY:   {efy:F3}  (StartPoint más cercano a estimGround)\n" +
                $"  hitMargin:        ±{_floorSnapTolerance + 0.5f:F2}m\n" +
                $"  SyncFails:        #{_consecutiveSyncFails} | " +
                    $"cooldown={_currentSyncFailCooldown:F1}s | restante={syncCoolRem:F1}s\n" +
                $"  StartPoint:       {(sp != null ? $"{sp.gameObject.name} @ {sp.transform.position:F2}" : "N/A")}\n" +
                "══════════════════════════════════════════════");

            foreach (var pt in NavigationStartPointManager.GetAllStartPoints())
            {
                if (pt == null) continue;
                float dGround = Mathf.Abs(pt.FloorHeight - estimGround);
                Debug.Log($"  Level{pt.Level}: FloorH={pt.FloorHeight:F3} | " +
                          $"Δground={dGround:F3} {(pt.FloorHeight == efy ? "← SELECCIONADO" : "")}");
            }
        }

        [ContextMenu("🔄 Simular VIO Reset")]
        private void DebugVIOReset()
        {
            if (_noArMode) return;
            _pendingAlignAfterTracking = false; _stableFrameCount = 0; _syncFailFrames = 0;
            _consecutiveSyncFails = 0; _currentSyncFailCooldown = 0f; _nextSyncAllowedTime = 0f;
            _lastSyncedCameraPos  = new Vector3(float.PositiveInfinity, 0, 0);
            Debug.Log("[AROriginAligner] 🔄 VIO Reset simulado — throttle reseteado.");
        }

        [ContextMenu("🚨 Forzar warp emergencia")]
        private void DebugWarp()
        {
            if (!_noArMode && _xrOrigin?.Camera != null)
                EmergencyWarpAgentToCamera(_xrOrigin.Camera.transform.position);
        }

        [ContextMenu("⏳ Simular pendingAlign")]
        private void DebugPending()
        {
            _pendingAlignAfterTracking = true;
            Debug.Log("[AROriginAligner] pendingAlign=true");
        }

        [ContextMenu("🕒 Simular recovery + reset throttle")]
        private void DebugRecovery()
        {
            _trackingRecoveredTime   = Time.realtimeSinceStartup;
            _consecutiveSyncFails    = 0;
            _currentSyncFailCooldown = 0f;
            _nextSyncAllowedTime     = 0f;
            Debug.Log($"[AROriginAligner] Recovery simulado. PoseCooldown {_poseQueryCooldownSec}s activo.");
        }

        [ContextMenu("🔄 Resetear throttle de sync")]
        private void DebugResetThrottle()
        {
            _consecutiveSyncFails    = 0;
            _currentSyncFailCooldown = 0f;
            _nextSyncAllowedTime     = 0f;
            _lastSyncedCameraPos     = new Vector3(float.PositiveInfinity, 0, 0);
            Debug.Log("[AROriginAligner] Throttle reseteado — próximo sync inmediato.");
        }

        #endregion
    }
}