// File: ARGuideController.cs
// ✅ v5.6 — FIX TTS SATURACIÓN: Escaleras solo si la ruta cambia de piso real.
//
// ══════════════════════════════════════════════════════════════════════════════
// CAMBIOS v5.5 → v5.6
// ══════════════════════════════════════════════════════════════════════════════
//
// FIX R — Anuncios de escaleras solo cuando la ruta incluye cambio de piso real
// ──────────────────────────────────────────────────────────────────────────────
//   PROBLEMA v5.5: DetectStairsAhead() usaba solo el umbral de altura en el
//   path del NavMesh (_stairHeightThreshold = 0.4m). Esto causaba falsos
//   positivos en cualquier rampa, desnivel o irregularidad del modelo 3D
//   que no implica un cambio real de planta.
//
//   Además _stairAnnounced se reseteaba al iniciar cada destino, permitiendo
//   que el mismo tramo se anunciara múltiples veces si el agente oscilaba
//   cerca del umbral.
//
//   FIX:
//   • Se añade _routeIncludesFloorChange (bool) que se calcula al establecer
//     el destino comparando el piso origen (_currentFloor) con _destFloor.
//     Si son iguales → DetectStairsAhead() devuelve siempre false.
//   • Se añade _stairSilenceCooldown: una vez que se anuncia el tramo de
//     escaleras, se impone un cooldown de _stairAnnounceCooldown segundos
//     antes de permitir otro anuncio, aunque el agente se aleje y vuelva
//     al mismo tramo.
//   • Se mantiene toda la lógica de FullAR (supresión de anuncios de piso).
//
// TODOS LOS FIXES ANTERIORES (v5.0 … v5.5) SE CONSERVAN ÍNTEGRAMENTE.

