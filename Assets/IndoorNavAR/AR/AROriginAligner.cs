// File: AROriginAligner.cs
// ✅ v8.6 — Integración con ARSessionManager.IsFullyStable + IsQuickMovePaused.
//
// ============================================================================
//  CAMBIOS v8.5 → v8.6
// ============================================================================
//
//  FIX 1 — Esperar IsFullyStable antes de AlignXROriginOnce():
//    CAUSA del bug de desalineación al inicio:
//      HandleModelReady() llamaba AlignXROriginOnce() inmediatamente tras
//      cargar el modelo, sin verificar que ARCore ya había estabilizado
//      el world origin.
//      ARCore hace un shift del world origin en los primeros frames de
//      SessionTracking — si el modelo se posiciona antes de ese shift,
//      queda desalineado.
//    FIX: WaitForFullyStable() espera ARSessionManager.IsFullyStable antes
//      de llamar AlignXROriginOnce(). Con _initialStableFrames=30 en
//      ARSessionManager, esto garantiza que el world origin está estable
//      antes de posicionar el modelo.
//
//  FIX 2 — Respetar IsQuickMovePaused en SyncAgentToCameraFullAR():
//    CAUSA del bug de salto del agente al mover rápido:
//      SyncAgentToCameraFullAR() sincronizaba el agente incluso durante
//      el período de inestabilidad post-movimiento brusco, usando poses
//      de cámara temporalmente incorrectas mientras ARCore se re-estabilizaba.
//    FIX: Si ARSessionManager.IsQuickMovePaused == true, se omite la
//      sincronización ese frame. El agente mantiene su última posición
//      estable hasta que ARCore se re-estabilice.
//
// ============================================================================
//  CAMBIOS v8.4 → v8.5 (conservados íntegramente)
// ============================================================================
//  [Ver versión anterior para el historial completo de cambios]

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
        [SerializeField] private XROrigin _xrOrigin;
        [SerializeField] private NavigationAgent _navigationAgent;

        [Header("─── Configuración ─────────────────────────────────────────")]
        [Tooltip("Nivel del modelo al que buscar el StartPoint (normalmente 0).")]
        [SerializeField] private int _targetLevel = 0;

        [Tooltip("Altura adicional sobre el StartPoint para la alineación inicial del XR Origin (FullAR).")]
        [SerializeField] private float _eyeHeightOffset = 1.6f;

        [Tooltip("Frames de espera después de que el modelo se carga antes de inicializar.")]
        [SerializeField] private int _delayFrames = 2;

        [Header("─── Modo NoAR — Seguimiento del agente ─────────────────────")]
        [SerializeField] private float _noArCameraHeight = 1.65f;
        [SerializeField] private float _noArCameraBack = 0.0f;
        [SerializeField] private float _noArPitchAngle = 0.0f;
        [SerializeField] private float _noArFollowSmooth = 8f;
        [SerializeField] private bool _noArFollowRotation = true;

        [Header("─── Modo FullAR — Sincronización agente con cámara ─────────")]
        [Tooltip("Radio máximo de búsqueda en el NavMesh.")]
        [SerializeField] private float _fullArSnapRadius = 3.0f;

        [Tooltip("Distancia mínima de movimiento de la cámara para re-sincronizar el agente (m).")]
        [SerializeField] private float _fullArSyncThreshold = 0.05f;

        [Tooltip("Tolerancia vertical (m) entre el hit del NavMesh y el piso esperado.")]
        [SerializeField] private float _floorSnapTolerance = 0.8f;

        [Header("─── VIO Recovery (v8) ──────────────────────────────────────")]
        [Tooltip("Segundos de espera tras recuperar tracking antes de realinear.")]
        [SerializeField] private float _vioRecoveryDelay = 0.8f;

        [Tooltip("Si true, congela la posición del agente durante pérdida de tracking.")]
        [SerializeField] private bool _freezeAgentOnTrackingLoss = true;

        [Header("─── Estabilización post-VIO (v8.3) ───────────────────────")]
        [Tooltip("Frames consecutivos de SessionTracking requeridos antes de " +
                 "reanudar SyncAgentToCameraFullAR() tras un VIO fault. Default 10.")]
        [SerializeField] private int _stableFramesRequired = 10;

        [Header("─── Warp de emergencia (v8.5) ──────────────────────────────")]
        [Tooltip("Frames sin hit válido antes de warp de emergencia. 0 = desactivado.")]
        [SerializeField] private int _syncFailThreshold = 120;

        [Header("─── Espera de estabilidad inicial (v8.6) ───────────────────")]
        [Tooltip("Segundos máximos esperando IsFullyStable antes de alinear el modelo. " +
                 "Fallback para evitar bloqueo si ARSessionManager no está en escena.")]
        [SerializeField] private float _fullStabilityTimeout = 12f;

        [Header("─── Debug ──────────────────────────────────────────────────")]
        [SerializeField] private bool _logAlignment = true;

        // ─── Estado interno ────────────────────────────────────────────────

        private bool _noArMode = false;
        private bool _followActive = false;
        private bool _capabilityResolved = false;
        private bool _initialAlignDone = false;
        private ARCapabilityDetector _capDetector;
        private ARSessionManager _arSessionManager; // ✅ v8.6

        private Vector3 _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
        private NavMeshAgent _agentNavMeshAgent;

        // ─── v8: Estado de tracking VIO ───────────────────────────────────
        private ARSessionState _lastARState = ARSessionState.None;
        private bool _trackingLost = false;
        private Vector3 _lastStableAgentPos;
        private bool _hasStablePos = false;

        // ─── v8.3: Contador de frames estables tras VIO recovery ──────────
        private int _stableFrameCount = 0;

        // ─── v8.5: Contador de frames sin hit válido ──────────────────────
        private int _syncFailFrames = 0;

        // ─── Propiedades públicas ──────────────────────────────────────────

        public bool IsNoArMode => _noArMode;
        public bool IsFullARMode => !_noArMode;
        public bool IsTrackingStable => ARSession.state == ARSessionState.SessionTracking;

        #region Unity Lifecycle

        private void Awake()
        {
            FindComponents();

            if (_capDetector != null && _capDetector.IsReady &&
                _capDetector.Current != ARCapabilityLevel.NoAR)
            {
                if (_navigationAgent != null)
                {
                    var pc = _navigationAgent.GetComponent<NavigationPathController>();
                    if (pc != null)
                    {
                        pc.SetFullARMode(true);
                        Log("✅ [Awake] PathController.SetFullARMode(true)");
                    }
                }
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
            if (_followActive && _noArMode)
            {
                FollowAgent();
                return;
            }

            if (!_noArMode && _initialAlignDone)
                SyncAgentToCameraFullAR();
        }

        #endregion

        #region Component Discovery

        private void FindComponents()
        {
            if (_xrOrigin == null)
                _xrOrigin = FindFirstObjectByType<XROrigin>();
            if (_navigationAgent == null)
                _navigationAgent = FindFirstObjectByType<NavigationAgent>();
            if (_navigationAgent != null)
                _agentNavMeshAgent = _navigationAgent.GetComponent<NavMeshAgent>();

            _capDetector = ARCapabilityDetector.Instance
                        ?? FindFirstObjectByType<ARCapabilityDetector>();

            // ✅ v8.6: Buscar ARSessionManager para consultar IsFullyStable
            _arSessionManager = FindFirstObjectByType<ARSessionManager>();

            if (_xrOrigin == null)
                Debug.LogWarning("[AROriginAligner] ⚠️ XROrigin no encontrado.");
            if (_capDetector == null)
                Debug.LogWarning("[AROriginAligner] ⚠️ ARCapabilityDetector no encontrado.");
            if (_arSessionManager == null)
                Debug.LogWarning("[AROriginAligner] ⚠️ ARSessionManager no encontrado — " +
                                 "la espera de estabilidad inicial no estará disponible.");
        }

        #endregion

        #region Capability Initialization

        private IEnumerator InitializeCapabilityRoutine()
        {
            yield return null;

            if (_capDetector != null)
                yield return _capDetector.WaitUntilReady();

            ARCapabilityLevel level = _capDetector != null
                ? _capDetector.Current
                : ARCapabilityLevel.FullAR;

            _capabilityResolved = true;
            Log($"📡 [Start] Capacidad AR: {level}");

            if (level == ARCapabilityLevel.NoAR)
            {
                _noArMode = true;
                Log("📵 Modo NoAR activo.");

                if (_navigationAgent != null)
                {
                    var pc = _navigationAgent.GetComponent<NavigationPathController>();
                    pc?.SetFullARMode(false);
                }

                var arSession = FindFirstObjectByType<ARSession>();
                if (arSession != null) { arSession.enabled = false; Log("📵 ARSession desactivada."); }

                var planeManager = FindFirstObjectByType<ARPlaneManager>();
                if (planeManager != null) { planeManager.enabled = false; Log("📵 ARPlaneManager desactivado."); }

                SetAgentActiveAndVisible(makeVisible: true);

                var modelMgr = FindFirstObjectByType<IndoorNavAR.Core.Managers.ModelLoadManager>();
                if (modelMgr != null && modelMgr.IsModelLoaded)
                    ActivateNoArMode();
            }
            else
            {
                _noArMode = false;
                _followActive = false;

                Log("📡 Modo FullAR activo.");

                if (_navigationAgent != null)
                {
                    var pc = _navigationAgent.GetComponent<NavigationPathController>();
                    if (pc != null)
                    {
                        pc.SetFullARMode(true);
                        Log("✅ PathController.SetFullARMode(true)");
                    }
                }

                SetAgentActiveAndVisible(makeVisible: false);
                StopAgentMovement();

                if (level == ARCapabilityLevel.ARWithoutPlanes)
                {
                    var pm = FindFirstObjectByType<ARPlaneManager>();
                    if (pm != null) { pm.enabled = false; Log("📡 ARPlaneManager desactivado."); }
                }
            }
        }

        private void SetAgentActiveAndVisible(bool makeVisible)
        {
            if (_navigationAgent == null) return;

            if (!_navigationAgent.gameObject.activeSelf)
            {
                _navigationAgent.gameObject.SetActive(true);
                Log($"✅ Agente activado.");
            }

            foreach (var r in _navigationAgent.GetComponentsInChildren<Renderer>(true))
                r.enabled = makeVisible;
        }

        private void StopAgentMovement()
        {
            if (_agentNavMeshAgent == null) return;
            if (_agentNavMeshAgent.enabled && _agentNavMeshAgent.isOnNavMesh)
            {
                _agentNavMeshAgent.isStopped = true;
                _agentNavMeshAgent.ResetPath();
            }
        }

        #endregion

        #region v8 — VIO Reset Detection

        private void OnARSessionStateChanged(ARSessionStateChangedEventArgs args)
        {
            ARSessionState newState = args.state;

            bool wasLost = IsTrackingDegraded(_lastARState);
            bool nowTracking = newState == ARSessionState.SessionTracking;
            bool nowLost = IsTrackingDegraded(newState);

            Log($"📡 ARSession: {_lastARState} → {newState}");
            _lastARState = newState;

            NotifyFlutterTrackingState(isStable: nowTracking, stateStr: newState.ToString());

            if (_noArMode) return;

            if (nowLost && !_trackingLost)
            {
                _trackingLost = true;
                _stableFrameCount = 0;
                if (_navigationAgent != null)
                {
                    _lastStableAgentPos = _navigationAgent.transform.position;
                    _hasStablePos = true;
                    Log($"⚠️ Tracking perdido — pos estable guardada: {_lastStableAgentPos:F2}");
                }
            }

            if (wasLost && nowTracking && _initialAlignDone)
            {
                _trackingLost = false;
                _initialAlignDone = false;
                _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);

                Log("🔄 VIO reset — programando realineación...");
                ARWorldOriginStabilizer.Instance?.DisableStabilization();
                StartCoroutine(RealignAfterVIORecovery());
            }
            else if (nowTracking)
            {
                _trackingLost = false;
            }
        }

        private static bool IsTrackingDegraded(ARSessionState state)
            => state == ARSessionState.SessionInitializing;

        private IEnumerator RealignAfterVIORecovery()
        {
            yield return new WaitForSeconds(_vioRecoveryDelay);
            if (_noArMode) yield break;

            Log($"🔄 Realineando tras VIO recovery...");
            AlignXROriginOnce();

            yield return new WaitForSeconds(0.5f);
            ARWorldOriginStabilizer.Instance?.ScheduleAnchorRecapture();
            Log("🔄 ARWorldOriginStabilizer programado para recapturar anchor.");
        }

        private void NotifyFlutterTrackingState(bool isStable, string stateStr)
        {
            if (ARSession.state == ARSessionState.Ready) return;

            var api = VoiceCommandAPI.Instance;
            if (api == null) return;

            string reason = isStable ? "None" : ARSession.notTrackingReason.ToString();
            string enrichedState = isStable ? stateStr : $"{stateStr}|{reason}";
            api.NotifyTrackingState(isStable, enrichedState);
        }

        #endregion

        #region Event Handlers

        private void OnModelLoaded(ModelLoadedEvent evt)
        {
            Log($"📦 Modelo cargado: {evt.ModelName}");
            StartCoroutine(HandleModelReady());
        }

        #endregion

        #region Public API

        public void NotifySessionRestored() => StartCoroutine(HandleModelReady());
        public void AlignToStartPoint() => StartCoroutine(HandleModelReady());

        public void ForceRealign()
        {
            if (!_noArMode)
            {
                _initialAlignDone = false;
                _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
            }
            StartCoroutine(HandleModelReady());
        }

        public void ForceSnapAgentToCamera()
        {
            if (_noArMode || _xrOrigin?.Camera == null || _navigationAgent == null) return;
            Vector3 cameraPos = _xrOrigin.Camera.transform.position;
            Log("🔧 ForceSnapAgentToCamera() llamado externamente.");
            EmergencyWarpAgentToCamera(cameraPos);
        }

        #endregion

        #region Core Logic

        private IEnumerator HandleModelReady()
        {
            for (int i = 0; i < _delayFrames; i++)
                yield return null;

            if (_capDetector != null && !_capabilityResolved)
                yield return _capDetector.WaitUntilReady();

            ARCapabilityLevel level = _capDetector != null
                ? _capDetector.Current
                : ARCapabilityLevel.FullAR;

            if (level == ARCapabilityLevel.NoAR)
            {
                _noArMode = true;
                ActivateNoArMode();
            }
            else
            {
                _noArMode = false;
                _followActive = false;

                // ✅ v8.6 FIX 1: Esperar IsFullyStable antes de alinear el modelo.
                // Evita desalineación por world origin shift de ARCore al inicio.
                yield return WaitForFullyStable();

                AlignXROriginOnce();
            }
        }

        /// <summary>
        /// ✅ v8.6 FIX 1: Espera hasta que ARSessionManager.IsFullyStable sea true.
        ///
        /// IsFullyStable = SessionTracking + _initialStableFrames frames consecutivos.
        /// Esto garantiza que el world origin de ARCore está estable antes de
        /// posicionar el modelo con AlignXROriginOnce().
        ///
        /// Si ARSessionManager no está disponible, esperamos solo SessionTracking
        /// básico para no bloquear indefinidamente.
        ///
        /// Con _fullStabilityTimeout como fallback de seguridad.
        /// </summary>
        private IEnumerator WaitForFullyStable()
        {
            if (_noArMode) yield break;

            float elapsed = 0f;

            // Si ya está estable, salir inmediatamente
            if (_arSessionManager != null && _arSessionManager.IsFullyStable)
            {
                Log("✅ [WaitForFullyStable] Ya estable — sin espera.");
                yield break;
            }

            Log($"⏳ [WaitForFullyStable] Esperando IsFullyStable " +
                $"(timeout={_fullStabilityTimeout}s)...");

            while (elapsed < _fullStabilityTimeout)
            {
                yield return null;
                elapsed += Time.deltaTime;

                bool isStable = _arSessionManager != null
                    ? _arSessionManager.IsFullyStable
                    : ARSession.state == ARSessionState.SessionTracking;

                if (isStable)
                {
                    Log($"✅ [WaitForFullyStable] Estabilidad alcanzada en {elapsed:F1}s.");
                    yield break;
                }
            }

            Debug.LogWarning($"[AROriginAligner] ⚠️ WaitForFullyStable: timeout {_fullStabilityTimeout}s " +
                             $"— alineando igualmente. Estado: {ARSession.state}");
        }

        private void AlignXROriginOnce()
        {
            if (_xrOrigin == null)
            {
                Debug.LogError("[AROriginAligner] ❌ XROrigin es null.");
                return;
            }

            var startPoint = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            if (startPoint == null)
            {
                Debug.LogWarning($"[AROriginAligner] ⚠️ No hay StartPoint para nivel {_targetLevel}.");
                return;
            }

            startPoint.ConfirmModelPositioned();

            if (!_initialAlignDone)
            {
                Vector3 targetPos = startPoint.transform.position + Vector3.up * _eyeHeightOffset;
                _xrOrigin.MoveCameraToWorldLocation(targetPos);
                _initialAlignDone = true;
                _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
                Log($"✅ [FullAR] XR Origin → {targetPos}.");
            }
            else
            {
                Log("📡 [FullAR] Alineación ya realizada — XR Origin intocado.");
            }

            SetAgentActiveAndVisible(makeVisible: false);
            StopAgentMovement();

            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message = "Navegación lista",
                Type = MessageType.Success,
                Duration = 3f
            });
        }

        #endregion

        #region FullAR — Sincronización agente con cámara XR

        /// <summary>
        /// ✅ v8.6: Agrega guard IsQuickMovePaused para evitar sincronización
        /// durante inestabilidad post-movimiento brusco.
        ///
        /// Si ARSessionManager detectó movimiento rápido de cámara, ARCore puede
        /// estar haciendo micro-resets del world origin. En ese período, la pose
        /// de la cámara es temporalmente incorrecta y sincronizar el agente
        /// produciría saltos de posición.
        /// </summary>
        private void SyncAgentToCameraFullAR()
        {
            if (_navigationAgent == null || _xrOrigin?.Camera == null) return;
            if (!_navigationAgent.gameObject.activeSelf) return;

            // ✅ v8 FIX #2 + v8.3 FIX B: Solo sincronizar con tracking estable
            if (ARSession.state != ARSessionState.SessionTracking)
            {
                _stableFrameCount = 0;
                _syncFailFrames = 0;

                if (_freezeAgentOnTrackingLoss && _hasStablePos && _navigationAgent != null)
                {
                    float dist = Vector3.Distance(
                        _navigationAgent.transform.position, _lastStableAgentPos);

                    if (dist > _fullArSyncThreshold)
                    {
                        _navigationAgent.transform.position = _lastStableAgentPos;
                        if (_agentNavMeshAgent != null && _agentNavMeshAgent.enabled
                            && _agentNavMeshAgent.isOnNavMesh)
                        {
                            _agentNavMeshAgent.Warp(_lastStableAgentPos);
                            _agentNavMeshAgent.isStopped = true;
                        }
                    }
                }
                return;
            }

            // ✅ v8.6 FIX 2: Guard de movimiento rápido
            // ARSessionManager detectó movimiento brusco → ARCore puede estar
            // micro-reseteando el world origin. Esperar a que se estabilice.
            if (_arSessionManager != null && _arSessionManager.IsQuickMovePaused)
                return;

            // ✅ v8.3 FIX B: Esperar frames estables consecutivos
            _stableFrameCount++;
            if (_stableFrameCount < _stableFramesRequired)
                return;

            Vector3 cameraPos = _xrOrigin.Camera.transform.position;

            if (Vector3.Distance(cameraPos, _lastSyncedCameraPos) < _fullArSyncThreshold)
                return;

            _lastSyncedCameraPos = cameraPos;

            if (_navigationAgent.IsNavigating)
            {
                if (_agentNavMeshAgent != null && _agentNavMeshAgent.enabled
                    && _agentNavMeshAgent.isOnNavMesh && !_agentNavMeshAgent.isStopped)
                {
                    _agentNavMeshAgent.isStopped = true;
                }
                return;
            }

            float expectedFloorY = GetExpectedFloorY(cameraPos.y);
            NavMeshHit bestHit = default;
            bool found = false;

            float[] searchRadii = { 0.5f, 1.0f, 2.0f, _fullArSnapRadius };

            foreach (float radius in searchRadii)
            {
                if (!NavMesh.SamplePosition(cameraPos, out NavMeshHit hit, radius, NavMesh.AllAreas))
                    continue;

                float yDelta = Mathf.Abs(hit.position.y - expectedFloorY);
                if (yDelta <= _floorSnapTolerance)
                {
                    bestHit = hit;
                    found = true;
                    break;
                }

                Log($"⚠️ Hit radio {radius}m descartado: Y={hit.position.y:F2} " +
                    $"vs esperado {expectedFloorY:F2} (ΔY={yDelta:F2})");
            }

            if (found)
            {
                _syncFailFrames = 0;

                if (Vector3.Distance(_navigationAgent.transform.position, bestHit.position)
                    < _fullArSyncThreshold)
                    return;

                _navigationAgent.transform.position = bestHit.position;

                if (_agentNavMeshAgent != null && _agentNavMeshAgent.enabled
                    && _agentNavMeshAgent.isOnNavMesh)
                {
                    _agentNavMeshAgent.Warp(bestHit.position);
                    _agentNavMeshAgent.isStopped = true;
                }

                _lastStableAgentPos = bestHit.position;
                _hasStablePos = true;
            }
            else
            {
                _syncFailFrames++;
                Log($"⚠️ Sin hit válido (frame #{_syncFailFrames}/{_syncFailThreshold}).");

                if (_syncFailThreshold > 0 && _syncFailFrames >= _syncFailThreshold)
                {
                    _syncFailFrames = 0;
                    EmergencyWarpAgentToCamera(cameraPos);
                }
            }
        }

        private void EmergencyWarpAgentToCamera(Vector3 cameraPos)
        {
            if (!NavMesh.SamplePosition(cameraPos, out NavMeshHit emergencyHit,
                _fullArSnapRadius * 2f, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[AROriginAligner] ⚠️ Warp emergencia: sin NavMesh cerca.");
                return;
            }

            Debug.LogWarning($"[AROriginAligner] 🚨 WARP EMERGENCIA: " +
                             $"{_navigationAgent.transform.position:F2} → {emergencyHit.position:F2}");

            _navigationAgent.transform.position = emergencyHit.position;

            if (_agentNavMeshAgent != null && _agentNavMeshAgent.enabled)
            {
                _agentNavMeshAgent.Warp(emergencyHit.position);
                _agentNavMeshAgent.isStopped = true;
            }

            _lastStableAgentPos = emergencyHit.position;
            _hasStablePos = true;
            _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
        }

        private float GetExpectedFloorY(float cameraY)
        {
            var startPoints = NavigationStartPointManager.GetAllStartPoints();
            if (startPoints.Count == 0) return cameraY;

            float bestFloorY = cameraY;
            float bestDist = float.MaxValue;

            foreach (var pt in startPoints)
            {
                if (pt == null) continue;
                float dist = Mathf.Abs(pt.FloorHeight - cameraY);
                if (dist < bestDist) { bestDist = dist; bestFloorY = pt.FloorHeight; }
            }

            return bestDist <= _floorSnapTolerance ? bestFloorY : cameraY;
        }

        #endregion

        #region NoAR Follower Mode

        private void ActivateNoArMode()
        {
            if (_xrOrigin == null) { Debug.LogError("[AROriginAligner] ❌ XROrigin es null."); return; }

            SetAgentActiveAndVisible(makeVisible: true);

            var startPoint = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            if (startPoint != null)
            {
                startPoint.ConfirmModelPositioned();
                startPoint.ReteleportAgent();
            }

            if (_navigationAgent != null)
                SnapCameraToAgent(_navigationAgent.transform.position,
                                  _navigationAgent.transform.forward);

            _followActive = true;

            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message = "Modo visualización activo (sin ARCore)",
                Type = MessageType.Info,
                Duration = 4f
            });
        }

        private void FollowAgent()
        {
            if (_navigationAgent == null || _xrOrigin == null) return;

            Transform agentTf = _navigationAgent.transform;
            Vector3 agentPos = agentTf.position;
            Vector3 agentFwd = agentTf.forward;

            Vector3 desiredCamPos = agentPos
                + Vector3.up * _noArCameraHeight
                - agentFwd * _noArCameraBack;

            Quaternion desiredCamRot;
            if (_noArFollowRotation && agentFwd != Vector3.zero)
            {
                Vector3 lookDir = _noArCameraBack > 0f
                    ? (agentPos - desiredCamPos).normalized
                    : agentFwd;
                desiredCamRot = Quaternion.LookRotation(lookDir)
                              * Quaternion.Euler(_noArPitchAngle, 0f, 0f);
            }
            else
            {
                desiredCamRot = _xrOrigin.Camera.transform.rotation;
            }

            float t = _noArFollowSmooth > 0f ? Time.deltaTime * _noArFollowSmooth : 1f;

            Vector3 smoothPos = Vector3.Lerp(_xrOrigin.Camera.transform.position, desiredCamPos, t);
            Quaternion smoothRot = Quaternion.Slerp(_xrOrigin.Camera.transform.rotation, desiredCamRot, t);

            _xrOrigin.MoveCameraToWorldLocation(smoothPos);
            if (_noArFollowRotation)
                _xrOrigin.MatchOriginUpCameraForward(Vector3.up, smoothRot * Vector3.forward);
        }

        private void SnapCameraToAgent(Vector3 agentPos, Vector3 agentFwd)
        {
            Vector3 desiredCamPos = agentPos
                + Vector3.up * _noArCameraHeight
                - agentFwd * _noArCameraBack;

            _xrOrigin.MoveCameraToWorldLocation(desiredCamPos);
            if (_noArFollowRotation && agentFwd != Vector3.zero)
                _xrOrigin.MatchOriginUpCameraForward(Vector3.up, agentFwd);
        }

        #endregion

        #region Debug

        private void Log(string msg)
        {
            if (_logAlignment) Debug.Log($"[AROriginAligner] {msg}");
        }

        [ContextMenu("ℹ️ Info de estado")]
        private void DebugInfo()
        {
            var sp = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            var level = _capDetector?.Current ?? ARCapabilityLevel.FullAR;
            var stabilizer = ARWorldOriginStabilizer.Instance;

            float expectedFloorY = _xrOrigin?.Camera != null
                ? GetExpectedFloorY(_xrOrigin.Camera.transform.position.y)
                : -999f;

            Debug.Log("══════════════════════════════════════════════");
            Debug.Log("  AROriginAligner v8.6 — Estado");
            Debug.Log("══════════════════════════════════════════════");
            Debug.Log($"  Modo:               {(IsNoArMode ? "NoAR" : "FullAR")}");
            Debug.Log($"  Capacidad AR:       {level}");
            Debug.Log($"  ARSession state:    {ARSession.state}");
            Debug.Log($"  Tracking estable:   {IsTrackingStable}");
            Debug.Log($"  IsFullyStable:      {(_arSessionManager != null ? _arSessionManager.IsFullyStable.ToString() : "N/A (sin ARSessionManager)")}");
            Debug.Log($"  IsQuickMovePaused:  {(_arSessionManager != null ? _arSessionManager.IsQuickMovePaused.ToString() : "N/A")}");
            Debug.Log($"  Frames estables:    {_stableFrameCount}/{_stableFramesRequired}");
            Debug.Log($"  Tracking perdido:   {_trackingLost}");
            Debug.Log($"  Sync fail frames:   {_syncFailFrames}/{_syncFailThreshold}");
            Debug.Log($"  Última pos estable: {(_hasStablePos ? _lastStableAgentPos.ToString() : "N/A")}");
            Debug.Log($"  Alineación inicial: {_initialAlignDone}");
            Debug.Log($"  XR Origin:          {(_xrOrigin != null ? _xrOrigin.gameObject.name : "NULL")}");
            Debug.Log($"  Camera pos:         {(_xrOrigin?.Camera?.transform.position.ToString() ?? "N/A")}");
            Debug.Log($"  Piso esperado Y:    {expectedFloorY:F3}m");
            Debug.Log($"  StartPoint:         {(sp != null ? $"{sp.gameObject.name} @ {sp.transform.position}" : "No encontrado")}");
            Debug.Log($"  [Stabilizer] Captured:{(stabilizer != null ? stabilizer.AnchorCaptured.ToString() : "N/A")}");
            Debug.Log("══════════════════════════════════════════════");
        }

        [ContextMenu("🔄 Simular VIO Reset")]
        private void DebugSimulateVIOReset()
        {
            if (_noArMode) return;
            ARWorldOriginStabilizer.Instance?.DisableStabilization();
            _initialAlignDone = false;
            _stableFrameCount = 0;
            _syncFailFrames = 0;
            _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
            StartCoroutine(RealignAfterVIORecovery());
        }

        [ContextMenu("🚨 Forzar warp emergencia")]
        private void DebugForceEmergencyWarp()
        {
            if (_noArMode || _xrOrigin?.Camera == null) return;
            EmergencyWarpAgentToCamera(_xrOrigin.Camera.transform.position);
        }

        #endregion
    }
}