// File: AROriginAligner.cs
// ✅ v8.5 — Fix: agente varado en piso incorrecto bloquea navegación.
// ✅ v8.4 — Coordina con ARWorldOriginStabilizer durante VIO recovery.
//
// ============================================================================
//  CAMBIOS v8.4 → v8.5
// ============================================================================
//
//  BUG RAÍZ (confirmado por log):
//    agentPos=(5.66, 3.36, -2.61) — agente varado en piso 2.
//    GetExpectedFloorY(cameraY≈0) devuelve 0.00 (StartPoint piso 0).
//    Todos los hits de NavMesh están en Y=3.36 (piso 2).
//    ΔY = 3.36 > tolerancia 0.80 → TODOS los hits descartados.
//    Resultado: SyncAgentToCameraFullAR() omite la sincronización
//    indefinidamente → agente nunca sale del piso 2 → PathPartial →
//    navegación falla con "¿El NavMesh cubre la posición del usuario?"
//
//  CAUSA PROFUNDA — Deadlock de filtro de piso:
//    _lastStableAgentPos quedó en Y=3.36 de una sesión anterior.
//    El filtro de piso descarta todos los hits porque están en piso 2
//    cuando la cámara está en piso 0. Pero si el agente YA está en piso
//    2, el mismo filtro que lo protege de subir también le impide bajar.
//    Es un deadlock: el agente nunca puede corregirse por sí solo.
//
//  FIX 1 — GetExpectedFloorY(): usar cameraY directamente como piso esperado
//    si la cámara está a menos de _floorSnapTolerance de algún StartPoint.
//    Antes siempre forzaba el StartPoint más cercano aunque estuviera a
//    3.36m — haciendo que el hit del piso real (Y=3.36 = cámara real)
//    fuera descartado porque no coincidía con StartPoint Y=0.
//
//  FIX 2 — _syncFailFrames: contador de fallo consecutivo.
//    Tras _syncFailThreshold frames sin hit válido (default 120 ≈ 2s),
//    se hace un warp de emergencia al hit más cercano SIN filtro de piso.
//    Desatasca al agente varado. Se resetea al primer hit válido.
//
//  FIX 3 — ForceSnapAgentToCamera(): API pública para warp inmediato.
//    NavigationManager llama esto antes de calcular ruta si el agente
//    no está en el NavMesh → evita PathPartial silencioso.
//
// ============================================================================
//  CAMBIOS v8.3 → v8.4
// ============================================================================
//
//  INTEGRACIÓN con ARWorldOriginStabilizer:
//
//  CAMBIO 1 — OnARSessionStateChanged(): pausar estabilizador antes de
//    RealignAfterVIORecovery(). Evita que el estabilizador intente corregir
//    la posición del modelo mientras el XR Origin se está realineando,
//    lo que produciría correcciones en conflicto.
//
//  CAMBIO 2 — RealignAfterVIORecovery(): llamar ScheduleAnchorRecapture()
//    al finalizar la realineación. Después de AlignXROriginOnce(), la cámara
//    queda en una nueva posición estable. El estabilizador necesita recapturar
//    el offset modelo↔cámara desde esta nueva posición de referencia.
//    Se añade un delay de 0.5s para que la cámara se estabilice antes de
//    la recaptura.
//
// ============================================================================
//  CAMBIOS v8.2 → v8.3 (conservados íntegramente)
// ============================================================================
//
//  FIX A — Eliminado fallback sin validación de piso en SyncAgentToCameraFullAR():
//    Si ningún hit de NavMesh cumple la tolerancia de piso, se omite la
//    sincronización ese frame. Evita teleportar al agente al piso 2 (Y=3.36).
//
//  FIX B — Contador de frames estables antes de sincronizar tras VIO recovery:
//    Se requieren _stableFramesRequired frames consecutivos de SessionTracking
//    antes de reanudar SyncAgentToCameraFullAR(). Default 10 ≈ 167ms a 60fps.
//
//  FIX C — _lastStableAgentPos solo se actualiza con hits de piso validado:
//    Evita congelar en la posición de piso incorrecto (Y=3.36 del piso 2).
//
// ============================================================================
//  CAMBIOS v8.1 → v8.2 (conservados íntegramente)
// ============================================================================
//
//  BUG CORREGIDO: Flutter mostraba badge "Tracking inestable" en arranque normal.
//
//  FIX: Salir de NotifyFlutterTrackingState() sin notificar cuando el estado
//       es Ready — es una transición de sesión pausada, no un fallo de tracking.
//
// ============================================================================
//  CAMBIOS v7 → v8.1 (conservados íntegramente)
// ============================================================================
//
//  PROBLEMA RAÍZ: VIO de ARCore colapsa → nuevo sistema de coordenadas mundial.
//    Como _initialAlignDone = true, AlignXROriginOnce() no volvía a ejecutarse.
//    El XR Origin "flota" o salta a posición arbitraria.
//
//  FIX #1 — Suscripción a ARSession.stateChanged (AF 6.x).
//    Transición tracking degradado → SessionTracking dispara realineación.
//
//  FIX #2 — Guard de tracking estable en SyncAgentToCameraFullAR().
//    Si ARSession.state != SessionTracking, se omite la sincronización.
//
//  FIX #3 — NotifyTrackingState() → VoiceCommandAPI → Flutter.
//
//  FIX #4 — Congelado de posición durante tracking inestable.
//    Se guarda _lastStableAgentPos y se mantiene durante pérdida de tracking.
//
//  FIX M — Eliminado campo _isFullAR cacheado (race condition).
//    Reemplazado por propiedad dinámica IsFullAR => _navAgent.IsFullARMode.
//
//  BREAKING CHANGES de AR Foundation 6.x respecto a 4.x:
//    - LimitedWithRelocalizing, LimitedWithInsufficientLight,
//      LimitedWithInsufficientFeatures ELIMINADOS en AF 6.x.
//    - Detección de "tracking degradado" por exclusión de SessionTracking.
//    - ARSession.stateChanged sigue siendo estático en AF 6.x.
//    - FindFirstObjectByType<T>() reemplaza FindObjectOfType<T>() (Unity 6).

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
        [Tooltip("Nivel del modelo al que buscar el StartPoint (normalmente 0).")]
        [SerializeField] private int _targetLevel = 0;

        [Tooltip("Altura adicional sobre el StartPoint para la alineación inicial del XR Origin (FullAR).")]
        [SerializeField] private float _eyeHeightOffset = 1.6f;

        [Tooltip("Frames de espera después de que el modelo se carga antes de inicializar.")]
        [SerializeField] private int _delayFrames = 2;

        [Header("─── Modo NoAR — Seguimiento del agente ─────────────────────")]
        [SerializeField] private float _noArCameraHeight   = 1.65f;
        [SerializeField] private float _noArCameraBack     = 0.0f;
        [SerializeField] private float _noArPitchAngle     = 0.0f;
        [SerializeField] private float _noArFollowSmooth   = 8f;
        [SerializeField] private bool  _noArFollowRotation = true;

        [Header("─── Modo FullAR — Sincronización agente con cámara ─────────")]
        [Tooltip("Radio máximo de búsqueda en el NavMesh.")]
        [SerializeField] private float _fullArSnapRadius = 3.0f;

        [Tooltip("Distancia mínima de movimiento de la cámara para re-sincronizar el agente (m).")]
        [SerializeField] private float _fullArSyncThreshold = 0.05f;

        [Tooltip("Tolerancia vertical (m) entre el hit del NavMesh y el piso esperado.")]
        [SerializeField] private float _floorSnapTolerance = 0.8f;

        [Header("─── VIO Recovery (v8) ──────────────────────────────────────")]
        [Tooltip("Segundos de espera tras recuperar tracking antes de realinear. " +
                 "Dar tiempo a ARCore para estabilizar el nuevo mapa VIO.")]
        [SerializeField] private float _vioRecoveryDelay = 0.8f;

        [Tooltip("Si true, congela la posición del agente en la última posición " +
                 "conocida durante pérdida de tracking en vez de usar posición basura.")]
        [SerializeField] private bool _freezeAgentOnTrackingLoss = true;

        [Header("─── Estabilización post-VIO (v8.3) ───────────────────────")]
        [Tooltip("Frames consecutivos de SessionTracking requeridos antes de " +
                 "reanudar SyncAgentToCameraFullAR() tras un VIO fault. " +
                 "Evita el salto brusco en el primer frame tras recuperación. " +
                 "Default 10 ≈ 167ms a 60fps.")]
        [SerializeField] private int _stableFramesRequired = 10;

        [Header("─── Warp de emergencia (v8.5) ──────────────────────────────")]
        [Tooltip("Frames consecutivos sin hit válido de NavMesh antes de hacer " +
                 "un warp de emergencia al hit más cercano SIN filtro de piso. " +
                 "Desatasca al agente varado en un piso incorrecto (ej. Y=3.36 " +
                 "cuando la cámara está en piso 0). Default 120 ≈ 2s a 60fps. " +
                 "0 = desactivado.")]
        [SerializeField] private int _syncFailThreshold = 120;

        [Header("─── Debug ──────────────────────────────────────────────────")]
        [SerializeField] private bool _logAlignment = true;

        // ─── Estado interno ────────────────────────────────────────────────

        private bool _noArMode           = false;
        private bool _followActive       = false;
        private bool _capabilityResolved = false;
        private bool _initialAlignDone   = false;
        private ARCapabilityDetector _capDetector;

        private Vector3 _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
        private NavMeshAgent _agentNavMeshAgent;

        // ─── v8: Estado de tracking VIO ───────────────────────────────────
        private ARSessionState _lastARState   = ARSessionState.None;
        private bool           _trackingLost  = false;
        private Vector3        _lastStableAgentPos;
        private bool           _hasStablePos  = false;

        // ─── v8.3: Contador de frames estables tras VIO recovery ──────────
        private int _stableFrameCount = 0;

        // ─── v8.5: Contador de frames sin hit válido (warp de emergencia) ─
        private int _syncFailFrames = 0;

        // ─── Propiedades públicas ──────────────────────────────────────────

        public bool IsNoArMode   => _noArMode;
        public bool IsFullARMode => !_noArMode;

        /// <summary>v8: True cuando ARCore está en tracking estable.</summary>
        public bool IsTrackingStable =>
            ARSession.state == ARSessionState.SessionTracking;

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
                        Log("✅ [Awake] PathController.SetFullARMode(true) — detector ya resuelto.");
                    }
                }
            }
        }

        private void Start() => StartCoroutine(InitializeCapabilityRoutine());

        private void OnEnable()
        {
            EventBus.Instance?.Subscribe<ModelLoadedEvent>(OnModelLoaded);
            // ✅ v8 FIX #1: Suscribir a cambios de estado de ARSession.
            ARSession.stateChanged += OnARSessionStateChanged;
        }

        private void OnDisable()
        {
            EventBus.Instance?.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);
            // ✅ v8: Desuscribir para evitar memory leaks.
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

            if (_xrOrigin == null)
                Debug.LogWarning("[AROriginAligner] ⚠️ XROrigin no encontrado.");
            if (_capDetector == null)
                Debug.LogWarning("[AROriginAligner] ⚠️ ARCapabilityDetector no encontrado — asumiendo FullAR.");
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
                Log("📵 Modo NoAR — agente ES el usuario virtual (activo y visible).");

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
                _noArMode     = false;
                _followActive = false;

                Log("📡 Modo FullAR — agente ACTIVO e INVISIBLE, sincronizado con cámara XR.");

                if (_navigationAgent != null)
                {
                    var pc = _navigationAgent.GetComponent<NavigationPathController>();
                    if (pc != null)
                    {
                        pc.SetFullARMode(true);
                        Log("✅ PathController.SetFullARMode(true) — movimiento del agente bloqueado.");
                    }
                    else
                    {
                        Debug.LogWarning("[AROriginAligner] ⚠️ NavigationPathController no encontrado.");
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
                Log($"✅ Agente activado (estaba desactivado).");
            }

            foreach (var r in _navigationAgent.GetComponentsInChildren<Renderer>(true))
                r.enabled = makeVisible;

            Log(makeVisible
                ? "👁️ Agente VISIBLE (NoAR)."
                : "🙈 Agente INVISIBLE (FullAR) — Renderers desactivados, MonoBehaviour activos.");
        }

        private void StopAgentMovement()
        {
            if (_agentNavMeshAgent == null) return;

            if (_agentNavMeshAgent.enabled && _agentNavMeshAgent.isOnNavMesh)
            {
                _agentNavMeshAgent.isStopped = true;
                _agentNavMeshAgent.ResetPath();
            }

            Log("🛑 [FullAR] NavMeshAgent detenido — el agente no caminará autónomamente.");
        }

        #endregion

        #region v8 — VIO Reset Detection

        /// <summary>
        /// ✅ v8 FIX #1 + #3: Callback de ARSession.stateChanged.
        ///
        /// Detecta el ciclo: tracking degradado → SessionTracking (VIO recovery).
        /// Al recuperarse, resetea _initialAlignDone para forzar realineación.
        /// Notifica a Flutter el estado actual del tracking.
        ///
        /// ✅ v8.4: Pausa ARWorldOriginStabilizer antes de RealignAfterVIORecovery()
        /// para evitar correcciones en conflicto durante la realineación del XR Origin.
        /// </summary>
        private void OnARSessionStateChanged(ARSessionStateChangedEventArgs args)
        {
            ARSessionState newState = args.state;

            bool wasLost     = IsTrackingDegraded(_lastARState);
            bool nowTracking = newState == ARSessionState.SessionTracking;
            bool nowLost     = IsTrackingDegraded(newState);

            Log($"📡 ARSession state: {_lastARState} → {newState}");
            _lastARState = newState;

            // ✅ v8 FIX #3: Notificar a Flutter del estado del tracking.
            NotifyFlutterTrackingState(isStable: nowTracking, stateStr: newState.ToString());

            // Ignorar si estamos en modo NoAR — ARCore no importa
            if (_noArMode) return;

            // ✅ v8 FIX #2: Marcar tracking como perdido para congelar posición
            if (nowLost && !_trackingLost)
            {
                _trackingLost     = true;
                _stableFrameCount = 0; // ✅ v8.3 FIX B: resetear contador al perder tracking
                if (_navigationAgent != null)
                {
                    _lastStableAgentPos = _navigationAgent.transform.position;
                    _hasStablePos       = true;
                    Log($"⚠️ Tracking perdido ({newState}) — posición estable guardada: {_lastStableAgentPos:F2}");
                }
            }

            // ✅ v8 FIX #1: VIO se recuperó → realinear XR Origin
            if (wasLost && nowTracking && _initialAlignDone)
            {
                _trackingLost        = false;
                _initialAlignDone    = false;
                _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);

                Log("🔄 VIO reset detectado — programando realineación del XR Origin...");

                // ✅ v8.4: Pausar estabilizador durante la realineación del XR Origin.
                // Evita que ARWorldOriginStabilizer corrija la posición del modelo
                // mientras el XR Origin se está moviendo con AlignXROriginOnce().
                ARWorldOriginStabilizer.Instance?.DisableStabilization();

                StartCoroutine(RealignAfterVIORecovery());
            }
            else if (nowTracking)
            {
                _trackingLost = false;
            }
        }

        /// <summary>
        /// Determina si un ARSessionState indica tracking VIO degradado.
        ///
        /// En AF 6.x los valores LimitedWith* fueron eliminados.
        /// Cuando ARCore pierde el VIO en vuelo, el estado va a SessionInitializing.
        /// Ready solo ocurre en OnDisable/OnDestroy (sesión pausada).
        ///
        /// Ciclo VIO-fault detectado:
        ///   SessionTracking → SessionInitializing → SessionTracking
        /// </summary>
        private static bool IsTrackingDegraded(ARSessionState state)
            => state == ARSessionState.SessionInitializing;

        /// <summary>
        /// ✅ v8: Espera _vioRecoveryDelay segundos y luego realinea.
        ///
        /// ✅ v8.4: Llama ScheduleAnchorRecapture() al finalizar la realineación
        /// para que ARWorldOriginStabilizer recapture el offset modelo↔cámara
        /// desde la nueva posición estable del XR Origin.
        /// El delay adicional de 0.5s da tiempo a que la cámara se estabilice
        /// antes de la recaptura.
        /// </summary>
        private IEnumerator RealignAfterVIORecovery()
        {
            yield return new WaitForSeconds(_vioRecoveryDelay);

            if (_noArMode) yield break;

            Log($"🔄 Realineando XR Origin tras VIO recovery (delay={_vioRecoveryDelay}s)...");
            AlignXROriginOnce();

            // ✅ v8.4: Reactivar el estabilizador después de la realineación.
            // AlignXROriginOnce() mueve la cámara a una nueva posición estable.
            // El estabilizador necesita recapturar el offset desde esta nueva posición.
            // El delay de 0.5s garantiza que la cámara se estabilice antes de la recaptura.
            yield return new WaitForSeconds(0.5f);
            ARWorldOriginStabilizer.Instance?.ScheduleAnchorRecapture();
            Log("🔄 ARWorldOriginStabilizer programado para recapturar anchor.");
        }

        /// <summary>
        /// ✅ v8.2 FIX: No notificar estado Ready a Flutter.
        ///
        /// ARSessionState.Ready ocurre cuando la sesión se pausa (OnDisable/OnDestroy).
        /// NO es un fallo de tracking — notificarlo como stable=false causaba que
        /// Flutter mostrara el badge "Tracking inestable" incorrectamente.
        ///
        /// Solo notificar:
        ///   - SessionTracking → stable=true
        ///   - SessionInitializing → stable=false (VIO fault real)
        ///
        /// El campo "reason" se codifica en stateStr con formato "State|Reason"
        /// para no romper la firma existente de VoiceCommandAPI.NotifyTrackingState().
        /// </summary>
        private void NotifyFlutterTrackingState(bool isStable, string stateStr)
        {
            // ✅ v8.2 FIX: Ready = sesión pausada (OnDisable), no tracking inestable.
            if (ARSession.state == ARSessionState.Ready) return;

            var api = VoiceCommandAPI.Instance;
            if (api == null) return;

            string reason = isStable
                ? "None"
                : ARSession.notTrackingReason.ToString();

            string enrichedState = isStable
                ? stateStr
                : $"{stateStr}|{reason}";

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
        public void AlignToStartPoint()     => StartCoroutine(HandleModelReady());

        public void ForceRealign()
        {
            if (!_noArMode)
            {
                _initialAlignDone    = false;
                _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
            }
            StartCoroutine(HandleModelReady());
        }

        /// <summary>
        /// ✅ v8.5: Warp inmediato del agente al NavMesh más cercano a la cámara,
        /// SIN filtro de piso. Llamar desde NavigationManager antes de calcular
        /// una ruta si el agente no está en el NavMesh o está en piso incorrecto.
        ///
        /// Uso desde NavigationManager:
        ///   var aligner = FindFirstObjectByType&lt;AROriginAligner&gt;();
        ///   if (aligner != null &amp;&amp; aligner.IsFullARMode)
        ///       aligner.ForceSnapAgentToCamera();
        /// </summary>
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
                _noArMode     = false;
                _followActive = false;
                AlignXROriginOnce();
            }
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
                _initialAlignDone    = true;
                _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);

                Log($"✅ [FullAR] XR Origin → {targetPos}. ARCore toma control.");
            }
            else
            {
                Log("📡 [FullAR] Alineación ya realizada — XR Origin intocado.");
            }

            SetAgentActiveAndVisible(makeVisible: false);
            StopAgentMovement();

            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message  = "Navegación lista",
                Type     = MessageType.Success,
                Duration = 3f
            });
        }

        #endregion

        #region FullAR — Sincronización agente con cámara XR

        /// <summary>
        /// ✅ v8.5: Sincronización agente ↔ cámara con fixes acumulados.
        ///
        /// FIX A (v8.3) — Sin fallback de piso:
        ///   Si ningún hit cumple la tolerancia, se omite ese frame.
        ///
        /// FIX B (v8.3) — Contador de frames estables:
        ///   Requiere _stableFramesRequired frames consecutivos de SessionTracking
        ///   antes de reanudar tras VIO fault.
        ///
        /// FIX C (v8.3) — _lastStableAgentPos solo con hit validado.
        ///
        /// FIX 1 (v8.5) — GetExpectedFloorY() usa cameraY directamente:
        ///   Si la cámara está dentro de ±_floorSnapTolerance de un StartPoint,
        ///   usa ese StartPoint. Si no hay ninguno cercano, usa cameraY directo.
        ///   Antes forzaba siempre el StartPoint más cercano aunque estuviera
        ///   a 3.36m → hacía que hits del piso real fueran incorrectamente
        ///   descartados cuando la cámara no correspondía con ningún piso.
        ///
        /// FIX 2 (v8.5) — Warp de emergencia tras _syncFailThreshold frames:
        ///   Si el agente lleva _syncFailThreshold frames consecutivos sin
        ///   sincronizarse (deadlock de filtro de piso), se hace warp al hit
        ///   más cercano SIN filtro de piso. Desatasca al agente varado en
        ///   piso incorrecto que no puede salir por sus propios medios.
        /// </summary>
        private void SyncAgentToCameraFullAR()
        {
            if (_navigationAgent == null || _xrOrigin?.Camera == null) return;
            if (!_navigationAgent.gameObject.activeSelf)               return;

            // ✅ v8 FIX #2 + v8.3 FIX B: Solo sincronizar con tracking estable
            if (ARSession.state != ARSessionState.SessionTracking)
            {
                _stableFrameCount = 0;
                _syncFailFrames   = 0; // ✅ v8.5: resetear también al perder tracking

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

            // ✅ v8.3 FIX B: Esperar frames estables consecutivos
            _stableFrameCount++;
            if (_stableFrameCount < _stableFramesRequired)
                return;

            Vector3 cameraPos = _xrOrigin.Camera.transform.position;

            if (Vector3.Distance(cameraPos, _lastSyncedCameraPos) < _fullArSyncThreshold)
                return;

            _lastSyncedCameraPos = cameraPos;

            // ✅ v7 FIX #1: No mover si el agente está navegando
            if (_navigationAgent.IsNavigating)
            {
                if (_agentNavMeshAgent != null && _agentNavMeshAgent.enabled
                    && _agentNavMeshAgent.isOnNavMesh && !_agentNavMeshAgent.isStopped)
                {
                    _agentNavMeshAgent.isStopped = true;
                }
                return;
            }

            // ✅ v8.5 FIX 1: GetExpectedFloorY usa cameraY directamente si no
            // hay StartPoint dentro de ±_floorSnapTolerance (ver método abajo).
            float expectedFloorY = GetExpectedFloorY(cameraPos.y);
            NavMeshHit bestHit   = default;
            bool       found     = false;

            float[] searchRadii = { 0.5f, 1.0f, 2.0f, _fullArSnapRadius };

            foreach (float radius in searchRadii)
            {
                if (!NavMesh.SamplePosition(cameraPos, out NavMeshHit hit, radius, NavMesh.AllAreas))
                    continue;

                float yDelta = Mathf.Abs(hit.position.y - expectedFloorY);
                if (yDelta <= _floorSnapTolerance)
                {
                    bestHit = hit;
                    found   = true;
                    break;
                }

                Log($"⚠️ [Sync] Hit en radio {radius}m descartado: " +
                    $"Y={hit.position.y:F2} vs piso esperado {expectedFloorY:F2} " +
                    $"(ΔY={yDelta:F2}m > tolerancia {_floorSnapTolerance:F2}m)");
            }

            if (found)
            {
                // Hit válido encontrado → sincronizar normalmente y resetear contador
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

                // ✅ v8.3 FIX C: Solo con hit validado
                _lastStableAgentPos = bestHit.position;
                _hasStablePos       = true;
            }
            else
            {
                // ✅ v8.5 FIX 2: Contar frames fallidos consecutivos
                _syncFailFrames++;

                Log($"⚠️ [Sync] Sin hit válido (frame fallido #{_syncFailFrames}/{_syncFailThreshold}).");

                // Warp de emergencia si llevamos demasiados frames sin sincronizar
                if (_syncFailThreshold > 0 && _syncFailFrames >= _syncFailThreshold)
                {
                    _syncFailFrames = 0;
                    EmergencyWarpAgentToCamera(cameraPos);
                }
            }
        }

        /// <summary>
        /// ✅ v8.5: Warp de emergencia al hit más cercano SIN filtro de piso.
        ///
        /// Se usa cuando el agente lleva _syncFailThreshold frames consecutivos
        /// sin poder sincronizarse con un hit válido. Esto ocurre cuando el agente
        /// está varado en un piso incorrecto y el filtro de piso impide que salga
        /// (deadlock). El warp sin filtro acepta cualquier piso del NavMesh.
        ///
        /// También llamado desde ForceSnapAgentToCamera() / NavigationManager.
        /// </summary>
        private void EmergencyWarpAgentToCamera(Vector3 cameraPos)
        {
            // Buscar hit más cercano SIN filtro de piso
            if (!NavMesh.SamplePosition(cameraPos, out NavMeshHit emergencyHit,
                _fullArSnapRadius * 2f, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[AROriginAligner] ⚠️ Warp emergencia: " +
                                 $"sin NavMesh en radio {_fullArSnapRadius * 2f}m " +
                                 $"desde cámara {cameraPos:F2}");
                return;
            }

            Debug.LogWarning($"[AROriginAligner] 🚨 WARP EMERGENCIA: " +
                             $"agente {_navigationAgent.transform.position:F2} → " +
                             $"{emergencyHit.position:F2} " +
                             $"(cámara en {cameraPos:F2})");

            _navigationAgent.transform.position = emergencyHit.position;

            if (_agentNavMeshAgent != null && _agentNavMeshAgent.enabled)
            {
                _agentNavMeshAgent.Warp(emergencyHit.position);
                _agentNavMeshAgent.isStopped = true;
            }

            _lastStableAgentPos = emergencyHit.position;
            _hasStablePos       = true;
            _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
        }

        /// <summary>
        /// ✅ v8.5 FIX 1: Devuelve el Y de piso esperado para la posición de la cámara.
        ///
        /// LÓGICA v8.5:
        ///   1. Busca el StartPoint cuyo FloorHeight está más cerca de cameraY.
        ///   2. Si la diferencia es ≤ _floorSnapTolerance, ese es el piso actual
        ///      y devuelve su FloorHeight (comportamiento igual a versiones anteriores).
        ///   3. Si NO hay ningún StartPoint dentro de ±_floorSnapTolerance de la cámara,
        ///      devuelve cameraY directamente.
        ///
        /// POR QUÉ EL CAMBIO:
        ///   Antes siempre devolvía el StartPoint más cercano sin importar la distancia.
        ///   Si la cámara estaba en Y≈0 y los StartPoints eran Y=0 y Y=3.36, devolvía
        ///   Y=0. Pero si el NavMesh solo tenía hits en Y=3.36 (agente varado en piso 2),
        ///   todos los hits quedaban descartados (ΔY=3.36 > tolerancia 0.8).
        ///   Con el fix, si la cámara está en Y≈0 pero no hay NavMesh allí, al usar
        ///   cameraY=0 como referencia los hits en Y=3.36 se siguen descartando — lo cual
        ///   es correcto. El warp de emergencia (_syncFailThreshold) es quien finalmente
        ///   desatasca el agente en esos casos.
        /// </summary>
        private float GetExpectedFloorY(float cameraY)
        {
            var startPoints = NavigationStartPointManager.GetAllStartPoints();
            if (startPoints.Count == 0) return cameraY;

            float bestFloorY = cameraY;
            float bestDist   = float.MaxValue;

            foreach (var pt in startPoints)
            {
                if (pt == null) continue;
                float dist = Mathf.Abs(pt.FloorHeight - cameraY);
                if (dist < bestDist) { bestDist = dist; bestFloorY = pt.FloorHeight; }
            }

            // ✅ v8.5: Solo usar el StartPoint si está dentro de la tolerancia.
            // Si ningún StartPoint está cerca de la cámara, usar cameraY directamente.
            return bestDist <= _floorSnapTolerance ? bestFloorY : cameraY;
        }

        #endregion

        #region NoAR Follower Mode

        private void ActivateNoArMode()
        {
            if (_xrOrigin == null)
            {
                Debug.LogError("[AROriginAligner] ❌ XROrigin es null.");
                return;
            }

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
                Message  = "Modo visualización activo (sin ARCore)",
                Type     = MessageType.Info,
                Duration = 4f
            });

            Log("✅ [NoAR] Cámara siguiendo al agente (usuario virtual).");
        }

        private void FollowAgent()
        {
            if (_navigationAgent == null || _xrOrigin == null) return;

            Transform agentTf  = _navigationAgent.transform;
            Vector3   agentPos = agentTf.position;
            Vector3   agentFwd = agentTf.forward;

            Vector3 desiredCamPos = agentPos
                + Vector3.up * _noArCameraHeight
                - agentFwd   * _noArCameraBack;

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

            Vector3    smoothPos = Vector3.Lerp(
                _xrOrigin.Camera.transform.position, desiredCamPos, t);
            Quaternion smoothRot = Quaternion.Slerp(
                _xrOrigin.Camera.transform.rotation, desiredCamRot, t);

            _xrOrigin.MoveCameraToWorldLocation(smoothPos);
            if (_noArFollowRotation)
                _xrOrigin.MatchOriginUpCameraForward(Vector3.up, smoothRot * Vector3.forward);
        }

        private void SnapCameraToAgent(Vector3 agentPos, Vector3 agentFwd)
        {
            Vector3 desiredCamPos = agentPos
                + Vector3.up * _noArCameraHeight
                - agentFwd   * _noArCameraBack;

            _xrOrigin.MoveCameraToWorldLocation(desiredCamPos);
            if (_noArFollowRotation && agentFwd != Vector3.zero)
                _xrOrigin.MatchOriginUpCameraForward(Vector3.up, agentFwd);
        }

        #endregion

        #region Utilities & Debug

        private void Log(string msg)
        {
            if (_logAlignment) Debug.Log($"[AROriginAligner] {msg}");
        }

        [ContextMenu("ℹ️ Info de estado")]
        private void DebugInfo()
        {
            var sp    = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            var level = _capDetector?.Current ?? ARCapabilityLevel.FullAR;

            float expectedFloorY = _xrOrigin?.Camera != null
                ? GetExpectedFloorY(_xrOrigin.Camera.transform.position.y)
                : -999f;

            var stabilizer = ARWorldOriginStabilizer.Instance;

            Debug.Log("══════════════════════════════════════════════");
            Debug.Log("  AROriginAligner v8.4 — Estado");
            Debug.Log("══════════════════════════════════════════════");
            Debug.Log($"  Modo:               {(IsNoArMode ? "NoAR" : "FullAR (agente invisible, activo)")}");
            Debug.Log($"  Capacidad AR:       {level}");
            Debug.Log($"  ARSession state:    {ARSession.state}");
            Debug.Log($"  Tracking estable:   {IsTrackingStable}");
            Debug.Log($"  Frames estables:    {_stableFrameCount}/{_stableFramesRequired}");
            Debug.Log($"  Tracking perdido:   {_trackingLost}");
            Debug.Log($"  Sync fail frames:   {_syncFailFrames}/{_syncFailThreshold} (warp emergencia)");
            Debug.Log($"  Última pos estable: {(_hasStablePos ? _lastStableAgentPos.ToString() : "N/A")}");
            Debug.Log($"  Seguimiento NoAR:   {_followActive}");
            Debug.Log($"  Alineación inicial: {_initialAlignDone}");
            Debug.Log($"  XR Origin:          {(_xrOrigin != null ? _xrOrigin.gameObject.name : "NULL")}");
            Debug.Log($"  Camera pos:         {(_xrOrigin?.Camera?.transform.position.ToString() ?? "N/A")}");
            Debug.Log($"  Piso esperado Y:    {expectedFloorY:F3}m");
            Debug.Log($"  Floor snap tol:     ±{_floorSnapTolerance:F2}m");
            Debug.Log($"  StartPoint:         {(sp != null ? $"{sp.gameObject.name} @ {sp.transform.position}" : "No encontrado")}");
            Debug.Log($"  Agente:             {(_navigationAgent != null ? $"{_navigationAgent.gameObject.name} activo={_navigationAgent.gameObject.activeSelf}" : "NULL")}");
            Debug.Log($"  Agente pos:         {(_navigationAgent?.transform.position.ToString() ?? "N/A")}");
            Debug.Log($"  Agente IsNavigating:{(_navigationAgent?.IsNavigating.ToString() ?? "N/A")}");
            Debug.Log($"  NavMeshAgent stop:  {(_agentNavMeshAgent != null ? _agentNavMeshAgent.isStopped.ToString() : "N/A")}");
            Debug.Log($"  Última sync cámara: {_lastSyncedCameraPos}");
            Debug.Log($"  [Stabilizer]");
            Debug.Log($"    AnchorCaptured:   {(stabilizer != null ? stabilizer.AnchorCaptured.ToString() : "N/A (no instancia)")}");
            Debug.Log($"    IsStabilizing:    {(stabilizer != null ? stabilizer.IsStabilizing.ToString() : "N/A")}");
            Debug.Log("══════════════════════════════════════════════");
        }

        [ContextMenu("📵 Forzar modo NoAR (test)")]
        private void DebugForceNoAr()
        {
            _noArMode = true;
            SetAgentActiveAndVisible(makeVisible: true);
            ActivateNoArMode();
        }

        [ContextMenu("📡 Forzar modo FullAR (test)")]
        private void DebugForceFullAr()
        {
            _noArMode            = false;
            _followActive        = false;
            _initialAlignDone    = false;
            _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
            SetAgentActiveAndVisible(makeVisible: false);
            StopAgentMovement();
            AlignXROriginOnce();
        }

        [ContextMenu("🔄 Simular VIO Reset (test)")]
        private void DebugSimulateVIOReset()
        {
            if (_noArMode) { Log("No aplica en modo NoAR"); return; }
            Log("🧪 Simulando VIO reset...");

            // Pausar estabilizador igual que en VIO real
            ARWorldOriginStabilizer.Instance?.DisableStabilization();

            _initialAlignDone    = false;
            _stableFrameCount    = 0;
            _syncFailFrames      = 0;
            _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
            StartCoroutine(RealignAfterVIORecovery());
        }

        [ContextMenu("🚨 Forzar warp emergencia (test)")]
        private void DebugForceEmergencyWarp()
        {
            if (_noArMode || _xrOrigin?.Camera == null) { Log("No aplicable"); return; }
            Log("🧪 Forzando warp emergencia...");
            EmergencyWarpAgentToCamera(_xrOrigin.Camera.transform.position);
        }

        #endregion
    }
}