using UnityEngine;
using UnityEngine.AI;
using Unity.XR.CoreUtils;
using IndoorNavAR.Navigation;
using IndoorNavAR.Navigation.Voice;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Agent
{
    [RequireComponent(typeof(NavigationAgent))]
    public sealed class ARGuideController : MonoBehaviour
    {
        public enum GuideState
        {
            Idle,
            Leading,
            Waiting,
            Returning,
            Reorienting,
            ApproachingStairs,
            WaitingAtGoal,
            PausedForTTS,
            PausedAfterTurn,
            WaitingToStart,
        }

        [Header("─── Referencias ─────────────────────────────────────────────")]
        [SerializeField] private XROrigin _xrOrigin;

        [Header("─── Distancias ──────────────────────────────────────────────")]
        [SerializeField] private float _safeDistance      = 2f;
        [SerializeField] private float _minDistance       = 1.0f;
        [SerializeField] private float _maxReturnDistance = 3f;

        [Header("─── Llegada ─────────────────────────────────────────────────")]
        [SerializeField] private float _arrivalConfirmDist = 1.5f;

        [SerializeField] private float _arrivalTimeoutConfirmDist = 2.2f;
        [SerializeField] private float _arrivalVerticalTolerance = 1.2f;
        [SerializeField] private int _arrivalStableFramesRequired = 6;

        private int _arrivalStableFrames = 0;
        [SerializeField] private float _arrivalTimeout     = 45.0f;
        [SerializeField] private float _arrivalMinDelay    = 3.0f;

        [Header("─── Llegada FullAR (FIX N) ─────────────────────────────────")]
        [SerializeField] private float _arrivalMovementThreshold = 1.5f;

        [Header("─── v5.5 FIX P — Llegada mejorada ───────────────────────────")]
        [Tooltip("✅ v5.5 — Distancia máxima en XZ (m) para confirmar llegada en NoAR.")]
        [SerializeField] private float _arrivalXZDistance = 1.5f;

        [Tooltip("✅ v5.5 — Frames consecutivos dentro del radio de llegada para confirmar.")]
        [SerializeField] private int _arrivalConvergenceFrames = 4;
        private int _arrivalConvergenceCount = 0;

        [Tooltip("✅ v5.5 — Distancia mínima acercada al destino para que la llegada sea válida.")]
        [SerializeField] private float _arrivalProgressRequired = 2.0f;

        [Header("─── Velocidad ──────────────────────────────────────────────")]
        [SerializeField] private float _maxAgentSpeed        = 0.5f;
        [SerializeField] private float _minLeadingSpeed      = 0.3f;
        [Range(0.2f, 1f)]
        [SerializeField] private float _closedistSpeedFactor = 0.4f;
        [Range(0.1f, 0.8f)]
        [SerializeField] private float _stairSpeedFactor     = 0.3f;

        [Header("─── Pausa inicial ─────────────────────────────────────────")]
        [SerializeField] private float _initialNavigationPause = 3.5f;

        [Header("─── Pausa tras giros ─────────────────────────────────────")]
        [SerializeField] private float _postTurnPauseSeconds = 4.0f;
        [SerializeField] private float _postTurnMaxWait      = 10.0f;
        [SerializeField] private float _postTurnResumeSpeed  = 0.15f;

        [Header("─── TTS ─────────────────────────────────────────────────────")]
        [SerializeField] private bool  _pauseForHighPriorityTTS = true;
        [SerializeField] private int   _ttsPauseMinPriority     = 1;
        [SerializeField] private float _maxTTSWaitTime          = 15.0f;

        [Header("─── Escaleras ───────────────────────────────────────────────")]
        [SerializeField] private float _stairWarningDistance = 2.5f;
        [SerializeField] private float _stairConfirmDistance = 1.5f;
        [Range(0.1f, 1f)]
        [SerializeField] private float _stairHeightThreshold = 0.4f;

        [Header("─── v5.6 FIX R — Escaleras solo con cambio de piso real ─────")]
        [Tooltip("✅ v5.6 — Si true, solo anuncia escaleras cuando la ruta implica\n" +
                 "un cambio real de piso (destFloor != currentFloor).\n" +
                 "Evita anuncios en rampas o desniveles del modelo sin cambio de planta.\n" +
                 "Default: true")]
        [SerializeField] private bool _requireFloorChangeForStairAnnounce = true;

        [Tooltip("✅ v5.6 — Cooldown (s) tras anunciar escaleras antes de permitir\n" +
                 "otro anuncio del mismo tramo. Evita repetición si el agente oscila.\n" +
                 "Default: 15s")]
        [SerializeField] private float _stairAnnounceCooldown = 15f;

        private float _lastStairAnnounceTime = -999f;
        private bool  _routeIncludesFloorChange = false;

        [Header("─── Ángulo ──────────────────────────────────────────────────")]
        [SerializeField] private float _maxAngle      = 120f;
        [SerializeField] private float _rotationSpeed = 90f;

        [Header("─── Evaluación ─────────────────────────────────────────────")]
        [SerializeField] private float _evaluationInterval = 0.25f;

        [Header("─── Transición de piso ──────────────────────────────────────")]
        [SerializeField] private bool  _suppressFloorAnnouncementsInFullAR = true;
        [SerializeField] private float _floorTransitionDedup = 4.0f;
        [SerializeField] private float _floorYTolerance      = 1.2f;

        [Header("─── Debug ────────────────────────────────────────────────────")]
        [SerializeField] private bool _logStateChanges  = true;
        [SerializeField] private bool _logEvaluations   = false;
        [SerializeField] private bool _logStairs        = true;
        [SerializeField] private bool _logTTSSync       = true;
        [SerializeField] private bool _logAccessibility = true;
        [SerializeField] private bool _logFullAR        = true;

#if UNITY_EDITOR
        [Header("─── Gizmos ──────────────────────────────────────────────────")]
        [SerializeField] private bool  _drawGizmos       = true;
        [SerializeField] private Color _safeColor        = new Color(0f, 1f, 0f,   0.25f);
        [SerializeField] private Color _returnColor      = new Color(1f, 0.4f, 0f, 0.20f);
        [SerializeField] private Color _minColor         = new Color(0f, 0.8f, 1f, 0.20f);
        [SerializeField] private Color _destinationColor = new Color(1f, 1f, 0f,   0.80f);
        [SerializeField] private Color _stairColor       = new Color(1f, 0f,   1f, 0.35f);
#endif

        // ─── Estado ───────────────────────────────────────────────────────────

        private NavigationAgent _navAgent;
        private NavMeshAgent    _rawAgent;
        private GuideState      _currentState     = GuideState.Idle;
        private GuideState      _stateBeforePause = GuideState.Idle;
        private Vector3         _guideDestination;
        private bool            _hasDestination       = false;
        private bool            _isHandlingNavigation = false;

        private float _originalSpeed  = -1f;
        private int   _currentFloor   = 0;
        private bool  _isOnStairs     = false;
        private bool  _stairAnnounced = false;
        private bool  _climbAnnounced = false;

        private readonly Vector3[] _pathCornersBuffer = new Vector3[24];
        private int _pathCornersCount = 0;

        private bool    _isReorienting  = false;
        private Vector3 _reorientTarget;
        private bool    _agentWasPaused = false;

        private bool  _ttsPauseActive    = false;
        private float _ttsPauseTimer     = 0f;
        private float _arrivalWaitTimer  = 0f;
        private bool  _navCompletedFired = false;

        private bool  _initialPauseDone    = false;
        private float _initialPauseTimer   = 0f;
        private float _postTurnPauseTimer  = 0f;
        private bool  _inPostTurnPause     = false;

        private UserPositionBridge _userBridge;

        private int   _lastProcessedFloorTo    = -999;
        private float _lastFloorTransitionTime = -999f;

        // FIX N: Tracking de movimiento hacia destino
        private float _initialDistToDest         = float.MaxValue;
        private bool  _userHasMovedTowardsDest   = false;

        // FIX O: Piso del destino
        private int _destFloor = 0;

        // v5.5 FIX P: Convergencia de llegada declarada en Inspector arriba

        // ─────────────────────────────────────────────────────────────────────
        //  Propiedad dinámica de modo AR
        // ─────────────────────────────────────────────────────────────────────
        private bool IsFullAR => _navAgent != null && _navAgent.IsFullARMode;

        // ─────────────────────────────────────────────────────────────────────
        //  LIFECYCLE
        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            _navAgent = GetComponent<NavigationAgent>();
            _rawAgent = GetComponent<NavMeshAgent>();
            if (_rawAgent != null)
            {
                _rawAgent.speed = Mathf.Min(_rawAgent.speed, _maxAgentSpeed);
                _originalSpeed  = _rawAgent.speed;
            }
        }

        private void OnEnable()  => SubscribeEvents();
        private void OnDisable() => UnsubscribeEvents();

        private void Start()
        {
            SubscribeEvents();

            if (_xrOrigin == null)
                _xrOrigin = FindFirstObjectByType<XROrigin>();

            _userBridge = FindFirstObjectByType<UserPositionBridge>(FindObjectsInactive.Include);
            if (_userBridge == null)
                Debug.LogWarning("[ARGuideController] ⚠️ UserPositionBridge no encontrado.");

            if (IsFullAR && _rawAgent != null && _rawAgent.enabled && _rawAgent.isOnNavMesh)
                _rawAgent.isStopped = true;

            InvokeRepeating(nameof(EvaluateState), 0.5f, _evaluationInterval);
        }

        private void SubscribeEvents()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Subscribe<NavigationStartedEvent>  (OnNavigationStarted);
            bus.Subscribe<NavigationCompletedEvent>(OnNavigationCompleted);
            bus.Subscribe<NavigationCancelledEvent>(OnNavigationCancelled);
            bus.Subscribe<FloorTransitionEvent>    (OnFloorTransition);
            bus.Subscribe<TTSSpeakingEvent>        (OnTTSSpeaking);
            bus.Subscribe<GuideAnnouncementEvent>  (OnGuideAnnouncement);
        }

        private void UnsubscribeEvents()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Unsubscribe<NavigationStartedEvent>  (OnNavigationStarted);
            bus.Unsubscribe<NavigationCompletedEvent>(OnNavigationCompleted);
            bus.Unsubscribe<NavigationCancelledEvent>(OnNavigationCancelled);
            bus.Unsubscribe<FloorTransitionEvent>    (OnFloorTransition);
            bus.Unsubscribe<TTSSpeakingEvent>        (OnTTSSpeaking);
            bus.Unsubscribe<GuideAnnouncementEvent>  (OnGuideAnnouncement);
        }

        private void Update()
        {
            bool fullAR = IsFullAR;

            if (fullAR)
            {
                UpdateFullARMode();
                return;
            }

            if (!_initialPauseDone && _hasDestination)
            {
                _initialPauseTimer += Time.deltaTime;
                if (_initialPauseTimer >= _initialNavigationPause)
                {
                    _initialPauseDone = true;
                    if (_currentState == GuideState.WaitingToStart)
                    {
                        ResumeAgent();
                        TransitionTo(GuideState.Leading);
                        if (_logAccessibility)
                            Debug.Log($"[ARGuideController] ♿ Pausa inicial completada ({_initialNavigationPause}s).");
                    }
                }
                return;
            }

            if (_isReorienting) PerformSmoothReorientation();

            if (_ttsPauseActive)
            {
                _ttsPauseTimer += Time.deltaTime;
                if (_ttsPauseTimer >= _maxTTSWaitTime)
                {
                    if (_logTTSSync) Debug.Log("[ARGuideController] ⏱️ Timeout TTS.");
                    ResumFromTTSPause();
                }
            }

            if (_inPostTurnPause)
            {
                _postTurnPauseTimer += Time.deltaTime;
                float userSpeed  = _userBridge?.UserSpeed ?? 0f;
                bool  userMoving = userSpeed >= _postTurnResumeSpeed;
                bool  timedOut   = _postTurnPauseTimer >= _postTurnMaxWait;
                bool  canResume  = (_postTurnPauseTimer >= _postTurnPauseSeconds && userMoving) || timedOut;

                if (canResume)
                {
                    _inPostTurnPause    = false;
                    _postTurnPauseTimer = 0f;
                    if (timedOut && !userMoving)
                        Debug.LogWarning("[ARGuideController] ♿ Timeout post-giro.");
                    ResumeAgent();
                    if (_currentState == GuideState.PausedAfterTurn)
                        TransitionTo(GuideState.Leading);
                }
            }

            if (_currentState == GuideState.WaitingAtGoal)
                EvaluateArrivalConfirmation();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  FullAR — Update
        // ─────────────────────────────────────────────────────────────────────

        private void UpdateFullARMode()
        {
            if (!_hasDestination) return;

            // FIX N: Evaluar si el usuario se acerca al destino
            if (!_userHasMovedTowardsDest)
            {
                float currentDist = DistXZ(GetUserPosition(), _guideDestination);
                float reduction   = _initialDistToDest - currentDist;

                if (reduction >= _arrivalMovementThreshold)
                {
                    _userHasMovedTowardsDest = true;
                    if (_logFullAR)
                        Debug.Log($"[ARGuideController] 📡 [FullAR] FIX N: usuario se acercó " +
                                  $"{reduction:F1}m — timeout de llegada habilitado.");
                }
            }

            if (_ttsPauseActive)
            {
                _ttsPauseTimer += Time.deltaTime;
                if (_ttsPauseTimer >= _maxTTSWaitTime) { _ttsPauseActive = false; _ttsPauseTimer = 0f; }
            }

            EvaluateArrivalInFullAR();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  v5.5 FIX P — Evaluación de llegada FullAR mejorada
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateArrivalInFullAR()
        {
            if (_navCompletedFired) return;

            _arrivalWaitTimer += Time.deltaTime;
            if (_arrivalWaitTimer < _arrivalMinDelay) return;

            Vector3 userPos = GetUserPosition();

            // FIX O: Verificar piso
            int userFloor = ResolveFloorForY(userPos.y);
            if (userFloor != _destFloor)
            {
                _arrivalConvergenceCount = 0;
                return;
            }

            // v5.5 FIX P: Usar distancia XZ como criterio principal
            float distXZ = DistXZ(userPos, _guideDestination);
            float deltaY = Mathf.Abs(userPos.y - _guideDestination.y);

            bool timedOut = _arrivalWaitTimer >= _arrivalTimeout;

            if (timedOut && distXZ > _arrivalTimeoutConfirmDist)
            {
                _arrivalConvergenceCount = 0;
                return;
            }

            if (timedOut && !_userHasMovedTowardsDest)
            {
                _arrivalConvergenceCount = 0;
                return;
            }

            // v5.5 FIX P: Zona de llegada estable
            bool inArrivalZone = distXZ <= _arrivalConfirmDist &&
                                 deltaY <= _arrivalVerticalTolerance;

            if (inArrivalZone)
                _arrivalConvergenceCount++;
            else
                _arrivalConvergenceCount = Mathf.Max(0, _arrivalConvergenceCount - 1);

            bool stableArrival = _arrivalConvergenceCount >= Mathf.Max(1, _arrivalStableFramesRequired);
            bool timeoutArrival = timedOut &&
                                  distXZ <= _arrivalTimeoutConfirmDist &&
                                  deltaY <= _arrivalVerticalTolerance  &&
                                  _userHasMovedTowardsDest;

            if (stableArrival || timeoutArrival)
            {
                if (_logFullAR)
                    Debug.Log($"[ARGuideController] 📡 FullAR: llegada confirmada | " +
                              $"distXZ={distXZ:F2}m | dY={deltaY:F2}m | " +
                              $"convergencia={_arrivalConvergenceCount}/{_arrivalStableFramesRequired} | " +
                              $"timeout={timedOut} | piso={userFloor}=={_destFloor}");

                _navCompletedFired = true;
                FireNavigationCompleted();
            }
        }

        private void OnGuideAnnouncement(GuideAnnouncementEvent evt) { }

        // ─────────────────────────────────────────────────────────────────────
        //  TTS
        // ─────────────────────────────────────────────────────────────────────

        private void OnTTSSpeaking(TTSSpeakingEvent evt)
        {
            if (!_pauseForHighPriorityTTS || !_hasDestination) return;
            if (_currentState == GuideState.WaitingAtGoal || _currentState == GuideState.Idle) return;

            bool fullAR      = IsFullAR;
            bool shouldPause = evt.IsSpeaking && evt.Priority >= _ttsPauseMinPriority;

            if (shouldPause && !_ttsPauseActive)
            {
                _stateBeforePause = _currentState;
                _ttsPauseActive   = true;
                _ttsPauseTimer    = 0f;
                if (!fullAR) PauseAgent();
                TransitionTo(GuideState.PausedForTTS);
                if (_logTTSSync)
                    Debug.Log($"[ARGuideController] ⏸️ {(fullAR ? "[FullAR]" : "")} TTS p={evt.Priority}");
            }
            else if (!evt.IsSpeaking && _ttsPauseActive)
            {
                ResumFromTTSPause();
            }
        }

        private void ResumFromTTSPause()
        {
            if (!_ttsPauseActive) return;
            _ttsPauseActive = false;
            _ttsPauseTimer  = 0f;

            bool fullAR = IsFullAR;
            var  stateToRestore = _stateBeforePause != GuideState.PausedForTTS
                ? _stateBeforePause : GuideState.Leading;

            if (fullAR)
            {
                TransitionTo(stateToRestore == GuideState.Leading ? GuideState.Idle : stateToRestore);
                if (_logTTSSync) Debug.Log("[ARGuideController] 📡 [FullAR] TTS terminó.");
                return;
            }

            if (_stateBeforePause == GuideState.Leading && !_inPostTurnPause)
            {
                _inPostTurnPause    = true;
                _postTurnPauseTimer = 0f;
                TransitionTo(GuideState.PausedAfterTurn);
                return;
            }

            ResumeAgent();
            TransitionTo(stateToRestore);
            if (_logTTSSync) Debug.Log($"[ARGuideController] ▶️ Reanudado → {stateToRestore}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  v5.5 FIX Q — Llegada NoAR con distancia XZ
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateArrivalConfirmation()
        {
            if (_navCompletedFired) return;
            _arrivalWaitTimer += Time.deltaTime;

            Vector3 userPos = GetUserPosition();

            int userFloor = ResolveFloorForY(userPos.y);
            if (userFloor != _destFloor)
            {
                _arrivalConvergenceCount = 0;
                return;
            }

            float distXZ = DistXZ(userPos, _guideDestination);
            float deltaY = Mathf.Abs(userPos.y - _guideDestination.y);

            bool inArrivalZone = distXZ <= _arrivalXZDistance &&
                                 deltaY <= _arrivalVerticalTolerance;

            if (inArrivalZone)
                _arrivalConvergenceCount++;
            else
                _arrivalConvergenceCount = Mathf.Max(0, _arrivalConvergenceCount - 1);

            bool stableArrival = _arrivalConvergenceCount >= _arrivalConvergenceFrames;
            bool timedOut      = _arrivalWaitTimer >= _arrivalTimeout;

            if (stableArrival || (timedOut && distXZ <= _arrivalTimeoutConfirmDist))
            {
                if (_logStateChanges)
                    Debug.Log($"[ARGuideController] 🏁 Llegada NoAR — distXZ={distXZ:F1}m | " +
                              $"dY={deltaY:F1}m | convergencia={_arrivalConvergenceCount}");
                _navCompletedFired = true;
                FireNavigationCompleted();
            }
        }

        private void FireNavigationCompleted()
        {
            bool wasHandling = _isHandlingNavigation;
            _isHandlingNavigation = true;
            try
            {
                EventBus.Instance?.Publish(new NavigationCompletedEvent
                {
                    DestinationWaypointId = string.Empty,
                    TotalDistance         = 0f,
                    TotalTime             = _arrivalWaitTimer,
                });
            }
            finally { _isHandlingNavigation = wasHandling; }
            StopGuide();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVENTBUS HANDLERS
        // ─────────────────────────────────────────────────────────────────────

        private void OnNavigationStarted(NavigationStartedEvent evt)
        {
            if (_isHandlingNavigation) return;
            _isHandlingNavigation = true;
            try   { SetGuideDestination(evt.DestinationPosition); }
            finally { _isHandlingNavigation = false; }
        }

        private void OnNavigationCompleted(NavigationCompletedEvent evt)
        {
            if (_navCompletedFired || _isHandlingNavigation) return;
            _isHandlingNavigation = true;
            try   { StopGuide(); }
            finally { _isHandlingNavigation = false; }
        }

        private void OnNavigationCancelled(NavigationCancelledEvent evt)
        {
            if (_isHandlingNavigation) return;
            _isHandlingNavigation = true;
            try   { StopGuide(); }
            finally { _isHandlingNavigation = false; }
        }

        private void OnFloorTransition(FloorTransitionEvent evt)
        {
            bool fullAR = IsFullAR;

            if (evt.ToLevel == _lastProcessedFloorTo &&
                Time.time - _lastFloorTransitionTime < _floorTransitionDedup)
            {
                return;
            }

            _lastProcessedFloorTo    = evt.ToLevel;
            _lastFloorTransitionTime = Time.time;

            _currentFloor   = evt.ToLevel;
            _isOnStairs     = false;
            _climbAnnounced = false;
            _stairAnnounced = false;
            if (!fullAR) RestoreSpeed();

            if (fullAR && _suppressFloorAnnouncementsInFullAR)
            {
                if (_logFullAR)
                    Debug.Log($"[ARGuideController] 📡 [FullAR] FloorTransition {evt.FromLevel}→{evt.ToLevel} (sin anuncio).");
                return;
            }

            float expectedY = GetFloorHeight(evt.ToLevel);
            float userY     = GetUserPosition().y;
            if (expectedY != float.MinValue && Mathf.Abs(userY - expectedY) > _floorYTolerance)
            {
                if (_logStateChanges)
                    Debug.Log($"[ARGuideController] 🔇 FloorTransition ignorado: UserY={userY:F2} lejos de expectedY={expectedY:F2}");
                return;
            }

            string floorName = evt.ToLevel == 0 ? "planta baja" : $"piso {evt.ToLevel}";
            Announce(GuideAnnouncementType.FloorReached,
                evt.ToLevel > evt.FromLevel ? $"Has llegado al {floorName}" : $"Has bajado a {floorName}");
            Announce(GuideAnnouncementType.StairsComplete, "Tramo de escaleras completado");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  API PÚBLICA
        // ─────────────────────────────────────────────────────────────────────

        public void SetGuideDestination(Vector3 destination)
        {
            bool fullAR = IsFullAR;

            _guideDestination    = destination;
            _hasDestination      = true;
            _isReorienting       = false;
            _stairAnnounced      = false;
            _climbAnnounced      = false;
            _isOnStairs          = false;
            _navCompletedFired   = false;
            _arrivalWaitTimer    = 0f;
            _arrivalStableFrames = 0;
            _arrivalConvergenceCount = 0;
            _ttsPauseActive      = false;
            _ttsPauseTimer       = 0f;
            _initialPauseDone    = false;
            _initialPauseTimer   = 0f;
            _inPostTurnPause     = false;
            _postTurnPauseTimer  = 0f;
            _lastStairAnnounceTime = -999f; // ✅ v5.6: reset cooldown escaleras al iniciar ruta

            // FIX N
            _userHasMovedTowardsDest = false;
            _initialDistToDest       = DistXZ(GetUserPosition(), destination);

            // FIX O
            _destFloor = ResolveFloorForY(destination.y);

            // ✅ v5.6 FIX R: Determinar si la ruta implica cambio de piso real
            _routeIncludesFloorChange = _destFloor != _currentFloor;

            if (_logStairs)
                Debug.Log($"[ARGuideController] 🪜 [v5.6] routeIncludesFloorChange={_routeIncludesFloorChange} " +
                          $"(currentFloor={_currentFloor} destFloor={_destFloor})");

            if (fullAR)
            {
                if (_logFullAR)
                    Debug.Log($"[ARGuideController] 📡 [FullAR] Destino: {destination:F2}. " +
                              $"initialDistXZ={_initialDistToDest:F1}m. " +
                              $"destFloor={_destFloor}.");
                if (_rawAgent != null && _rawAgent.enabled && _rawAgent.isOnNavMesh)
                    _rawAgent.isStopped = true;
                TransitionTo(GuideState.Leading);
                return;
            }

            if (_rawAgent != null)
            {
                _rawAgent.speed = Mathf.Min(_rawAgent.speed, _maxAgentSpeed);
                _originalSpeed  = _rawAgent.speed;
            }
            RestoreAgentRotationControl();
            RestoreSpeed();
            NavStart(destination);
            PauseAgent();
            TransitionTo(GuideState.WaitingToStart);

            if (_logAccessibility)
                Debug.Log($"[ARGuideController] ♿ [NoAR] Destino establecido. Pausa {_initialNavigationPause}s.");
        }

        public void StopGuide()
        {
            bool fullAR = IsFullAR;

            _hasDestination          = false;
            _isReorienting           = false;
            _isOnStairs              = false;
            _stairAnnounced          = false;
            _climbAnnounced          = false;
            _agentWasPaused          = false;
            _ttsPauseActive          = false;
            _ttsPauseTimer           = 0f;
            _navCompletedFired       = false;
            _arrivalWaitTimer        = 0f;
            _arrivalConvergenceCount = 0;
            _initialPauseDone        = false;
            _initialPauseTimer       = 0f;
            _inPostTurnPause         = false;
            _postTurnPauseTimer      = 0f;
            _userHasMovedTowardsDest = false;
            _initialDistToDest       = float.MaxValue;
            _destFloor               = 0;
            _routeIncludesFloorChange = false; // ✅ v5.6
            _lastStairAnnounceTime   = -999f;  // ✅ v5.6

            if (!fullAR) { RestoreAgentRotationControl(); RestoreSpeed(); }

            bool wasHandling = _isHandlingNavigation;
            _isHandlingNavigation = true;
            try   { _navAgent.StopNavigation(fullAR ? "Guía detenida (FullAR)" : "Guía detenida"); }
            finally { _isHandlingNavigation = wasHandling; }

            if (fullAR && _logFullAR) Debug.Log("[ARGuideController] 📡 [FullAR] Navegación detenida.");
            TransitionTo(GuideState.Idle);
        }

        public GuideState CurrentState => _currentState;

        // ─────────────────────────────────────────────────────────────────────
        //  MÁQUINA DE ESTADOS
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateState()
        {
            if (IsFullAR) return;

            if (_currentState == GuideState.PausedForTTS     ||
                _currentState == GuideState.WaitingAtGoal    ||
                _currentState == GuideState.WaitingToStart   ||
                _currentState == GuideState.PausedAfterTurn) return;

            if (!_hasDestination || _xrOrigin == null || _navAgent == null) return;

            Vector3 userPos  = GetUserPosition();
            Vector3 guidePos = transform.position;
            float   distance = Vector3.Distance(guidePos, userPos);

            switch (_currentState)
            {
                case GuideState.Leading:           EvaluateLeading(userPos, guidePos, distance);           break;
                case GuideState.Waiting:           EvaluateWaiting(userPos, distance);                     break;
                case GuideState.Returning:         EvaluateReturning(userPos, distance);                   break;
                case GuideState.Reorienting:       EvaluateReorienting(userPos, guidePos, distance);       break;
                case GuideState.ApproachingStairs: EvaluateApproachingStairs(userPos, guidePos, distance); break;
            }
        }

        private void EvaluateLeading(Vector3 userPos, Vector3 guidePos, float distance)
        {
            if (IsFullAR)
            {
                if (DistXZ(userPos, _guideDestination) <= _arrivalConfirmDist)
                {
                    PauseAgent();
                    _arrivalWaitTimer = 0f;
                    TransitionTo(GuideState.WaitingAtGoal);
                    Announce(GuideAnnouncementType.ResumeGuide, "Has llegado al destino.");
                }
                return;
            }

            float distNPCToGoal = Vector3.Distance(guidePos, _guideDestination);
            if (distNPCToGoal < 0.8f && _rawAgent != null &&
                !_rawAgent.pathPending &&
                (_rawAgent.remainingDistance < 0.5f || !_rawAgent.hasPath))
            {
                PauseAgent();
                _arrivalWaitTimer = 0f;
                TransitionTo(GuideState.WaitingAtGoal);
                Announce(GuideAnnouncementType.ResumeGuide, "El guía llegó al destino. Continúa avanzando hacia él.");
                return;
            }

            if (distance > _maxReturnDistance)
            {
                _isReorienting = false;
                RestoreAgentRotationControl();
                RestoreSpeed();
                NavStart(userPos);
                TransitionTo(GuideState.Returning);
                Announce(GuideAnnouncementType.WaitingForUser, "El guía vuelve hacia ti. Detente y espera.");
                return;
            }

            // ✅ v5.6 FIX R: Solo detectar escaleras si la ruta incluye cambio de piso
            //    y no se ha superado el cooldown de anuncio.
            if (distance <= _safeDistance && StairDetectionAllowed() && DetectStairsAhead(guidePos))
            {
                if (!_stairAnnounced)
                {
                    _stairAnnounced        = true;
                    _lastStairAnnounceTime = Time.time;
                    PauseAgent();
                    Announce(GuideAnnouncementType.ApproachingStairs, "Atención: escaleras próximas.");
                    TransitionTo(GuideState.ApproachingStairs);
                    return;
                }
            }

            if (distance > _safeDistance)
            {
                _isReorienting = false;
                RestoreAgentRotationControl();
                PauseAgent();
                if (!_stairAnnounced)
                    Announce(GuideAnnouncementType.WaitingForUser, "El guía espera. Por favor acércate.");
                TransitionTo(GuideState.Waiting);
                return;
            }

            Vector3 toUser = userPos - guidePos;
            toUser.y = 0f;
            if (toUser.sqrMagnitude > 0.01f)
            {
                float angle = Vector3.Angle(transform.forward, toUser.normalized);
                if (angle > _maxAngle)
                {
                    PauseAgent();
                    TakeRotationControl();
                    _reorientTarget = userPos;
                    TransitionTo(GuideState.Reorienting);
                    return;
                }
            }

            if (_rawAgent != null && _originalSpeed > 0f)
            {
                float halfSafe    = _safeDistance * 0.5f;
                float targetSpeed = distance < halfSafe
                    ? Mathf.Max(_minLeadingSpeed, _originalSpeed * _closedistSpeedFactor)
                    : _isOnStairs ? _originalSpeed * _stairSpeedFactor : _originalSpeed;
                if (!Mathf.Approximately(_rawAgent.speed, targetSpeed))
                    _rawAgent.speed = targetSpeed;
            }

            if (_isOnStairs && !_climbAnnounced)
            {
                _climbAnnounced = true;
                bool climbing = IsClimbing();
                Announce(climbing ? GuideAnnouncementType.StartingClimb : GuideAnnouncementType.StartingDescent,
                         climbing ? "Iniciando subida. Ve despacio." : "Bajando escaleras. Agárrate al pasamanos.");
            }
        }

        private void EvaluateApproachingStairs(Vector3 userPos, Vector3 guidePos, float distance)
        {
            if (distance > _maxReturnDistance)
            {
                _stairAnnounced = false;
                RestoreAgentRotationControl();
                RestoreSpeed();
                NavStart(userPos);
                TransitionTo(GuideState.Returning);
                return;
            }
            if (distance <= _stairConfirmDistance)
            {
                _isOnStairs     = true;
                _climbAnnounced = false;
                if (_rawAgent != null && _originalSpeed > 0f)
                    _rawAgent.speed = _originalSpeed * _stairSpeedFactor;
                RestoreAgentRotationControl();
                ResumeAgent();
                Announce(GuideAnnouncementType.ResumeGuide, "Continuando. Sigue de cerca al guía.");
                TransitionTo(GuideState.Leading);
            }
        }

        private void EvaluateWaiting(Vector3 userPos, float distance)
        {
            if (distance > _maxReturnDistance)
            {
                RestoreAgentRotationControl();
                RestoreSpeed();
                NavStart(userPos);
                TransitionTo(GuideState.Returning);
                return;
            }
            if (distance <= _safeDistance)
            {
                RestoreAgentRotationControl();
                ResumeAgent();
                Announce(GuideAnnouncementType.ResumeGuide, "Continuando la ruta.");
                TransitionTo(GuideState.Leading);
            }
        }

        private void EvaluateReturning(Vector3 userPos, float distance)
        {
            if (distance <= _safeDistance)
            {
                RestoreAgentRotationControl();
                RestoreSpeed();
                NavStart(_guideDestination);
                Announce(GuideAnnouncementType.ResumeGuide, "Te encontré. Continuamos al destino.");
                TransitionTo(GuideState.Leading);
                return;
            }
            if (_rawAgent != null && Vector3.Distance(_rawAgent.destination, userPos) > 0.5f)
                NavStart(userPos);
        }

        private void EvaluateReorienting(Vector3 userPos, Vector3 guidePos, float distance)
        {
            if (distance > _maxReturnDistance)
            {
                _isReorienting = false;
                RestoreAgentRotationControl();
                NavStart(userPos);
                TransitionTo(GuideState.Returning);
                return;
            }
            _reorientTarget = userPos;
            Vector3 toUser = userPos - guidePos;
            toUser.y = 0f;
            if (toUser.sqrMagnitude > 0.01f &&
                Vector3.Angle(transform.forward, toUser.normalized) <= _maxAngle * 0.5f)
            {
                _isReorienting = false;
                RestoreAgentRotationControl();
                ResumeAgent();
                TransitionTo(GuideState.Leading);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ✅ v5.6 FIX R — Guard de detección de escaleras
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Devuelve true solo cuando está permitido anunciar escaleras:
        ///   1. La ruta incluye cambio de piso real (o _requireFloorChangeForStairAnnounce=false).
        ///   2. Ha pasado suficiente tiempo desde el último anuncio de escaleras.
        /// </summary>
        private bool StairDetectionAllowed()
        {
            if (_requireFloorChangeForStairAnnounce && !_routeIncludesFloorChange)
            {
                if (_logStairs)
                    Debug.Log("[ARGuideController] 🪜 [v5.6] Detección escaleras bloqueada — " +
                              "ruta sin cambio de piso real.");
                return false;
            }

            float elapsed = Time.time - _lastStairAnnounceTime;
            if (elapsed < _stairAnnounceCooldown)
            {
                if (_logStairs)
                    Debug.Log($"[ARGuideController] 🪜 [v5.6] Cooldown escaleras activo — " +
                              $"{elapsed:F1}s / {_stairAnnounceCooldown}s.");
                return false;
            }

            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>v5.5: Distancia horizontal XZ entre dos puntos (ignora altura).</summary>
        private static float DistXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private void PauseAgent()
        {
            if (IsFullAR || _rawAgent == null) return;
            _rawAgent.isStopped = true;
            _agentWasPaused     = true;
        }

        private void ResumeAgent()
        {
            if (IsFullAR || _rawAgent == null) return;
            _rawAgent.isStopped = false;
            _agentWasPaused     = false;
        }

        private void NavStart(Vector3 destination)
        {
            if (IsFullAR) return;
            if (_rawAgent != null)
            {
                _rawAgent.isStopped = false;
                if (_agentWasPaused && _rawAgent.hasPath) _rawAgent.ResetPath();
                _agentWasPaused = false;
            }
            bool wasHandling = _isHandlingNavigation;
            _isHandlingNavigation = true;
            try   { _navAgent.StartNavigation(destination); }
            finally { _isHandlingNavigation = wasHandling; }
        }

        private Vector3 GetUserPosition()
        {
            if (_userBridge != null) return _userBridge.UserPosition;
            if (_xrOrigin != null && _xrOrigin.Camera != null) return _xrOrigin.Camera.transform.position;
            return Camera.main != null ? Camera.main.transform.position : transform.position;
        }

        private static float GetFloorHeight(int level)
        {
            var pts = NavigationStartPointManager.GetAllStartPoints();
            foreach (var pt in pts) if (pt.Level == level) return pt.FloorHeight;
            return float.MinValue;
        }

        private static int ResolveFloorForY(float worldY)
        {
            var pts = NavigationStartPointManager.GetAllStartPoints();
            if (pts == null || pts.Count == 0) return 0;
            int   bestLevel = 0;
            float bestDist  = float.MaxValue;
            foreach (var pt in pts)
            {
                float dist = Mathf.Abs(worldY - pt.FloorHeight);
                if (dist < bestDist) { bestDist = dist; bestLevel = pt.Level; }
            }
            return bestLevel;
        }

        private bool DetectStairsAhead(Vector3 currentPos)
        {
            if (_currentState != GuideState.Leading || _rawAgent == null || !_rawAgent.hasPath) return false;
            _pathCornersCount = _rawAgent.path.GetCornersNonAlloc(_pathCornersBuffer);
            if (_pathCornersCount < 2) return false;
            float accDist  = 0f;
            float currentY = currentPos.y;
            for (int i = 0; i < _pathCornersCount - 1; i++)
            {
                Vector3 from = i == 0 ? currentPos : _pathCornersBuffer[i];
                Vector3 to   = _pathCornersBuffer[i + 1];
                accDist += Vector3.Distance(new Vector3(from.x, 0, from.z), new Vector3(to.x, 0, to.z));
                if (accDist > _stairWarningDistance) break;
                if (Mathf.Abs(to.y - currentY) >= _stairHeightThreshold) return true;
            }
            return false;
        }

        private bool IsClimbing()
        {
            if (_pathCornersCount < 2) return true;
            float currentY = transform.position.y;
            for (int i = 0; i < _pathCornersCount; i++)
            {
                float delta = _pathCornersBuffer[i].y - currentY;
                if (Mathf.Abs(delta) >= _stairHeightThreshold) return delta > 0f;
            }
            return true;
        }

        private void PerformSmoothReorientation()
        {
            Vector3 toTarget = _reorientTarget - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.001f) return;
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(toTarget.normalized),
                _rotationSpeed * Time.deltaTime / 180f);
        }

        private void RestoreSpeed()
        {
            if (IsFullAR) return;
            if (_rawAgent != null && _originalSpeed > 0f &&
                !Mathf.Approximately(_rawAgent.speed, _originalSpeed))
                _rawAgent.speed = _originalSpeed;
        }

        private void TakeRotationControl()
        {
            if (_rawAgent != null) _rawAgent.updateRotation = false;
            _isReorienting = true;
        }

        private void RestoreAgentRotationControl()
        {
            if (_rawAgent != null) _rawAgent.updateRotation = true;
            _isReorienting = false;
        }

        private void Announce(GuideAnnouncementType type, string message)
        {
            EventBus.Instance?.Publish(new GuideAnnouncementEvent
            {
                AnnouncementType = type,
                Message          = message,
                CurrentFloor     = _currentFloor
            });
        }

        private void TransitionTo(GuideState newState)
        {
            if (_currentState == newState) return;
            if (_logStateChanges)
                Debug.Log($"[ARGuideController] {_currentState} → {newState}" +
                          (IsFullAR ? " [FullAR]" : ""));
            _currentState = newState;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  GIZMOS
        // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!_drawGizmos) return;
            Vector3 pos    = transform.position;
            bool    fullAR = IsFullAR;

            Gizmos.color = _safeColor;   DrawCircle(pos, _safeDistance,        32);
            Gizmos.color = _minColor;    DrawCircle(pos, _minDistance,          32);
            Gizmos.color = _returnColor; DrawCircle(pos, _maxReturnDistance,    48);
            Gizmos.color = _stairColor;  DrawCircle(pos, _stairWarningDistance, 32);

            if (Application.isPlaying && _hasDestination)
            {
                Gizmos.color = _destinationColor;
                Gizmos.DrawSphere(_guideDestination, 0.15f);
                Gizmos.DrawLine(pos, _guideDestination);

                if (fullAR)
                {
                    Gizmos.color = new Color(0f, 1f, 0.5f, 0.3f);
                    DrawCircle(_guideDestination, _arrivalConfirmDist, 32);
                }
                else
                {
                    Gizmos.color = new Color(0f, 1f, 0.5f, 0.3f);
                    DrawCircle(_guideDestination, _arrivalXZDistance, 32);
                }
            }

            GUIStyle style = new GUIStyle
            {
                normal    = new GUIStyleState { textColor = GetStateColor() },
                fontSize  = 12,
                fontStyle = UnityEngine.FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };

            string floorInfo  = fullAR && _hasDestination ? $" 🏢→piso{_destFloor}" : string.Empty;
            string convInfo   = _hasDestination ? $" conv={_arrivalConvergenceCount}" : string.Empty;
            // ✅ v5.6: mostrar si la ruta incluye cambio de piso
            string floorChange = _hasDestination && _routeIncludesFloorChange ? " 🪜↕" : string.Empty;

            UnityEditor.Handles.Label(pos + Vector3.up * 1.8f,
                $"[{(fullAR ? "AR" : "NoAR")} {_currentState}" +
                (_isOnStairs ? " 🪜" : "") + (_ttsPauseActive ? " 🔊" : "") +
                floorInfo + convInfo + floorChange + "]",
                style);
        }

        private void DrawCircle(Vector3 center, float radius, int segments)
        {
            float   step = 360f / segments;
            Vector3 prev = center + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float   rad  = i * step * Mathf.Deg2Rad;
                Vector3 next = center + new Vector3(Mathf.Cos(rad) * radius, 0f, Mathf.Sin(rad) * radius);
                Gizmos.DrawLine(prev, next);
                prev = next;
            }
        }

        private Color GetStateColor() => _currentState switch
        {
            GuideState.Leading           => IsFullAR ? Color.cyan : Color.green,
            GuideState.Waiting           => Color.yellow,
            GuideState.Returning         => new Color(1f, 0.4f, 0f),
            GuideState.Reorienting       => Color.cyan,
            GuideState.ApproachingStairs => Color.magenta,
            GuideState.WaitingAtGoal     => new Color(0f, 1f, 0.5f),
            GuideState.PausedForTTS      => new Color(0.8f, 0.8f, 0f),
            GuideState.PausedAfterTurn   => new Color(1f, 0.5f, 0f),
            GuideState.WaitingToStart    => new Color(0.5f, 0.5f, 1f),
            _                            => Color.white
        };
#endif

        // ─────────────────────────────────────────────────────────────────────
        //  CONTEXT MENU
        // ─────────────────────────────────────────────────────────────────────

        [ContextMenu("ℹ️ Estado actual")]
        private void DebugStatus()
        {
            Vector3 userPos  = GetUserPosition();
            bool    fullAR   = IsFullAR;
            float   dxz      = DistXZ(userPos, _guideDestination);
            float   dy       = Mathf.Abs(userPos.y - _guideDestination.y);
            Debug.Log(
                $"[ARGuideController] v5.6\n" +
                $"  IsFullAR = {fullAR}\n" +
                $"  Estado = {_currentState}\n" +
                $"  UserPos = {userPos:F2}\n" +
                $"  DistXZ al destino = {dxz:F2}m (threshold={_arrivalConfirmDist}m)\n" +
                $"  DeltaY = {dy:F2}m (tolerance={_arrivalVerticalTolerance}m)\n" +
                $"  Dest = {_guideDestination:F2}\n" +
                $"  ArrivalTimer = {_arrivalWaitTimer:F1}s / {_arrivalTimeout}s\n" +
                $"  Convergencia = {_arrivalConvergenceCount}/{_arrivalConvergenceFrames}\n" +
                $"  [FIX N] userHasMoved={_userHasMovedTowardsDest} | " +
                $"initialDist={_initialDistToDest:F1}m\n" +
                $"  [FIX O] destFloor={_destFloor} | userFloor={ResolveFloorForY(userPos.y)}\n" +
                $"  [FIX R v5.6] routeIncludesFloorChange={_routeIncludesFloorChange} | " +
                $"lastStairAnnounce={_lastStairAnnounceTime:F1}s | cooldown={_stairAnnounceCooldown}s");
        }

        [ContextMenu("🚀 Test: destino 10m al frente")]
        private void DebugSetDest() => SetGuideDestination(transform.position + transform.forward * 10f);

        [ContextMenu("⏹ Test: StopGuide")]
        private void DebugStop() => StopGuide();
    }
}