// File: AROriginAligner.cs
// ✅ v8.12 — FIX_TIMESTAMP: Guarda de pose-query en los primeros 1.5s post-recovery.
//
// ============================================================================
//  CAMBIOS v8.11 → v8.12
// ============================================================================
//
//  PROBLEMA (contribuyente, no causa raíz):
//  ─────────────────────────────────────────────────────────────────────────
//  Log: "GetRecentDevicePose failed: Passed timestamp is too new (~60ms)"
//
//  Unity consulta la pose con un timestamp ~60ms por delante del último IMU
//  procesado. Esto sucede especialmente en los primeros frames tras recuperar
//  tracking, cuando el pipeline IMU-VIO aún no está sincronizado.
//
//  No causa el crash directamente, pero contribuye a los inlier failures
//  (VioFaultDetector: Insufficient inliers) durante la ventana crítica.
//
//  SOLUCIÓN FIX_TIMESTAMP:
//  ─────────────────────────────────────────────────────────────────────────
//  _trackingRecoveredTime: timestamp de cuándo se recuperó el tracking.
//
//  SyncAgentToCameraFullAR() retorna inmediatamente durante los primeros
//  _poseQueryCooldownSec tras un tracking recovery, dejando que el pipeline
//  IMU-VIO se sincronice sin timestamp conflicts.
//
//  El cooldown se aplica POR RECOVERY, no una sola vez global.
//  En Editor → _trackingRecoveredTime = 0 → la guarda nunca aplica
//  (Time.realtimeSinceStartup siempre es >> _poseQueryCooldownSec).
//
//  TODOS LOS COMPORTAMIENTOS DE v8.11 SE CONSERVAN ÍNTEGRAMENTE.

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

        [Tooltip("Tolerancia en Y para aceptar un hit de NavMesh como 'mismo piso'.\n\n" +
                 "⚠️ v8.9: Este valor ya NO se usa en GetExpectedFloorY() para filtrar\n" +
                 "el piso esperado. Se usa solo en SyncAgentToCameraFullAR() como\n" +
                 "margen BASE (al que se suma 0.5f adicionales).\n" +
                 "Default: 0.8f → margen efectivo = 1.3f")]
        [SerializeField] private float _floorSnapTolerance  = 0.8f;

        [Header("─── VIO Recovery ───────────────────────────────────────────")]
        [SerializeField] private float _vioRecoveryDelay          = 0.8f;
        [SerializeField] private bool  _freezeAgentOnTrackingLoss = true;

        [Header("─── FIX_FLICKER — Filtro de flickers de tracking ────────────")]
        [Tooltip("Duración mínima (segundos) que debe durar una pérdida de tracking\n" +
                 "para considerarse un VIO reset real.\n\n" +
                 "Pérdidas de tracking más cortas son flickers causados por CPU starvation\n" +
                 "durante la carga de NavMesh/escaleras/waypoints — se ignoran.\n\n" +
                 "Default: 0.5s — cubre el rango de flickers observados con margen.")]
        [SerializeField] private float _minTrackingLostDuration = 0.5f;

        [Header("─── FIX_TIMESTAMP — Cooldown de pose-query post-recovery ───")]
        [Tooltip("Segundos de cooldown tras recuperar tracking durante los cuales\n" +
                 "SyncAgentToCameraFullAR() no consulta poses.\n\n" +
                 "Evita 'GetRecentDevicePose: Passed timestamp is too new' que\n" +
                 "contribuye a Insufficient inliers en el VIO.\n\n" +
                 "Default: 1.5s — cubre la ventana de sincronización IMU-VIO.")]
        [SerializeField] private float _poseQueryCooldownSec = 1.5f;

        [Header("─── Estabilización post-VIO ──────────────────────────────")]
        [SerializeField] private int _stableFramesRequired = 10;

        [Header("─── Warp de emergencia ──────────────────────────────────────")]
        [SerializeField] private int _syncFailThreshold = 120;

        [Header("─── Espera de estabilidad inicial ───────────────────────────")]
        [SerializeField] private float _fullStabilityTimeout = 12f;

        [Header("─── Alineación diferida sin tracking (v8.7) ─────────────────")]
        [Tooltip("Si true, cuando WaitForFullyStable() hace timeout sin tracking,\n" +
                 "NO alinea inmediatamente. Espera a SessionTracking.\n" +
                 "Evita el bug de 'entorno se va lejos'.")]
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

        private Vector3    _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
        private NavMeshAgent _agentNavMeshAgent;

        private ARSessionState _lastARState        = ARSessionState.None;
        private bool           _trackingLost       = false;
        private Vector3        _lastStableAgentPos;
        private bool           _hasStablePos       = false;

        // v8.10 FIX_FLICKER
        private float _trackingLostTime = 0f;

        // ✅ v8.12 FIX_TIMESTAMP — Timestamp de cuándo se recuperó el tracking.
        // SyncAgentToCameraFullAR() espera _poseQueryCooldownSec antes de consultar poses.
        private float _trackingRecoveredTime = 0f;

        private int _stableFrameCount = 0;
        private int _syncFailFrames   = 0;

        // v8.7
        private bool _pendingAlignAfterTracking    = false;
        private bool _alignedWithoutTracking       = false;
        private bool _lastWaitForFullyStableResult = false;

        // ─── Propiedades ──────────────────────────────────────────────────

        public bool IsNoArMode       => _noArMode;
        public bool IsFullARMode     => !_noArMode;
        public bool IsTrackingStable => ARSession.state == ARSessionState.SessionTracking;

        /// <summary>
        /// ✅ v8.9 — Indica si AlignXROriginOnce() ya completó al menos una vez.
        /// </summary>
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
                if (arSess != null) { arSess.enabled = false; }

                var pm = FindFirstObjectByType<ARPlaneManager>();
                if (pm != null) { pm.enabled = false; }

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
                    if (arpm != null) { arpm.enabled = false; }
                }
            }
        }

        private void SetAgentActiveAndVisible(bool makeVisible)
        {
            if (_navigationAgent == null) return;
            if (!_navigationAgent.gameObject.activeSelf)
            { _navigationAgent.gameObject.SetActive(true); Log("✅ Agente activado."); }
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

            // ✅ v8.12 FIX_TIMESTAMP: registrar cuándo se recuperó el tracking.
            // SyncAgentToCameraFullAR() usará este timestamp para el cooldown de poses.
            if (nowTracking)
            {
                _trackingRecoveredTime = Time.realtimeSinceStartup;
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

        private static bool IsTrackingDegraded(ARSessionState s) => s == ARSessionState.SessionInitializing;

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

        private void OnModelLoaded(ModelLoadedEvent evt) { Log($"📦 Modelo: {evt.ModelName}"); StartCoroutine(HandleModelReady()); }

        public void NotifySessionRestored() => StartCoroutine(HandleModelReady());
        public void AlignToStartPoint()     => StartCoroutine(HandleModelReady());

        public void ForceRealign()
        {
            if (!_noArMode) { _pendingAlignAfterTracking = false; _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0); }
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
            if (_capDetector != null && !_capabilityResolved) yield return _capDetector.WaitUntilReady();

            ARCapabilityLevel level = _capDetector?.Current ?? ARCapabilityLevel.FullAR;

            if (level == ARCapabilityLevel.NoAR) { _noArMode = true; ActivateNoArMode(); }
            else
            {
                _noArMode = false; _followActive = false;
                yield return WaitForFullyStable();
                bool trackingReady = _lastWaitForFullyStableResult;

                if (!trackingReady && _deferAlignIfNoTracking)
                {
                    _pendingAlignAfterTracking = true; _alignedWithoutTracking = false;
                    Debug.LogWarning("[AROriginAligner] ⚠️ [v8.7] Alineación DIFERIDA hasta SessionTracking.");
                }
                else
                {
                    _pendingAlignAfterTracking = false; _alignedWithoutTracking = !trackingReady;
                    if (!trackingReady) Debug.LogWarning("[AROriginAligner] ⚠️ [v8.7] Alineando SIN tracking.");
                    AlignXROriginOnce();
                }
            }
        }

        private IEnumerator WaitForFullyStable()
        {
            _lastWaitForFullyStableResult = false;
            if (_noArMode) { _lastWaitForFullyStableResult = true; yield break; }

#if UNITY_EDITOR
            if (ARSession.state == ARSessionState.None ||
                ARSession.state == ARSessionState.Ready)
            {
                Debug.LogWarning("[AROriginAligner] ✅ [v8.12] Editor sin ARCore activo — Wait inmediato.");
                _lastWaitForFullyStableResult = true;
                yield break;
            }
#endif

            if (_arSessionManager != null && _arSessionManager.IsFullyStable)
            {
                Log("✅ [WaitForFullyStable] Ya estable.");
                _lastWaitForFullyStableResult = true;
                yield break;
            }

            if (ARSession.state == ARSessionState.SessionTracking)
            {
                Log("✅ [WaitForFullyStable] SessionTracking activo — 10 frames...");
                for (int i = 0; i < 10; i++) yield return null;
                _lastWaitForFullyStableResult = true;
                yield break;
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
            if (startPoint == null) { Debug.LogWarning($"[AROriginAligner] ⚠️ Sin StartPoint nivel {_targetLevel}."); return; }

            startPoint.ConfirmModelPositioned();

            if (!_initialAlignDone)
            {
                Vector3 targetPos = startPoint.transform.position + Vector3.up * _eyeHeightOffset;

                _arSessionManager?.SuppressQuickMoveDetection(frames: 5);

                _xrOrigin.MoveCameraToWorldLocation(targetPos);
                _initialAlignDone    = true;
                _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);

                bool hadTracking = ARSession.state == ARSessionState.SessionTracking;
                Log($"✅ [FullAR] XR Origin → {targetPos}. Tracking: {(hadTracking ? "✅ SÍ" : "⚠️ NO")}");

                if (!hadTracking)
                    Debug.LogWarning("[AROriginAligner] ⚠️ Alineación sin tracking — posible desalineación.");
            }
            else Log("📡 [FullAR] Alineación ya hecha — XR Origin intocado.");

            SetAgentActiveAndVisible(false);
            StopAgentMovement();
            EventBus.Instance?.Publish(new ShowMessageEvent { Message = "Navegación lista", Type = MessageType.Success, Duration = 3f });
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
                    float dist = Vector3.Distance(_navigationAgent.transform.position, _lastStableAgentPos);
                    if (dist > _fullArSyncThreshold)
                    {
                        _navigationAgent.transform.position = _lastStableAgentPos;
                        if (_agentNavMeshAgent != null && _agentNavMeshAgent.enabled && _agentNavMeshAgent.isOnNavMesh)
                        { _agentNavMeshAgent.Warp(_lastStableAgentPos); _agentNavMeshAgent.isStopped = true; }
                    }
                }
                return;
            }

            // ✅ v8.12 FIX_TIMESTAMP: No consultar poses en los primeros _poseQueryCooldownSec
            // tras recuperar tracking, para dejar que el pipeline IMU-VIO se sincronice.
            // Esto mitiga "GetRecentDevicePose: Passed timestamp is too new" que contribuye
            // a Insufficient inliers en el VIO.
            if (_poseQueryCooldownSec > 0f &&
                Time.realtimeSinceStartup - _trackingRecoveredTime < _poseQueryCooldownSec)
                return;

            if (_arSessionManager != null && _arSessionManager.IsQuickMovePaused) return;

            _stableFrameCount++;
            if (_stableFrameCount < _stableFramesRequired) return;

            Vector3 cameraPos = _xrOrigin.Camera.transform.position;
            if (Vector3.Distance(cameraPos, _lastSyncedCameraPos) < _fullArSyncThreshold) return;
            _lastSyncedCameraPos = cameraPos;

            if (_navigationAgent.IsNavigating)
            {
                if (_agentNavMeshAgent != null && _agentNavMeshAgent.enabled &&
                    _agentNavMeshAgent.isOnNavMesh && !_agentNavMeshAgent.isStopped)
                    _agentNavMeshAgent.isStopped = true;
                return;
            }

            float efy = GetExpectedFloorY(cameraPos.y);
            float hitMargin = _floorSnapTolerance + 0.5f;

            NavMeshHit bestHit = default;
            bool found = false;

            foreach (float r in new[] { 0.5f, 1.0f, 2.0f, _fullArSnapRadius })
            {
                if (!NavMesh.SamplePosition(cameraPos, out NavMeshHit hit, r, NavMesh.AllAreas)) continue;
                if (Mathf.Abs(hit.position.y - efy) <= hitMargin) { bestHit = hit; found = true; break; }
                Log($"⚠️ Hit r={r}m descartado: Y={hit.position.y:F2} vs {efy:F2} (margen={hitMargin:F2})");
            }

            if (found)
            {
                _syncFailFrames = 0;
                if (Vector3.Distance(_navigationAgent.transform.position, bestHit.position) < _fullArSyncThreshold) return;
                _navigationAgent.transform.position = bestHit.position;
                if (_agentNavMeshAgent != null && _agentNavMeshAgent.enabled && _agentNavMeshAgent.isOnNavMesh)
                { _agentNavMeshAgent.Warp(bestHit.position); _agentNavMeshAgent.isStopped = true; }
                _lastStableAgentPos = bestHit.position; _hasStablePos = true;
            }
            else
            {
                _syncFailFrames++;
                if (_syncFailThreshold > 0 && _syncFailFrames >= _syncFailThreshold)
                { _syncFailFrames = 0; EmergencyWarpAgentToCamera(cameraPos); }
            }
        }

        private void EmergencyWarpAgentToCamera(Vector3 pos)
        {
            if (!NavMesh.SamplePosition(pos, out NavMeshHit hit, _fullArSnapRadius * 2f, NavMesh.AllAreas))
            { Debug.LogWarning("[AROriginAligner] ⚠️ Warp emergencia: sin NavMesh."); return; }
            Debug.LogWarning($"[AROriginAligner] 🚨 WARP: {_navigationAgent.transform.position:F2} → {hit.position:F2}");
            _navigationAgent.transform.position = hit.position;
            if (_agentNavMeshAgent != null && _agentNavMeshAgent.enabled)
            { _agentNavMeshAgent.Warp(hit.position); _agentNavMeshAgent.isStopped = true; }
            _lastStableAgentPos = hit.position; _hasStablePos = true;
            _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
        }

        /// <summary>
        /// ✅ v8.9 FIX — Retorna SIEMPRE el FloorHeight del StartPoint más cercano.
        /// </summary>
        private float GetExpectedFloorY(float cameraY)
        {
            var pts = NavigationStartPointManager.GetAllStartPoints();
            if (pts.Count == 0) return cameraY;

            float bestY    = cameraY;
            float bestDist = float.MaxValue;

            foreach (var pt in pts)
            {
                if (pt == null) continue;
                float d = Mathf.Abs(pt.FloorHeight - cameraY);
                if (d < bestDist) { bestDist = d; bestY = pt.FloorHeight; }
            }

            return bestY;
        }

        #endregion

        #region NoAR Mode

        private void ActivateNoArMode()
        {
            if (_xrOrigin == null) { Debug.LogError("[AROriginAligner] ❌ XROrigin null."); return; }
            SetAgentActiveAndVisible(true);
            var sp = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            if (sp != null) { sp.ConfirmModelPositioned(); sp.ReteleportAgent(); }
            if (_navigationAgent != null) SnapCameraToAgent(_navigationAgent.transform.position, _navigationAgent.transform.forward);
            _followActive = true;
            EventBus.Instance?.Publish(new ShowMessageEvent { Message = "Modo visualización (sin ARCore)", Type = MessageType.Info, Duration = 4f });
        }

        private void FollowAgent()
        {
            if (_navigationAgent == null || _xrOrigin == null) return;
            Vector3 pos = _navigationAgent.transform.position;
            Vector3 fwd = _navigationAgent.transform.forward;
            Vector3 cam = pos + Vector3.up * _noArCameraHeight - fwd * _noArCameraBack;

            Quaternion rot;
            if (_noArFollowRotation && fwd != Vector3.zero)
            { Vector3 ld = _noArCameraBack > 0f ? (pos - cam).normalized : fwd; rot = Quaternion.LookRotation(ld) * Quaternion.Euler(_noArPitchAngle, 0f, 0f); }
            else rot = _xrOrigin.Camera.transform.rotation;

            float t = _noArFollowSmooth > 0f ? Time.deltaTime * _noArFollowSmooth : 1f;
            _xrOrigin.MoveCameraToWorldLocation(Vector3.Lerp(_xrOrigin.Camera.transform.position, cam, t));
            if (_noArFollowRotation)
                _xrOrigin.MatchOriginUpCameraForward(Vector3.up, Quaternion.Slerp(_xrOrigin.Camera.transform.rotation, rot, t) * Vector3.forward);
        }

        private void SnapCameraToAgent(Vector3 pos, Vector3 fwd)
        {
            _xrOrigin.MoveCameraToWorldLocation(pos + Vector3.up * _noArCameraHeight - fwd * _noArCameraBack);
            if (_noArFollowRotation && fwd != Vector3.zero) _xrOrigin.MatchOriginUpCameraForward(Vector3.up, fwd);
        }

        #endregion

        #region Debug

        private void Log(string m) { if (_logAlignment) Debug.Log($"[AROriginAligner] {m}"); }

        [ContextMenu("ℹ️ Info")]
        private void DebugInfo()
        {
            var sp  = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            float camY = _xrOrigin?.Camera != null ? _xrOrigin.Camera.transform.position.y : -999f;
            float efy  = GetExpectedFloorY(camY);
            float lostDuration = _trackingLost ? Time.realtimeSinceStartup - _trackingLostTime : 0f;
            float recoveredAgo = Time.realtimeSinceStartup - _trackingRecoveredTime;
            bool  inCooldown   = recoveredAgo < _poseQueryCooldownSec;
            Debug.Log("══════════════════════════════════════════════\n" +
                      "  AROriginAligner v8.12\n" +
                      "══════════════════════════════════════════════\n" +
                      $"  Modo:                   {(IsNoArMode ? "NoAR" : "FullAR")}\n" +
                      $"  ARSession:              {ARSession.state}\n" +
                      $"  IsFullyStable:          {(_arSessionManager?.IsFullyStable.ToString() ?? "N/A")}\n" +
                      $"  IsQuickMovePaused:      {(_arSessionManager?.IsQuickMovePaused.ToString() ?? "N/A")}\n" +
                      $"  Frames estables:        {_stableFrameCount}/{_stableFramesRequired}\n" +
                      $"  PendingAlign v8.7:      {_pendingAlignAfterTracking}\n" +
                      $"  AlinSinTracking:        {_alignedWithoutTracking}\n" +
                      $"  InitialAlignDone:       {_initialAlignDone}\n" +
                      $"  [v8.10] TrackingLost:   {_trackingLost} ({lostDuration * 1000:F0}ms)\n" +
                      $"  [v8.10] Umbral flicker: {_minTrackingLostDuration * 1000:F0}ms\n" +
                      $"  [v8.12] PoseCooldown:   {_poseQueryCooldownSec}s | " +
                          $"recoveredAgo={recoveredAgo:F2}s | inCooldown={inCooldown}\n" +
                      $"  [v8.9] FloorY:          camY={camY:F3} → efy={efy:F3} (Δ={Mathf.Abs(camY - efy):F3}m)\n" +
                      $"  [v8.9] HitMargin:       {_floorSnapTolerance + 0.5f:F2}m\n" +
                      $"  Tracking perdido:       {_trackingLost}\n" +
                      $"  Sync fail frames:       {_syncFailFrames}/{_syncFailThreshold}\n" +
                      $"  Última pos estable:     {(_hasStablePos ? _lastStableAgentPos.ToString() : "N/A")}\n" +
                      $"  StartPoint:             {(sp != null ? $"{sp.gameObject.name} @ {sp.transform.position}" : "No encontrado")}\n" +
                      $"  [v8.11] Stabilizer:     N/A — usando ARAnchorManager nativo (ModelLoadManager v2)\n" +
                      "══════════════════════════════════════════════");
        }

        [ContextMenu("🔄 Simular VIO Reset")]
        private void DebugVIOReset()
        {
            if (_noArMode) return;

            _pendingAlignAfterTracking = false;
            _stableFrameCount = 0;
            _syncFailFrames = 0;
            _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);

            Debug.Log("[AROriginAligner] 🔄 Simulación VIO — sin realineación (ARCore maneja tracking).");
        }

        [ContextMenu("🚨 Forzar warp emergencia")]
        private void DebugWarp() { if (!_noArMode && _xrOrigin?.Camera != null) EmergencyWarpAgentToCamera(_xrOrigin.Camera.transform.position); }

        [ContextMenu("⏳ Simular pendingAlign")]
        private void DebugPending() { _pendingAlignAfterTracking = true; Debug.Log("[AROriginAligner] pendingAlign=true"); }

        [ContextMenu("🕒 Simular recovery ahora")]
        private void DebugRecovery()
        {
            _trackingRecoveredTime = Time.realtimeSinceStartup;
            Debug.Log($"[AROriginAligner] _trackingRecoveredTime seteado. Cooldown activo {_poseQueryCooldownSec}s.");
        }

        #endregion
    }
}