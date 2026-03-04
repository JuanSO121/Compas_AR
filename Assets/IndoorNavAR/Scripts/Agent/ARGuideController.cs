// File: ARGuideController.cs
// Carpeta: Assets/IndoorNavAR/Agent/
//
// ============================================================================
//  NPC GUÍA — IndoorNavAR  v4.5  (fix NavStart con isStopped=true previo)
// ============================================================================
//
//  BUG v4.4 → v4.5:
//  ─────────────────────────────────────────────────────────────────────────
//  PROBLEMA:
//    [VoiceGuide] 'VirtualAssistant' sin ruta tras 3,00s
//    (pathStatus=PathComplete pathPending=False)
//
//  SÍNTOMA:
//    El NavMeshAgent del VirtualAssistant tiene hasPath=False durante todo
//    el timeout de 3s de WaitForPathAndPreprocess(), aunque NavigationAgent.
//    StartNavigation() fue llamado correctamente.
//
//  CAUSA RAÍZ:
//    En Unity, NavMeshAgent.SetDestination() es silenciosamente IGNORADO
//    cuando isStopped=true en el mismo frame. El flujo problemático:
//
//      EvaluateLeading() detecta ángulo > _maxAngle
//        → PauseAgent()  → _rawAgent.isStopped = true
//        → TransitionTo(Reorienting)
//
//      EvaluateReorienting() (tick siguiente) — usuario muy lejos
//        → NavStart(userPos)
//            → _rawAgent.isStopped = false   ← OK
//            → _navAgent.StartNavigation()
//                → internamente: _rawAgent.SetDestination(destination)
//                   Unity procesa isStopped=false y SetDestination en el
//                   MISMO FRAME. En algunas versiones de Unity (2022+),
//                   el cambio de isStopped no se aplica hasta el siguiente
//                   tick del NavMesh, por lo que SetDestination llega con
//                   el agente aún "parado" → path descartado.
//
//      Resultado: hasPath=false, pathPending=false, pathStatus=PathComplete
//      (el PathComplete es del path ANTERIOR, ya invalidado).
//      VoiceGuide espera 3s y emite WARNING.
//
//  FIX:
//    NavStart() ahora hace:
//      1. _rawAgent.isStopped = false
//      2. Si hay path stale (hasPath=true pero íbamos a cambiar destino),
//         _rawAgent.ResetPath() — limpia el path anterior para que
//         SetDestination parta de un estado limpio.
//      3. yield return null (un frame) antes de StartNavigation() cuando
//         el agente venía de isStopped=true.
//
//    Como NavStart() es síncrono (no corrutina), la solución práctica
//    y segura es: ResetPath() antes de StartNavigation(). Esto garantiza
//    que el NavMeshAgent esté en estado "sin path" cuando recibe el nuevo
//    SetDestination, forzando un cálculo limpio en lugar de reutilizar
//    el path invalidado.
//
//    IMPORTANTE: ResetPath() NO cancela la navegación global —
//    solo borra el path local del NavMeshAgent. No publica ningún evento.
//
//  CAMBIOS RESPECTO A v4.4:
//    + NavStart(): añadido _rawAgent.ResetPath() antes de StartNavigation()
//      cuando el agente tiene un path stale.
//    + _wasStoppedBeforeNavStart: flag interno para detectar si el agente
//      venía de PauseAgent() — no es necesario con ResetPath(), pero se
//      documenta para claridad.
//
//  TODO LO DEMÁS ES IDÉNTICO A v4.4.

