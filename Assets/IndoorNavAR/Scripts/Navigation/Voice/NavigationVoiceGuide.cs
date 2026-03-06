// File: NavigationVoiceGuide.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Navigation/Voice/
// ✅ v4.4 — Fix EvalPos en FullAR + WaitForPath robusto + StairsWarning no se repite
//
// ══════════════════════════════════════════════════════════════════════════════
// BUGS CORREGIDOS v4.3 → v4.4
// ══════════════════════════════════════════════════════════════════════════════
//
// ── BUG 1 CRÍTICO: StairsWarning se repetía en loop ─────────────────────────
//
//   SÍNTOMA (logs):
//     "[VoiceGuide] ⏳ Evento [StairsWarning] aún dentro de trigger (4.2m <= 6.0m).
//      Esperando..." → se repetía decenas de veces → timeout → guía activaba
//      con todos los eventos todavía dentro del radio de trigger.
//
//   CAUSA:
//     EvalPos en FullAR devolvía UserPositionBridge.AgentPosition.
//     El comentario de v4.3 decía "el NPC recorre el NavMesh, los triggers
//     deben dispararse cuando el NPC llega al punto". Esto era CORRECTO para
//     NoAR (donde el NPC se mueve) pero INCORRECTO para FullAR.
//
//     En FullAR el agente NPC es ESTÁTICO: AROriginAligner lo mueve solo
//     para reflejar la posición del usuario, NO para avanzar por la ruta.
//     Entonces AgentPosition ≈ UserPosition ≈ inicio de la ruta.
//
//     Resultado: el agente siempre estaba a 4.2m de la escalera (dentro
//     del trigger de 6.0m), el loop de "esperando" nunca terminaba, y al
//     hacer timeout el evento StairsWarning se disparaba inmediatamente.
//
//   FIX v4.4:
//     En FullAR, EvalPos = UserPos (posición real del usuario = cámara XR).
//     Son lo mismo en FullAR: el agente está sincronizado con el usuario.
//     En NoAR, EvalPos = AgentPosition (el NPC se mueve por el NavMesh,
//     puede estar adelante del usuario).
//
//     ADICIONALMENTE: el loop de "esperando" en WaitForPath ahora tiene
//     un límite distinto para cada tipo de evento:
//       - StartNavigation: siempre se activa (está en wp[0], siempre cerca)
//       - StairsWarning/Turn: se omiten del check de "dentro de trigger"
//         si la distancia actual es irreducible (usuario en el inicio)
//
// ── BUG 2: WaitForPath — loop de "dentro de trigger" nunca terminaba ─────────
//
//   CAUSA:
//     El loop revisaba si algún evento estaba dentro de su TriggerDistance.
//     En FullAR con escaleras cercanas al inicio, StairsWarning.TriggerDist
//     (6.0m) era mayor que la distancia actual (4.2m) desde el inicio.
//     El usuario no puede alejarse más del StartPoint hacia atrás, así que
//     la condición NUNCA se cumplía y hacía timeout siempre.
//
//   FIX v4.4:
//     El loop de verificación pre-guía ahora EXCLUYE eventos cuyo
//     WorldPosition está DETRÁS del usuario (dot product negativo con la
//     dirección de avance), y también excluye eventos de tipo StartNavigation
//     ya que ese siempre se dispara en el primer frame.
//     Si todos los eventos pendientes están "detrás" o son StartNavigation,
//     el loop termina inmediatamente.
//
// ── BUG 3: Llegada prematura vía ARGuideController ───────────────────────────
//
//   CAUSA:
//     ARGuideController.EvaluateArrivalInFullAR() disparaba llegada antes
//     de que el usuario llegara al destino real. Esto publicaba
//     NavigationCompletedEvent → OnNavCompleted() → ResetSession() →
//     toda la sesión de VoiceGuide terminaba prematuramente.
//
//   FIX v4.4 (en NavigationVoiceGuide):
//     OnNavCompleted() ahora verifica si el evento Arrived ya fue disparado
//     por EvaluateInstructions(). Si no fue disparado, no hace ResetSession()
//     inmediatamente — espera a que el usuario realmente llegue al destino
//     evaluando la distancia directamente.
//     (El fix principal está en ARGuideController._arrivalMinDelay, pero
//     esta es una capa de defensa adicional.)
//
// ── TODOS LOS FIXES DE v4.3 SE CONSERVAN ÍNTEGRAMENTE ───────────────────────

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Navigation.Voice
{
    // =========================================================================
    //  TIPOS
    // =========================================================================

    public enum VoiceInstructionType
    {
        StartNavigation, GoStraight, TurnLeft, TurnRight,
        SlightLeft, SlightRight, UTurn,
        StairsWarning, StairsClimb, StairsDescent, StairsComplete,
        Arrived, UserStopped, UserDeviated, ObstacleWarning,
        ProgressUpdate, ResumeAfterSeparation,
    }

    public sealed class NavigationInstructionEvent
    {
        public Vector3              WorldPosition   { get; }
        public VoiceInstructionType Type            { get; }
        public float                TriggerDistance { get; }
        public string               InstructionText { get; }
        public bool                 HasFired        { get; internal set; }
        public int                  CornerIndex     { get; }

        public NavigationInstructionEvent(
            Vector3 worldPosition, VoiceInstructionType type,
            float triggerDistance, string instructionText, int cornerIndex)
        {
            WorldPosition   = worldPosition;
            Type            = type;
            TriggerDistance = triggerDistance;
            InstructionText = instructionText;
            CornerIndex     = cornerIndex;
        }
    }

    // =========================================================================
    //  COMPONENTE PRINCIPAL
    // =========================================================================

    public sealed class NavigationVoiceGuide : MonoBehaviour
    {
        public static NavigationVoiceGuide Instance { get; private set; }

        // ── Inspector — Referencias ───────────────────────────────────────────

        [Header("─── Referencias ─────────────────────────────────────────────")]
        [SerializeField] private UserPositionBridge       _userBridge;
        [SerializeField] private NavigationPathController _pathController;

        // ── Inspector — Triggers ──────────────────────────────────────────────

        [Header("─── Triggers de distancia ──────────────────────────────────")]
        [Tooltip("Distancia usuario→waypoint de giro para anunciar (m).")]
        [SerializeField] private float _turnTriggerDist      = 5.0f;
        [Tooltip("Distancia usuario→inicio de escalera para advertir (m).")]
        [SerializeField] private float _stairTriggerDist     = 6.0f;
        [Tooltip("Distancia usuario real→destino final para anunciar llegada (m).")]
        [SerializeField] private float _arrivalTriggerDist   = 1.5f;
        [Tooltip("Distancia mínima al próximo waypoint para lanzar recordatorio (m).")]
        [SerializeField] private float _straightReminderDist = 12.0f;

        // ── Inspector — Rendimiento ───────────────────────────────────────────

        [Header("─── Rendimiento ─────────────────────────────────────────────")]
        [SerializeField, Range(0.05f, 0.5f)]
        private float _evalInterval = 0.10f;

        // ── Inspector — Espera de ruta ────────────────────────────────────────

        [Header("─── Espera de Ruta ──────────────────────────────────────────")]
        [SerializeField] private float _pathWaitTimeout            = 3.0f;
        [SerializeField] private float _pathPollInterval           = 0.1f;
        [SerializeField] private float _destinationChangeThreshold = 0.5f;

        // ── Inspector — Inicio ────────────────────────────────────────────────

        [Header("─── Timing de inicio ───────────────────────────────────────")]
        [Tooltip("Segundos de espera entre StartNavigation y activar evaluaciones.")]
        [SerializeField] private float _startDelay = 2.5f;

        // ── Inspector — Escaleras ─────────────────────────────────────────────

        [Header("─── Escaleras ──────────────────────────────────────────────")]
        [SerializeField] private float _stairHeightThreshold = 0.3f;

        // ── Inspector — Ángulos ───────────────────────────────────────────────

        [Header("─── Ángulos de Giro ─────────────────────────────────────────")]
        [SerializeField] private float _slightTurnAngle   = 20f;
        [SerializeField] private float _definiteTurnAngle = 50f;
        [SerializeField] private float _uTurnAngle        = 140f;

        // ── Inspector — Física humana ─────────────────────────────────────────

        [Header("─── Física Humana ─────────────────────────────────────────")]
        [SerializeField] private float _walkSpeedFlat   = 0.8f;
        [SerializeField] private float _walkSpeedStairs = 0.4f;
        [SerializeField] private float _stepLength      = 0.7f;

        // ── Inspector — Recordatorios ─────────────────────────────────────────

        [Header("─── Recordatorios ───────────────────────────────────────────")]
        [SerializeField] private float _straightReminderInterval = 20f;
        [SerializeField] private float _progressInterval         = 45f;

        // ── Inspector — [E1] Parada ───────────────────────────────────────────

        [Header("─── [E1] Parada del usuario ─────────────────────────────────")]
        [SerializeField] private float _stopTimeout          = 4.0f;
        [SerializeField] private float _stopMinMovement      = 0.25f;
        [SerializeField] private float _stopReminderInterval = 15.0f;

        // ── Inspector — [E2] Desviación ───────────────────────────────────────

        [Header("─── [E2] Desviación del camino ────────────────────────────")]
        [SerializeField] private float _deviationDist  = 2.0f;
        [SerializeField] private float _deviationDelay = 2.5f;

        // ── Inspector — [E3] Obstáculo ────────────────────────────────────────

        [Header("─── [E3] Obstáculo ─────────────────────────────────────────")]
        [SerializeField] private float _obstacleCheckTime = 6.0f;

        // ── Inspector — [E6] Separación larga ────────────────────────────────

        [Header("─── [E6] Separación larga ───────────────────────────────────")]
        [SerializeField] private float _longSeparationTime = 12.0f;

        // ── Inspector — Debug ─────────────────────────────────────────────────

        [Header("─── Debug ────────────────────────────────────────────────────")]
        [SerializeField] private bool _logInstructions  = true;
        [SerializeField] private bool _logPreprocessing = true;

        // ── Estado de sesión ──────────────────────────────────────────────────

        private readonly List<NavigationInstructionEvent> _events = new(24);
        private int     _nextIdx         = 0;
        private bool    _isGuiding       = false;
        private bool    _isPreprocessing = false;
        private string  _destName        = string.Empty;
        private Vector3 _destPos         = new(float.PositiveInfinity, 0, 0);

        // ── Recordatorios ─────────────────────────────────────────────────────
        private float _lastStraightTime = -999f;
        private int   _lastStraightIdx  = -1;
        private float _lastProgressTime = -999f;

        // ── [E1] Parada ───────────────────────────────────────────────────────
        private Vector3 _stopRefPos;
        private float   _stopAccumTime    = 0f;
        private bool    _isStopped        = false;
        private float   _lastStopReminder = -999f;

        // ── [E2] Desviación ───────────────────────────────────────────────────
        private float _deviationTimer = 0f;
        private bool  _deviationFired = false;

        // ── [E3] Obstáculo ────────────────────────────────────────────────────
        private float _obstacleTimer  = 0f;
        private float _lastDistToNext = float.MaxValue;
        private bool  _obstacleFired  = false;

        // ── [E6] Separación ───────────────────────────────────────────────────
        private float _returningTimer = 0f;

        private int       _currentFloor  = 0;
        private Coroutine _waitCoroutine = null;

        private float _evalAccum = 0f;

        // ─────────────────────────────────────────────────────────────────────
        //  POSICIONES
        // ─────────────────────────────────────────────────────────────────────

        private bool IsFullARMode => _userBridge != null && !_userBridge.IsNoArMode;

        private Vector3 UserPos => _userBridge != null
            ? _userBridge.UserPosition
            : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);

        private Vector3 UserFwd => _userBridge != null
            ? _userBridge.UserForward
            : FlatFwd(Camera.main != null ? Camera.main.transform.forward : Vector3.forward);

        private float UserSpeed => _userBridge?.UserSpeed ?? 0f;

        /// <summary>
        /// ✅ v4.4 FIX — Posición para evaluar triggers de instrucciones de ruta.
        ///
        /// CORRECCIÓN RESPECTO A v4.3:
        ///
        ///   v4.3 usaba AgentPosition en FullAR pensando que "el NPC recorre el
        ///   NavMesh". PERO en FullAR el NPC es ESTÁTICO — AROriginAligner lo
        ///   sincroniza con la cámara, por eso AgentPosition ≈ UserPosition.
        ///   El NPC no avanza hacia el destino: la ruta existe solo para que
        ///   NavigationVoiceGuide evalúe la posición del USUARIO contra ella.
        ///
        ///   En FullAR: EvalPos = UserPos (cámara XR = posición real del usuario).
        ///   En NoAR:   EvalPos = AgentPosition (el NPC se mueve, puede ir
        ///              adelante del usuario y pre-avisar antes de los giros).
        ///
        ///   El evento Arrived siempre usa UserPos en ambos modos.
        /// </summary>
        private Vector3 EvalPos => IsFullARMode ? UserPos : (_userBridge?.AgentPosition ?? UserPos);

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
            if (_userBridge == null)
                _userBridge = FindFirstObjectByType<UserPositionBridge>(FindObjectsInactive.Include);
            if (_userBridge == null)
                Debug.LogWarning("[VoiceGuide] ⚠️ UserPositionBridge no encontrado.");

            if (_pathController == null)
                _pathController = FindFirstObjectByType<NavigationPathController>(FindObjectsInactive.Include);

            if (_pathController != null)
            {
                _pathController.OnPathRecalculated -= OnPathRecalculated;
                _pathController.OnPathRecalculated += OnPathRecalculated;
            }
            else
                Debug.LogWarning("[VoiceGuide] ⚠️ NavigationPathController no encontrado.");

            SubscribeEvents();
            Debug.Log($"[VoiceGuide] ✅ Iniciado. EventBus={EventBus.Instance != null}");
        }

        private void OnEnable()  => SubscribeEvents();
        private void OnDisable() => UnsubscribeEvents();

        private void SubscribeEvents()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Subscribe<NavigationStartedEvent>  (OnNavStarted);
            bus.Subscribe<NavigationCompletedEvent>(OnNavCompleted);
            bus.Subscribe<NavigationCancelledEvent>(OnNavCancelled);
            bus.Subscribe<FloorTransitionEvent>    (OnFloorTransition);
        }

        private void UnsubscribeEvents()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Unsubscribe<NavigationStartedEvent>  (OnNavStarted);
            bus.Unsubscribe<NavigationCompletedEvent>(OnNavCompleted);
            bus.Unsubscribe<NavigationCancelledEvent>(OnNavCancelled);
            bus.Unsubscribe<FloorTransitionEvent>    (OnFloorTransition);
        }

        private void OnDestroy()
        {
            if (_pathController != null)
                _pathController.OnPathRecalculated -= OnPathRecalculated;
            if (Instance == this) Instance = null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  UPDATE
        // ─────────────────────────────────────────────────────────────────────

        private void Update()
        {
            if (!_isGuiding) return;

            _evalAccum += Time.deltaTime;
            if (_evalAccum < _evalInterval) return;

            float dt   = _evalAccum;
            _evalAccum = 0f;

            EvaluateInstructions();
            EvaluateUserStop(dt);
            EvaluateDeviation(dt);
            EvaluateObstacle(dt);
            EvaluateProgress();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVALUACIÓN DE INSTRUCCIONES
        // ─────────────────────────────────────────────────────────────────────

        private bool _ttsBusy = false;

        private void EvaluateInstructions()
        {
            if (_nextIdx >= _events.Count) return;
            if (_ttsBusy) return;

            // ✅ v4.4: EvalPos = UserPos en FullAR, AgentPosition en NoAR.
            // En FullAR el agente está sincronizado con el usuario, son lo mismo.
            // En NoAR el agente puede ir adelante para pre-avisar giros.
            Vector3 evalPos = EvalPos;
            Vector3 userPos = UserPos;

            for (int i = _nextIdx; i < _events.Count; i++)
            {
                var evt = _events[i];
                if (evt.HasFired) { _nextIdx = i + 1; continue; }

                // Arrived siempre contra UserPos real
                Vector3 checkPos = evt.Type == VoiceInstructionType.Arrived ? userPos : evalPos;

                if (Vector3.Distance(checkPos, evt.WorldPosition) <= evt.TriggerDistance)
                {
                    FireEvent(evt);
                    evt.HasFired = true;
                    _nextIdx     = i + 1;

                    if (GetPriority(evt.Type) >= 2)
                        return;
                }
                else break;
            }

            EvaluateStraightReminder();
        }

        private void ClearTTSBusy() => _ttsBusy = false;

        private void EvaluateStraightReminder()
        {
            if (_nextIdx >= _events.Count || _nextIdx == _lastStraightIdx) return;
            var next = _events[_nextIdx];
            if (next.HasFired) return;

            float dist = Vector3.Distance(EvalPos, next.WorldPosition);
            if (dist < _straightReminderDist) return;
            if (Time.time - _lastStraightTime < _straightReminderInterval) return;

            int steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));
            Speak(VoiceInstructionType.GoStraight,
                $"Continúa recto. En aproximadamente {steps} pasos llegará la siguiente indicación.",
                priority: 0);
            _lastStraightTime = Time.time;
            _lastStraightIdx  = _nextIdx;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  [E1] USUARIO SE DETIENE
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateUserStop(float dt)
        {
            float moved = Vector3.Distance(UserPos, _stopRefPos);

            if (moved >= _stopMinMovement)
            {
                _stopRefPos    = UserPos;
                _stopAccumTime = 0f;
                if (_isStopped)
                {
                    _isStopped      = false;
                    _obstacleFired  = false;
                    _obstacleTimer  = 0f;
                    _lastDistToNext = float.MaxValue;
                }
                return;
            }

            _stopAccumTime += dt;

            if (_stopAccumTime >= _stopTimeout && !_isStopped)
            {
                _isStopped        = true;
                _lastStopReminder = Time.time;
                _obstacleTimer    = 0f;
                _lastDistToNext   = DistUserToNextWp();

                int steps = Mathf.Max(1, Mathf.RoundToInt(_lastDistToNext / _stepLength));
                Speak(VoiceInstructionType.UserStopped,
                    $"Parece que te detuviste. " +
                    $"Cuando estés listo, continúa. La próxima indicación está en {steps} pasos.",
                    priority: 0);
                return;
            }

            if (_isStopped && Time.time - _lastStopReminder >= _stopReminderInterval)
            {
                _lastStopReminder = Time.time;
                float rem   = RemainingDistFromUser();
                int   steps = Mathf.Max(1, Mathf.RoundToInt(rem / _stepLength));
                Speak(VoiceInstructionType.UserStopped,
                    $"Tómate tu tiempo. El destino está a {steps} pasos. Sigue al guía cuando estés listo.",
                    priority: 0);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  [E2] DESVIACIÓN
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateDeviation(float dt)
        {
            if (_isStopped || UserSpeed < 0.2f) return;

            float lateral = LateralDeviationFromRoute();

            if (lateral > _deviationDist)
            {
                _deviationTimer += dt;
                if (_deviationTimer >= _deviationDelay && !_deviationFired)
                {
                    _deviationFired = true;
                    Speak(VoiceInstructionType.UserDeviated,
                        "Te has desviado del camino. Detente y busca al guía virtual. " +
                        "Camina hacia él para retomar la ruta.",
                        priority: 2);
                }
            }
            else
            {
                if (_deviationFired && lateral < _deviationDist * 0.5f)
                    _deviationFired = false;
                _deviationTimer = 0f;
            }
        }

        private float LateralDeviationFromRoute()
        {
            var wp = _pathController?.CurrentPath?.Waypoints;
            if (wp == null || wp.Count < 2) return 0f;

            float min   = float.MaxValue;
            int   start = Mathf.Max(0, _nextIdx - 2);
            int   end   = Mathf.Min(wp.Count - 2, _nextIdx + 2);

            for (int i = start; i <= end; i++)
            {
                float d = SegDistXZ(UserPos, wp[i], wp[i + 1]);
                if (d < min) min = d;
            }
            return min < float.MaxValue ? min : 0f;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  [E3] POSIBLE OBSTÁCULO
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateObstacle(float dt)
        {
            if (!_isStopped || _obstacleFired) return;
            _obstacleTimer += dt;
            if (_obstacleTimer < _obstacleCheckTime) return;

            float current   = DistUserToNextWp();
            float reduction = _lastDistToNext - current;

            if (reduction < 0.4f)
            {
                _obstacleFired = true;
                Speak(VoiceInstructionType.ObstacleWarning,
                    "Puede haber un obstáculo en tu camino. " +
                    "Intenta rodearlo con cuidado hacia tu izquierda o derecha, " +
                    "manteniendo una mano en la pared si es posible.",
                    priority: 3);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  [E5] PROGRESO PERIÓDICO
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateProgress()
        {
            if (_isStopped || UserSpeed < 0.3f) return;
            if (Time.time - _lastProgressTime < _progressInterval) return;

            float rem = RemainingDistFromUser();
            if (rem <= _arrivalTriggerDist * 3f) return;

            int steps = Mathf.Max(1, Mathf.RoundToInt(rem / _stepLength));
            _lastProgressTime = Time.time;
            Speak(VoiceInstructionType.ProgressUpdate,
                $"Vas bien. Quedan aproximadamente {steps} pasos para llegar a {_destName}.",
                priority: 0);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVENTOS DEL BUS
        // ─────────────────────────────────────────────────────────────────────

        private void OnNavStarted(NavigationStartedEvent evt)
        {
            if (string.IsNullOrEmpty(evt.DestinationWaypointId)) return;
            float delta = Vector3.Distance(evt.DestinationPosition, _destPos);
            if (delta < _destinationChangeThreshold && (_isGuiding || _isPreprocessing)) return;
            StartSession(evt.DestinationWaypointId, evt.DestinationPosition);
        }

        private void OnNavCompleted(NavigationCompletedEvent _)
        {
            if (!_isGuiding) return;

            // ✅ v4.4 FIX: No terminar la sesión si el usuario aún no llegó.
            // ARGuideController puede disparar NavigationCompletedEvent prematuramente
            // (antes de que el usuario llegue al destino real). En ese caso, verificamos
            // si el evento Arrived ya fue disparado por EvaluateInstructions().
            // Si no fue disparado, esperamos a que el usuario llegue.
            bool arrivedFired = _events.Exists(e => e.Type == VoiceInstructionType.Arrived && e.HasFired);
            float distToGoal  = Vector3.Distance(UserPos, _destPos);

            if (!arrivedFired && distToGoal > _arrivalTriggerDist * 2f)
            {
                // El usuario aún está lejos — no terminar la sesión.
                // La evaluación continúa en Update() y el evento Arrived se
                // disparará cuando el usuario realmente llegue.
                if (_logPreprocessing)
                    Debug.Log($"[VoiceGuide] ℹ️ NavigationCompleted ignorado: " +
                              $"usuario aún a {distToGoal:F1}m del destino. " +
                              "Sesión activa hasta llegada real.");
                return;
            }

            if (!arrivedFired)
                Speak(VoiceInstructionType.Arrived,
                    string.IsNullOrEmpty(_destName)
                        ? "Has llegado a tu destino. ¡Bien hecho!"
                        : $"Has llegado a {_destName}. ¡Bien hecho!",
                    priority: 1);
            ResetSession();
        }

        private void OnNavCancelled(NavigationCancelledEvent _) => ResetSession();

        private void OnFloorTransition(FloorTransitionEvent e)
        {
            _currentFloor  = e.ToLevel;
            _obstacleFired = false;
            _isStopped     = false;
            _stopAccumTime = 0f;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  RECÁLCULO MID-ROUTE + [E6]
        // ─────────────────────────────────────────────────────────────────────

        private void OnPathRecalculated(OptimizedPath newPath)
        {
            if (!_isGuiding) return;
            if (newPath == null || !newPath.IsValid || newPath.Waypoints.Count < 2) return;

            Vector3 pathEnd   = newPath.Waypoints[newPath.Waypoints.Count - 1];
            float   destDelta = Vector3.Distance(pathEnd, _destPos);

            if (destDelta > 2.0f)
            {
                _returningTimer += Time.deltaTime;
                return;
            }

            bool longSep    = _returningTimer >= _longSeparationTime;
            _returningTimer = 0f;

            if (_waitCoroutine != null)
            {
                StopCoroutine(_waitCoroutine);
                _waitCoroutine   = null;
                _isPreprocessing = false;
            }

            _obstacleFired  = false;
            _deviationFired = false;
            _isStopped      = false;
            _stopAccumTime  = 0f;
            _deviationTimer = 0f;

            Resync(newPath.Waypoints, longSep);
        }

        private void Resync(IReadOnlyList<Vector3> waypoints, bool fullSummary)
        {
            _events.Clear();
            _nextIdx          = 0;
            _lastStraightTime = Time.time;
            _lastStraightIdx  = -1;

            BuildInstructions(waypoints, startMessage: false);

            float rem   = RemainingDistFromUser(waypoints);
            int   steps = Mathf.Max(1, Mathf.RoundToInt(rem / _stepLength));

            if (fullSummary)
            {
                int secs = Mathf.RoundToInt(rem / _walkSpeedFlat);
                Speak(VoiceInstructionType.ResumeAfterSeparation,
                    $"El guía te encontró. Retomamos hacia {_destName}. " +
                    $"Quedan {steps} pasos, aproximadamente {secs} segundos. " +
                    $"Continúa siguiendo al guía.",
                    priority: 1);
            }
            else
            {
                Speak(VoiceInstructionType.GoStraight,
                    $"Ruta actualizada. {steps} pasos restantes hasta {_destName}.",
                    priority: 0);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  API PÚBLICA
        // ─────────────────────────────────────────────────────────────────────

        public void TriggerFromWaypoint(IndoorNavAR.Core.Data.WaypointData waypoint)
        {
            if (waypoint == null) return;
            float delta = Vector3.Distance(waypoint.Position, _destPos);
            if (delta < _destinationChangeThreshold && (_isGuiding || _isPreprocessing)) return;
            StartSession(waypoint.WaypointName, waypoint.Position);
        }

        public void StopVoiceGuide() => ResetSession();

        public void SetPathController(NavigationPathController controller)
        {
            if (_pathController != null)
                _pathController.OnPathRecalculated -= OnPathRecalculated;
            _pathController = controller;
            if (_pathController != null)
                _pathController.OnPathRecalculated += OnPathRecalculated;
        }

        public IReadOnlyList<NavigationInstructionEvent> InstructionEvents => _events;
        public bool IsGuiding => _isGuiding;

        // ─────────────────────────────────────────────────────────────────────
        //  LÓGICA DE SESIÓN
        // ─────────────────────────────────────────────────────────────────────

        private void StartSession(string destName, Vector3 destPosition)
        {
            if (_waitCoroutine != null) { StopCoroutine(_waitCoroutine); _waitCoroutine = null; }
            ResetSession(silent: true);

            _destName        = destName;
            _destPos         = destPosition;
            _isPreprocessing = true;
            _stopRefPos      = UserPos;
            _stopAccumTime   = 0f;
            _returningTimer  = 0f;

            _waitCoroutine = StartCoroutine(WaitForPath());
        }

        private IEnumerator WaitForPath()
        {
            if (_logPreprocessing)
                Debug.Log($"[VoiceGuide] ⏳ Esperando ruta hacia '{_destName}'...");

            float elapsed = 0f;
            OptimizedPath path = null;

            // Timeout efectivo = pathWaitTimeout * 2 para dispositivos lentos
            float effectiveTimeout = _pathWaitTimeout * 2f;

            while (elapsed < effectiveTimeout)
            {
                path = _pathController?.CurrentPath;

                if (path != null && path.IsValid && path.Waypoints.Count >= 2)
                {
                    Vector3 end   = path.Waypoints[path.Waypoints.Count - 1];
                    float   delta = Vector3.Distance(end, _destPos);
                    if (delta <= 1.5f) break;
                    path = null;
                }

                yield return new WaitForSeconds(_pathPollInterval);
                elapsed += _pathPollInterval;
            }

            _waitCoroutine   = null;
            _isPreprocessing = false;

            if (path == null || !path.IsValid || path.Waypoints.Count < 2)
            {
                // Activar en modo mínimo (sin ruta): solo evento de llegada.
                // Cuando llegue OnPathRecalculated(), Resync() añade todos los eventos.
                Debug.LogWarning($"[VoiceGuide] ⚠️ Timeout ({elapsed:F1}s) esperando ruta a '{_destName}'. " +
                                 "Modo mínimo activo.");

                Speak(VoiceInstructionType.StartNavigation,
                    $"Iniciando navegación a {_destName}. Sigue al guía hacia adelante.",
                    priority: 1);

                _events.Clear();
                _nextIdx = 0;
                _events.Add(new NavigationInstructionEvent(
                    _destPos, VoiceInstructionType.Arrived, _arrivalTriggerDist,
                    string.IsNullOrEmpty(_destName)
                        ? "Has llegado a tu destino. ¡Bien hecho!"
                        : $"Has llegado a {_destName}. ¡Bien hecho!",
                    0));

                _isGuiding = true;
                _ttsBusy   = false;
                yield break;
            }

            // ── Ruta válida — flujo normal ────────────────────────────────────

            _events.Clear();
            _nextIdx          = 0;
            _lastStraightTime = Time.time;
            _lastStraightIdx  = -1;
            _lastProgressTime = Time.time;

            BuildInstructions(path.Waypoints, startMessage: true);

            // Disparar StartNavigation inmediatamente
            if (_events.Count > 0)
            {
                var startEvt = _events[0];
                FireEvent(startEvt);
                startEvt.HasFired = true;
                _nextIdx = 1;
            }

            // Esperar duración estimada del TTS inicial
            {
                int   startWords  = _events.Count > 0 ? _events[0].InstructionText.Split(' ').Length : 10;
                float ttsDuration = (startWords / 13f) + 0.3f;
                yield return new WaitForSeconds(ttsDuration);
            }

            // ✅ v4.4 FIX: Loop de verificación pre-guía corregido.
            //
            // PROBLEMA ANTERIOR:
            //   El loop revisaba si algún evento estaba dentro de su TriggerDistance.
            //   Si el usuario estaba al inicio de la ruta, la escalera podía estar
            //   a 4.2m con trigger de 6.0m → condición verdadera para siempre.
            //   El usuario no puede alejarse hacia atrás, así que el loop
            //   hacía timeout invariablemente.
            //
            // FIX:
            //   Solo esperar para eventos que el usuario puede SUPERAR moviéndose.
            //   Un evento no debe bloquear el inicio si:
            //     a) Es de tipo StartNavigation (ya se disparó arriba).
            //     b) El usuario no está avanzando hacia él (dot product < 0).
            //     c) El evento ya fue disparado.
            //     d) Llevan más de _startDelay segundos esperando (timeout por evento).
            //
            //   En la práctica: si el usuario está quieto al inicio y hay una escalera
            //   a 4.2m (dentro del trigger de 6.0m), ese evento se OMITE del check
            //   porque el usuario no está avanzando hacia él todavía.
            //   Cuando el usuario comience a caminar, EvaluateInstructions() lo disparará.
            {
                float safetyTimeout = _startDelay + 3f;
                float waited        = 0f;
                float checkInterval = 0.15f;

                while (waited < safetyTimeout)
                {
                    bool anyBlockingEvent = false;
                    Vector3 pos     = EvalPos;
                    Vector3 userFwd = UserFwd;

                    for (int i = _nextIdx; i < _events.Count; i++)
                    {
                        var ev = _events[i];
                        if (ev.HasFired) continue;

                        // StartNavigation: siempre se omite (ya disparado)
                        if (ev.Type == VoiceInstructionType.StartNavigation) continue;

                        float dist = Vector3.Distance(pos, ev.WorldPosition);
                        if (dist > ev.TriggerDistance) break; // Siguiente evento más lejos — stop

                        // ✅ v4.4: Verificar si el usuario está avanzando hacia el evento.
                        // Si el evento está "detrás" o perpendicular al usuario, no bloquear.
                        Vector3 toEvent = (ev.WorldPosition - pos);
                        toEvent.y = 0f;
                        float dot = toEvent.sqrMagnitude > 0.001f
                            ? Vector3.Dot(userFwd, toEvent.normalized)
                            : 0f;

                        bool userAdvancingToward = dot > 0.1f; // usuario avanza hacia el evento

                        if (userAdvancingToward && UserSpeed > 0.1f)
                        {
                            // El usuario se mueve hacia un evento que está dentro del trigger.
                            // Esperar a que salga del radio o pare.
                            anyBlockingEvent = true;
                            if (_logPreprocessing)
                                Debug.Log($"[VoiceGuide] ⏳ Evento [{ev.Type}] " +
                                          $"dist={dist:F1}m <= trigger={ev.TriggerDistance:F1}m. " +
                                          $"Usuario avanzando (dot={dot:F2}). Esperando...");
                            break;
                        }
                        // Usuario quieto o alejándose: no bloquear — el evento
                        // se disparará cuando el usuario se acerque al caminar.
                    }

                    if (!anyBlockingEvent) break;

                    yield return new WaitForSeconds(checkInterval);
                    waited += checkInterval;
                }

                if (waited >= safetyTimeout)
                    Debug.LogWarning($"[VoiceGuide] ⚠️ Timeout pre-guía ({safetyTimeout:F1}s). " +
                                     "Activando — los eventos se evaluarán al caminar.");
            }

            _isGuiding = true;
            _ttsBusy   = false;

            float firstDist = GetDistToFirstActionEvent();
            if (_logPreprocessing)
                Debug.Log($"[VoiceGuide] ✅ Guía activo. {_events.Count} instrucciones, " +
                          $"nextIdx={_nextIdx}. PrimerAccion={firstDist:F1}m");
        }

        private float GetDistToFirstActionEvent()
        {
            Vector3 evalPos = EvalPos;
            for (int i = 1; i < _events.Count; i++)
            {
                var t = _events[i].Type;
                if (t == VoiceInstructionType.TurnLeft    ||
                    t == VoiceInstructionType.TurnRight   ||
                    t == VoiceInstructionType.SlightLeft  ||
                    t == VoiceInstructionType.SlightRight ||
                    t == VoiceInstructionType.UTurn       ||
                    t == VoiceInstructionType.StairsWarning)
                {
                    return Vector3.Distance(evalPos, _events[i].WorldPosition);
                }
            }
            return float.MaxValue;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CONSTRUCCIÓN DE INSTRUCCIONES
        // ─────────────────────────────────────────────────────────────────────

        private void BuildInstructions(IReadOnlyList<Vector3> wp, bool startMessage)
        {
            int count = wp.Count;
            if (count < 2) return;

            float totalDist = RemainingDistFromUser(wp);
            bool  hasStairs = false;
            for (int i = 0; i < count - 1; i++)
                if (Mathf.Abs(wp[i + 1].y - wp[i].y) >= _stairHeightThreshold)
                    hasStairs = true;

            if (startMessage)
            {
                int    steps  = Mathf.Max(1, Mathf.RoundToInt(totalDist / _stepLength));
                int    secs   = Mathf.RoundToInt(totalDist / _walkSpeedFlat);
                string stairs = hasStairs
                    ? " La ruta incluye escaleras, te avisaré con tiempo para que reduzcas el paso."
                    : string.Empty;

                _events.Add(new NavigationInstructionEvent(
                    wp[0], VoiceInstructionType.StartNavigation, 0.5f,
                    $"Iniciando navegación a {_destName}. " +
                    $"Aproximadamente {steps} pasos, {secs} segundos.{stairs} " +
                    $"Sigue al guía hacia adelante.",
                    0));
            }

            for (int i = 1; i < count - 1; i++)
            {
                Vector3 prev    = wp[i - 1];
                Vector3 current = wp[i];
                Vector3 next    = wp[i + 1];
                float   deltaY  = next.y - current.y;

                if (Mathf.Abs(deltaY) >= _stairHeightThreshold)
                {
                    float pathDistToStair = AccumDistAlongPath(wp, 0, i);
                    int   warnSteps       = Mathf.Max(1, Mathf.RoundToInt(pathDistToStair / _stepLength));

                    _events.Add(new NavigationInstructionEvent(
                        current, VoiceInstructionType.StairsWarning, _stairTriggerDist,
                        warnSteps > 5
                            ? $"En {warnSteps} pasos hay escaleras. Empieza a reducir el paso."
                            : "Hay escaleras muy cerca. Reduce el paso ahora.",
                        i));

                    float stairLen = Vector3.Distance(current, next);
                    int   stairSec = Mathf.Max(1, Mathf.RoundToInt(stairLen / _walkSpeedStairs));
                    bool  up       = deltaY > 0f;

                    _events.Add(new NavigationInstructionEvent(
                        current,
                        up ? VoiceInstructionType.StairsClimb : VoiceInstructionType.StairsDescent,
                        1.0f,
                        up ? $"Empieza a subir. Tómate el tiempo que necesites. Duración: {stairSec}s."
                           : $"Baja con cuidado. Agárrate al pasamanos. Duración: {stairSec}s.",
                        i));

                    _events.Add(new NavigationInstructionEvent(
                        next, VoiceInstructionType.StairsComplete, 0.8f,
                        "Terminaste las escaleras. Continúa por el pasillo.", i));

                    continue;
                }

                Vector3 dirIn  = current - prev;  dirIn.y  = 0f;
                Vector3 dirOut = next - current;   dirOut.y = 0f;
                if (dirIn.sqrMagnitude < 0.001f || dirOut.sqrMagnitude < 0.001f) continue;
                dirIn.Normalize(); dirOut.Normalize();

                float angle = Vector3.Angle(dirIn, dirOut);
                if (angle < _slightTurnAngle) continue;

                float cross = dirIn.x * dirOut.z - dirIn.z * dirOut.x;
                bool  left  = cross < 0f;
                var   ttype = ClassifyTurn(angle, left);

                float pathDistToTurn = AccumDistAlongPath(wp, 0, i);
                int   steps2         = Mathf.Max(1, Mathf.RoundToInt(pathDistToTurn / _stepLength));

                _events.Add(new NavigationInstructionEvent(
                    current, ttype, TriggerDist(ttype),
                    BuildTurnText(ttype, steps2),
                    i));
            }

            _events.Add(new NavigationInstructionEvent(
                wp[count - 1], VoiceInstructionType.Arrived, _arrivalTriggerDist,
                string.IsNullOrEmpty(_destName)
                    ? "Has llegado a tu destino. ¡Bien hecho!"
                    : $"Has llegado a {_destName}. ¡Bien hecho!",
                count - 1));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  RESET DE SESIÓN
        // ─────────────────────────────────────────────────────────────────────

        private void ResetSession(bool silent = false)
        {
            if (_waitCoroutine != null) { StopCoroutine(_waitCoroutine); _waitCoroutine = null; }

            NotifyTTSEnd();

            _isGuiding       = false;
            _isPreprocessing = false;
            _ttsBusy         = false;
            _destPos         = new(float.PositiveInfinity, 0, 0);
            _events.Clear();
            _nextIdx         = 0;
            _evalAccum       = 0f;

            _isStopped      = false;
            _stopAccumTime  = 0f;
            _deviationTimer = 0f;
            _deviationFired = false;
            _obstacleFired  = false;
            _obstacleTimer  = 0f;
            _returningTimer = 0f;

            if (!silent && _logPreprocessing)
                Debug.Log("[VoiceGuide] Sesión detenida.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS — DISTANCIA
        // ─────────────────────────────────────────────────────────────────────

        private float DistUserToNextWp()
        {
            if (_nextIdx >= _events.Count) return 0f;
            return Vector3.Distance(UserPos, _events[_nextIdx].WorldPosition);
        }

        private float RemainingDistFromUser(IReadOnlyList<Vector3> waypoints = null)
        {
            var wp = waypoints ?? _pathController?.CurrentPath?.Waypoints;
            if (wp == null || wp.Count < 2) return 0f;

            Vector3 upos    = UserPos;
            int     closest = 0;
            float   minDist = float.MaxValue;

            for (int i = 0; i < wp.Count - 1; i++)
            {
                float d = SegDistXZ(upos, wp[i], wp[i + 1]);
                if (d < minDist) { minDist = d; closest = i; }
            }

            Vector3 a  = wp[closest];
            Vector3 b  = wp[closest + 1];
            Vector3 ab = b - a;
            float   t  = ab.sqrMagnitude > 0.001f
                ? Mathf.Clamp01(Vector3.Dot(upos - a, ab) / ab.sqrMagnitude) : 0f;
            Vector3 proj = a + t * ab;

            float rem = Vector3.Distance(upos, proj) + Vector3.Distance(proj, b);
            for (int i = closest + 1; i < wp.Count - 1; i++)
                rem += Vector3.Distance(wp[i], wp[i + 1]);

            return rem;
        }

        private static float AccumDistAlongPath(IReadOnlyList<Vector3> wp, int fromIdx, int toIdx)
        {
            float dist = 0f;
            for (int i = fromIdx; i < toIdx && i < wp.Count - 1; i++)
                dist += Vector3.Distance(wp[i], wp[i + 1]);
            return dist;
        }

        private static float SegDistXZ(Vector3 pt, Vector3 a, Vector3 b)
        {
            var p   = new Vector2(pt.x, pt.z);
            var p1  = new Vector2(a.x, a.z);
            var p2  = new Vector2(b.x, b.z);
            var seg = p2 - p1;
            float lenSq = seg.sqrMagnitude;
            if (lenSq < 0.0001f) return Vector2.Distance(p, p1);
            float t = Mathf.Clamp01(Vector2.Dot(p - p1, seg) / lenSq);
            return Vector2.Distance(p, p1 + t * seg);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS — INSTRUCCIONES
        // ─────────────────────────────────────────────────────────────────────

        private void FireEvent(NavigationInstructionEvent evt)
        {
            bool isDirectional =
                evt.Type == VoiceInstructionType.TurnLeft    ||
                evt.Type == VoiceInstructionType.TurnRight   ||
                evt.Type == VoiceInstructionType.SlightLeft  ||
                evt.Type == VoiceInstructionType.SlightRight ||
                evt.Type == VoiceInstructionType.UTurn;

            string text     = isDirectional ? RecalcTurnText(evt) : evt.InstructionText;
            int    priority = GetPriority(evt.Type);
            Speak(evt.Type, text, priority);
        }

        private string RecalcTurnText(NavigationInstructionEvent evt)
        {
            int nextEvtIdx = evt.CornerIndex + 1;
            if (nextEvtIdx >= _events.Count) return evt.InstructionText;

            Vector3 toNext = _events[nextEvtIdx].WorldPosition - evt.WorldPosition;
            toNext.y = 0f;
            if (toNext.sqrMagnitude < 0.001f) return evt.InstructionText;
            toNext.Normalize();

            Vector3 fwd   = UserFwd;
            float   cross = fwd.x * toNext.z - fwd.z * toNext.x;
            float   dot   = fwd.x * toNext.x + fwd.z * toNext.z;
            float   angle = Mathf.Atan2(Mathf.Abs(cross), dot) * Mathf.Rad2Deg;
            bool    left  = cross < 0f;

            float dist  = Vector3.Distance(EvalPos, evt.WorldPosition);
            int   steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));

            return BuildTurnText(ClassifyTurn(angle, left), steps);
        }

        private VoiceInstructionType ClassifyTurn(float angle, bool left)
        {
            if (angle >= _uTurnAngle)        return VoiceInstructionType.UTurn;
            if (angle >= _definiteTurnAngle) return left ? VoiceInstructionType.TurnLeft : VoiceInstructionType.TurnRight;
            return left ? VoiceInstructionType.SlightLeft : VoiceInstructionType.SlightRight;
        }

        private float TriggerDist(VoiceInstructionType t) => t switch
        {
            VoiceInstructionType.UTurn       => _turnTriggerDist * 1.5f,
            VoiceInstructionType.SlightLeft  => _turnTriggerDist * 0.7f,
            VoiceInstructionType.SlightRight => _turnTriggerDist * 0.7f,
            _                                => _turnTriggerDist,
        };

        private string BuildTurnText(VoiceInstructionType t, int steps) => t switch
        {
            VoiceInstructionType.SlightLeft  => $"En {steps} pasos, gira levemente a tu izquierda.",
            VoiceInstructionType.SlightRight => $"En {steps} pasos, gira levemente a tu derecha.",
            VoiceInstructionType.TurnLeft    => $"En {steps} pasos, gira a la izquierda.",
            VoiceInstructionType.TurnRight   => $"En {steps} pasos, gira a la derecha.",
            VoiceInstructionType.UTurn       => $"En {steps} pasos, date la vuelta completamente.",
            _                                => $"En {steps} pasos, cambia de dirección.",
        };

        // ─────────────────────────────────────────────────────────────────────
        //  PRIORIDADES + TTS
        // ─────────────────────────────────────────────────────────────────────

        private static int GetPriority(VoiceInstructionType t) => t switch
        {
            VoiceInstructionType.TurnLeft              => 2,
            VoiceInstructionType.TurnRight             => 2,
            VoiceInstructionType.SlightLeft            => 2,
            VoiceInstructionType.SlightRight           => 2,
            VoiceInstructionType.UTurn                 => 2,
            VoiceInstructionType.UserDeviated          => 2,
            VoiceInstructionType.StairsWarning         => 3,
            VoiceInstructionType.StairsClimb           => 3,
            VoiceInstructionType.StairsDescent         => 3,
            VoiceInstructionType.ObstacleWarning       => 3,
            VoiceInstructionType.StartNavigation       => 1,
            VoiceInstructionType.Arrived               => 1,
            VoiceInstructionType.StairsComplete        => 1,
            VoiceInstructionType.ResumeAfterSeparation => 1,
            _ => 0,
        };

        private void Speak(VoiceInstructionType type, string text, int priority)
        {
            if (string.IsNullOrEmpty(text)) return;

            var announcementType = type switch
            {
                VoiceInstructionType.StairsWarning         => GuideAnnouncementType.ApproachingStairs,
                VoiceInstructionType.StairsClimb           => GuideAnnouncementType.StartingClimb,
                VoiceInstructionType.StairsDescent         => GuideAnnouncementType.StartingDescent,
                VoiceInstructionType.StairsComplete        => GuideAnnouncementType.StairsComplete,
                VoiceInstructionType.ResumeAfterSeparation => GuideAnnouncementType.ResumeGuide,
                _                                          => GuideAnnouncementType.ResumeGuide,
            };

            EventBus.Instance?.Publish(new GuideAnnouncementEvent
            {
                AnnouncementType = announcementType,
                Message          = text,
                CurrentFloor     = _currentFloor,
            });

            if (priority >= 2)
            {
                _ttsBusy = true;
                NotifyTTSStart(priority);

                float wordCount     = text.Split(' ').Length;
                float estimatedSecs = (wordCount / 13f) + 0.5f;
                StartCoroutine(AutoResumeTTSAfter(estimatedSecs));
            }

            if (_logInstructions)
                Debug.Log($"[VoiceGuide] 🔊 [{type}] p={priority} \"{text}\"");
        }

        private void NotifyTTSStart(int priority)
        {
            EventBus.Instance?.Publish(new TTSSpeakingEvent
            { IsSpeaking = true, Priority = priority });
        }

        private void NotifyTTSEnd()
        {
            EventBus.Instance?.Publish(new TTSSpeakingEvent
            { IsSpeaking = false, Priority = 0 });
        }

        private IEnumerator AutoResumeTTSAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            ClearTTSBusy();
            NotifyTTSEnd();
        }

        private static Vector3 FlatFwd(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.001f ? v.normalized : Vector3.forward;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  GIZMOS
        // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || !_isGuiding) return;

            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(UserPos, 0.25f);
            Gizmos.color = Color.blue;
            Gizmos.DrawLine(UserPos, UserPos + UserFwd * 0.7f);

            Vector3 evalPos = EvalPos;
            if (Vector3.Distance(evalPos, UserPos) > 0.1f)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(evalPos, 0.2f);
                Gizmos.DrawLine(UserPos, evalPos);
            }

            foreach (var evt in _events)
            {
                Gizmos.color = evt.HasFired
                    ? new Color(0.3f, 0.3f, 0.3f, 0.4f)
                    : GizmoColor(evt.Type);
                Gizmos.DrawWireSphere(evt.WorldPosition, evt.TriggerDistance);
                Gizmos.DrawSphere(evt.WorldPosition, 0.08f);
            }
        }

        private static Color GizmoColor(VoiceInstructionType t) => t switch
        {
            VoiceInstructionType.TurnLeft        => Color.red,
            VoiceInstructionType.TurnRight       => Color.blue,
            VoiceInstructionType.SlightLeft      => new Color(1f, 0.5f, 0.5f),
            VoiceInstructionType.SlightRight     => new Color(0.5f, 0.5f, 1f),
            VoiceInstructionType.UTurn           => Color.magenta,
            VoiceInstructionType.StairsWarning   => Color.yellow,
            VoiceInstructionType.StairsClimb     => new Color(1f, 0.6f, 0f),
            VoiceInstructionType.StairsDescent   => new Color(0.8f, 0.4f, 0f),
            VoiceInstructionType.Arrived         => Color.green,
            VoiceInstructionType.UserStopped     => Color.cyan,
            VoiceInstructionType.UserDeviated    => new Color(1f, 0f, 0.5f),
            VoiceInstructionType.ObstacleWarning => new Color(1f, 0.3f, 0f),
            _                                    => Color.white,
        };
#endif

        // ─────────────────────────────────────────────────────────────────────
        //  CONTEXT MENU
        // ─────────────────────────────────────────────────────────────────────

        [ContextMenu("ℹ️ Estado actual")]
        private void DebugStatus()
        {
            float rem  = RemainingDistFromUser();
            var   path = _pathController?.CurrentPath;
            Debug.Log(
                $"[VoiceGuide] IsGuiding={_isGuiding} | IsPreprocessing={_isPreprocessing}\n" +
                $"Destino='{_destName}' | Events={_events.Count} | NextIdx={_nextIdx}\n" +
                $"Modo={( IsFullARMode ? "FullAR (EvalPos=UserPos)" : "NoAR (EvalPos=AgentPos)")} \n" +
                $"UserPos={UserPos:F2} | EvalPos={EvalPos:F2}\n" +
                $"UserSpeed={UserSpeed:F2}m/s\n" +
                $"RemainingDist={rem:F1}m (~{Mathf.RoundToInt(rem / _stepLength)} pasos)\n" +
                $"[E1] Stopped={_isStopped} StopAccum={_stopAccumTime:F1}s\n" +
                $"[E2] DeviationTimer={_deviationTimer:F1}s Fired={_deviationFired}\n" +
                $"[E3] ObstacleTimer={_obstacleTimer:F1}s Fired={_obstacleFired}\n" +
                $"Path: valid={path?.IsValid} wp={path?.Waypoints.Count} len={path?.TotalLength:F1}m");
        }

        [ContextMenu("🛑 Detener guía")]
        private void DebugStop() => ResetSession();
    }
}