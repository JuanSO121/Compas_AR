// File: AROriginAligner.cs
// ✅ v9.2 — Optimización de raycasts, reducción de trabajo por frame y diagnóstico de clearance
//
// ============================================================================
//  CAMBIOS v9.1 → v9.2
// ============================================================================
//
//  BUG #1 — searchOrigin.y demasiado alto causa todos los hits descartados
//  ─────────────────────────────────────────────────────────────────────────
//  SÍNTOMA (log v9.2):
//    hitY=3.43 | estimFloor=1.14–1.26 | ΔY > hitMargin → TODOS descartados pasada1
//    Solo pasa la segunda pasada con r=1m
//    → múltiples SamplePosition fallidos cada frame
//
//  CAUSA:
//    searchOrigin.y = estimFloor + hitMargin = 1.26 + 2.0 = 3.26m
//    El NavMesh correcto está a Y≈1.14m (Δ=2.12m > hitMargin=2.0m)
//    Los hits de la losa del techo a Y=3.43 son los únicos encontrados y
//    siempre se descartan. Cada frame ejecuta 4 SamplePosition fallidos
//    antes de caer a la segunda pasada con searchOriginLow.
//
//  FIX 1 — searchOrigin usa estimFloor directamente (sin sumar hitMargin)
//    El motivo original de sumar hitMargin al Y de búsqueda era evitar
//    que el origen cayera dentro de la geometría del piso. Sin embargo,
//    NavMesh.SamplePosition ya busca en una esfera de radio r alrededor
//    del punto dado — no necesita estar "sobre" el NavMesh.
//    Nuevo searchOrigin.y = estimFloor (centro) en lugar de estimFloor+hitMargin.
//    Esto permite que los radios pequeños (0.5m, 1.0m) alcancen el NavMesh
//    sin ejecutar raycasts hacia la losa del techo.
//
//  FIX 2 — Throttle por distancia mínima más agresivo
//    _fullArSyncThreshold era la única guarda de distancia. Se añade
//    _syncMinMoveDelta (default 0.08m) como umbral de movimiento mínimo
//    entre syncs consecutivos para evitar recalcular posición cada frame
//    cuando el usuario está quieto (situación más frecuente).
//
//  FIX 3 — Skip de pasada1 cuando estimFloor es claramente incorrecto
//    Si el delta entre estimFloor y el mejor StartPoint.FloorHeight
//    supera hitMargin*0.8, saltamos directamente a la pasada2 (searchOriginLow)
//    en lugar de ejecutar 4 SamplePosition sabiendo que fallarán.
//    Esto elimina el overhead en el caso "edificio con losa alta".
//
//  FIX 4 — Guard de clearance antes de Warp
//    Antes de mover el agente a bestHit.position, se verifica que
//    NavMesh.FindClosestEdge reporta una distancia > _minClearanceRequired (0.15m).
//    Si la clearance es cero o muy baja, se descarta el hit y se incrementa
//    _syncFailFrames igual que si no hubiera hit. Esto previene el ciclo
//    Stall → Clearance 0.000m → recálculo de PathController.
//
//  TODOS LOS FIXES v9.1 SE MANTIENEN INTACTOS.

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
                 "✅ v9.1: Subido de 0.9 → 1.5 para cubrir edificios donde la\n" +
                 "losa del techo está a >1.4m del NavMesh del piso correcto.\n\n" +
                 "hitMargin efectivo = _floorSnapTolerance + 0.5f\n" +
                 "v9.0: 0.9 → hitMargin=1.40m — insuficiente cuando ΔY=1.92m\n" +
                 "v9.1: 1.5 → hitMargin=2.00m — cubre ΔY hasta 2.0m\n\n" +
                 "Si sigues viendo 'Sync fallo' aumentar en 0.2 hasta que cese.\n" +
                 "Valor máximo recomendado: 2.5 (evitar falsos positivos en pisos)")]
        [SerializeField] private float _floorSnapTolerance = 1.5f;

        [Header("─── v9.2 Optimizaciones ────────────────────────────────────")]
        [Tooltip("Movimiento mínimo (m) entre syncs consecutivos.\n" +
                 "Evita recalcular posición cada frame cuando el usuario está quieto.\n" +
                 "Default: 0.08m (mayor que _fullArSyncThreshold para reducir trabajo)")]
        [SerializeField] private float _syncMinMoveDelta = 0.08f;

        [Tooltip("Clearance mínima (m) requerida en el hit de NavMesh antes de mover el agente.\n" +
                 "Un hit con clearance=0 está dentro de geometría y causa PathController stalls.\n" +
                 "Default: 0.15m")]
        [SerializeField] private float _minClearanceRequired = 0.15f;

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
                 "Default: 0.5s")]
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

        private float _trackingLostTime      = 0f;
        private float _trackingRecoveredTime = 0f;

        private int _stableFrameCount = 0;
        private int _syncFailFrames   = 0;

        private bool _pendingAlignAfterTracking    = false;
        private bool _alignedWithoutTracking       = false;
        private bool _lastWaitForFullyStableResult = false;

        // v9.0 FIX B: throttle exponencial
        private float _nextSyncAllowedTime     = 0f;
        private float _currentSyncFailCooldown = 0f;
        private int   _consecutiveSyncFails    = 0;

        // v9.2: posición de cámara en el último sync exitoso (para _syncMinMoveDelta)
        private Vector3 _lastSuccessfulSyncCamPos = new Vector3(float.PositiveInfinity, 0, 0);

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
                _consecutiveSyncFails    = 0;
                _currentSyncFailCooldown = 0f;
                _nextSyncAllowedTime     = 0f;
                // v9.2: resetear también el guard de movimiento mínimo
                _lastSuccessfulSyncCamPos = new Vector3(float.PositiveInfinity, 0, 0);
            }

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
                _lastSuccessfulSyncCamPos  = new Vector3(float.PositiveInfinity, 0, 0);
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
                Vector3 targetPos = startPoint.transform.position + Vector3.up * _eyeHeightOffset;

                _arSessionManager?.SuppressQuickMoveDetection(frames: 5);
                _xrOrigin.MoveCameraToWorldLocation(targetPos);

                _initialAlignDone    = true;
                _lastSyncedCameraPos        = new Vector3(float.PositiveInfinity, 0, 0);
                _lastSuccessfulSyncCamPos   = new Vector3(float.PositiveInfinity, 0, 0);

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

        private void SyncAgentToCameraFullAR()
        {
            if (_navigationAgent == null || _xrOrigin?.Camera == null) return;
            if (!_navigationAgent.gameObject.activeSelf) return;

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

            if (_poseQueryCooldownSec > 0f &&
                Time.realtimeSinceStartup - _trackingRecoveredTime < _poseQueryCooldownSec)
                return;

            if (_arSessionManager != null && _arSessionManager.IsQuickMovePaused) return;

            _stableFrameCount++;
            if (_stableFrameCount < _stableFramesRequired) return;

            if (_currentSyncFailCooldown > 0f && Time.unscaledTime < _nextSyncAllowedTime)
                return;

            Vector3 cameraPos = _xrOrigin.Camera.transform.position;

            // v9.2 FIX 2: guard de movimiento mínimo más agresivo
            // _fullArSyncThreshold controla si el agente necesita moverse (post-hit),
            // pero _syncMinMoveDelta controla si vale la pena hacer el raycast en absoluto.
            if (Vector3.Distance(cameraPos, _lastSuccessfulSyncCamPos) < _syncMinMoveDelta)
                return;

            // Mantener el guard original de threshold como segunda capa
            if (Vector3.Distance(cameraPos, _lastSyncedCameraPos) < _fullArSyncThreshold) return;
            _lastSyncedCameraPos = cameraPos;

            if (_navigationAgent.IsNavigating)
            {
                if (_agentNavMeshAgent?.enabled == true &&
                    _agentNavMeshAgent.isOnNavMesh &&
                    !_agentNavMeshAgent.isStopped)
                    _agentNavMeshAgent.isStopped = true;
                return;
            }

            float estimatedFloorY = GetExpectedFloorY(cameraPos.y);
            float hitMargin       = _floorSnapTolerance + 0.5f;

            NavMeshHit bestHit = default;
            bool       found   = false;

            // v9.2 FIX 3: detectar si pasada1 va a fallar antes de ejecutarla
            // Si la diferencia entre estimFloor y el piso más cercano del StartPoint
            // ya supera hitMargin*0.8, los hits de pasada1 (desde arriba) van a ser
            // la losa del techo. Saltar directamente a pasada2 en ese caso.
            bool skipPasada1 = ShouldSkipPasada1(estimatedFloorY, hitMargin);

            if (!skipPasada1)
            {
                // Pasada 1 — v9.2 FIX 1: searchOrigin.y = estimFloor (sin sumar hitMargin)
                // NavMesh.SamplePosition busca en esfera de radio r — no necesita estar "sobre"
                // el NavMesh. Sumar hitMargin provocaba que el origen quedara cerca de la losa
                // del techo, causando que todos los hits apuntaran a ella y fueran descartados.
                Vector3 searchOrigin = new Vector3(cameraPos.x, estimatedFloorY, cameraPos.z);

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

                    if (_logAlignment)
                        Debug.Log($"[AROriginAligner] Hit p1 r={r}m descartado: " +
                                  $"hitY={hit.position.y:F2} estimFloor={estimatedFloorY:F2} " +
                                  $"ΔY={deltaY:F2} margen={hitMargin:F2}");
                }
            }
            else
            {
                if (_logAlignment)
                    Debug.Log($"[AROriginAligner] ⏭ Pasada1 omitida — estimFloor={estimatedFloorY:F2} " +
                              $"demasiado lejos del StartPoint más cercano (hitMargin={hitMargin:F2})");
            }

            // Pasada 2 si la primera falló (o fue omitida)
            // Cubre casos donde el NavMesh está por debajo del suelo estimado.
            // v9.2: también actúa como pasada principal cuando pasada1 se omite.
            if (!found)
            {
                Vector3 searchOriginLow = new Vector3(
                    cameraPos.x,
                    estimatedFloorY - hitMargin * 0.5f,
                    cameraPos.z);

                foreach (float r in new[] { 1.0f, 2.0f, _fullArSnapRadius })
                {
                    if (!NavMesh.SamplePosition(searchOriginLow, out NavMeshHit hit, r, NavMesh.AllAreas))
                        continue;

                    float deltaY = Mathf.Abs(hit.position.y - estimatedFloorY);
                    if (deltaY <= hitMargin)
                    {
                        bestHit = hit;
                        found   = true;
                        if (_logAlignment)
                            Log($"Hit pasada2 r={r}m: hitY={hit.position.y:F2} ΔY={deltaY:F2} ✅");
                        break;
                    }
                }
            }

            if (found)
            {
                // v9.2 FIX 4: verificar clearance antes de mover el agente
                // Un hit con clearance=0 está dentro de geometría → PathController stall
                if (!HasSufficientClearance(bestHit.position))
                {
                    if (_logAlignment)
                        Debug.LogWarning($"[AROriginAligner] ⚠️ Hit descartado por clearance insuficiente: " +
                                         $"pos={bestHit.position:F2} (< {_minClearanceRequired:F3}m)");
                    // Tratar como fallo para activar throttle y evitar loop
                    RecordSyncFail(cameraPos, estimatedFloorY, new Vector3(cameraPos.x, estimatedFloorY, cameraPos.z));
                    return;
                }

                _syncFailFrames          = 0;
                _consecutiveSyncFails    = 0;
                _currentSyncFailCooldown = 0f;
                _nextSyncAllowedTime     = 0f;
                _lastSuccessfulSyncCamPos = cameraPos; // v9.2: actualizar guard de movimiento

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
                RecordSyncFail(cameraPos, estimatedFloorY,
                    new Vector3(cameraPos.x, estimatedFloorY, cameraPos.z));
            }
        }

        // v9.2: extrae la lógica de fallo a método para reutilización
        private void RecordSyncFail(Vector3 cameraPos, float estimatedFloorY, Vector3 searchOrigin)
        {
            _syncFailFrames++;
            _consecutiveSyncFails++;

            _currentSyncFailCooldown = Mathf.Min(
                _syncFailCooldown * Mathf.Pow(2f, _consecutiveSyncFails - 1),
                _syncFailCooldownMax);
            _nextSyncAllowedTime = Time.unscaledTime + _currentSyncFailCooldown;

            Debug.LogWarning(
                $"[AROriginAligner] ! Sync fallo #{_consecutiveSyncFails} — " +
                $"camY={cameraPos.y:F2} estimFloor={estimatedFloorY:F2} " +
                $"searchOrigin={searchOrigin:F2} " +
                $"cooldown={_currentSyncFailCooldown:F1}s");

            // FIX 2 (v9.1): diagnóstico automático al 5º fallo
            if (_consecutiveSyncFails == 5)
                LogStartPointDiagnostic(estimatedFloorY, _floorSnapTolerance + 0.5f);

            if (_syncFailThreshold > 0 && _syncFailFrames >= _syncFailThreshold)
            {
                _syncFailFrames = 0; _consecutiveSyncFails = 0;
                EmergencyWarpAgentToCamera(cameraPos);
            }
        }

        // v9.2 FIX 3: determina si la pasada1 va a fallar basado en StartPoints
        private bool ShouldSkipPasada1(float estimatedFloorY, float hitMargin)
        {
            var pts = NavigationStartPointManager.GetAllStartPoints();
            if (pts.Count == 0) return false;

            float bestDist = float.MaxValue;
            foreach (var pt in pts)
            {
                if (pt == null) continue;
                float d = Mathf.Abs(pt.FloorHeight - estimatedFloorY);
                if (d < bestDist) bestDist = d;
            }

            // Si el StartPoint más cercano está a más de 80% del hitMargin,
            // es probable que pasada1 encuentre la losa del techo en lugar del piso.
            return bestDist > hitMargin * 0.8f;
        }

        // v9.2 FIX 4: verificar que el hit tiene espacio libre suficiente
        private bool HasSufficientClearance(Vector3 position)
        {
            if (_minClearanceRequired <= 0f) return true;

            if (!NavMesh.FindClosestEdge(position, out NavMeshHit edgeHit, NavMesh.AllAreas))
                return true; // No encontró borde → asumir que hay espacio

            return edgeHit.distance >= _minClearanceRequired;
        }

        // FIX 2 (v9.1): diagnóstico automático de StartPoints para ajuste de Inspector
        private void LogStartPointDiagnostic(float estimatedFloorY, float hitMargin)
        {
            var pts = NavigationStartPointManager.GetAllStartPoints();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[AROriginAligner] ⚠️ DIAGNÓSTICO SYNC — 5 fallos consecutivos:");
            sb.AppendLine($"  estimatedFloorY={estimatedFloorY:F3} | hitMargin={hitMargin:F3}");
            sb.AppendLine($"  _floorSnapTolerance={_floorSnapTolerance} | _eyeHeightOffset={_eyeHeightOffset}");
            sb.AppendLine($"  Para que el sync funcione: ΔY entre FloorHeight y estimatedFloorY ≤ hitMargin");
            sb.AppendLine("  StartPoints disponibles:");

            foreach (var pt in pts)
            {
                if (pt == null) continue;
                float delta = Mathf.Abs(pt.FloorHeight - estimatedFloorY);
                bool  ok    = delta <= hitMargin;
                sb.AppendLine($"    Level{pt.Level}: FloorH={pt.FloorHeight:F3} | " +
                              $"Δ={delta:F3} | {(ok ? "✅ EN RANGO" : $"❌ FUERA — necesita _floorSnapTolerance ≥ {delta - 0.5f:F2}")}");
            }

            sb.AppendLine($"  ACCIÓN: en Inspector, subir _floorSnapTolerance hasta que todos sean ✅");
            Debug.LogWarning(sb.ToString());
        }

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

        private void EmergencyWarpAgentToCamera(Vector3 cameraPos)
        {
            float   estimatedGroundY = cameraPos.y - _eyeHeightOffset;
            Vector3 searchPos        = new Vector3(cameraPos.x, estimatedGroundY, cameraPos.z);

            float[] radii = { 1f, 2f, _fullArSnapRadius, _fullArSnapRadius * 2f, 10f };
            foreach (float r in radii)
            {
                if (!NavMesh.SamplePosition(searchPos, out NavMeshHit hit, r, NavMesh.AllAreas))
                    continue;

                // v9.2: verificar clearance también en warp de emergencia
                if (!HasSufficientClearance(hit.position))
                {
                    if (_logAlignment)
                        Debug.LogWarning($"[AROriginAligner] 🚨 WARP r={r}m: clearance insuficiente, buscando alternativa...");
                    continue;
                }

                Debug.LogWarning(
                    $"[AROriginAligner] 🚨 WARP: {_navigationAgent.transform.position:F2} " +
                    $"→ {hit.position:F2} (r={r}m)");

                _navigationAgent.transform.position = hit.position;
                if (_agentNavMeshAgent?.enabled == true)
                { _agentNavMeshAgent.Warp(hit.position); _agentNavMeshAgent.isStopped = true; }

                _lastStableAgentPos       = hit.position;
                _hasStablePos             = true;
                _lastSyncedCameraPos      = new Vector3(float.PositiveInfinity, 0, 0);
                _lastSuccessfulSyncCamPos = new Vector3(float.PositiveInfinity, 0, 0);
                _consecutiveSyncFails     = 0;
                _currentSyncFailCooldown  = 0f;
                _nextSyncAllowedTime      = 0f;
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

        [ContextMenu("ℹ️ Info v9.2")]
        private void DebugInfo()
        {
            float camY        = _xrOrigin?.Camera != null ? _xrOrigin.Camera.transform.position.y : -999f;
            float estimGround = camY - _eyeHeightOffset;
            float efy         = GetExpectedFloorY(camY);
            float hitMargin   = _floorSnapTolerance + 0.5f;
            float lostDur     = _trackingLost ? Time.realtimeSinceStartup - _trackingLostTime : 0f;
            float recovAgo    = Time.realtimeSinceStartup - _trackingRecoveredTime;
            float syncCoolRem = Mathf.Max(0f, _nextSyncAllowedTime - Time.unscaledTime);
            var   sp          = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            bool  skip1       = ShouldSkipPasada1(efy, hitMargin);

            Debug.Log(
                "══════════════════════════════════════════════\n" +
                "  AROriginAligner v9.2\n" +
                "══════════════════════════════════════════════\n" +
                $"  Modo:                {(IsNoArMode ? "NoAR" : "FullAR")}\n" +
                $"  ARSession:           {ARSession.state}\n" +
                $"  InitialAlignDone:    {_initialAlignDone}\n" +
                $"  TrackingLost:        {_trackingLost} ({lostDur * 1000:F0}ms)\n" +
                $"  PoseCooldown:        {_poseQueryCooldownSec}s | recovAgo={recovAgo:F2}s\n" +
                $"  camY:                {camY:F3}\n" +
                $"  estimGround:         {estimGround:F3}  (camY - eyeOffset={_eyeHeightOffset})\n" +
                $"  expectedFloorY:      {efy:F3}\n" +
                $"  _floorSnapTolerance: {_floorSnapTolerance} → hitMargin={hitMargin:F2}m\n" +
                $"  ShouldSkipPasada1:   {skip1}\n" +
                $"  SyncFails:           #{_consecutiveSyncFails} | cooldown={_currentSyncFailCooldown:F1}s\n" +
                $"  _syncMinMoveDelta:   {_syncMinMoveDelta}m\n" +
                $"  _minClearanceReq:   {_minClearanceRequired}m\n" +
                $"  StartPoint:          {(sp != null ? $"{sp.gameObject.name} @ {sp.transform.position:F2}" : "N/A")}\n" +
                "══════════════════════════════════════════════");

            foreach (var pt in NavigationStartPointManager.GetAllStartPoints())
            {
                if (pt == null) continue;
                float dGround = Mathf.Abs(pt.FloorHeight - estimGround);
                bool  inRange = dGround <= hitMargin;
                Debug.Log($"  Level{pt.Level}: FloorH={pt.FloorHeight:F3} | " +
                          $"Δground={dGround:F3} | {(inRange ? "✅ EN RANGO" : $"❌ — sube _floorSnapTolerance a ≥{dGround - 0.5f:F2}")} " +
                          $"{(pt.FloorHeight == efy ? "← SELECCIONADO" : "")}");
            }
        }

        [ContextMenu("🔄 Simular VIO Reset")]
        private void DebugVIOReset()
        {
            if (_noArMode) return;
            _pendingAlignAfterTracking = false; _stableFrameCount = 0; _syncFailFrames = 0;
            _consecutiveSyncFails = 0; _currentSyncFailCooldown = 0f; _nextSyncAllowedTime = 0f;
            _lastSyncedCameraPos        = new Vector3(float.PositiveInfinity, 0, 0);
            _lastSuccessfulSyncCamPos   = new Vector3(float.PositiveInfinity, 0, 0);
            Debug.Log("[AROriginAligner] 🔄 VIO Reset simulado — throttle reseteado.");
        }

        [ContextMenu("🚨 Forzar warp emergencia")]
        private void DebugWarp()
        {
            if (!_noArMode && _xrOrigin?.Camera != null)
                EmergencyWarpAgentToCamera(_xrOrigin.Camera.transform.position);
        }

        [ContextMenu("🔄 Resetear throttle de sync")]
        private void DebugResetThrottle()
        {
            _consecutiveSyncFails       = 0;
            _currentSyncFailCooldown    = 0f;
            _nextSyncAllowedTime        = 0f;
            _lastSyncedCameraPos        = new Vector3(float.PositiveInfinity, 0, 0);
            _lastSuccessfulSyncCamPos   = new Vector3(float.PositiveInfinity, 0, 0);
            Debug.Log("[AROriginAligner] Throttle reseteado — próximo sync inmediato.");
        }

        [ContextMenu("🔬 Test clearance posición actual del agente")]
        private void DebugTestClearance()
        {
            if (_navigationAgent == null) return;
            var pos = _navigationAgent.transform.position;
            bool ok = HasSufficientClearance(pos);
            NavMesh.FindClosestEdge(pos, out NavMeshHit eh, NavMesh.AllAreas);
            Debug.Log($"[AROriginAligner] Clearance en agente: {eh.distance:F4}m " +
                      $"(requerido: {_minClearanceRequired:F3}m) → {(ok ? "✅ OK" : "❌ INSUFICIENTE")}");
        }

        #endregion
    }
}