using UnityEngine;
using UnityEngine.AI;
using Unity.XR.CoreUtils;
using IndoorNavAR.Navigation;
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
            ApproachingStairs
        }

        // ─── Inspector — Referencias ──────────────────────────────────────────

        [Header("─── Referencias ─────────────────────────────────────────────")]
        [Tooltip("XROrigin de la escena. Si se deja vacío se busca automáticamente.")]
        [SerializeField] private XROrigin _xrOrigin;

        // ─── Inspector — Distancias generales ────────────────────────────────

        [Header("─── Distancias ──────────────────────────────────────────────")]
        [Tooltip("Distancia máxima al usuario antes de detenerse a esperarlo (m).")]
        [SerializeField] private float _safeDistance       = 3f;

        [Tooltip("Distancia mínima al usuario. El guía no se acercará más de esto.")]
        [SerializeField] private float _minDistance        = 1.5f;

        [Tooltip("Distancia a la que el guía regresa hacia el usuario en lugar de solo esperar.")]
        [SerializeField] private float _maxReturnDistance  = 5f;

        // ─── Inspector — Escaleras ────────────────────────────────────────────

        [Header("─── Escaleras (accesibilidad) ────────────────────────────────")]
        [Tooltip("Distancia (m) a la que el guía avisa de escaleras próximas y se detiene a esperar.")]
        [SerializeField] private float _stairWarningDistance = 2.5f;

        [Tooltip("Distancia (m) a la que el usuario debe estar del guía para que este continúe.")]
        [SerializeField] private float _stairConfirmDistance = 2.0f;

        [Tooltip("Factor de reducción de velocidad durante el tramo de escaleras.")]
        [Range(0.2f, 1f)]
        [SerializeField] private float _stairSpeedFactor   = 0.5f;

        [Tooltip("Diferencia mínima de altura (m) para considerar escaleras próximas.")]
        [SerializeField] private float _stairHeightThreshold = 0.4f;

        // ─── Inspector — Ángulo ───────────────────────────────────────────────

        [Header("─── Ángulo ──────────────────────────────────────────────────")]
        [Tooltip("Ángulo máximo (grados) antes de que el guía gire para encarar al usuario.")]
        [SerializeField] private float _maxAngle           = 120f;

        [Tooltip("Velocidad de rotación suave en Reorienting (grados/segundo).")]
        [SerializeField] private float _rotationSpeed      = 90f;

        // ─── Inspector — Evaluación ───────────────────────────────────────────

        [Header("─── Evaluación ─────────────────────────────────────────────")]
        [Tooltip("Intervalo de evaluación de estado (s).")]
        [SerializeField] private float _evaluationInterval = 0.25f;

        // ─── Inspector — Debug ────────────────────────────────────────────────

        [Header("─── Debug ────────────────────────────────────────────────────")]
        [SerializeField] private bool _logStateChanges = true;
        [SerializeField] private bool _logEvaluations  = false;
        [SerializeField] private bool _logStairs       = true;

        // ─── Inspector — Gizmos ───────────────────────────────────────────────

        [Header("─── Gizmos ──────────────────────────────────────────────────")]
        [SerializeField] private bool  _drawGizmos       = true;
        [SerializeField] private Color _safeColor        = new Color(0f, 1f, 0f,   0.25f);
        [SerializeField] private Color _returnColor      = new Color(1f, 0.4f, 0f, 0.20f);
        [SerializeField] private Color _minColor         = new Color(0f, 0.8f, 1f, 0.20f);
        [SerializeField] private Color _destinationColor = new Color(1f, 1f, 0f,   0.80f);
        [SerializeField] private Color _stairColor       = new Color(1f, 0f,   1f, 0.35f);

        // ─── Estado interno ───────────────────────────────────────────────────

        private NavigationAgent _navAgent;
        private NavMeshAgent    _rawAgent;
        private GuideState      _currentState   = GuideState.Idle;
        private Vector3         _guideDestination;
        private bool            _hasDestination = false;

        // Guard re-entrada (v4.3): evita recursión en handlers del EventBus
        // cuando NavStart/NavStop publican eventos sincrónicamente.
        private bool _isHandlingNavigation = false;

        private float _originalSpeed  = -1f;
        private int   _currentFloor   = 0;
        private bool  _isOnStairs     = false;
        private bool  _stairAnnounced = false;
        private bool  _climbAnnounced = false;

        private readonly Vector3[] _pathCornersBuffer = new Vector3[32];
        private int _pathCornersCount = 0;

        private bool    _isReorienting = false;
        private Vector3 _reorientTarget;

        // ✅ v4.5: flag para saber si el agente venía de PauseAgent() antes
        // de llamar a NavStart(). Usado para decidir si hacer ResetPath().
        private bool _agentWasPaused = false;

        // ─────────────────────────────────────────────────────────────────────
        //  LIFECYCLE
        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            _navAgent = GetComponent<NavigationAgent>();
            _rawAgent = GetComponent<NavMeshAgent>();

            if (_navAgent == null)
                Debug.LogError("[ARGuideController] ❌ NavigationAgent no encontrado en " +
                               $"{gameObject.name}.");

            if (_rawAgent != null)
                _originalSpeed = _rawAgent.speed;
        }

        private void Start()
        {
            if (_xrOrigin == null)
            {
                _xrOrigin = FindFirstObjectByType<XROrigin>();
                if (_xrOrigin == null)
                    Debug.LogError("[ARGuideController] ❌ XROrigin no encontrado. " +
                                   "Asígnalo manualmente en el Inspector.");
                else
                    Debug.Log("[ARGuideController] ✅ XROrigin detectado automáticamente.");
            }

            InvokeRepeating(nameof(EvaluateState), 0.5f, _evaluationInterval);
        }

        private void OnEnable()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Subscribe<NavigationStartedEvent>   (OnNavigationStarted);
            bus.Subscribe<NavigationCompletedEvent> (OnNavigationCompleted);
            bus.Subscribe<NavigationCancelledEvent> (OnNavigationCancelled);
            bus.Subscribe<FloorTransitionEvent>     (OnFloorTransition);
        }

        private void OnDisable()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Unsubscribe<NavigationStartedEvent>  (OnNavigationStarted);
            bus.Unsubscribe<NavigationCompletedEvent>(OnNavigationCompleted);
            bus.Unsubscribe<NavigationCancelledEvent>(OnNavigationCancelled);
            bus.Unsubscribe<FloorTransitionEvent>    (OnFloorTransition);
        }

        private void Update()
        {
            if (_isReorienting)
                PerformSmoothReorientation();
        }

        private void OnDestroy()
        {
            CancelInvoke(nameof(EvaluateState));
            RestoreSpeed();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVENTBUS HANDLERS
        // ─────────────────────────────────────────────────────────────────────

        private void OnNavigationStarted(NavigationStartedEvent evt)
        {
            if (_isHandlingNavigation) return;
            _isHandlingNavigation = true;
            try
            {
                Log($"🎯 NavigationStartedEvent → destino {evt.DestinationPosition}");
                SetGuideDestination(evt.DestinationPosition);
            }
            finally { _isHandlingNavigation = false; }
        }

        private void OnNavigationCompleted(NavigationCompletedEvent evt)
        {
            if (_isHandlingNavigation) return;
            _isHandlingNavigation = true;
            try
            {
                Log("✅ NavigationCompletedEvent → Idle.");
                StopGuide();
            }
            finally { _isHandlingNavigation = false; }
        }

        private void OnNavigationCancelled(NavigationCancelledEvent evt)
        {
            if (_isHandlingNavigation) return;
            _isHandlingNavigation = true;
            try
            {
                Log($"🛑 NavigationCancelledEvent ({evt.Reason}) → Idle.");
                StopGuide();
            }
            finally { _isHandlingNavigation = false; }
        }

        private void OnFloorTransition(FloorTransitionEvent evt)
        {
            int previousFloor = _currentFloor;
            _currentFloor   = evt.ToLevel;
            _isOnStairs     = false;
            _climbAnnounced = false;
            _stairAnnounced = false;
            RestoreSpeed();

            if (_logStairs)
                Debug.Log($"[ARGuideController] 🏢 FloorTransition: piso {previousFloor} → {_currentFloor}");

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

        public void SetGuideDestination(Vector3 destination)
        {
            _guideDestination = destination;
            _hasDestination   = true;
            _isReorienting    = false;
            _stairAnnounced   = false;
            _climbAnnounced   = false;
            _isOnStairs       = false;

            RestoreAgentRotationControl();
            RestoreSpeed();
            NavStart(destination);
            TransitionTo(GuideState.Leading);
        }

        public void StopGuide()
        {
            _hasDestination   = false;
            _isReorienting    = false;
            _isOnStairs       = false;
            _stairAnnounced   = false;
            _climbAnnounced   = false;
            _agentWasPaused   = false;

            RestoreAgentRotationControl();
            RestoreSpeed();

            // StopNavigation aquí SÍ es correcto: el guía termina su sesión.
            bool wasHandling = _isHandlingNavigation;
            _isHandlingNavigation = true;
            try   { _navAgent.StopNavigation("Guía detenida"); }
            finally { _isHandlingNavigation = wasHandling; }

            TransitionTo(GuideState.Idle);
        }

        public GuideState CurrentState => _currentState;

        // ─────────────────────────────────────────────────────────────────────
        //  MÁQUINA DE ESTADOS
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateState()
        {
            if (!_hasDestination || _xrOrigin == null || _navAgent == null) return;

            Vector3 userPos  = _xrOrigin.Camera.transform.position;
            Vector3 guidePos = transform.position;
            float   distance = Vector3.Distance(guidePos, userPos);

            if (_logEvaluations)
                Debug.Log($"[ARGuideController] [{_currentState}] dist={distance:F2}m stairs={_isOnStairs}");

            switch (_currentState)
            {
                case GuideState.Leading:           EvaluateLeading(userPos, guidePos, distance);           break;
                case GuideState.Waiting:           EvaluateWaiting(userPos, distance);                     break;
                case GuideState.Returning:         EvaluateReturning(userPos, distance);                   break;
                case GuideState.Reorienting:       EvaluateReorienting(userPos, guidePos, distance);       break;
                case GuideState.ApproachingStairs: EvaluateApproachingStairs(userPos, guidePos, distance); break;
                case GuideState.Idle: break;
            }
        }

        // ─── Leading ─────────────────────────────────────────────────────────

        private void EvaluateLeading(Vector3 userPos, Vector3 guidePos, float distance)
        {
            // PRIORIDAD 1: usuario muy lejos → Returning (ruta nueva hacia él)
            if (distance > _maxReturnDistance)
            {
                _isReorienting = false;
                RestoreAgentRotationControl();
                RestoreSpeed();
                NavStart(userPos);
                TransitionTo(GuideState.Returning);
                return;
            }

            // PRIORIDAD 2: escaleras próximas → ApproachingStairs
            if (distance <= _safeDistance && DetectStairsAhead(guidePos))
            {
                if (!_stairAnnounced)
                {
                    _stairAnnounced = true;
                    PauseAgent();
                    Announce(GuideAnnouncementType.ApproachingStairs,
                             "Atención: hay escaleras próximas. Por favor acércate al guía");
                    if (_logStairs)
                        Debug.Log("[ARGuideController] 🪜 Escaleras detectadas — esperando usuario");
                    TransitionTo(GuideState.ApproachingStairs);
                    return;
                }
            }

            // PRIORIDAD 3: usuario moderadamente lejos → Waiting
            if (distance > _safeDistance)
            {
                _isReorienting = false;
                RestoreAgentRotationControl();
                PauseAgent();
                if (!_stairAnnounced)
                    Announce(GuideAnnouncementType.WaitingForUser,
                             "Por favor acércate al guía para continuar");
                TransitionTo(GuideState.Waiting);
                return;
            }

            // PRIORIDAD 4: ángulo excesivo → Reorienting
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

            // PRIORIDAD 5: escaleras activas → reducir velocidad y anunciar
            if (_isOnStairs && _rawAgent != null && _originalSpeed > 0f)
            {
                float targetSpeed = _originalSpeed * _stairSpeedFactor;
                if (!Mathf.Approximately(_rawAgent.speed, targetSpeed))
                    _rawAgent.speed = targetSpeed;

                if (!_climbAnnounced)
                {
                    _climbAnnounced = true;
                    bool climbing = IsClimbing();
                    Announce(climbing ? GuideAnnouncementType.StartingClimb
                                      : GuideAnnouncementType.StartingDescent,
                             climbing ? "Iniciando subida de escaleras. Ve con cuidado"
                                      : "Iniciando bajada de escaleras. Ve con cuidado");
                }
            }
        }

        // ─── ApproachingStairs ────────────────────────────────────────────────

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
                Announce(GuideAnnouncementType.ResumeGuide,
                         "Continuando la ruta. Sigue de cerca al guía");
                TransitionTo(GuideState.Leading);
            }
        }

        // ─── Waiting ─────────────────────────────────────────────────────────

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
                Announce(GuideAnnouncementType.ResumeGuide, "Continuando la ruta");
                TransitionTo(GuideState.Leading);
            }
        }

        // ─── Returning ───────────────────────────────────────────────────────

        private void EvaluateReturning(Vector3 userPos, float distance)
        {
            if (distance <= _safeDistance)
            {
                RestoreAgentRotationControl();
                RestoreSpeed();
                NavStart(_guideDestination);
                Announce(GuideAnnouncementType.ResumeGuide, "Continuando la ruta");
                TransitionTo(GuideState.Leading);
                return;
            }

            if (_rawAgent != null &&
                Vector3.Distance(_rawAgent.destination, userPos) > 0.5f)
                NavStart(userPos);
        }

        // ─── Reorienting ─────────────────────────────────────────────────────

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
                float angle = Vector3.Angle(transform.forward, toUser.normalized);
                if (angle <= _maxAngle * 0.5f)
                {
                    _isReorienting = false;
                    RestoreAgentRotationControl();
                    ResumeAgent();
                    TransitionTo(GuideState.Leading);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS — PauseAgent / ResumeAgent / NavStart / NavStop
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Pausa el movimiento del NavMeshAgent SIN publicar
        /// NavigationCancelledEvent. Usar para pausas internas del guía
        /// (Waiting, Reorienting, ApproachingStairs).
        /// La ruta se conserva; ResumeAgent() la retoma.
        /// </summary>
        private void PauseAgent()
        {
            if (_rawAgent == null) return;
            _rawAgent.isStopped  = true;
            _agentWasPaused      = true;
        }

        /// <summary>
        /// Reanuda el movimiento del NavMeshAgent SIN publicar
        /// NavigationStartedEvent. Usar para reanudar tras una pausa interna.
        /// </summary>
        private void ResumeAgent()
        {
            if (_rawAgent == null) return;
            _rawAgent.isStopped = false;
            _agentWasPaused     = false;
        }

        /// <summary>
        /// ✅ FIX v4.5 — Inicia una navegación completa hacia un nuevo destino.
        ///
        /// PROBLEMA ANTERIOR:
        ///   Si el agente tenía isStopped=true (de PauseAgent) y luego se
        ///   llamaba StartNavigation() en el mismo frame que isStopped=false,
        ///   Unity 2022+ descartaba silenciosamente SetDestination() porque
        ///   el cambio de isStopped no se propagaba hasta el siguiente tick
        ///   del NavMesh. Resultado: hasPath=false indefinidamente.
        ///
        /// FIX:
        ///   Si el agente venía de PauseAgent() (_agentWasPaused=true),
        ///   llamar ResetPath() antes de StartNavigation(). ResetPath() pone
        ///   el agente en estado limpio (hasPath=false, pathPending=false)
        ///   sin publicar ningún evento, y garantiza que el próximo
        ///   SetDestination() sea procesado correctamente.
        ///
        /// Publica NavigationStartedEvent — usar solo cuando se cambia de
        /// destino (Returning hacia usuario, retomar ruta tras Returning).
        /// </summary>
        private void NavStart(Vector3 destination)
        {
            if (_rawAgent != null)
            {
                _rawAgent.isStopped = false;

                // ✅ FIX v4.5: si el agente venía pausado, limpiar path stale
                // para que SetDestination sea aceptado en el mismo frame.
                if (_agentWasPaused && _rawAgent.hasPath)
                {
                    _rawAgent.ResetPath();
                    if (_logStateChanges)
                        Debug.Log("[ARGuideController] 🔄 ResetPath() antes de NavStart (agente venía de PauseAgent).");
                }
                _agentWasPaused = false;
            }

            bool wasHandling = _isHandlingNavigation;
            _isHandlingNavigation = true;
            try   { _navAgent.StartNavigation(destination); }
            finally { _isHandlingNavigation = wasHandling; }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  DETECCIÓN DE ESCALERAS
        // ─────────────────────────────────────────────────────────────────────

        private bool DetectStairsAhead(Vector3 currentPos)
        {
            if (_currentState != GuideState.Leading) return false;
            if (_rawAgent == null || !_rawAgent.hasPath) return false;

            _pathCornersCount = _rawAgent.path.GetCornersNonAlloc(_pathCornersBuffer);
            if (_pathCornersCount < 2) return false;

            float accumulatedDistance = 0f;
            float currentY            = currentPos.y;

            for (int i = 0; i < _pathCornersCount - 1; i++)
            {
                Vector3 from = i == 0 ? currentPos : _pathCornersBuffer[i];
                Vector3 to   = _pathCornersBuffer[i + 1];

                accumulatedDistance += Vector3.Distance(
                    new Vector3(from.x, 0, from.z),
                    new Vector3(to.x,   0, to.z));

                if (accumulatedDistance > _stairWarningDistance) break;

                float heightDelta = Mathf.Abs(to.y - currentY);
                if (heightDelta >= _stairHeightThreshold)
                {
                    if (_logStairs)
                        Debug.Log($"[ARGuideController] 🪜 Escaleras detectadas: " +
                                  $"Δh={heightDelta:F2}m a {accumulatedDistance:F2}m");
                    return true;
                }
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

            Quaternion targetRot = Quaternion.LookRotation(toTarget.normalized);
            transform.rotation = Quaternion.Slerp(
                transform.rotation, targetRot,
                _rotationSpeed * Time.deltaTime / 180f);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS — Velocidad / Rotación
        // ─────────────────────────────────────────────────────────────────────

        private void RestoreSpeed()
        {
            if (_rawAgent != null && _originalSpeed > 0f &&
                !Mathf.Approximately(_rawAgent.speed, _originalSpeed))
            {
                _rawAgent.speed = _originalSpeed;
                if (_logStairs)
                    Debug.Log($"[ARGuideController] 🏃 Velocidad restaurada: {_originalSpeed:F1} m/s");
            }
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

            if (_logStateChanges)
                Debug.Log($"[ARGuideController] 🔊 [{type}] \"{message}\" (piso {_currentFloor})");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS — Transiciones / Log
        // ─────────────────────────────────────────────────────────────────────

        private void TransitionTo(GuideState newState)
        {
            if (_currentState == newState) return;
            if (_logStateChanges)
                Debug.Log($"[ARGuideController] 🔄 {_currentState} → {newState}");
            _currentState = newState;
        }

        private void Log(string msg)
        {
            if (_logStateChanges) Debug.Log($"[ARGuideController] {msg}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  GIZMOS
        // ─────────────────────────────────────────────────────────────────────

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

#if UNITY_EDITOR
            if (Application.isPlaying && _rawAgent != null && _rawAgent.hasPath)
            {
                int count = _pathCornersCount > 0
                    ? _pathCornersCount
                    : _rawAgent.path.GetCornersNonAlloc(_pathCornersBuffer);
                for (int i = 0; i < count - 1; i++)
                {
                    float hd = Mathf.Abs(_pathCornersBuffer[i + 1].y - _pathCornersBuffer[i].y);
                    Gizmos.color = hd >= _stairHeightThreshold
                        ? new Color(1f, 0f, 1f, 0.9f)
                        : new Color(0f, 1f, 0f, 0.6f);
                    Gizmos.DrawLine(_pathCornersBuffer[i], _pathCornersBuffer[i + 1]);
                }
            }

            GUIStyle style = new GUIStyle
            {
                normal    = new GUIStyleState { textColor = GetStateColor() },
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            string stateLabel = _currentState.ToString() + (_isOnStairs ? " 🪜" : "");
            UnityEditor.Handles.Label(pos + Vector3.up * 1.8f, $"[{stateLabel}]", style);
#endif
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
            GuideState.Leading           => Color.green,
            GuideState.Waiting           => Color.yellow,
            GuideState.Returning         => new Color(1f, 0.4f, 0f),
            GuideState.Reorienting       => Color.cyan,
            GuideState.ApproachingStairs => Color.magenta,
            _                            => Color.white
        };

        // ─────────────────────────────────────────────────────────────────────
        //  CONTEXT MENU
        // ─────────────────────────────────────────────────────────────────────

        [ContextMenu("ℹ️ Estado actual")]
        private void DebugStatus()
        {
            Debug.Log($"[ARGuideController] Estado={_currentState} | " +
                      $"HasDest={_hasDestination} | Dest={_guideDestination} | " +
                      $"Floor={_currentFloor} | OnStairs={_isOnStairs} | " +
                      $"StairAnnounced={_stairAnnounced} | " +
                      $"AgentWasPaused={_agentWasPaused} | " +
                      $"NavAgentNavigating={_navAgent?.IsNavigating} | " +
                      $"AgentStopped={_rawAgent?.isStopped} | " +
                      $"AgentHasPath={_rawAgent?.hasPath} | " +
                      $"Speed={_rawAgent?.speed:F1}/{_originalSpeed:F1}");
        }

        [ContextMenu("🚀 Test: guiar 10m al frente")]
        private void DebugSetDest() =>
            SetGuideDestination(transform.position + transform.forward * 10f);

        [ContextMenu("⏹ Test: StopGuide")]
        private void DebugStop() => StopGuide();

        [ContextMenu("🪜 Test: Simular FloorTransition (0→1)")]
        private void DebugFloorUp() =>
            OnFloorTransition(new FloorTransitionEvent
            { FromLevel = 0, ToLevel = 1, AgentPosition = transform.position });

        [ContextMenu("🪜 Test: Simular FloorTransition (1→0)")]
        private void DebugFloorDown() =>
            OnFloorTransition(new FloorTransitionEvent
            { FromLevel = 1, ToLevel = 0, AgentPosition = transform.position });

        [ContextMenu("🔊 Test: Anunciar escaleras próximas")]
        private void DebugAnnounceStairs() =>
            Announce(GuideAnnouncementType.ApproachingStairs,
                     "Atención: hay escaleras próximas. Por favor acércate al guía");
    }
}