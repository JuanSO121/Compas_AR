// File: NavigationVoiceGuide.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Navigation/Voice/
// ✅ v5.5 — FIX: Anti-saturación TTS — mensajes cortos, espaciado global, supresión durante TTS
//
// ============================================================================
//  CAMBIOS v5.4 → v5.5
// ============================================================================
//
//  PROBLEMA — Mensajes se acumulaban al punto de que el TTS no terminaba uno
//             antes de que comenzara el siguiente:
//
//    CAUSA 1: EvaluateStraightReminder(), EvaluateProgress() y EvaluateUserStop()
//             disparaban mensajes sin comprobar si _ttsBusy era true.
//             → Mensajes low se apilaban mientras el TTS hablaba.
//
//    CAUSA 2: Textos de inicio/orientación demasiado largos (20-30 palabras).
//             Mientras el TTS los decía, llegaban 2-3 mensajes nuevos.
//
//    CAUSA 3: Sin espaciado mínimo global entre mensajes consecutivos.
//             Un recordatorio de GoStraight podía dispararse 0.1s después
//             de un giro porque _lastStraightTime no cubría ese caso.
//
//  FIX v5.5:
//    1. _lastAnyMessageTime — timestamp global de cualquier Speak().
//       _minMessageInterval (3.5s) — ningún mensaje low/medium puede
//       dispararse si no han pasado 3.5s desde el último mensaje.
//
//    2. Guard _ttsBusy en EvaluateStraightReminder(), EvaluateProgress()
//       y en el recordatorio de EvaluateUserStop(). Los mensajes de prioridad
//       0-1 no se emiten si el TTS está ocupado con algo más urgente.
//
//    3. Textos recortados ~40%:
//       - AnnounceInitialOrientation: sala abierta, pasillo, giro inmediato
//       - BuildTurnTextWithContext: forma "N pasos recto, luego [giro]"
//       - EvaluateStraightReminder: "Sigue recto. N pasos."
//       - EvaluateProgress: "Vas bien. N pasos para [dest]."
//       - EvaluateUserStop recordatorio: "Cuando estés listo. N pasos."
//
//  TODOS LOS FIXES DE v5.0 - v5.4 SE CONSERVAN ÍNTEGRAMENTE.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Navigation.Voice
{
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

    public sealed class NavigationVoiceGuide : MonoBehaviour
    {
        public static NavigationVoiceGuide Instance { get; private set; }

        [Header("─── Referencias ─────────────────────────────────────────────")]
        [SerializeField] private UserPositionBridge       _userBridge;
        [SerializeField] private NavigationPathController _pathController;

        [Header("─── Triggers de distancia ──────────────────────────────────")]
        [SerializeField] private float _turnTriggerDist      = 5.0f;
        [SerializeField] private float _stairTriggerDist     = 6.0f;
        [SerializeField] private float _arrivalTriggerDist   = 1.5f;
        [SerializeField] private float _straightReminderDist = 12.0f;

        [Header("─── Rendimiento ─────────────────────────────────────────────")]
        [SerializeField, Range(0.05f, 0.5f)]
        private float _evalInterval = 0.10f;

        [Header("─── Espera de Ruta ──────────────────────────────────────────")]
        [SerializeField] private float _pathWaitTimeout            = 3.0f;
        [SerializeField] private float _pathPollInterval           = 0.1f;
        [SerializeField] private float _destinationChangeThreshold = 0.5f;

        [Header("─── Timing de inicio ───────────────────────────────────────")]
        [SerializeField] private float _startDelay = 2.5f;

        [Header("─── Escaleras ──────────────────────────────────────────────")]
        [SerializeField] private float _stairHeightThreshold = 0.3f;

        [Header("─── Escaleras — Tolerancia Y ───────────────────────────────")]
        [SerializeField] private float _stairYTolerance = 1.2f;

        [Header("─── Ángulos de Giro ─────────────────────────────────────────")]
        [SerializeField] private float _slightTurnAngle   = 20f;
        [SerializeField] private float _definiteTurnAngle = 50f;
        [SerializeField] private float _uTurnAngle        = 140f;

        [Header("─── v5.4: Subdivisión de segmentos diagonales ───────────────")]
        [SerializeField] private float _maxSegmentLength         = 3.0f;
        [SerializeField] private float _straightSegmentAngle     = 15f;
        [SerializeField] private float _minMentionableStraightDist = 1.5f;

        [Header("─── Física Humana ─────────────────────────────────────────")]
        [SerializeField] private float _walkSpeedFlat   = 0.8f;
        [SerializeField] private float _walkSpeedStairs = 0.4f;
        [SerializeField] private float _stepLength      = 0.7f;

        [Header("─── Recordatorios ───────────────────────────────────────────")]
        [SerializeField] private float _straightReminderInterval = 20f;
        [SerializeField] private float _progressInterval         = 45f;

        [Header("─── [E1] Parada del usuario ─────────────────────────────────")]
        [SerializeField] private float _stopTimeout          = 4.0f;
        [SerializeField] private float _stopMinMovement      = 0.25f;
        [SerializeField] private float _stopReminderInterval = 15.0f;

        [Header("─── [E2] Desviación del camino ────────────────────────────")]
        [SerializeField] private float _deviationDist  = 2.0f;
        [SerializeField] private float _deviationDelay = 2.5f;

        [Header("─── [E3] Obstáculo ─────────────────────────────────────────")]
        [SerializeField] private float _obstacleCheckTime = 6.0f;

        [Header("─── [E6] Separación larga ───────────────────────────────────")]
        [SerializeField] private float _longSeparationTime = 12.0f;

        [Header("─── [E7] Desorientación ────────────────────────────────────")]
        [SerializeField] private float _misalignAngleThreshold   = 45f;
        [SerializeField] private float _misalignConfirmTime      = 3.0f;
        [SerializeField] private float _misalignReminderInterval = 12f;
        [SerializeField] private float _misalignMinSpeed         = 0.2f;

        [Header("─── Dedup de instrucciones ───────────────────────────────────")]
        [SerializeField] private float _dedupWindow = 3.0f;

        // ✅ v5.5 — Anti-saturación TTS
        [Header("─── Anti-saturación TTS (v5.5) ───────────────────────────────")]
        [Tooltip("Intervalo mínimo (s) entre mensajes de prioridad 0 (low). " +
                 "Evita que recordatorios se apilen mientras el TTS habla algo urgente.")]
        [SerializeField] private float _minMessageInterval = 3.5f;

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

        private float _lastStraightTime = -999f;
        private int   _lastStraightIdx  = -1;
        private float _lastProgressTime = -999f;

        // ✅ v5.5: Timestamp global del último Speak() — cualquier prioridad
        private float _lastAnyMessageTime = -999f;

        private Vector3 _stopRefPos;
        private float   _stopAccumTime    = 0f;
        private bool    _isStopped        = false;
        private float   _lastStopReminder = -999f;

        private float _deviationTimer = 0f;
        private bool  _deviationFired = false;

        private float _obstacleTimer  = 0f;
        private float _lastDistToNext = float.MaxValue;
        private bool  _obstacleFired  = false;

        private float _returningTimer = 0f;

        private int       _currentFloor  = 0;
        private Coroutine _waitCoroutine = null;

        private float _evalAccum = 0f;

        private Coroutine _ttsResumeCoroutine = null;

        // FIX E: Dedup de Speak()
        private string _lastSpokenText = string.Empty;
        private float  _lastSpokenTime = -999f;

        // v5.0: [E7] Desorientación
        private float _misalignTimer    = 0f;
        private float _lastMisalignTime = -999f;

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
            Debug.Log($"[VoiceGuide] ✅ v5.5 Iniciado. EventBus={EventBus.Instance != null}");
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
            EvaluateMisalignment(dt);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVALUACIÓN DE INSTRUCCIONES
        // ─────────────────────────────────────────────────────────────────────

        private bool _ttsBusy = false;

        private void EvaluateInstructions()
        {
            if (_nextIdx >= _events.Count) return;
            if (_ttsBusy) return;

            Vector3 evalPos = EvalPos;
            Vector3 userPos = UserPos;

            for (int i = _nextIdx; i < _events.Count; i++)
            {
                var evt = _events[i];
                if (evt.HasFired) { _nextIdx = i + 1; continue; }

                Vector3 checkPos = evt.Type == VoiceInstructionType.Arrived ? userPos : evalPos;

                if (!ShouldFireEvent(evt, checkPos))
                    break;

                FireEvent(evt);
                evt.HasFired = true;
                _nextIdx     = i + 1;

                if (GetPriority(evt.Type) >= 2)
                    return;
            }

            EvaluateStraightReminder();
        }

        private bool ShouldFireEvent(NavigationInstructionEvent evt, Vector3 checkPos)
        {
            float dist = Vector3.Distance(checkPos, evt.WorldPosition);
            if (dist > evt.TriggerDistance) return false;

            if (evt.Type == VoiceInstructionType.StairsComplete ||
                evt.Type == VoiceInstructionType.Arrived)
            {
                float yDelta = Mathf.Abs(UserPos.y - evt.WorldPosition.y);
                if (yDelta > _stairYTolerance)
                    return false;
            }

            return true;
        }

        private void ClearTTSBusy() => _ttsBusy = false;

        /// <summary>
        /// ✅ v5.5: Guard _ttsBusy + _minMessageInterval global.
        /// Recordatorios de "sigue recto" no se emiten si el TTS está ocupado
        /// o si no han pasado _minMessageInterval segundos desde el último mensaje.
        /// Texto recortado para reducir duración del TTS.
        /// </summary>
        private void EvaluateStraightReminder()
        {
            if (_nextIdx >= _events.Count || _nextIdx == _lastStraightIdx) return;
            var next = _events[_nextIdx];
            if (next.HasFired) return;

            float dist = Vector3.Distance(EvalPos, next.WorldPosition);
            if (dist < _straightReminderDist) return;
            if (Time.time - _lastStraightTime < _straightReminderInterval) return;

            // ✅ v5.5: No interrumpir si TTS está hablando algo más importante
            if (_ttsBusy) return;
            if (Time.time - _lastAnyMessageTime < _minMessageInterval) return;

            int steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));
            // ✅ v5.5: Texto corto (~6 palabras vs ~12 anteriores)
            Speak(VoiceInstructionType.GoStraight,
                $"Sigue recto. {steps} pasos.",
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
                // ✅ v5.5: Texto corto
                Speak(VoiceInstructionType.UserStopped,
                    $"Cuando estés listo, continúa. Próxima indicación en {steps} pasos.",
                    priority: 0);
                return;
            }

            if (_isStopped && Time.time - _lastStopReminder >= _stopReminderInterval)
            {
                // ✅ v5.5: No recordar si TTS ocupado
                if (_ttsBusy) return;
                if (Time.time - _lastAnyMessageTime < _minMessageInterval) return;

                _lastStopReminder = Time.time;
                float rem   = RemainingDistFromUser();
                int   steps = Mathf.Max(1, Mathf.RoundToInt(rem / _stepLength));
                // ✅ v5.5: Texto corto
                Speak(VoiceInstructionType.UserStopped,
                    $"Tómate tu tiempo. {steps} pasos al destino.",
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
                    string deviationMsg = IsFullARMode
                        ? "Te desviaste. Detente, gira hacia la ruta y retoma el camino."
                        : "Te desviaste. Busca al guía y vuelve a la ruta.";
                    Speak(VoiceInstructionType.UserDeviated, deviationMsg, priority: 2);
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
                    "Posible obstáculo. Rodéalo con cuidado hacia un lado.",
                    priority: 3);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  [E5] PROGRESO PERIÓDICO
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ✅ v5.5: Guard _ttsBusy + _minMessageInterval.
        /// Texto corto para no acaparar el TTS.
        /// </summary>
        private void EvaluateProgress()
        {
            if (_isStopped || UserSpeed < 0.3f) return;
            if (Time.time - _lastProgressTime < _progressInterval) return;

            // ✅ v5.5: No emitir si TTS ocupado o muy reciente
            if (_ttsBusy) return;
            if (Time.time - _lastAnyMessageTime < _minMessageInterval) return;

            float rem = RemainingDistFromUser();
            if (rem <= _arrivalTriggerDist * 3f) return;

            int steps = Mathf.Max(1, Mathf.RoundToInt(rem / _stepLength));
            _lastProgressTime = Time.time;
            // ✅ v5.5: Texto corto (~6 palabras vs ~12 anteriores)
            Speak(VoiceInstructionType.ProgressUpdate,
                $"Vas bien. {steps} pasos para {_destName}.",
                priority: 0);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  v5.0 — [E7] DESORIENTACIÓN DEL USUARIO
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateMisalignment(float dt)
        {
            if (!IsFullARMode) return;
            if (UserSpeed < _misalignMinSpeed) return;
            if (_isStopped) return;
            if (_nextIdx >= _events.Count) return;
            if (_ttsBusy) return;

            Vector3 nextWpPos = _events[_nextIdx].WorldPosition;
            Vector3 toNext    = nextWpPos - UserPos;
            toNext.y = 0f;

            if (toNext.sqrMagnitude < 0.25f) return;

            toNext.Normalize();

            float signedAngle = SignedAngleXZ(UserFwd, toNext);
            float absAngle    = Mathf.Abs(signedAngle);

            if (absAngle > _misalignAngleThreshold)
            {
                _misalignTimer += dt;

                if (_misalignTimer >= _misalignConfirmTime &&
                    Time.time - _lastMisalignTime >= _misalignReminderInterval)
                {
                    _lastMisalignTime = Time.time;
                    _misalignTimer    = 0f;

                    string dir   = DirectionLabel(signedAngle);
                    float  dist  = Vector3.Distance(UserPos, nextWpPos);
                    int    steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));
                    string text;

                    // ✅ v5.5: Textos más cortos
                    if (absAngle <= 50f)
                        text = $"El camino está {dir}. Gira levemente. {steps} pasos.";
                    else if (absAngle <= 130f)
                        text = $"Dirección equivocada. Gira {dir}. {steps} pasos.";
                    else
                        text = $"Estás al revés. Date la vuelta {dir}. {steps} pasos.";

                    Speak(VoiceInstructionType.UserDeviated, text, priority: 2);

                    if (_logInstructions)
                        Debug.Log($"[VoiceGuide] ⚠️ [E7] Desorientación: " +
                                  $"ángulo={signedAngle:F1}° ({dir}) | dist={dist:F1}m");
                }
            }
            else
            {
                if (_misalignTimer > 0f)
                    _misalignTimer = Mathf.Max(0f, _misalignTimer - dt * 1.5f);
            }
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

            bool arrivedFired = _events.Exists(e => e.Type == VoiceInstructionType.Arrived && e.HasFired);
            float distToGoal  = Vector3.Distance(UserPos, _destPos);

            if (!arrivedFired && distToGoal > _arrivalTriggerDist * 2f)
            {
                if (_logPreprocessing)
                    Debug.Log($"[VoiceGuide] ℹ️ NavigationCompleted ignorado: " +
                              $"usuario aún a {distToGoal:F1}m del destino.");
                return;
            }

            if (!arrivedFired)
                Speak(VoiceInstructionType.Arrived,
                    string.IsNullOrEmpty(_destName)
                        ? "Llegaste. ¡Bien hecho!"
                        : $"Llegaste a {_destName}. ¡Bien hecho!",
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

            _lastSpokenText = string.Empty;
            _lastSpokenTime = -999f;

            _misalignTimer    = 0f;
            _lastMisalignTime = -999f;
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

            _misalignTimer    = 0f;
            _lastMisalignTime = -999f;

            Resync(newPath.Waypoints, longSep);
        }

        private void Resync(IReadOnlyList<Vector3> waypoints, bool fullSummary)
        {
            _events.Clear();
            _nextIdx          = 0;
            _lastStraightTime = Time.time;
            _lastStraightIdx  = -1;

            var subdivided = SubdivideWaypointSegments(waypoints);
            BuildInstructions(subdivided, startMessage: false);

            float rem   = RemainingDistFromUser(subdivided);
            int   steps = Mathf.Max(1, Mathf.RoundToInt(rem / _stepLength));

            if (fullSummary)
            {
                int secs = Mathf.RoundToInt(rem / _walkSpeedFlat);
                // ✅ v5.5: Texto corto
                string resumeMsg = IsFullARMode
                    ? $"Ruta recalculada. {steps} pasos a {_destName}."
                    : $"El guía te encontró. {steps} pasos a {_destName}.";
                Speak(VoiceInstructionType.ResumeAfterSeparation, resumeMsg, priority: 1);
            }
            else
            {
                Speak(VoiceInstructionType.GoStraight,
                    $"Ruta actualizada. {steps} pasos a {_destName}.",
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
                Debug.LogWarning($"[VoiceGuide] ⚠️ Timeout ({elapsed:F1}s) esperando ruta a '{_destName}'.");

                string timeoutCta = IsFullARMode
                    ? "Camina hacia el destino indicado."
                    : "Sigue al guía.";

                Speak(VoiceInstructionType.StartNavigation,
                    $"Navegando a {_destName}. {timeoutCta}",
                    priority: 1);

                _events.Clear();
                _nextIdx = 0;
                _events.Add(new NavigationInstructionEvent(
                    _destPos, VoiceInstructionType.Arrived, _arrivalTriggerDist,
                    string.IsNullOrEmpty(_destName)
                        ? "Llegaste. ¡Bien hecho!"
                        : $"Llegaste a {_destName}. ¡Bien hecho!",
                    0));

                _isGuiding = true;
                _ttsBusy   = false;
                yield break;
            }

            var subdivided = SubdivideWaypointSegments(path.Waypoints);

            _events.Clear();
            _nextIdx          = 0;
            _lastStraightTime = Time.time;
            _lastStraightIdx  = -1;
            _lastProgressTime = Time.time;

            BuildInstructions(subdivided, startMessage: true);

            if (_events.Count > 0)
            {
                var startEvt = _events[0];
                FireEvent(startEvt);
                startEvt.HasFired = true;
                _nextIdx = 1;

                AnnounceInitialOrientation(subdivided);
            }

            {
                int   startWords  = _events.Count > 0 ? CountWords(_events[0].InstructionText) : 10;
                float ttsDuration = (startWords / 10f) + 1.5f;
                yield return new WaitForSeconds(ttsDuration);
            }

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
                        if (ev.Type == VoiceInstructionType.StartNavigation) continue;

                        float dist = Vector3.Distance(pos, ev.WorldPosition);
                        if (dist > ev.TriggerDistance) break;

                        Vector3 toEvent = (ev.WorldPosition - pos);
                        toEvent.y = 0f;
                        float dot = toEvent.sqrMagnitude > 0.001f
                            ? Vector3.Dot(userFwd, toEvent.normalized)
                            : 0f;

                        bool userAdvancingToward = dot > 0.1f;

                        if (userAdvancingToward && UserSpeed > 0.1f)
                        {
                            anyBlockingEvent = true;
                            break;
                        }
                    }

                    if (!anyBlockingEvent) break;

                    yield return new WaitForSeconds(checkInterval);
                    waited += checkInterval;
                }
            }

            _isGuiding = true;
            _ttsBusy   = false;

            float firstDist = GetDistToFirstActionEvent();
            if (_logPreprocessing)
                Debug.Log($"[VoiceGuide] ✅ v5.5 Guía activo. {_events.Count} instrucciones " +
                          $"(desde {path.Waypoints.Count} → {subdivided.Count} wp subdivididos), " +
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
        //  v5.4 — SUBDIVISIÓN DE SEGMENTOS DIAGONALES
        // ─────────────────────────────────────────────────────────────────────

        private List<Vector3> SubdivideWaypointSegments(IReadOnlyList<Vector3> waypoints)
        {
            var result = new List<Vector3>(waypoints.Count * 2);
            if (waypoints.Count == 0) return result;

            result.Add(waypoints[0]);

            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                Vector3 a = waypoints[i];
                Vector3 b = waypoints[i + 1];

                float deltaY = Mathf.Abs(b.y - a.y);
                if (deltaY >= _stairHeightThreshold)
                {
                    result.Add(b);
                    continue;
                }

                float segLen = Vector3.Distance(a, b);

                if (_maxSegmentLength <= 0f || segLen <= _maxSegmentLength)
                {
                    result.Add(b);
                    continue;
                }

                int subdivisions = Mathf.CeilToInt(segLen / _maxSegmentLength);
                for (int s = 1; s <= subdivisions; s++)
                {
                    float t = (float)s / subdivisions;
                    result.Add(Vector3.Lerp(a, b, t));
                }
            }

            if (_logPreprocessing && result.Count != waypoints.Count)
                Debug.Log($"[VoiceGuide] 📐 SubdivideWaypointSegments: " +
                          $"{waypoints.Count} → {result.Count} waypoints.");

            return result;
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
                // ✅ v5.5: Texto de inicio más corto
                string stairs = hasStairs ? " Hay escaleras en la ruta." : string.Empty;

                string startCta = IsFullARMode
                    ? "Sigue la ruta indicada."
                    : "Sigue al guía.";

                _events.Add(new NavigationInstructionEvent(
                    wp[0], VoiceInstructionType.StartNavigation, 0.5f,
                    $"Navegando a {_destName}. {steps} pasos, {secs} segundos.{stairs} {startCta}",
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
                            ? $"En {warnSteps} pasos, escaleras. Reduce el paso."
                            : "Escaleras muy cerca. Reduce el paso.",
                        i));

                    float stairLen = Vector3.Distance(current, next);
                    int   stairSec = Mathf.Max(1, Mathf.RoundToInt(stairLen / _walkSpeedStairs));
                    bool  up       = deltaY > 0f;

                    _events.Add(new NavigationInstructionEvent(
                        current,
                        up ? VoiceInstructionType.StairsClimb : VoiceInstructionType.StairsDescent,
                        1.0f,
                        up ? $"Sube. Tómate tu tiempo. {stairSec}s."
                           : $"Baja con cuidado. Agárrate al pasamanos. {stairSec}s.",
                        i));

                    _events.Add(new NavigationInstructionEvent(
                        next, VoiceInstructionType.StairsComplete, 0.8f,
                        "Terminaste las escaleras. Continúa.", i));

                    continue;
                }

                Vector3 dirIn  = current - prev;  dirIn.y  = 0f;
                Vector3 dirOut = next - current;   dirOut.y = 0f;
                if (dirIn.sqrMagnitude < 0.001f || dirOut.sqrMagnitude < 0.001f) continue;
                dirIn.Normalize(); dirOut.Normalize();

                var (ttype, userRelativeAngle) = ClassifyTurnRelativeToUser(dirIn, dirOut, isImmediateTurn: false);
                float routeDeflectionAngle = Vector3.Angle(dirIn, dirOut);

                if (ttype == VoiceInstructionType.GoStraight) continue;

                float pathDistToTurn = AccumDistAlongPath(wp, 0, i);
                int   stepsToTurn    = Mathf.Max(1, Mathf.RoundToInt(pathDistToTurn / _stepLength));

                string turnText = BuildTurnTextWithContext(ttype, userRelativeAngle, stepsToTurn,
                                                          wp, i, dirIn, dirOut);

                _events.Add(new NavigationInstructionEvent(
                    current, ttype, TriggerDist(ttype),
                    turnText,
                    i));

                if (_logInstructions)
                    Debug.Log($"[VoiceGuide] 📍 [v5.5] Instrucción en wp[{i}]: " +
                              $"tipo={ttype} | ángulo={userRelativeAngle:F1}° | " +
                              $"deflexión={routeDeflectionAngle:F1}° | pasos={stepsToTurn}");
            }

            _events.Add(new NavigationInstructionEvent(
                wp[count - 1], VoiceInstructionType.Arrived, _arrivalTriggerDist,
                string.IsNullOrEmpty(_destName)
                    ? "Llegaste. ¡Bien hecho!"
                    : $"Llegaste a {_destName}. ¡Bien hecho!",
                count - 1));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  v5.4 — CLASIFICACIÓN RELATIVA AL USUARIO
        // ─────────────────────────────────────────────────────────────────────

        private (VoiceInstructionType type, float signedAngle) ClassifyTurnRelativeToUser(
            Vector3 dirIn, Vector3 dirOut, bool isImmediateTurn = false)
        {
            Vector3 reference;

            if (IsFullARMode && isImmediateTurn)
            {
                Vector3 userFwd = UserFwd;
                userFwd.y = 0f;
                reference = userFwd.sqrMagnitude > 0.001f ? userFwd.normalized : dirIn;
            }
            else
            {
                reference = dirIn;
            }

            float signedAngle = SignedAngleXZ(reference, dirOut);
            float absAngle    = Mathf.Abs(signedAngle);
            bool  isRight     = signedAngle >= 0f;

            VoiceInstructionType ttype;
            if      (absAngle < _slightTurnAngle)   ttype = VoiceInstructionType.GoStraight;
            else if (absAngle >= _uTurnAngle)        ttype = VoiceInstructionType.UTurn;
            else if (absAngle >= _definiteTurnAngle) ttype = isRight ? VoiceInstructionType.TurnRight
                                                                     : VoiceInstructionType.TurnLeft;
            else                                     ttype = isRight ? VoiceInstructionType.SlightRight
                                                                     : VoiceInstructionType.SlightLeft;

            return (ttype, signedAngle);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  v5.4 — TEXTO DE GIRO CON CONTEXTO (v5.5: textos más cortos)
        // ─────────────────────────────────────────────────────────────────────

        private string BuildTurnTextWithContext(
            VoiceInstructionType ttype,
            float signedAngle,
            int stepsToTurn,
            IReadOnlyList<Vector3> wp,
            int cornerIdx,
            Vector3 dirIn,
            Vector3 dirOut)
        {
            float prevSegLen = 0f;
            bool  prevIsStr  = false;

            if (cornerIdx >= 2)
            {
                Vector3 prevPrev = wp[cornerIdx - 2];
                Vector3 prev     = wp[cornerIdx - 1];
                Vector3 curr     = wp[cornerIdx];

                Vector3 dPrevIn  = (prev - prevPrev); dPrevIn.y = 0f;
                Vector3 dPrevOut = (curr - prev);     dPrevOut.y = 0f;

                if (dPrevIn.sqrMagnitude > 0.001f && dPrevOut.sqrMagnitude > 0.001f)
                {
                    float deflection = Vector3.Angle(dPrevIn.normalized, dPrevOut.normalized);
                    prevIsStr = deflection < _straightSegmentAngle;
                }

                prevSegLen = Vector3.Distance(
                    new Vector3(prev.x, 0, prev.z),
                    new Vector3(curr.x, 0, curr.z));
            }
            else if (cornerIdx >= 1)
            {
                Vector3 prev = wp[cornerIdx - 1];
                Vector3 curr = wp[cornerIdx];
                prevSegLen   = Vector3.Distance(
                    new Vector3(prev.x, 0, prev.z),
                    new Vector3(curr.x, 0, curr.z));
                prevIsStr    = true;
            }

            float distFromUser = Vector3.Distance(
                new Vector3(EvalPos.x, 0, EvalPos.z),
                new Vector3(wp[cornerIdx].x, 0, wp[cornerIdx].z));
            int stepsFromUser = Mathf.Max(1, Mathf.RoundToInt(distFromUser / _stepLength));

            string turnLabel = TurnLabel(ttype, signedAngle);

            if (prevIsStr && prevSegLen >= _minMentionableStraightDist && stepsFromUser > 2)
            {
                int approachSteps = Mathf.Max(1, Mathf.RoundToInt(distFromUser / _stepLength));
                // ✅ v5.5: Texto corto
                return $"{approachSteps} pasos recto, luego {turnLabel}.";
            }

            if (stepsFromUser <= 3)
                return $"{TurnLabelImperative(ttype, signedAngle)} ahora.";

            return $"En {stepsFromUser} pasos, {turnLabel}.";
        }

        private static string TurnLabel(VoiceInstructionType ttype, float signedAngle)
        {
            if (ttype == VoiceInstructionType.UTurn)
                return "date la vuelta";

            int    clock    = ClockPosition(signedAngle);
            string clockStr = ClockText(clock);

            if (ttype == VoiceInstructionType.SlightRight || ttype == VoiceInstructionType.SlightLeft)
                return $"gira levemente {clockStr}";

            return $"gira {clockStr}";
        }

        private static string TurnLabelImperative(VoiceInstructionType ttype, float signedAngle)
        {
            if (ttype == VoiceInstructionType.UTurn)
                return "Date la vuelta";

            int    clock    = ClockPosition(signedAngle);
            string clockStr = ClockText(clock);

            if (ttype == VoiceInstructionType.SlightRight || ttype == VoiceInstructionType.SlightLeft)
                return $"Gira levemente {clockStr}";

            return $"Gira {clockStr}";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  RESET DE SESIÓN
        // ─────────────────────────────────────────────────────────────────────

        private void ResetSession(bool silent = false)
        {
            if (_waitCoroutine != null)       { StopCoroutine(_waitCoroutine);      _waitCoroutine      = null; }
            if (_ttsResumeCoroutine != null)  { StopCoroutine(_ttsResumeCoroutine); _ttsResumeCoroutine = null; }

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

            _lastSpokenText    = string.Empty;
            _lastSpokenTime    = -999f;
            _lastAnyMessageTime = -999f; // ✅ v5.5

            _misalignTimer    = 0f;
            _lastMisalignTime = -999f;

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
                float d = PointToSeg3D(upos, wp[i], wp[i + 1]);
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

        private static float PointToSeg3D(Vector3 pt, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq < 0.0001f) return Vector3.Distance(pt, a);
            float t = Mathf.Clamp01(Vector3.Dot(pt - a, ab) / lenSq);
            return Vector3.Distance(pt, a + t * ab);
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
        //  HELPERS — PALABRAS SIN ALLOC
        // ─────────────────────────────────────────────────────────────────────

        private static int CountWords(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int  count  = 0;
            bool inWord = false;
            for (int i = 0; i < s.Length; i++)
            {
                bool isSpace = s[i] == ' ';
                if (!isSpace && !inWord) { count++; inWord = true;  }
                else if (isSpace)        {          inWord = false; }
            }
            return count;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  v5.0 — INSTRUCCIONES DIRECCIONALES RELATIVAS AL USUARIO
        // ─────────────────────────────────────────────────────────────────────

        private static int ClockPosition(float signedAngle)
        {
            float a = signedAngle % 360f;
            if (a < 0f) a += 360f;
            int hour = Mathf.RoundToInt(a / 30f) % 12;
            return hour == 0 ? 12 : hour;
        }

        private static string ClockText(int hour)
        {
            return hour == 1 ? "a la 1" : $"a las {hour}";
        }

        private static float SignedAngleXZ(Vector3 from, Vector3 to)
        {
            from.y = 0f;
            to.y   = 0f;
            if (from.sqrMagnitude < 0.001f || to.sqrMagnitude < 0.001f) return 0f;
            return Vector3.SignedAngle(from, to, Vector3.up);
        }

        private static string DirectionLabel(float signedAngle)
        {
            float abs   = Mathf.Abs(signedAngle);
            bool  right = signedAngle >= 0f;

            if (abs <= 15f)  return "recto";
            if (abs <= 50f)  return right ? "ligeramente a la derecha" : "ligeramente a la izquierda";
            if (abs <= 130f) return right ? "a la derecha"             : "a la izquierda";
                             return right ? "casi detrás tuyo, hacia la derecha"
                                          : "casi detrás tuyo, hacia la izquierda";
        }

        /// <summary>
        /// ✅ v5.5: Textos de orientación inicial recortados ~40%.
        /// Mismo algoritmo v5.4d pero mensajes más concisos.
        /// </summary>
        private void AnnounceInitialOrientation(IReadOnlyList<Vector3> waypoints)
        {
            if (!IsFullARMode) return;
            if (waypoints == null || waypoints.Count < 2) return;

            float totalDist  = AccumDistAlongPath(waypoints, 0, waypoints.Count - 1);
            int   totalSteps = Mathf.Max(1, Mathf.RoundToInt(totalDist / _stepLength));

            float straightDist     = 0f;
            int   firstTurnWpIdx   = -1;
            float firstTurnDeflect = 0f;
            Vector3 routeFirstDir  = Vector3.zero;

            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                Vector3 seg = waypoints[i + 1] - waypoints[i];
                seg.y = 0f;
                if (seg.sqrMagnitude < 0.001f) continue;

                float segLen = new Vector3(seg.x, 0, seg.z).magnitude;

                if (i == 0)
                    routeFirstDir = seg.normalized;

                if (i == 0)
                {
                    straightDist += segLen;
                    continue;
                }

                Vector3 prevSeg = waypoints[i] - waypoints[i - 1];
                prevSeg.y = 0f;
                if (prevSeg.sqrMagnitude < 0.001f) { straightDist += segLen; continue; }

                float deflection = Vector3.Angle(prevSeg.normalized, seg.normalized);

                if (deflection < _straightSegmentAngle)
                {
                    straightDist += segLen;
                }
                else
                {
                    firstTurnWpIdx   = i;
                    firstTurnDeflect = deflection;
                    break;
                }
            }

            float  initialAngle = routeFirstDir.sqrMagnitude > 0.001f
                ? SignedAngleXZ(UserFwd, routeFirstDir)
                : 0f;
            string initialDir   = DirectionLabel(initialAngle);

            int    straightSteps = Mathf.Max(1, Mathf.RoundToInt(straightDist / _stepLength));
            string text;

            int    clockHour = ClockPosition(initialAngle);
            string clockStr  = ClockText(clockHour);

            if (firstTurnWpIdx < 0)
            {
                // ✅ v5.5: Sala abierta — texto corto
                if (clockHour == 12)
                    text = $"Destino al frente. {totalSteps} pasos en línea recta.";
                else if (clockHour == 6)
                    text = $"Destino {clockStr}. Date la vuelta y camina {totalSteps} pasos.";
                else
                    text = $"Destino {clockStr}. Gira hasta tenerlo al frente y camina {totalSteps} pasos.";
            }
            else if (straightDist >= _minMentionableStraightDist)
            {
                // ✅ v5.5: Pasillo — texto corto
                Vector3 dirIn  = (waypoints[firstTurnWpIdx]     - waypoints[firstTurnWpIdx - 1]);
                Vector3 dirOut = (waypoints[firstTurnWpIdx + 1] - waypoints[firstTurnWpIdx]);
                dirIn.y = 0f; dirOut.y = 0f;
                float  turnAngle  = dirIn.sqrMagnitude > 0.001f && dirOut.sqrMagnitude > 0.001f
                    ? SignedAngleXZ(dirIn.normalized, dirOut.normalized) : 0f;
                string turnDir    = DirectionLabel(turnAngle);
                float  absTurnAng = Mathf.Abs(turnAngle);
                string giroLabel  = absTurnAng >= _definiteTurnAngle
                    ? $"gira {turnDir}"
                    : $"gira levemente {turnDir}";

                if (clockHour == 12)
                    text = $"{straightSteps} pasos recto, luego {giroLabel}.";
                else
                    text = $"Pasillo {clockStr}. {straightSteps} pasos recto, luego {giroLabel}.";
            }
            else
            {
                // ✅ v5.5: Giro casi inmediato — texto corto
                if (clockHour == 6)
                    text = $"Destino {clockStr}. Date la vuelta y camina {totalSteps} pasos.";
                else if (clockHour == 12)
                    text = $"Destino al frente. {totalSteps} pasos.";
                else
                    text = $"Destino {clockStr}. Gira al frente y camina {totalSteps} pasos.";
            }

            Speak(VoiceInstructionType.StartNavigation, text, priority: 1);

            if (_logInstructions)
                Debug.Log($"[VoiceGuide] 🧭 [v5.5] Orientación inicial: " +
                          $"recto={straightDist:F1}m ({straightSteps}p) | " +
                          $"primerGiroWp={firstTurnWpIdx} deflexión={firstTurnDeflect:F1}° | " +
                          $"ángulo inicial={initialAngle:F1}° | total={totalDist:F1}m ({totalSteps}p).");
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

            string text     = isDirectional ? RecalcTurnTextRelativeToUser(evt) : evt.InstructionText;
            int    priority = GetPriority(evt.Type);
            Speak(evt.Type, text, priority);
        }

        private string RecalcTurnTextRelativeToUser(NavigationInstructionEvent evt)
        {
            int nextEvtIdx = -1;
            for (int i = 0; i < _events.Count; i++)
            {
                if (_events[i] == evt) { nextEvtIdx = i + 1; break; }
            }

            if (nextEvtIdx < 0 || nextEvtIdx >= _events.Count)
                return evt.InstructionText;

            Vector3 dirOut = _events[nextEvtIdx].WorldPosition - evt.WorldPosition;
            dirOut.y = 0f;
            if (dirOut.sqrMagnitude < 0.001f) return evt.InstructionText;
            dirOut.Normalize();

            Vector3 dirIn = evt.WorldPosition - (nextEvtIdx >= 2
                ? _events[nextEvtIdx - 2].WorldPosition
                : EvalPos);
            dirIn.y = 0f;
            if (dirIn.sqrMagnitude < 0.001f) dirIn = dirOut;
            dirIn.Normalize();

            var (ttype, signedAngle) = ClassifyTurnRelativeToUser(dirIn, dirOut, isImmediateTurn: true);

            float distFromUser = Vector3.Distance(
                new Vector3(EvalPos.x, 0, EvalPos.z),
                new Vector3(evt.WorldPosition.x, 0, evt.WorldPosition.z));
            int stepsFromUser = Mathf.Max(1, Mathf.RoundToInt(distFromUser / _stepLength));

            if (stepsFromUser <= 3)
                return $"{TurnLabelImperative(ttype, signedAngle)} ahora.";

            return $"En {stepsFromUser} pasos, {TurnLabel(ttype, signedAngle)}.";
        }

        private float TriggerDist(VoiceInstructionType t) => t switch
        {
            VoiceInstructionType.UTurn       => _turnTriggerDist * 1.5f,
            VoiceInstructionType.SlightLeft  => _turnTriggerDist * 0.7f,
            VoiceInstructionType.SlightRight => _turnTriggerDist * 0.7f,
            _                                => _turnTriggerDist,
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

        /// <summary>
        /// ✅ v5.5: Registra _lastAnyMessageTime en cada Speak() para el
        /// espaciado mínimo global entre mensajes de baja prioridad.
        /// </summary>
        private void Speak(VoiceInstructionType type, string text, int priority)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (text == _lastSpokenText && Time.time - _lastSpokenTime < _dedupWindow)
            {
                if (_logInstructions)
                    Debug.Log($"[VoiceGuide] 🔇 DEDUP suprimido [{type}]: \"{text}\"");
                return;
            }
            _lastSpokenText     = text;
            _lastSpokenTime     = Time.time;
            _lastAnyMessageTime = Time.time; // ✅ v5.5: tracking global

            var announcementType = type switch
            {
                VoiceInstructionType.StairsWarning         => GuideAnnouncementType.ApproachingStairs,
                VoiceInstructionType.StairsClimb           => GuideAnnouncementType.StartingClimb,
                VoiceInstructionType.StairsDescent         => GuideAnnouncementType.StartingDescent,
                VoiceInstructionType.StairsComplete        => GuideAnnouncementType.StairsComplete,
                VoiceInstructionType.ResumeAfterSeparation => GuideAnnouncementType.ResumeAfterSeparation,
                VoiceInstructionType.StartNavigation       => GuideAnnouncementType.StartNavigation,
                VoiceInstructionType.Arrived               => GuideAnnouncementType.Arrived,
                VoiceInstructionType.TurnLeft              => GuideAnnouncementType.TurnLeft,
                VoiceInstructionType.TurnRight             => GuideAnnouncementType.TurnRight,
                VoiceInstructionType.SlightLeft            => GuideAnnouncementType.SlightLeft,
                VoiceInstructionType.SlightRight           => GuideAnnouncementType.SlightRight,
                VoiceInstructionType.UTurn                 => GuideAnnouncementType.UTurn,
                VoiceInstructionType.GoStraight            => GuideAnnouncementType.GoStraight,
                VoiceInstructionType.UserStopped           => GuideAnnouncementType.WaitingForUser,
                VoiceInstructionType.UserDeviated          => GuideAnnouncementType.UserDeviated,
                VoiceInstructionType.ObstacleWarning       => GuideAnnouncementType.ObstacleWarning,
                VoiceInstructionType.ProgressUpdate        => GuideAnnouncementType.ProgressUpdate,
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

                float wordCount = CountWords(text);
                float estimatedSecs = (wordCount / 10f) + 1.5f;

                if (_ttsResumeCoroutine != null) StopCoroutine(_ttsResumeCoroutine);
                _ttsResumeCoroutine = StartCoroutine(AutoResumeTTSAfter(estimatedSecs));
            }

            if (_logInstructions)
                Debug.Log($"[VoiceGuide] 🔊 [{type}→{announcementType}] p={priority} \"{text}\"");
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
            _ttsResumeCoroutine = null;
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

            if (IsFullARMode && _nextIdx < _events.Count)
            {
                Vector3 nextWpPos = _events[_nextIdx].WorldPosition;
                Vector3 toNext    = nextWpPos - UserPos;
                toNext.y = 0f;

                if (toNext.sqrMagnitude > 0.25f)
                {
                    float angle = SignedAngleXZ(UserFwd, toNext.normalized);
                    float abs   = Mathf.Abs(angle);

                    Gizmos.color = abs <= 15f  ? Color.green :
                                   abs <= 50f  ? Color.yellow :
                                   abs <= 130f ? new Color(1f, 0.5f, 0f) : Color.red;

                    Gizmos.DrawLine(UserPos, nextWpPos);
                    Gizmos.DrawWireSphere(nextWpPos, 0.12f);

                    Gizmos.color = new Color(0f, 1f, 1f, 0.6f);
                    Gizmos.DrawLine(UserPos, UserPos + UserFwd * 1.5f);
                }
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
                $"[VoiceGuide] v5.5 — IsGuiding={_isGuiding} | IsPreprocessing={_isPreprocessing}\n" +
                $"Destino='{_destName}' | Events={_events.Count} | NextIdx={_nextIdx}\n" +
                $"Modo={( IsFullARMode ? "FullAR" : "NoAR")} \n" +
                $"UserPos={UserPos:F2} | EvalPos={EvalPos:F2}\n" +
                $"UserFwd={UserFwd:F2} | UserSpeed={UserSpeed:F2}m/s\n" +
                $"RemainingDist={rem:F1}m (~{Mathf.RoundToInt(rem / _stepLength)} pasos)\n" +
                $"[TTS] ttsBusy={_ttsBusy} | lastMsg={Time.time - _lastAnyMessageTime:F1}s ago\n" +
                $"[E1] Stopped={_isStopped} StopAccum={_stopAccumTime:F1}s\n" +
                $"[E2] DeviationTimer={_deviationTimer:F1}s Fired={_deviationFired}\n" +
                $"[E3] ObstacleTimer={_obstacleTimer:F1}s Fired={_obstacleFired}\n" +
                $"[E7] MisalignTimer={_misalignTimer:F1}s\n" +
                $"Path: valid={path?.IsValid} wp={path?.Waypoints.Count} len={path?.TotalLength:F1}m");
        }

        [ContextMenu("🛑 Detener guía")]
        private void DebugStop() => ResetSession();

        [ContextMenu("📐 Test: Ver waypoints subdivididos")]
        private void DebugSubdivision()
        {
            var path = _pathController?.CurrentPath;
            if (path == null || !path.IsValid)
            {
                Debug.Log("[VoiceGuide] Sin ruta activa.");
                return;
            }
            var original   = path.Waypoints;
            var subdivided = SubdivideWaypointSegments(original);
            Debug.Log($"[VoiceGuide] 📐 Subdivisión: {original.Count} → {subdivided.Count} waypoints.");
            for (int i = 0; i < subdivided.Count; i++)
                Debug.Log($"  [{i}] {subdivided[i]:F2}");
        }

        [ContextMenu("🧭 Test: Forzar AnnounceInitialOrientation")]
        private void DebugForceOrientation()
        {
            var path = _pathController?.CurrentPath;
            if (path == null || !path.IsValid)
            {
                Debug.Log("[VoiceGuide] Sin ruta activa.");
                return;
            }
            var subdivided = SubdivideWaypointSegments(path.Waypoints);
            AnnounceInitialOrientation(subdivided);
        }
    }
}