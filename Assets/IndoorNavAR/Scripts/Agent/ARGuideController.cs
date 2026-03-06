// File: ARGuideController.cs
// Carpeta: Assets/IndoorNavAR/Agent/
// ✅ v4.9 — FullAR: lógica de movimiento del agente completamente desactivada
//
// ══════════════════════════════════════════════════════════════════════════════
// CAMBIOS v4.8 → v4.9  (soporte FullAR)
// ══════════════════════════════════════════════════════════════════════════════
//
// PROBLEMA:
//   ARGuideController gestiona el movimiento autónomo del agente NPC
//   (Leading, Waiting, Returning, etc.). En FullAR el agente debe estar
//   ESTÁTICO: es AROriginAligner quien lo posiciona siguiendo la cámara XR.
//   Si ARGuideController intentaba mover el agente en FullAR, colisionaba
//   con la sincronización de AROriginAligner, produciendo:
//     • El agente saltando entre la posición del usuario y el destino.
//     • NavMesh.Warp() llamado desde dos sitios en el mismo frame.
//     • Instrucciones de voz disparadas en el lugar equivocado.
//
// SOLUCIÓN v4.9:
//   1. Al iniciar (Start/Awake), detectar el modo AR mediante NavigationAgent.IsFullARMode.
//   2. Si IsFullARMode:
//      • EvaluateState() retorna inmediatamente sin ejecutar ninguna lógica.
//      • SetGuideDestination() solo registra el destino pero NO mueve el agente.
//        El agente permanece donde AROriginAligner lo dejó (posición del usuario).
//        NavigationPathController calcula la ruta desde esa posición.
//      • PauseAgent() / ResumeAgent() no llaman a _rawAgent.isStopped en FullAR
//        (AROriginAligner es el único que gestiona isStopped).
//      • La sincronización TTS sigue funcionando en FullAR (pausa/reanuda
//        solo la emisión de instrucciones, no el movimiento).
//   3. Si IsNoArMode (comportamiento original v4.8 íntegro):
//      • El agente camina de Leading → Waiting → Returning como antes.
//      • La cámara sigue al agente (FollowAgent en AROriginAligner).
//
// TODOS LOS CAMBIOS DE ACCESIBILIDAD v4.8 SE CONSERVAN ÍNTEGRAMENTE:
//   - Velocidad 0.5 m/s, pausa inicial 3.5s, pausa post-giro 4s, etc.
//
// ══════════════════════════════════════════════════════════════════════════════

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
        // ─── Estados ──────────────────────────────────────────────────────────

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

        // ─── Inspector ────────────────────────────────────────────────────────

        [Header("─── Referencias ─────────────────────────────────────────────")]
        [SerializeField] private XROrigin _xrOrigin;

        [Header("─── Distancias (accesibilidad) ──────────────────────────────")]
        [SerializeField] private float _safeDistance      = 2f;
        [SerializeField] private float _minDistance       = 1.0f;
        [SerializeField] private float _maxReturnDistance = 3f;

        [Header("─── Llegada ─────────────────────────────────────────────────")]
        [SerializeField] private float _arrivalConfirmDist = 1.5f;
        [SerializeField] private float _arrivalTimeout     = 45.0f;
        [Tooltip("Tiempo mínimo (s) antes de evaluar llegada en FullAR. " +
                 "Evita falsos positivos si el usuario ya está cerca del destino al iniciar.")]
        [SerializeField] private float _arrivalMinDelay    = 3.0f;

        [Header("─── Velocidad (accesibilidad) ──────────────────────────────")]
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

        [Header("─── Sincronización con TTS ────────────────────────────────")]
        [SerializeField] private bool  _pauseForHighPriorityTTS = true;
        [SerializeField] private int   _ttsPauseMinPriority  = 1;
        [SerializeField] private float _maxTTSWaitTime       = 15.0f;

        [Header("─── Escaleras ────────────────────────────────────────────────")]
        [SerializeField] private float _stairWarningDistance = 2.5f;
        [SerializeField] private float _stairConfirmDistance = 1.5f;
        [Range(0.1f, 1f)]
        [SerializeField] private float _stairHeightThreshold = 0.4f;

        [Header("─── Ángulo ──────────────────────────────────────────────────")]
        [SerializeField] private float _maxAngle      = 120f;
        [SerializeField] private float _rotationSpeed = 90f;

        [Header("─── Evaluación ─────────────────────────────────────────────")]
        [SerializeField] private float _evaluationInterval = 0.25f;

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

        // ─── Estado interno ───────────────────────────────────────────────────

        private NavigationAgent _navAgent;
        private NavMeshAgent    _rawAgent;
        private GuideState      _currentState     = GuideState.Idle;
        private GuideState      _stateBeforePause = GuideState.Idle;
        private Vector3         _guideDestination;
        private bool            _hasDestination       = false;
        private bool            _isHandlingNavigation = false;

        // ✅ v4.9: modo FullAR detectado al inicio
        private bool _isFullAR = false;

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

        private bool  _ttsPauseActive   = false;
        private float _ttsPauseTimer    = 0f;
        private float _arrivalWaitTimer = 0f;
        private bool  _navCompletedFired = false;

        private bool  _initialPauseDone    = false;
        private float _initialPauseTimer   = 0f;
        private float _postTurnPauseTimer  = 0f;
        private bool  _inPostTurnPause     = false;

        private UserPositionBridge _userBridge;

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

            // ✅ v4.9: Detectar modo AR al inicio
            // NavigationAgent.IsFullARMode busca AROriginAligner internamente.
            _isFullAR = _navAgent != null && _navAgent.IsFullARMode;

            if (_isFullAR)
            {
                if (_logFullAR)
                    Debug.Log("[ARGuideController] 📡 Modo FullAR detectado. " +
                              "La lógica de movimiento del agente está DESACTIVADA. " +
                              "Solo las instrucciones de voz y TTS están activas.");

                // En FullAR el agente debe permanecer detenido.
                // AROriginAligner gestiona su posición.
                if (_rawAgent != null && _rawAgent.enabled && _rawAgent.isOnNavMesh)
                    _rawAgent.isStopped = true;
            }
            else
            {
                if (_logAccessibility)
                    Debug.Log($"[ARGuideController] 📵 Modo NoAR. Accesibilidad activa:" +
                              $"\n  Velocidad máx: {_maxAgentSpeed} m/s" +
                              $"\n  Pausa inicial: {_initialNavigationPause}s" +
                              $"\n  Pausa post-giro: {_postTurnPauseSeconds}s (máx {_postTurnMaxWait}s)");
            }

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
            // ✅ v4.9: En FullAR, solo procesar TTS y pausa post-giro.
            // NO ejecutar lógica de movimiento del agente.
            if (_isFullAR)
            {
                UpdateFullARMode();
                return;
            }

            // ── Modo NoAR: comportamiento original ────────────────────────────

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
                            Debug.Log($"[ARGuideController] ♿ Pausa inicial completada ({_initialNavigationPause}s). " +
                                      "Iniciando movimiento.");
                    }
                }
                return;
            }

            if (_isReorienting)
                PerformSmoothReorientation();

            if (_ttsPauseActive)
            {
                _ttsPauseTimer += Time.deltaTime;
                if (_ttsPauseTimer >= _maxTTSWaitTime)
                {
                    if (_logTTSSync) Debug.Log("[ARGuideController] ⏱️ Timeout TTS — reanudando.");
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
                        Debug.LogWarning("[ARGuideController] ♿ Timeout post-giro. Reanudando.");
                    else if (_logAccessibility)
                        Debug.Log($"[ARGuideController] ♿ Usuario se movió ({userSpeed:F2} m/s) — " +
                                  $"reanudando tras pausa post-giro.");

                    ResumeAgent();
                    if (_currentState == GuideState.PausedAfterTurn)
                        TransitionTo(GuideState.Leading);
                }
            }

            if (_currentState == GuideState.WaitingAtGoal)
                EvaluateArrivalConfirmation();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ✅ v4.9 — LÓGICA EXCLUSIVA DE FullAR
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// En FullAR, el agente no se mueve. Solo procesamos:
        ///   1. TTS: pausas y timeouts de instrucciones.
        ///   2. Llegada: detectar cuando el usuario llega al destino.
        ///   3. Re-verificar que el NavMeshAgent siga detenido.
        ///
        /// NavigationVoiceGuide genera las instrucciones de giro/escalera
        /// basadas en la posición del agente (= posición del usuario en FullAR).
        /// AROriginAligner actualiza esa posición cada frame.
        /// </summary>
        private void UpdateFullARMode()
        {
            // Garantizar que el agente no se mueve por su cuenta
            if (_rawAgent != null && _rawAgent.enabled && _rawAgent.isOnNavMesh)
            {
                if (!_rawAgent.isStopped)
                {
                    _rawAgent.isStopped = true;
                    if (_logFullAR)
                        Debug.Log("[ARGuideController] 📡 FullAR: NavMeshAgent detenido en Update.");
                }
            }

            if (!_hasDestination) return;

            // Timeout TTS en FullAR también
            if (_ttsPauseActive)
            {
                _ttsPauseTimer += Time.deltaTime;
                if (_ttsPauseTimer >= _maxTTSWaitTime)
                {
                    _ttsPauseActive = false;
                    _ttsPauseTimer  = 0f;
                }
            }

            // Detectar llegada en FullAR: cuando el usuario (cámara XR) está cerca del destino
            if (_currentState == GuideState.WaitingAtGoal || _hasDestination)
                EvaluateArrivalInFullAR();
        }

        /// <summary>
        /// En FullAR, la llegada se confirma cuando el usuario (cámara XR / posición real)
        /// está dentro de _arrivalConfirmDist del destino.
        /// No dependemos del agente para esto — usamos UserPosition directamente.
        /// </summary>
        private void EvaluateArrivalInFullAR()
        {
            if (_navCompletedFired) return;

            _arrivalWaitTimer += Time.deltaTime;

            // Delay mínimo: no evaluar llegada en los primeros segundos.
            // Evita confirmación instantánea si el usuario ya estaba cerca del destino.
            if (_arrivalWaitTimer < _arrivalMinDelay) return;

            Vector3 userPos = GetUserPosition();
            float   dist    = Vector3.Distance(userPos, _guideDestination);

            bool timedOut = _arrivalWaitTimer >= _arrivalTimeout;

            if (dist <= _arrivalConfirmDist || timedOut)
            {
                if (_logFullAR)
                    Debug.Log($"[ARGuideController] 📡 FullAR: llegada confirmada. " +
                              $"dist={dist:F1}m | timeout={timedOut} | elapsed={_arrivalWaitTimer:F1}s");
                _navCompletedFired = true;
                FireNavigationCompleted();
            }
        }

        private void OnGuideAnnouncement(GuideAnnouncementEvent evt)
        {
            // Los anuncios de tipo giro activan la pausa post-giro en NoAR.
            // En FullAR no hay pausa de movimiento (el agente no se mueve),
            // pero sí gestionamos el estado interno para el TTS.
            // La implementación real está en ResumFromTTSPause().
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SINCRONIZACIÓN CON TTS
        // ─────────────────────────────────────────────────────────────────────

        private void OnTTSSpeaking(TTSSpeakingEvent evt)
        {
            if (!_pauseForHighPriorityTTS || !_hasDestination) return;
            if (_currentState == GuideState.WaitingAtGoal || _currentState == GuideState.Idle) return;

            bool shouldPause = evt.IsSpeaking && evt.Priority >= _ttsPauseMinPriority;

            if (shouldPause && !_ttsPauseActive)
            {
                _stateBeforePause = _currentState;
                _ttsPauseActive   = true;
                _ttsPauseTimer    = 0f;

                // En NoAR: pausar movimiento físico del agente
                if (!_isFullAR) PauseAgent();

                TransitionTo(GuideState.PausedForTTS);
                if (_logTTSSync)
                    Debug.Log($"[ARGuideController] ⏸️ {(_isFullAR ? "[FullAR]" : "")} " +
                              $"TTS pausa (prioridad {evt.Priority}).");
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

            var stateToRestore = _stateBeforePause != GuideState.PausedForTTS
                ? _stateBeforePause
                : GuideState.Leading;

            // En FullAR: no activamos pausa post-giro ni movimiento.
            // Solo restauramos el estado para que el VoiceGuide sepa que puede continuar.
            if (_isFullAR)
            {
                TransitionTo(stateToRestore == GuideState.Leading ? GuideState.Idle : stateToRestore);
                if (_logTTSSync)
                    Debug.Log($"[ARGuideController] 📡 [FullAR] TTS terminó — estado restaurado.");
                return;
            }

            // NoAR: pausa post-giro si el TTS era de instrucción de giro
            bool wasTurnInstruction = _stateBeforePause == GuideState.Leading;
            if (wasTurnInstruction && !_inPostTurnPause)
            {
                _inPostTurnPause    = true;
                _postTurnPauseTimer = 0f;
                TransitionTo(GuideState.PausedAfterTurn);

                if (_logAccessibility)
                    Debug.Log($"[ARGuideController] ♿ Pausa post-giro activada " +
                              $"({_postTurnPauseSeconds}s mín, {_postTurnMaxWait}s máx).");
                return;
            }

            ResumeAgent();
            TransitionTo(stateToRestore);

            if (_logTTSSync)
                Debug.Log($"[ARGuideController] ▶️ NPC reanudado → {stateToRestore}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CONFIRMACIÓN DE LLEGADA (NoAR)
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateArrivalConfirmation()
        {
            if (_navCompletedFired) return;
            _arrivalWaitTimer += Time.deltaTime;

            float dist        = Vector3.Distance(GetUserPosition(), _guideDestination);
            bool  userArrived = dist <= _arrivalConfirmDist;
            bool  timedOut    = _arrivalWaitTimer >= _arrivalTimeout;

            if (userArrived || timedOut)
            {
                if (_logStateChanges)
                    Debug.Log($"[ARGuideController] 🏁 Llegada confirmada — dist={dist:F1}m");
                _navCompletedFired = true;
                FireNavigationCompleted();
            }
        }

        private void FireNavigationCompleted()
        {
            bool wasHandling  = _isHandlingNavigation;
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
            _currentFloor   = evt.ToLevel;
            _isOnStairs     = false;
            _climbAnnounced = false;
            _stairAnnounced = false;
            if (!_isFullAR) RestoreSpeed();

            string floorName = _currentFloor == 0 ? "planta baja" : $"piso {_currentFloor}";
            string msg = evt.ToLevel > evt.FromLevel
                ? $"Has llegado al {floorName}"
                : $"Has bajado a {floorName}";

            Announce(GuideAnnouncementType.FloorReached, msg);
            Announce(GuideAnnouncementType.StairsComplete, "Tramo de escaleras completado");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  API PÚBLICA
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Establece el destino de navegación.
        ///
        /// En FullAR:
        ///   - Registra el destino para detectar llegada.
        ///   - NO mueve el agente (AROriginAligner lo gestiona).
        ///   - La ruta ya fue calculada por NavigationPathController.
        ///   - Transiciona a Idle (el agente no tiene estado de movimiento en FullAR).
        ///
        /// En NoAR:
        ///   - Comportamiento original: pausa inicial + WaitingToStart.
        /// </summary>
        public void SetGuideDestination(Vector3 destination)
        {
            _guideDestination    = destination;
            _hasDestination      = true;
            _isReorienting       = false;
            _stairAnnounced      = false;
            _climbAnnounced      = false;
            _isOnStairs          = false;
            _navCompletedFired   = false;
            _arrivalWaitTimer    = 0f;
            _ttsPauseActive      = false;
            _ttsPauseTimer       = 0f;
            _initialPauseDone    = false;
            _initialPauseTimer   = 0f;
            _inPostTurnPause     = false;
            _postTurnPauseTimer  = 0f;

            if (_isFullAR)
            {
                // FullAR: solo registrar destino. El agente no se mueve.
                // NavigationPathController ya calculó la ruta en NavigationAgent.NavigateToWaypoint().
                if (_logFullAR)
                    Debug.Log($"[ARGuideController] 📡 [FullAR] Destino registrado: {destination:F2}. " +
                              "El agente NO se moverá. VoiceGuide generará instrucciones basadas " +
                              "en la posición del usuario (cámara XR).");

                // Garantizar que el agente no se mueve
                if (_rawAgent != null && _rawAgent.enabled && _rawAgent.isOnNavMesh)
                    _rawAgent.isStopped = true;

                TransitionTo(GuideState.Leading); // Estado "activo" para que EvaluateArrivalInFullAR() funcione
                return;
            }

            // NoAR: comportamiento original
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
                Debug.Log($"[ARGuideController] ♿ [NoAR] Destino establecido. " +
                          $"Pausa inicial de {_initialNavigationPause}s.");
        }

        public void StopGuide()
        {
            _hasDestination    = false;
            _isReorienting     = false;
            _isOnStairs        = false;
            _stairAnnounced    = false;
            _climbAnnounced    = false;
            _agentWasPaused    = false;
            _ttsPauseActive    = false;
            _ttsPauseTimer     = 0f;
            _navCompletedFired = false;
            _arrivalWaitTimer  = 0f;
            _initialPauseDone    = false;
            _initialPauseTimer   = 0f;
            _inPostTurnPause     = false;
            _postTurnPauseTimer  = 0f;

            if (!_isFullAR)
            {
                RestoreAgentRotationControl();
                RestoreSpeed();

                bool wasHandling = _isHandlingNavigation;
                _isHandlingNavigation = true;
                try   { _navAgent.StopNavigation("Guía detenida"); }
                finally { _isHandlingNavigation = wasHandling; }
            }
            else
            {
                // FullAR: solo detener el path, no tocar isStopped (AROriginAligner lo gestiona)
                bool wasHandling = _isHandlingNavigation;
                _isHandlingNavigation = true;
                try   { _navAgent.StopNavigation("Guía detenida (FullAR)"); }
                finally { _isHandlingNavigation = wasHandling; }

                if (_logFullAR)
                    Debug.Log("[ARGuideController] 📡 [FullAR] Navegación detenida.");
            }

            TransitionTo(GuideState.Idle);
        }

        public GuideState CurrentState => _currentState;

        // ─────────────────────────────────────────────────────────────────────
        //  MÁQUINA DE ESTADOS (solo NoAR)
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateState()
        {
            // ✅ v4.9: En FullAR, no evaluar estados de movimiento.
            if (_isFullAR) return;

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
            float distNPCToGoal = Vector3.Distance(guidePos, _guideDestination);
            if (distNPCToGoal < 0.8f && _rawAgent != null &&
                !_rawAgent.pathPending &&
                (_rawAgent.remainingDistance < 0.5f || !_rawAgent.hasPath))
            {
                PauseAgent();
                _arrivalWaitTimer = 0f;
                TransitionTo(GuideState.WaitingAtGoal);
                Announce(GuideAnnouncementType.ResumeGuide,
                    "El guía llegó al destino. Continúa avanzando hacia él.");
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

            if (distance <= _safeDistance && DetectStairsAhead(guidePos))
            {
                if (!_stairAnnounced)
                {
                    _stairAnnounced = true;
                    PauseAgent();
                    Announce(GuideAnnouncementType.ApproachingStairs,
                             "Atención: hay escaleras próximas. Por favor acércate al guía.");
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
                    : _isOnStairs
                        ? _originalSpeed * _stairSpeedFactor
                        : _originalSpeed;

                if (!Mathf.Approximately(_rawAgent.speed, targetSpeed))
                    _rawAgent.speed = targetSpeed;
            }

            if (_isOnStairs && !_climbAnnounced)
            {
                _climbAnnounced = true;
                bool climbing = IsClimbing();
                Announce(climbing ? GuideAnnouncementType.StartingClimb : GuideAnnouncementType.StartingDescent,
                         climbing ? "Iniciando subida de escaleras. Ve despacio."
                                  : "Bajando escaleras. Agárrate al pasamanos.");
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
            if (toUser.sqrMagnitude > 0.01f)
            {
                if (Vector3.Angle(transform.forward, toUser.normalized) <= _maxAngle * 0.5f)
                {
                    _isReorienting = false;
                    RestoreAgentRotationControl();
                    ResumeAgent();
                    TransitionTo(GuideState.Leading);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS — PauseAgent / ResumeAgent / NavStart
        // ─────────────────────────────────────────────────────────────────────

        private void PauseAgent()
        {
            // En FullAR el agente ya está parado — no cambiar su estado
            if (_isFullAR) return;
            if (_rawAgent == null) return;
            _rawAgent.isStopped = true;
            _agentWasPaused     = true;
        }

        private void ResumeAgent()
        {
            // En FullAR el agente NO debe reanudar movimiento propio
            if (_isFullAR) return;
            if (_rawAgent == null) return;
            _rawAgent.isStopped = false;
            _agentWasPaused     = false;
        }

        private void NavStart(Vector3 destination)
        {
            // En FullAR el agente no navega por su cuenta
            if (_isFullAR) return;

            if (_rawAgent != null)
            {
                _rawAgent.isStopped = false;
                if (_agentWasPaused && _rawAgent.hasPath)
                    _rawAgent.ResetPath();
                _agentWasPaused = false;
            }

            bool wasHandling = _isHandlingNavigation;
            _isHandlingNavigation = true;
            try   { _navAgent.StartNavigation(destination); }
            finally { _isHandlingNavigation = wasHandling; }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS — Posición del usuario
        // ─────────────────────────────────────────────────────────────────────

        private Vector3 GetUserPosition()
        {
            if (_userBridge != null) return _userBridge.UserPosition;
            if (_xrOrigin != null && _xrOrigin.Camera != null) return _xrOrigin.Camera.transform.position;
            return Camera.main != null ? Camera.main.transform.position : transform.position;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  DETECCIÓN DE ESCALERAS
        // ─────────────────────────────────────────────────────────────────────

        private bool DetectStairsAhead(Vector3 currentPos)
        {
            if (_currentState != GuideState.Leading || _rawAgent == null || !_rawAgent.hasPath)
                return false;

            _pathCornersCount = _rawAgent.path.GetCornersNonAlloc(_pathCornersBuffer);
            if (_pathCornersCount < 2) return false;

            float accDist  = 0f;
            float currentY = currentPos.y;

            for (int i = 0; i < _pathCornersCount - 1; i++)
            {
                Vector3 from = i == 0 ? currentPos : _pathCornersBuffer[i];
                Vector3 to   = _pathCornersBuffer[i + 1];

                accDist += Vector3.Distance(
                    new Vector3(from.x, 0, from.z),
                    new Vector3(to.x,   0, to.z));

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
                if (Mathf.Abs(delta) >= _stairHeightThreshold)
                    return delta > 0f;
            }
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ROTACIÓN SUAVE
        // ─────────────────────────────────────────────────────────────────────

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

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS — Velocidad / Rotación
        // ─────────────────────────────────────────────────────────────────────

        private void RestoreSpeed()
        {
            if (_isFullAR) return;
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

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS — Anuncios TTS
        // ─────────────────────────────────────────────────────────────────────

        private void Announce(GuideAnnouncementType type, string message)
        {
            EventBus.Instance?.Publish(new GuideAnnouncementEvent
            {
                AnnouncementType = type,
                Message          = message,
                CurrentFloor     = _currentFloor
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS — Transiciones / Log
        // ─────────────────────────────────────────────────────────────────────

        private void TransitionTo(GuideState newState)
        {
            if (_currentState == newState) return;
            if (_logStateChanges)
                Debug.Log($"[ARGuideController] {_currentState} → {newState}" +
                          (_isFullAR ? " [FullAR - sin movimiento]" : ""));
            _currentState = newState;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  GIZMOS — solo en Editor
        // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!_drawGizmos) return;

            Vector3 pos = transform.position;
            Gizmos.color = _safeColor;   DrawCircle(pos, _safeDistance,        32);
            Gizmos.color = _minColor;    DrawCircle(pos, _minDistance,          32);
            Gizmos.color = _returnColor; DrawCircle(pos, _maxReturnDistance,    48);
            Gizmos.color = _stairColor;  DrawCircle(pos, _stairWarningDistance, 32);

            if (Application.isPlaying && _hasDestination)
            {
                Gizmos.color = _destinationColor;
                Gizmos.DrawSphere(_guideDestination, 0.15f);
                Gizmos.DrawLine(pos, _guideDestination);
            }

            if (Application.isPlaying && _currentState == GuideState.WaitingAtGoal)
            {
                Gizmos.color = new Color(0f, 1f, 0.5f, 0.4f);
                DrawCircle(_guideDestination, _arrivalConfirmDist, 32);
            }

            GUIStyle style = new GUIStyle
            {
                normal    = new GUIStyleState { textColor = GetStateColor() },
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };

            string modeTag  = _isFullAR ? "[AR]" : "[NoAR]";
            string pauseInfo = string.Empty;
            if (_inPostTurnPause)             pauseInfo = $" ⏸{_postTurnPauseTimer:F1}s";
            if (!_initialPauseDone && _hasDestination) pauseInfo = $" 🕐{_initialPauseTimer:F1}s";

            string label = $"{modeTag} {_currentState}"
                + (_isOnStairs ? " 🪜" : "")
                + (_ttsPauseActive ? " 🔊" : "")
                + pauseInfo;

            UnityEditor.Handles.Label(pos + Vector3.up * 1.8f, $"[{label}]", style);
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
            GuideState.Leading           => _isFullAR ? Color.cyan : Color.green,
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
            Vector3 userPos = GetUserPosition();
            Debug.Log(
                $"[ARGuideController] Estado={_currentState} | Modo={(_isFullAR ? "FullAR" : "NoAR")}\n" +
                $"HasDest={_hasDestination} | Dest={_guideDestination}\n" +
                $"UserPos={userPos:F2} | DistUser→Dest={Vector3.Distance(userPos, _guideDestination):F1}m\n" +
                $"Floor={_currentFloor} | OnStairs={_isOnStairs}\n" +
                $"TTSPauseActive={_ttsPauseActive} | Timer={_ttsPauseTimer:F1}s\n" +
                $"InitialPauseDone={_initialPauseDone} | Timer={_initialPauseTimer:F1}s\n" +
                $"InPostTurnPause={_inPostTurnPause} | Timer={_postTurnPauseTimer:F1}s\n" +
                $"NavMeshAgent stopped={_rawAgent?.isStopped} | Speed={_rawAgent?.speed:F1}m/s");
        }

        [ContextMenu("🚀 Test: guiar 10m al frente")]
        private void DebugSetDest() =>
            SetGuideDestination(transform.position + transform.forward * 10f);

        [ContextMenu("⏹ Test: StopGuide")]
        private void DebugStop() => StopGuide();

        [ContextMenu("♿ Test: Simular pausa post-giro")]
        private void DebugPostTurnPause()
        {
            if (_isFullAR) { Debug.Log("[ARGuideController] FullAR: pausa post-giro no aplica."); return; }
            _inPostTurnPause    = true;
            _postTurnPauseTimer = 0f;
            PauseAgent();
            TransitionTo(GuideState.PausedAfterTurn);
        }
    }
}