// File: NavigationVoiceGuide.cs
// ✅ v6.3 — Escaleras en cola · Orientación sin interrupt · TTS coordinado con Flutter
//
// ══════════════════════════════════════════════════════════════════════════════
// CAMBIOS v6.2 → v6.3
// ══════════════════════════════════════════════════════════════════════════════
//
// 1. ESCALERAS COMO COLA (no interrupt)
//    — StairsWarning, StairsClimb, StairsDescent: prioridad 2 (no 3).
//      Solo ObstacleWarning y UTurn mantienen prioridad 3 con interrupt.
//    — Mensajes de escaleras reducidos a frases cortas:
//        StairsWarning:  "Escaleras en X pasos."        (sin "Reduce el paso")
//        StairsClimb:    "Sube."
//        StairsDescent:  "Baja."
//        StairsComplete: "Escaleras terminadas."
//    — Resultado: las escaleras se encolan detrás de la instrucción activa
//      en lugar de cortarla.
//
// 2. ORIENTACIÓN INICIAL SIN INTERRUPT
//    — AnnounceInitialOrientation() cambia de priority=3 a priority=2.
//    — Esto evita que la orientación corte "Listo, vamos a X." que acaba
//      de empezar a leerse.
//    — El WaitForPath coroutine ya introduce 0.2s entre el StartNavigation
//      y la orientación; con priority=2 la orientación espera a que termine
//      el TTS en curso antes de reproducirse.
//
// 3. TIMEOUT DE ttsBusy REDUCIDO
//    — _ttsFallbackTimeout: 20s → 8s.
//      El motor Android tarda máximo 4-6s en leer las frases más largas.
//      20s de fallback era el motivo por el que el sistema quedaba bloqueado
//      tras un cancel/interrupt sin confirmación de Flutter.
//
// 4. _ttsBusy SOLO EN PRIORITY >= 2 (sin cambio) — documentado explícitamente
//    — Priority 0 y 1 nunca setean _ttsBusy, así que no bloquean el evaluador.
//    — Priority >= 2 sí lo setean. Flutter debe enviar tts_status done/cancel
//      para liberarlo, o el fallback de 8s lo libera automáticamente.
//
// 5. MENSAJES ELIMINADOS
//    — No se genera ningún TTS de "Sistema detenido" desde este componente.
//      Ese mensaje lo generaba NavigationCoordinator.stop() desde Flutter;
//      ya no se genera (ver NavigationCoordinator v7.3).
//
// TODOS LOS CAMBIOS DE v6.2 SE CONSERVAN ÍNTEGRAMENTE.

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
        StairsWarning,
        StairsSafetyWarning,   // ✅ FIX P2: nuevo — a ~2m, mensaje de seguridad
        StairsClimb, StairsDescent, StairsComplete,
        Arrived, UserStopped, UserDeviated, ObstacleWarning,
        ProgressUpdate, ResumeAfterSeparation,
    }

    public sealed class NavigationInstructionEvent
    {
        public Vector3 WorldPosition { get; }
        public VoiceInstructionType Type { get; }
        public float TriggerDistance { get; }
        public string InstructionText { get; }
        public bool HasFired { get; internal set; }
        public int CornerIndex { get; }
        public int Floor { get; }

        public NavigationInstructionEvent(
            Vector3 worldPosition, VoiceInstructionType type,
            float triggerDistance, string instructionText, int cornerIndex,
            int floor = 0)   // ← nuevo parámetro con default para compatibilidad
        {
            WorldPosition  = worldPosition;
            Type           = type;
            TriggerDistance = triggerDistance;
            InstructionText = instructionText;
            CornerIndex    = cornerIndex;
            Floor          = floor;
        }
    }

    public sealed class NavigationVoiceGuide : MonoBehaviour
    {
        public static NavigationVoiceGuide Instance { get; private set; }

        [Header("─── Referencias ─────────────────────────────────────────────")]
        [SerializeField] private UserPositionBridge _userBridge;
        [SerializeField] private NavigationPathController _pathController;

        [Header("─── Triggers de distancia ──────────────────────────────────")]
        [SerializeField] private float _turnTriggerDist = 5.0f;
        [SerializeField] private float _stairTriggerDist = 6.0f;
        [SerializeField] private float _arrivalTriggerDist = 1.5f;
        [SerializeField] private float _straightReminderDist = 12.0f;

        [Header("─── Rendimiento ─────────────────────────────────────────────")]
        [SerializeField, Range(0.05f, 0.5f)]
        private float _evalInterval = 0.10f;

        [Header("─── Espera de Ruta ──────────────────────────────────────────")]
        [SerializeField] private float _pathWaitTimeout = 3.0f;
        [SerializeField] private float _pathPollInterval = 0.1f;
        [SerializeField] private float _destinationChangeThreshold = 0.5f;

        [Header("─── Timing de inicio ───────────────────────────────────────")]
        [SerializeField] private float _startDelay = 2.5f;

        [Header("─── Escaleras ──────────────────────────────────────────────")]
        [SerializeField] private float _stairHeightThreshold = 0.3f;
        [SerializeField] private float _stairYTolerance = 1.2f;

        [Header("─── Ángulos de Giro ─────────────────────────────────────────")]
        [SerializeField] private float _slightTurnAngle = 20f;
        [SerializeField] private float _definiteTurnAngle = 50f;
        [SerializeField] private float _uTurnAngle = 140f;

        [Header("─── Subdivisión de segmentos ───────────────────────────────")]
        [SerializeField] private float _maxSegmentLength = 3.0f;
        [SerializeField] private float _straightSegmentAngle = 15f;
        [SerializeField] private float _minMentionableStraightDist = 1.5f;

        [Header("─── Física Humana ─────────────────────────────────────────")]
        [SerializeField] private float _walkSpeedFlat = 0.8f;
        [SerializeField] private float _walkSpeedStairs = 0.4f;
        [SerializeField] private float _stepLength = 0.7f;

        [Header("─── Recordatorios ───────────────────────────────────────────")]
        [SerializeField] private float _straightReminderInterval = 20f;
        [SerializeField] private float _progressInterval = 45f;

        [Header("─── [E1] Parada ─────────────────────────────────────────────")]
        [SerializeField] private float _stopTimeout = 4.0f;
        [SerializeField] private float _stopMinMovement = 0.25f;
        [SerializeField] private float _stopReminderInterval = 45.0f;

        [Header("─── [E2] Desviación ────────────────────────────────────────")]
        [SerializeField] private float _deviationDist = 2.0f;
        [SerializeField] private float _deviationDelay = 2.5f;

        [Header("─── [E3] Obstáculo ─────────────────────────────────────────")]
        [SerializeField] private float _obstacleCheckTime = 6.0f;
        [SerializeField] private float _obstacleWarningCooldown = 60f;

        [Header("─── [E6] Separación ────────────────────────────────────────")]
        [SerializeField] private float _longSeparationTime = 12.0f;

        [Header("─── [E7] Desorientación ────────────────────────────────────")]
        [SerializeField] private float _misalignAngleThreshold = 45f;
        [SerializeField] private float _misalignConfirmTime = 3.0f;
        [SerializeField] private float _misalignReminderInterval = 12f;
        [SerializeField] private float _misalignMinSpeed = 0.2f;

        [Header("─── Anti-saturación ──────────────────────────────────────")]
        [SerializeField] private float _dedupWindow = 15.0f;
        [SerializeField] private float _minMessageInterval = 3.5f;
        // ✅ v6.3: reducido de 20s a 8s — el motor Android tarda máx ~6s
        //          en leer las frases más largas del sistema.
        [SerializeField] private float _ttsFallbackTimeout = 8f;
        [SerializeField] private float _sameTypeCooldown = 8.0f;

        [Header("─── Debug ────────────────────────────────────────────────────")]
        [SerializeField] private bool _logInstructions = true;
        [SerializeField] private bool _logPreprocessing = true;

        // ── Estado ────────────────────────────────────────────────────────────
        private readonly List<NavigationInstructionEvent> _events = new(24);
        private int _nextIdx = 0;
        private bool _isGuiding = false;
        private bool _isPreprocessing = false;
        private string _destName = string.Empty;
        private Vector3 _destPos = new(float.PositiveInfinity, 0, 0);

        private string _pendingDestinationId = string.Empty;

        private float _lastStraightTime = -999f;
        private int _lastStraightIdx = -1;
        private float _lastProgressTime = -999f;
        private float _lastAnyMessageTime = -999f;

        private Vector3 _stopRefPos = Vector3.zero;
        private float _stopAccumTime = 0f;
        private bool _isStopped = false;
        private float _lastStopReminder = -999f;

        private float _deviationTimer = 0f;
        private bool _deviationFired = false;

        private float _obstacleTimer = 0f;
        private float _lastDistToNext = float.MaxValue;
        private bool _obstacleFired = false;
        private float _lastObstacleWarningTime = -999f;

        private float _returningTimer = 0f;
        private int _currentFloor = 0;

        private Coroutine _waitCoroutine = null;
        private Coroutine _ttsFallbackCoroutine = null;

        private float _evalAccum = 0f;
        private string _lastSpokenText = string.Empty;
        private float _lastSpokenTime = -999f;

        private float _misalignTimer = 0f;
        private float _lastMisalignTime = -999f;

        private bool _ttsBusy = false;

        private readonly Dictionary<VoiceInstructionType, float> _lastSpokenByType
            = new Dictionary<VoiceInstructionType, float>();

        private string _lastSpokenTextForRepeat = string.Empty;
        private int _lastSpokenPriorityRepeat = 0;

        // ─────────────────────────────────────────────────────────────────────
        //  POSICIONES
        // ─────────────────────────────────────────────────────────────────────

        private bool IsFullARMode => _userBridge != null && !_userBridge.IsNoArMode;
        private Vector3 UserPos => _userBridge != null ? _userBridge.UserPosition
                                        : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);
        private Vector3 UserFwd => _userBridge != null ? _userBridge.UserForward
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

            if (_pathController == null)
                _pathController = FindFirstObjectByType<NavigationPathController>(FindObjectsInactive.Include);

            if (_pathController != null)
            {
                _pathController.OnPathRecalculated -= OnPathRecalculated;
                _pathController.OnPathRecalculated += OnPathRecalculated;
            }

            SubscribeEvents();
            Debug.Log($"[VoiceGuide] ✅ v6.3");
        }

        private void OnEnable() => SubscribeEvents();
        private void OnDisable() => UnsubscribeEvents();

        private void SubscribeEvents()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Subscribe<NavigationStartedEvent>(OnNavStarted);
            bus.Subscribe<NavigationCompletedEvent>(OnNavCompleted);
            bus.Subscribe<NavigationCancelledEvent>(OnNavCancelled);
            bus.Subscribe<FloorTransitionEvent>(OnFloorTransition);
            bus.Subscribe<ObstacleDetectedEvent>(OnObstacleDetected);
        }

        private void UnsubscribeEvents()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Unsubscribe<NavigationStartedEvent>(OnNavStarted);
            bus.Unsubscribe<NavigationCompletedEvent>(OnNavCompleted);
            bus.Unsubscribe<NavigationCancelledEvent>(OnNavCancelled);
            bus.Unsubscribe<FloorTransitionEvent>(OnFloorTransition);
            bus.Unsubscribe<ObstacleDetectedEvent>(OnObstacleDetected);
        }

        private void OnDestroy()
        {
            if (_pathController != null) _pathController.OnPathRecalculated -= OnPathRecalculated;
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
            float dt = _evalAccum; _evalAccum = 0f;

            EvaluateInstructions();
            EvaluateUserStop(dt);
            EvaluateDeviation(dt);
            EvaluateObstacle(dt);
            EvaluateProgress();
            EvaluateMisalignment(dt);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVALUACIÓN
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateInstructions()
        {
            if (_nextIdx >= _events.Count) return;

            Vector3 evalPos = EvalPos;
            Vector3 userPos = UserPos;

            for (int i = _nextIdx; i < _events.Count; i++)
            {
                var evt = _events[i];
                if (evt.HasFired) { _nextIdx = i + 1; continue; }

                // ✅ FIX P1: saltar eventos generados para un piso distinto al actual.
                // Excepción: StairsWarning / StairsSafetyWarning se generan en el piso
                // de origen pero deben dispararse cuando el usuario sigue en ese piso,
                // así que los permitimos siempre que el evento sea de un piso adyacente.
                bool isStairTransitionEvent =
                    evt.Type == VoiceInstructionType.StairsWarning    ||
                    evt.Type == VoiceInstructionType.StairsSafetyWarning ||
                    evt.Type == VoiceInstructionType.StairsClimb      ||
                    evt.Type == VoiceInstructionType.StairsDescent     ||
                    evt.Type == VoiceInstructionType.StairsComplete;

                if (!isStairTransitionEvent && evt.Floor != _currentFloor)
                {
                    // El evento es de otro piso. Si el usuario ya lo superó en Y, marcarlo
                    // como disparado para no bloquear el índice.
                    float yDist = Mathf.Abs(UserPos.y - evt.WorldPosition.y);
                    if (yDist > 1.5f)
                    {
                        evt.HasFired = true;
                        _nextIdx = i + 1;
                        if (_logInstructions)
                            Debug.Log($"[VoiceGuide] ⏭ Evento [{evt.Type}] floor={evt.Floor} " +
                                    $"saltado (usuario en floor={_currentFloor}).");
                    }
                    // Si no está superado en Y, simplemente no disparar y romper el loop
                    else break;
                    continue;
                }

                if (_ttsBusy && GetPriority(evt.Type) < 3) break;

                Vector3 checkPos = evt.Type == VoiceInstructionType.Arrived ? userPos : evalPos;
                if (!ShouldFireEvent(evt, checkPos)) break;

                FireEvent(evt);
                evt.HasFired = true;
                _nextIdx = i + 1;
                if (GetPriority(evt.Type) >= 3) return;
            }

            EvaluateStraightReminder();
        }

        private bool ShouldFireEvent(NavigationInstructionEvent evt, Vector3 checkPos)
        {
            if (Vector3.Distance(checkPos, evt.WorldPosition) > evt.TriggerDistance) return false;
            if (evt.Type == VoiceInstructionType.StairsComplete || evt.Type == VoiceInstructionType.Arrived)
                if (Mathf.Abs(UserPos.y - evt.WorldPosition.y) > _stairYTolerance) return false;
            return true;
        }

        /// <summary>
        /// Llamado desde VoiceCommandAPI cuando Flutter confirma TTS done/cancel.
        /// </summary>
        public void ClearTTSBusy()
        {
            if (!_ttsBusy) return;
            _ttsBusy = false;
            if (_ttsFallbackCoroutine != null) { StopCoroutine(_ttsFallbackCoroutine); _ttsFallbackCoroutine = null; }
            if (_logInstructions) Debug.Log("[VoiceGuide] ✅ _ttsBusy liberado por Flutter.");
        }

        private void EvaluateStraightReminder()
        {
            if (_nextIdx >= _events.Count || _nextIdx == _lastStraightIdx) return;
            var next = _events[_nextIdx];
            if (next.HasFired) return;

            float dist = Vector3.Distance(EvalPos, next.WorldPosition);
            if (dist < _straightReminderDist) return;
            if (Time.time - _lastStraightTime < _straightReminderInterval) return;

            if (_ttsBusy || Time.time - _lastAnyMessageTime < _minMessageInterval) return;

            if (IsTypeCoolingDown(VoiceInstructionType.GoStraight)) return;

            int steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));
            bool sent = Speak(VoiceInstructionType.GoStraight, $"Sigue recto. {steps} pasos.", 0);

            if (sent)
            {
                _lastStraightTime = Time.time;
                _lastStraightIdx = _nextIdx;
            }
        }

        private void EvaluateUserStop(float dt)
        {
            float moved = Vector3.Distance(UserPos, _stopRefPos);
            if (moved >= _stopMinMovement)
            {
                _stopRefPos = UserPos; _stopAccumTime = 0f;
                if (_isStopped) { _isStopped = false; _obstacleTimer = 0f; _lastDistToNext = float.MaxValue; }
                return;
            }

            _stopAccumTime += dt;

            if (_stopAccumTime >= _stopTimeout && !_isStopped)
            {
                _isStopped = true; _lastStopReminder = Time.time;
                _obstacleTimer = 0f; _lastDistToNext = DistUserToNextWp();
                int steps = Mathf.Max(1, Mathf.RoundToInt(_lastDistToNext / _stepLength));
                Speak(VoiceInstructionType.UserStopped,
                    $"Cuando estés listo, continúa. {steps} pasos.", 0);
                return;
            }

            if (_isStopped && Time.time - _lastStopReminder >= _stopReminderInterval)
            {
                if (_ttsBusy || Time.time - _lastAnyMessageTime < _minMessageInterval)
                {
                    _lastStopReminder = Time.time;
                    return;
                }
                _lastStopReminder = Time.time;
                int steps = Mathf.Max(1, Mathf.RoundToInt(RemainingDistFromUser() / _stepLength));
                Speak(VoiceInstructionType.UserStopped, $"Tómate tu tiempo. {steps} pasos.", 0);
            }
        }

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
                    string msg = IsFullARMode
                        ? "Te desviaste. Detente y gira hacia la ruta."
                        : "Te desviaste. Busca al guía.";
                    Speak(VoiceInstructionType.UserDeviated, msg, 2);

                    // ✅ FIX P3: publicar evento para que PathController recalcule
                    EventBus.Instance?.Publish(new RouteDeviatedEvent
                    {
                        UserPosition      = UserPos,
                        DeviationDistance = lateral,
                        Destination       = _destPos,
                    });
                }
            }
            else
            {
                if (_deviationFired && lateral < _deviationDist * 0.5f) _deviationFired = false;
                _deviationTimer = 0f;
            }
        }


        private float LateralDeviationFromRoute()
        {
            var wp = _pathController?.CurrentPath?.Waypoints;
            if (wp == null || wp.Count < 2) return 0f;
            float min = float.MaxValue;
            int start = Mathf.Max(0, _nextIdx - 2), end = Mathf.Min(wp.Count - 2, _nextIdx + 2);
            for (int i = start; i <= end; i++) { float d = SegDistXZ(UserPos, wp[i], wp[i + 1]); if (d < min) min = d; }
            return min < float.MaxValue ? min : 0f;
        }

        private void EvaluateObstacle(float dt)
        {
            if (!_isStopped || _obstacleFired) return;
            _obstacleTimer += dt;
            if (_obstacleTimer < _obstacleCheckTime) return;
            if (Time.time - _lastObstacleWarningTime < _obstacleWarningCooldown) return;

            float current = DistUserToNextWp();
            float reduction = _lastDistToNext - current;
            if (reduction < 0.4f)
            {
                _obstacleFired = true; _lastObstacleWarningTime = Time.time;
                Speak(VoiceInstructionType.ObstacleWarning,
                    "Posible obstáculo. Rodéalo por un lado.", 3);
            }
        }

        private void EvaluateProgress()
        {
            if (_isStopped || UserSpeed < 0.3f) return;
            if (Time.time - _lastProgressTime < _progressInterval) return;
            if (_ttsBusy || Time.time - _lastAnyMessageTime < _minMessageInterval) return;
            float rem = RemainingDistFromUser();
            if (rem <= _arrivalTriggerDist * 3f) return;
            int steps = Mathf.Max(1, Mathf.RoundToInt(rem / _stepLength));
            _lastProgressTime = Time.time;
            Speak(VoiceInstructionType.ProgressUpdate, $"Vas bien. {steps} pasos.", 0);
        }

        private void EvaluateMisalignment(float dt)
        {
            if (!IsFullARMode || UserSpeed < _misalignMinSpeed || _isStopped) return;
            if (_nextIdx >= _events.Count || _ttsBusy) return;

            Vector3 toNext = _events[_nextIdx].WorldPosition - UserPos; toNext.y = 0f;
            if (toNext.sqrMagnitude < 0.25f) return;
            toNext.Normalize();

            float signedAngle = SignedAngleXZ(UserFwd, toNext);
            float absAngle = Mathf.Abs(signedAngle);

            if (absAngle > _misalignAngleThreshold)
            {
                _misalignTimer += dt;
                if (_misalignTimer >= _misalignConfirmTime &&
                    Time.time - _lastMisalignTime >= _misalignReminderInterval)
                {
                    _lastMisalignTime = Time.time; _misalignTimer = 0f;
                    string dir = DirectionLabel(signedAngle);
                    float dist = Vector3.Distance(UserPos, _events[_nextIdx].WorldPosition);
                    int steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));
                    string text = absAngle <= 50f ? $"El camino está {dir}. {steps} pasos."
                                 : absAngle <= 130f ? $"Gira {dir}. {steps} pasos."
                                                    : $"Date la vuelta {dir}. {steps} pasos.";
                    Speak(VoiceInstructionType.UserDeviated, text, 2);
                }
            }
            else if (_misalignTimer > 0f)
                _misalignTimer = Mathf.Max(0f, _misalignTimer - dt * 1.5f);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVENTOS DEL BUS
        // ─────────────────────────────────────────────────────────────────────

        private void OnNavStarted(NavigationStartedEvent evt)
        {
            if (string.IsNullOrEmpty(evt.DestinationWaypointId)) return;

            if (evt.DestinationWaypointId == _pendingDestinationId)
            {
                if (_logPreprocessing)
                    Debug.Log($"[VoiceGuide] 🔒 OnNavStarted duplicado bloqueado: {evt.DestinationWaypointId}");
                return;
            }

            if (Vector3.Distance(evt.DestinationPosition, _destPos) < _destinationChangeThreshold
                && (_isGuiding || _isPreprocessing)) return;

            _pendingDestinationId = evt.DestinationWaypointId;
            StartSession(evt.DestinationWaypointId, evt.DestinationPosition);
        }

        private void OnNavCompleted(NavigationCompletedEvent _)
        {
            if (!_isGuiding) return;
            bool arrivedFired = _events.Exists(e => e.Type == VoiceInstructionType.Arrived && e.HasFired);
            float distToGoal = Vector3.Distance(UserPos, _destPos);
            if (!arrivedFired && distToGoal > _arrivalTriggerDist * 2f) return;
            if (!arrivedFired)
                Speak(VoiceInstructionType.Arrived,
                    string.IsNullOrEmpty(_destName) ? "Llegaste." : $"Llegaste a {_destName}.", 1);
            ResetSession();
        }

        private void OnNavCancelled(NavigationCancelledEvent _) => ResetSession();

        private void OnFloorTransition(FloorTransitionEvent e)
        {
            _currentFloor = e.ToLevel;
            _obstacleFired = false;
            _isStopped     = false;
            _stopAccumTime = 0f;

            ResetTypeCooldown(VoiceInstructionType.StairsWarning);
            ResetTypeCooldown(VoiceInstructionType.StairsSafetyWarning);  // ✅ FIX P2
            ResetTypeCooldown(VoiceInstructionType.StairsClimb);
            ResetTypeCooldown(VoiceInstructionType.StairsDescent);
            ResetTypeCooldown(VoiceInstructionType.StairsComplete);

            _misalignTimer    = 0f;
            _lastMisalignTime = -999f;

            // ✅ FIX P1: Regenerar instrucciones desde la posición actual del usuario
            // para no heredar giros calculados con la geometría del piso anterior.
            var currentPath = _pathController?.CurrentPath;
            if (currentPath != null && currentPath.IsValid && currentPath.Waypoints.Count >= 2)
            {
                Debug.Log($"[VoiceGuide] 🔄 FloorTransition → Nivel {e.ToLevel}: " +
                        "Resync de instrucciones.");
                // Resync sin fullSummary: el VoiceGuide ya habrá dicho "Escaleras terminadas."
                Resync(currentPath.Waypoints, fullSummary: false);
            }
        }

        private void OnObstacleDetected(ObstacleDetectedEvent evt)
        {
            if (!_isGuiding) return;
            if (Time.time - _lastObstacleWarningTime < _obstacleWarningCooldown) return;

            _lastObstacleWarningTime = Time.time;
            _obstacleFired           = true;   // ← evita que EvaluateObstacle() lo dispare también
            _obstacleTimer           = 0f;     // ← resetea el timer interno

            Speak(VoiceInstructionType.ObstacleWarning,
                "Obstáculo detectado. Buscando ruta alternativa.", priority: 3);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  RECÁLCULO MID-ROUTE
        // ─────────────────────────────────────────────────────────────────────

        private void OnPathRecalculated(OptimizedPath newPath)
        {
            if (!_isGuiding || newPath == null || !newPath.IsValid || newPath.Waypoints.Count < 2) return;
            Vector3 pathEnd = newPath.Waypoints[newPath.Waypoints.Count - 1];
            if (Vector3.Distance(pathEnd, _destPos) > 2.0f) { _returningTimer += Time.deltaTime; return; }

            bool longSep = _returningTimer >= _longSeparationTime; _returningTimer = 0f;
            if (_waitCoroutine != null) { StopCoroutine(_waitCoroutine); _waitCoroutine = null; _isPreprocessing = false; }

            _obstacleFired = false; _deviationFired = false; _isStopped = false;
            _stopAccumTime = 0f; _deviationTimer = 0f; _misalignTimer = 0f; _lastMisalignTime = -999f;
            Resync(newPath.Waypoints, longSep);
        }

        private void Resync(IReadOnlyList<Vector3> waypoints, bool fullSummary)
        {
            _events.Clear(); _nextIdx = 0;
            _lastStraightTime = Time.time; _lastStraightIdx = -1;
            var subdivided = SubdivideWaypointSegments(waypoints);
            BuildInstructions(subdivided, startMessage: false);
            float rem = RemainingDistFromUser(subdivided);
            int steps = Mathf.Max(1, Mathf.RoundToInt(rem / _stepLength));
            if (fullSummary)
                Speak(VoiceInstructionType.ResumeAfterSeparation,
                    IsFullARMode ? $"Ruta recalculada. {steps} pasos a {_destName}."
                                 : $"El guía te encontró. {steps} pasos.", 1);
            else
                Speak(VoiceInstructionType.GoStraight, $"Ruta actualizada. {steps} pasos.", 0);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  API PÚBLICA — uso interno
        // ─────────────────────────────────────────────────────────────────────

        // ✅ v6.2 FIX conservado — guard idéntico al de OnNavStarted().
        public void TriggerFromWaypoint(IndoorNavAR.Core.Data.WaypointData waypoint)
        {
            if (waypoint == null) return;

            if (waypoint.WaypointName == _pendingDestinationId)
            {
                if (_logPreprocessing)
                    Debug.Log($"[VoiceGuide] 🔒 TriggerFromWaypoint duplicado bloqueado: {waypoint.WaypointName}");
                return;
            }

            if (Vector3.Distance(waypoint.Position, _destPos) < _destinationChangeThreshold
                && (_isGuiding || _isPreprocessing)) return;

            _pendingDestinationId = waypoint.WaypointName;
            StartSession(waypoint.WaypointName, waypoint.Position);
        }

        public void StopVoiceGuide() => ResetSession();

        public void SetPathController(NavigationPathController controller)
        {
            if (_pathController != null) _pathController.OnPathRecalculated -= OnPathRecalculated;
            _pathController = controller;
            if (_pathController != null) _pathController.OnPathRecalculated += OnPathRecalculated;
        }

        public IReadOnlyList<NavigationInstructionEvent> InstructionEvents => _events;
        public bool IsGuiding => _isGuiding;

        // ─────────────────────────────────────────────────────────────────────
        //  API PÚBLICA — llamable desde FlutterUnityBridge
        // ─────────────────────────────────────────────────────────────────────

        public void RepeatLastInstruction()
        {
            if (string.IsNullOrEmpty(_lastSpokenTextForRepeat))
            {
                Debug.Log("[VoiceGuide] RepeatLastInstruction: no hay instrucción previa.");
                return;
            }
            if (!_isGuiding && !_isPreprocessing)
            {
                Debug.Log("[VoiceGuide] RepeatLastInstruction: guía no activa.");
                return;
            }

            EventBus.Instance?.Publish(new TTSRequestEvent
            {
                Text = _lastSpokenTextForRepeat,
                Priority = Mathf.Max(1, _lastSpokenPriorityRepeat),
                Interrupt = false,
            });

            Debug.Log($"[VoiceGuide] 🔁 RepeatLastInstruction: \"{_lastSpokenTextForRepeat}\"");
        }

        public void StopVoiceGuideFromBridge()
        {
            Debug.Log("[VoiceGuide] 🛑 StopVoiceGuideFromBridge().");
            ResetSession();
        }

        public string GetVoiceStatusJson()
        {
            float rem = _isGuiding ? RemainingDistFromUser() : 0f;
            int steps = Mathf.Max(0, Mathf.RoundToInt(rem / _stepLength));
            return $"{{" +
                   $"\"action\":\"voice_status\"," +
                   $"\"isGuiding\":{(_isGuiding ? "true" : "false")}," +
                   $"\"isPreprocessing\":{(_isPreprocessing ? "true" : "false")}," +
                   $"\"ttsBusy\":{(_ttsBusy ? "true" : "false")}," +
                   $"\"destination\":\"{EscapeJson(_destName)}\"," +
                   $"\"remainingSteps\":{steps}," +
                   $"\"nextInstruction\":\"{EscapeJson(GetNextInstructionText())}\"" +
                   $"}}";
        }

        private string GetNextInstructionText()
        {
            for (int i = _nextIdx; i < _events.Count; i++)
                if (!_events[i].HasFired) return _events[i].InstructionText;
            return string.Empty;
        }

        private static string EscapeJson(string s) =>
            s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") ?? "";

        // ─────────────────────────────────────────────────────────────────────
        //  SESIÓN
        // ─────────────────────────────────────────────────────────────────────

        private void StartSession(string destName, Vector3 destPosition)
        {
            if (_waitCoroutine != null) { StopCoroutine(_waitCoroutine); _waitCoroutine = null; }
            ResetSession(silent: true);
            _destName = destName; _destPos = destPosition;
            _isPreprocessing = true; _stopRefPos = UserPos; _stopAccumTime = 0f; _returningTimer = 0f;
            _pendingDestinationId = destName;
            _waitCoroutine = StartCoroutine(WaitForPath());
        }

        private IEnumerator WaitForPath()
        {
            float elapsed = 0f; OptimizedPath path = null;
            float effectiveTimeout = _pathWaitTimeout * 2f;

            while (elapsed < effectiveTimeout)
            {
                path = _pathController?.CurrentPath;
                if (path != null && path.IsValid && path.Waypoints.Count >= 2)
                {
                    Vector3 end = path.Waypoints[path.Waypoints.Count - 1];
                    if (Vector3.Distance(end, _destPos) <= 1.5f) break;
                    path = null;
                }
                yield return new WaitForSeconds(_pathPollInterval);
                elapsed += _pathPollInterval;
            }

            _waitCoroutine = null; _isPreprocessing = false;

            if (path == null || !path.IsValid || path.Waypoints.Count < 2)
            {
                Speak(VoiceInstructionType.StartNavigation, $"Listo, vamos a {_destName}.", 1);
                _events.Clear(); _nextIdx = 0;
                _events.Add(new NavigationInstructionEvent(_destPos, VoiceInstructionType.Arrived,
                    _arrivalTriggerDist,
                    string.IsNullOrEmpty(_destName) ? "Llegaste." : $"Llegaste a {_destName}.", 0));
                _isGuiding = true; _ttsBusy = false;
                yield break;
            }

            var subdivided = SubdivideWaypointSegments(path.Waypoints);
            _events.Clear(); _nextIdx = 0;
            _lastStraightTime = Time.time; _lastStraightIdx = -1; _lastProgressTime = Time.time;

            BuildInstructions(subdivided, startMessage: true);

            if (_events.Count > 0)
            {
                var startEvt = _events[0];
                FireEvent(startEvt);          // "Listo, vamos a X." → p=1, no setea _ttsBusy
                startEvt.HasFired = true;
                _nextIdx = 1;

                // 0.2s de gracia antes de la orientación
                yield return new WaitForSeconds(0.2f);

                ResetTypeCooldown(VoiceInstructionType.GoStraight);
                // ✅ v6.3: orientación con p=2 — NO interrumpe el TTS de inicio,
                // se encola detrás de él.
                AnnounceInitialOrientation(subdivided);
            }

            yield return new WaitForSeconds(_startDelay);

            float safetyTimeout = _startDelay + 3f, waited = 0f, checkInterval = 0.15f;
            while (waited < safetyTimeout)
            {
                bool blocking = false;
                Vector3 pos = EvalPos, fwd = UserFwd;
                for (int i = _nextIdx; i < _events.Count; i++)
                {
                    var ev = _events[i];
                    if (ev.HasFired || ev.Type == VoiceInstructionType.StartNavigation) continue;
                    if (Vector3.Distance(pos, ev.WorldPosition) > ev.TriggerDistance) break;
                    Vector3 toEvt = ev.WorldPosition - pos; toEvt.y = 0f;
                    float dot = toEvt.sqrMagnitude > 0.001f ? Vector3.Dot(fwd, toEvt.normalized) : 0f;
                    if (dot > 0.1f && UserSpeed > 0.1f) { blocking = true; break; }
                }
                if (!blocking) break;
                yield return new WaitForSeconds(checkInterval);
                waited += checkInterval;
            }

            _isGuiding = true; _ttsBusy = false;
            if (_logPreprocessing)
                Debug.Log($"[VoiceGuide] ✅ v6.3 activo. {_events.Count} instrucciones, nextIdx={_nextIdx}.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SUBDIVISIÓN
        // ─────────────────────────────────────────────────────────────────────

        private List<Vector3> SubdivideWaypointSegments(IReadOnlyList<Vector3> waypoints)
        {
            var result = new List<Vector3>(waypoints.Count * 2);
            if (waypoints.Count == 0) return result;
            result.Add(waypoints[0]);
            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                Vector3 a = waypoints[i], b = waypoints[i + 1];
                if (Mathf.Abs(b.y - a.y) >= _stairHeightThreshold) { result.Add(b); continue; }
                float segLen = Vector3.Distance(a, b);
                if (_maxSegmentLength <= 0f || segLen <= _maxSegmentLength) { result.Add(b); continue; }
                int sub = Mathf.CeilToInt(segLen / _maxSegmentLength);
                for (int s = 1; s <= sub; s++) result.Add(Vector3.Lerp(a, b, (float)s / sub));
            }
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CONSTRUCCIÓN DE INSTRUCCIONES
        // ─────────────────────────────────────────────────────────────────────

        private void BuildInstructions(IReadOnlyList<Vector3> wp, bool startMessage)
        {
            int count = wp.Count;
            if (count < 2) return;

            if (startMessage)
            {
                _events.Add(new NavigationInstructionEvent(
                    wp[0], VoiceInstructionType.StartNavigation, 0.5f,
                    $"Listo, vamos a {_destName}.", 0,
                    floor: ResolveFloorForY(wp[0].y)));  // ✅ FIX P1
            }

            for (int i = 1; i < count - 1; i++)
            {
                Vector3 prev    = wp[i - 1];
                Vector3 current = wp[i];
                Vector3 next    = wp[i + 1];
                float   deltaY  = next.y - current.y;

                // ✅ FIX P1: resolver nivel una sola vez por waypoint
                int waypointFloor = ResolveFloorForY(current.y);

                if (Mathf.Abs(deltaY) >= _stairHeightThreshold)
                {
                    float pathDist = AccumDistAlongPath(wp, 0, i);
                    int   wSteps   = Mathf.Max(1, Mathf.RoundToInt(pathDist / _stepLength));
                    string warnText = wSteps > 5
                        ? $"Escaleras en {wSteps} pasos."
                        : "Escaleras cerca.";

                    _events.Add(new NavigationInstructionEvent(
                        current, VoiceInstructionType.StairsWarning,
                        _stairTriggerDist, warnText, i,
                        floor: waypointFloor));  // ✅ FIX P1

                    // ── FIX P2: StairsSafetyWarning (ver Problema 2) ─────────────
                    const float safetyTriggerDist = 2.0f;
                    bool   up          = deltaY > 0f;
                    string safetyText  = up ? "Reduce el paso. Escaleras inmediatas, sube con cuidado."
                                            : "Reduce el paso. Escaleras inmediatas, baja con cuidado.";
                    _events.Add(new NavigationInstructionEvent(
                        current, VoiceInstructionType.StairsSafetyWarning,
                        safetyTriggerDist, safetyText, i,
                        floor: waypointFloor));  // ✅ FIX P1 + P2

                    string actionText = up ? "Sube." : "Baja.";
                    _events.Add(new NavigationInstructionEvent(
                        current,
                        up ? VoiceInstructionType.StairsClimb : VoiceInstructionType.StairsDescent,
                        1.0f, actionText, i,
                        floor: waypointFloor));  // ✅ FIX P1

                    // El waypoint de StairsComplete es el de destino (next), ya en el nuevo piso
                    int nextFloor = ResolveFloorForY(next.y);
                    _events.Add(new NavigationInstructionEvent(
                        next, VoiceInstructionType.StairsComplete,
                        0.8f, "Escaleras terminadas.", i,
                        floor: nextFloor));  // ✅ FIX P1 — piso destino

                    continue;
                }

                Vector3 dirIn  = current - prev;  dirIn.y  = 0f;
                Vector3 dirOut = next - current;  dirOut.y = 0f;
                if (dirIn.sqrMagnitude < 0.001f || dirOut.sqrMagnitude < 0.001f) continue;
                dirIn.Normalize(); dirOut.Normalize();

                var (ttype, angle) = ClassifyTurnRelativeToUser(dirIn, dirOut, false);
                if (ttype == VoiceInstructionType.GoStraight) continue;

                int stepsToTurn = Mathf.Max(1,
                    Mathf.RoundToInt(AccumDistAlongPath(wp, 0, i) / _stepLength));
                string turnText = BuildTurnTextWithContext(
                    ttype, angle, stepsToTurn, wp, i, dirIn, dirOut);

                _events.Add(new NavigationInstructionEvent(
                    current, ttype, TriggerDist(ttype), turnText, i,
                    floor: waypointFloor));  // ✅ FIX P1

                if (_logInstructions)
                    Debug.Log($"[VoiceGuide] 📍 wp[{i}] floor={waypointFloor}: " +
                            $"{ttype} {angle:F1}° pasos={stepsToTurn}");
            }

            // Arrived siempre en el piso del destino
            int destFloor = ResolveFloorForY(wp[count - 1].y);
            _events.Add(new NavigationInstructionEvent(
                wp[count - 1], VoiceInstructionType.Arrived,
                _arrivalTriggerDist,
                string.IsNullOrEmpty(_destName) ? "Llegaste." : $"Llegaste a {_destName}.",
                count - 1,
                floor: destFloor));  // ✅ FIX P1
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CLASIFICACIÓN Y TEXTO DE GIRO
        // ─────────────────────────────────────────────────────────────────────

        private (VoiceInstructionType type, float signedAngle) ClassifyTurnRelativeToUser(
            Vector3 dirIn, Vector3 dirOut, bool isImmediateTurn)
        {
            Vector3 reference = (IsFullARMode && isImmediateTurn)
                ? (UserFwd.sqrMagnitude > 0.001f ? UserFwd : dirIn)
                : dirIn;

            float signedAngle = SignedAngleXZ(reference, dirOut);
            float absAngle = Mathf.Abs(signedAngle);
            bool isRight = signedAngle >= 0f;

            VoiceInstructionType ttype =
                absAngle < _slightTurnAngle ? VoiceInstructionType.GoStraight :
                absAngle >= _uTurnAngle ? VoiceInstructionType.UTurn :
                absAngle >= _definiteTurnAngle ? (isRight ? VoiceInstructionType.TurnRight : VoiceInstructionType.TurnLeft) :
                                                 (isRight ? VoiceInstructionType.SlightRight : VoiceInstructionType.SlightLeft);
            return (ttype, signedAngle);
        }

        private string BuildTurnTextWithContext(VoiceInstructionType ttype, float signedAngle,
            int stepsToTurn, IReadOnlyList<Vector3> wp, int cornerIdx, Vector3 dirIn, Vector3 dirOut)
        {
            float prevSegLen = 0f; bool prevIsStr = false;
            if (cornerIdx >= 2)
            {
                Vector3 dPIn = (wp[cornerIdx - 1] - wp[cornerIdx - 2]); dPIn.y = 0f;
                Vector3 dPOut = (wp[cornerIdx] - wp[cornerIdx - 1]); dPOut.y = 0f;
                if (dPIn.sqrMagnitude > 0.001f && dPOut.sqrMagnitude > 0.001f)
                    prevIsStr = Vector3.Angle(dPIn.normalized, dPOut.normalized) < _straightSegmentAngle;
                prevSegLen = Vector3.Distance(new Vector3(wp[cornerIdx - 1].x, 0, wp[cornerIdx - 1].z),
                                              new Vector3(wp[cornerIdx].x, 0, wp[cornerIdx].z));
            }
            else if (cornerIdx >= 1)
            {
                prevSegLen = Vector3.Distance(new Vector3(wp[cornerIdx - 1].x, 0, wp[cornerIdx - 1].z),
                                              new Vector3(wp[cornerIdx].x, 0, wp[cornerIdx].z));
                prevIsStr = true;
            }

            float distFromUser = Vector3.Distance(new Vector3(EvalPos.x, 0, EvalPos.z),
                                                   new Vector3(wp[cornerIdx].x, 0, wp[cornerIdx].z));
            int stepsFromUser = Mathf.Max(1, Mathf.RoundToInt(distFromUser / _stepLength));
            string turnLabel = TurnLabel(ttype, signedAngle);

            if (prevIsStr && prevSegLen >= _minMentionableStraightDist && stepsFromUser > 2)
                return $"{stepsFromUser} pasos recto, luego {turnLabel}.";
            if (stepsFromUser <= 3)
                return $"{TurnLabelImperative(ttype, signedAngle)} ahora.";
            return $"En {stepsFromUser} pasos, {turnLabel}.";
        }

        private static string TurnLabel(VoiceInstructionType t, float angle)
        {
            if (t == VoiceInstructionType.UTurn) return "date la vuelta";
            string c = ClockText(ClockPosition(angle));
            return (t == VoiceInstructionType.SlightRight || t == VoiceInstructionType.SlightLeft)
                ? $"gira levemente {c}" : $"gira {c}";
        }

        private static string TurnLabelImperative(VoiceInstructionType t, float angle)
        {
            if (t == VoiceInstructionType.UTurn) return "Date la vuelta";
            string c = ClockText(ClockPosition(angle));
            return (t == VoiceInstructionType.SlightRight || t == VoiceInstructionType.SlightLeft)
                ? $"Gira levemente {c}" : $"Gira {c}";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  RESET
        // ─────────────────────────────────────────────────────────────────────

        private void ResetSession(bool silent = false)
        {
            if (_waitCoroutine != null) { StopCoroutine(_waitCoroutine); _waitCoroutine = null; }
            if (_ttsFallbackCoroutine != null) { StopCoroutine(_ttsFallbackCoroutine); _ttsFallbackCoroutine = null; }

            _isGuiding = false; _isPreprocessing = false; _ttsBusy = false;
            _destPos = new(float.PositiveInfinity, 0, 0);
            _events.Clear(); _nextIdx = 0; _evalAccum = 0f;
            _isStopped = false; _stopAccumTime = 0f;
            _deviationTimer = 0f; _deviationFired = false;
            _obstacleFired = false; _obstacleTimer = 0f; _returningTimer = 0f;
            _lastObstacleWarningTime = -999f;
            _lastSpokenText = string.Empty; _lastSpokenTime = -999f; _lastAnyMessageTime = -999f;
            _misalignTimer = 0f; _lastMisalignTime = -999f;
            _lastSpokenByType.Clear();
            _pendingDestinationId = string.Empty;

            if (!silent && _logPreprocessing) Debug.Log("[VoiceGuide] Sesión detenida.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SPEAK
        // ─────────────────────────────────────────────────────────────────────

        private bool Speak(VoiceInstructionType type, string text, int priority)
        {
            if (string.IsNullOrEmpty(text)) return false;

            if (text == _lastSpokenText && Time.time - _lastSpokenTime < _dedupWindow)
            {
                if (_logInstructions) Debug.Log($"[VoiceGuide] 🔇 DEDUP [{type}]");
                return false;
            }

            if (IsTypeCoolingDown(type))
            {
                if (_logInstructions) Debug.Log($"[VoiceGuide] 🔇 COOLDOWN [{type}]");
                return false;
            }

            // ✅ v6.3: solo descartamos si priority <= 1 Y TTS ocupado.
            // Priority 2 (escaleras, giros) se deja pasar aunque _ttsBusy==true:
            // Flutter decide si encola o interrumpe según su propia cola.
            // Priority 3 siempre pasa (ya manejado en EvaluateInstructions).
            if (priority == 0 && _ttsBusy)
            {
                if (_logInstructions) Debug.Log($"[VoiceGuide] 🚫 DROP p=0 (TTS busy): \"{text}\"");
                return false;
            }

            _lastSpokenText = text;
            _lastSpokenTime = Time.time;
            _lastAnyMessageTime = Time.time;
            _lastSpokenByType[type] = Time.time;

            _lastSpokenTextForRepeat = text;
            _lastSpokenPriorityRepeat = priority;

            EventBus.Instance?.Publish(new TTSRequestEvent
            {
                Text = text,
                Priority = priority,
                // ✅ v6.3: interrupt=true SOLO para priority=3 (ObstacleWarning, UTurn).
                // Escaleras (p=2) usan interrupt=false → se encolan en Flutter.
                Interrupt = priority >= 3,
            });

            EventBus.Instance?.Publish(new GuideAnnouncementEvent
            {
                AnnouncementType = MapToAnnouncementType(type),
                Message = text,
                CurrentFloor = _currentFloor,
            });

            // _ttsBusy solo para p>=2 — evita que mensajes de bajo nivel
            // bloqueen el evaluador indefinidamente.
            if (priority >= 2)
            {
                _ttsBusy = true;
                if (_ttsFallbackCoroutine != null) StopCoroutine(_ttsFallbackCoroutine);
                _ttsFallbackCoroutine = StartCoroutine(TTSFallbackTimeout(_ttsFallbackTimeout));
            }

            if (_logInstructions) Debug.Log($"[VoiceGuide] 🔊 [{type}] p={priority} int={priority >= 3} \"{text}\"");
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS DE COOLDOWN POR TIPO
        // ─────────────────────────────────────────────────────────────────────

        private bool IsTypeCoolingDown(VoiceInstructionType type)
        {
            if (!_lastSpokenByType.TryGetValue(type, out float lastTime)) return false;
            return Time.time - lastTime < _sameTypeCooldown;
        }

        private void ResetTypeCooldown(VoiceInstructionType type)
        {
            _lastSpokenByType.Remove(type);
        }

        private IEnumerator TTSFallbackTimeout(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            _ttsFallbackCoroutine = null;
            if (_ttsBusy)
            {
                Debug.LogWarning($"[VoiceGuide] ⚠️ TTSFallback ({seconds}s) — liberando _ttsBusy.");
                _ttsBusy = false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  MAPEO
        // ─────────────────────────────────────────────────────────────────────

        private static GuideAnnouncementType MapToAnnouncementType(VoiceInstructionType type) => type switch
        {
            VoiceInstructionType.StairsWarning        => GuideAnnouncementType.ApproachingStairs,
            VoiceInstructionType.StairsSafetyWarning  => GuideAnnouncementType.ApproachingStairs, 
            VoiceInstructionType.StairsClimb          => GuideAnnouncementType.StartingClimb,
            VoiceInstructionType.StairsDescent => GuideAnnouncementType.StartingDescent,
            VoiceInstructionType.StairsComplete => GuideAnnouncementType.StairsComplete,
            VoiceInstructionType.ResumeAfterSeparation => GuideAnnouncementType.ResumeAfterSeparation,
            VoiceInstructionType.StartNavigation => GuideAnnouncementType.StartNavigation,
            VoiceInstructionType.Arrived => GuideAnnouncementType.Arrived,
            VoiceInstructionType.TurnLeft => GuideAnnouncementType.TurnLeft,
            VoiceInstructionType.TurnRight => GuideAnnouncementType.TurnRight,
            VoiceInstructionType.SlightLeft => GuideAnnouncementType.SlightLeft,
            VoiceInstructionType.SlightRight => GuideAnnouncementType.SlightRight,
            VoiceInstructionType.UTurn => GuideAnnouncementType.UTurn,
            VoiceInstructionType.GoStraight => GuideAnnouncementType.GoStraight,
            VoiceInstructionType.UserStopped => GuideAnnouncementType.WaitingForUser,
            VoiceInstructionType.UserDeviated => GuideAnnouncementType.UserDeviated,
            VoiceInstructionType.ObstacleWarning => GuideAnnouncementType.ObstacleWarning,
            VoiceInstructionType.ProgressUpdate => GuideAnnouncementType.ProgressUpdate,
            _ => GuideAnnouncementType.ResumeGuide,
        };

        // ─────────────────────────────────────────────────────────────────────
        //  FIRE EVENT
        // ─────────────────────────────────────────────────────────────────────

        private void FireEvent(NavigationInstructionEvent evt)
        {
            bool isDirectional = evt.Type == VoiceInstructionType.TurnLeft ||
                                 evt.Type == VoiceInstructionType.TurnRight ||
                                 evt.Type == VoiceInstructionType.SlightLeft ||
                                 evt.Type == VoiceInstructionType.SlightRight ||
                                 evt.Type == VoiceInstructionType.UTurn;
            string text = isDirectional ? RecalcTurnTextRelativeToUser(evt) : evt.InstructionText;
            int priority = GetPriority(evt.Type);
            Speak(evt.Type, text, priority);
        }

        private string RecalcTurnTextRelativeToUser(NavigationInstructionEvent evt)
        {
            int nextEvtIdx = -1;
            for (int i = 0; i < _events.Count; i++) if (_events[i] == evt) { nextEvtIdx = i + 1; break; }
            if (nextEvtIdx < 0 || nextEvtIdx >= _events.Count) return evt.InstructionText;

            Vector3 dirOut = _events[nextEvtIdx].WorldPosition - evt.WorldPosition; dirOut.y = 0f;
            if (dirOut.sqrMagnitude < 0.001f) return evt.InstructionText;
            dirOut.Normalize();

            Vector3 dirIn = evt.WorldPosition - (nextEvtIdx >= 2
                ? _events[nextEvtIdx - 2].WorldPosition : EvalPos);
            dirIn.y = 0f;
            if (dirIn.sqrMagnitude < 0.001f) dirIn = dirOut;
            dirIn.Normalize();

            var (ttype, signedAngle) = ClassifyTurnRelativeToUser(dirIn, dirOut, true);
            float dist = Vector3.Distance(new Vector3(EvalPos.x, 0, EvalPos.z),
                                           new Vector3(evt.WorldPosition.x, 0, evt.WorldPosition.z));
            int steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));
            if (steps <= 3) return $"{TurnLabelImperative(ttype, signedAngle)} ahora.";
            return $"En {steps} pasos, {TurnLabel(ttype, signedAngle)}.";
        }

        private float TriggerDist(VoiceInstructionType t) => t switch
        {
            VoiceInstructionType.UTurn               => _turnTriggerDist * 1.5f,
            VoiceInstructionType.SlightLeft          => _turnTriggerDist * 0.7f,
            VoiceInstructionType.SlightRight         => _turnTriggerDist * 0.7f,
            VoiceInstructionType.StairsSafetyWarning => 2.0f,   // ✅ FIX P2: siempre 2m
            _ => _turnTriggerDist,
        };

        private static int GetPriority(VoiceInstructionType t) => t switch
        {
            VoiceInstructionType.ObstacleWarning     => 3,
            VoiceInstructionType.UTurn               => 3,
            VoiceInstructionType.TurnLeft            => 2,
            VoiceInstructionType.TurnRight           => 2,
            VoiceInstructionType.SlightLeft          => 2,
            VoiceInstructionType.SlightRight         => 2,
            VoiceInstructionType.UserDeviated        => 2,
            VoiceInstructionType.StairsWarning       => 2,
            VoiceInstructionType.StairsSafetyWarning => 2,
            VoiceInstructionType.StairsClimb         => 2,
            VoiceInstructionType.StairsDescent       => 2,
            VoiceInstructionType.StartNavigation     => 1,
            VoiceInstructionType.Arrived             => 1,
            VoiceInstructionType.StairsComplete      => 1,
            VoiceInstructionType.ResumeAfterSeparation => 1,
            _ => 0,
        };

        // ─────────────────────────────────────────────────────────────────────
        //  ORIENTACIÓN INICIAL
        // ─────────────────────────────────────────────────────────────────────

        private void AnnounceInitialOrientation(IReadOnlyList<Vector3> waypoints)
        {
            if (!IsFullARMode || waypoints == null || waypoints.Count < 2) return;

            float totalDist = AccumDistAlongPath(waypoints, 0, waypoints.Count - 1);
            int totalSteps = Mathf.Max(1, Mathf.RoundToInt(totalDist / _stepLength));

            float straightDist = 0f; int firstTurnWpIdx = -1; float firstTurnAngle = 0f;
            Vector3 routeFirstDir = Vector3.zero;

            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                Vector3 seg = waypoints[i + 1] - waypoints[i]; seg.y = 0f;
                if (seg.sqrMagnitude < 0.001f) continue;
                float segLen = new Vector3(seg.x, 0, seg.z).magnitude;
                if (i == 0) routeFirstDir = seg.normalized;
                if (i == 0) { straightDist += segLen; continue; }

                Vector3 prevSeg = waypoints[i] - waypoints[i - 1]; prevSeg.y = 0f;
                if (prevSeg.sqrMagnitude < 0.001f) { straightDist += segLen; continue; }

                if (Vector3.Angle(prevSeg.normalized, seg.normalized) < _straightSegmentAngle)
                    straightDist += segLen;
                else
                {
                    firstTurnWpIdx = i;
                    firstTurnAngle = SignedAngleXZ(prevSeg.normalized, seg.normalized);
                    break;
                }
            }

            float initialAngle = routeFirstDir.sqrMagnitude > 0.001f ? SignedAngleXZ(UserFwd, routeFirstDir) : 0f;
            int clockHour = ClockPosition(initialAngle);
            string clockStr = ClockText(clockHour);
            int straightSteps = Mathf.Max(1, Mathf.RoundToInt(straightDist / _stepLength));
            string text;

            if (firstTurnWpIdx < 0)
            {
                text = clockHour == 12 ? $"Destino al frente. {totalSteps} pasos."
                     : clockHour == 6 ? $"Destino {clockStr}. Date la vuelta y camina {totalSteps} pasos."
                                       : $"Destino {clockStr}. Gira al frente y camina {totalSteps} pasos.";
            }
            else if (straightDist >= _minMentionableStraightDist)
            {
                var (turnType, _) = ClassifyTurnRelativeToUser(
                    (waypoints[firstTurnWpIdx] - waypoints[firstTurnWpIdx - 1]).normalized,
                    (waypoints[firstTurnWpIdx + 1] - waypoints[firstTurnWpIdx]).normalized, false);
                string giroLabel = TurnLabel(turnType, firstTurnAngle);
                text = clockHour == 12
                    ? $"{straightSteps} pasos recto, luego {giroLabel}."
                    : $"Pasillo {clockStr}. {straightSteps} pasos recto, luego {giroLabel}.";
            }
            else
            {
                text = clockHour == 6 ? $"Destino {clockStr}. Date la vuelta y camina {totalSteps} pasos."
                     : clockHour == 12 ? $"Destino al frente. {totalSteps} pasos."
                                       : $"Destino {clockStr}. Gira al frente y camina {totalSteps} pasos.";
            }

            // ✅ v6.3: priority=2 (era 3). No interrumpe "Listo, vamos a X.",
            // se encola detrás en Flutter.
            Speak(VoiceInstructionType.GoStraight, text, priority: 2);

            if (_logInstructions)
                Debug.Log($"[VoiceGuide] 🧭 Orientación p=2: \"{text}\"");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS
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
            Vector3 upos = UserPos; int closest = 0; float minDist = float.MaxValue;
            for (int i = 0; i < wp.Count - 1; i++)
            {
                float d = PointToSeg3D(upos, wp[i], wp[i + 1]);
                if (d < minDist) { minDist = d; closest = i; }
            }
            Vector3 a = wp[closest], b = wp[closest + 1], ab = b - a;
            float t = ab.sqrMagnitude > 0.001f
                ? Mathf.Clamp01(Vector3.Dot(upos - a, ab) / ab.sqrMagnitude) : 0f;
            Vector3 proj = a + t * ab;
            float rem = Vector3.Distance(upos, proj) + Vector3.Distance(proj, b);
            for (int i = closest + 1; i < wp.Count - 1; i++) rem += Vector3.Distance(wp[i], wp[i + 1]);
            return rem;
        }

        private static float PointToSeg3D(Vector3 pt, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a; float lenSq = ab.sqrMagnitude;
            if (lenSq < 0.0001f) return Vector3.Distance(pt, a);
            return Vector3.Distance(pt, a + Mathf.Clamp01(Vector3.Dot(pt - a, ab) / lenSq) * ab);
        }

        private static float AccumDistAlongPath(IReadOnlyList<Vector3> wp, int from, int to)
        {
            float dist = 0f;
            for (int i = from; i < to && i < wp.Count - 1; i++) dist += Vector3.Distance(wp[i], wp[i + 1]);
            return dist;
        }

        private static float SegDistXZ(Vector3 pt, Vector3 a, Vector3 b)
        {
            var p = new Vector2(pt.x, pt.z);
            var p1 = new Vector2(a.x, a.z);
            var p2 = new Vector2(b.x, b.z);
            var seg = p2 - p1;
            float lenSq = seg.sqrMagnitude;
            if (lenSq < 0.0001f) return Vector2.Distance(p, p1);
            return Vector2.Distance(p, p1 + Mathf.Clamp01(Vector2.Dot(p - p1, seg) / lenSq) * seg);
        }

        private static int ClockPosition(float a)
        {
            a = ((a % 360f) + 360f) % 360f;
            int h = Mathf.RoundToInt(a / 30f) % 12;
            return h == 0 ? 12 : h;
        }

        private static string ClockText(int h) => h == 1 ? "a la 1" : $"a las {h}";

        private static float SignedAngleXZ(Vector3 from, Vector3 to)
        {
            from.y = 0f; to.y = 0f;
            if (from.sqrMagnitude < 0.001f || to.sqrMagnitude < 0.001f) return 0f;
            return Vector3.SignedAngle(from, to, Vector3.up);
        }

        private static string DirectionLabel(float a)
        {
            float abs = Mathf.Abs(a); bool right = a >= 0f;
            if (abs <= 15f) return "recto";
            if (abs <= 50f) return right ? "ligeramente a la derecha" : "ligeramente a la izquierda";
            if (abs <= 130f) return right ? "a la derecha" : "a la izquierda";
            return right ? "casi detrás tuyo, hacia la derecha" : "casi detrás tuyo, hacia la izquierda";
        }

        private static Vector3 FlatFwd(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 0.001f ? v.normalized : Vector3.forward; }

        // ─── Helper: nivel de un waypoint por su Y ────────────────────────────────
        // Añadir como método privado en NavigationVoiceGuide

        /// <summary>
        /// Devuelve el nivel (floor) más cercano al valor Y dado,
        /// usando los StartPoints registrados en NavigationStartPointManager.
        /// Si no hay StartPoints, retorna 0 (fallback seguro).
        /// </summary>
        private int ResolveFloorForY(float worldY)
        {
            var pts = NavigationStartPointManager.GetAllStartPoints();
            if (pts == null || pts.Count == 0) return 0;

            int   bestLevel = 0;
            float bestDist  = float.MaxValue;

            foreach (var pt in pts)
            {
                float dist = Mathf.Abs(worldY - pt.FloorHeight);
                if (dist < bestDist)
                {
                    bestDist  = dist;
                    bestLevel = pt.Level;
                }
            }
            return bestLevel;
        }
        // ─────────────────────────────────────────────────────────────────────
        //  GIZMOS
        // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || !_isGuiding) return;
            Gizmos.color = Color.cyan; Gizmos.DrawWireSphere(UserPos, 0.25f);
            Gizmos.color = Color.blue; Gizmos.DrawLine(UserPos, UserPos + UserFwd * 0.7f);
            foreach (var evt in _events)
            {
                Gizmos.color = evt.HasFired ? new Color(0.3f, 0.3f, 0.3f, 0.4f) : GizmoColor(evt.Type);
                Gizmos.DrawWireSphere(evt.WorldPosition, evt.TriggerDistance);
                Gizmos.DrawSphere(evt.WorldPosition, 0.08f);
            }
        }
        private static Color GizmoColor(VoiceInstructionType t) => t switch
        {
            VoiceInstructionType.TurnLeft => Color.red,
            VoiceInstructionType.TurnRight => Color.blue,
            VoiceInstructionType.SlightLeft => new Color(1f, 0.5f, 0.5f),
            VoiceInstructionType.SlightRight => new Color(0.5f, 0.5f, 1f),
            VoiceInstructionType.UTurn => Color.magenta,
            VoiceInstructionType.StairsWarning => Color.yellow,
            VoiceInstructionType.StairsClimb => new Color(1f, 0.6f, 0f),
            VoiceInstructionType.StairsDescent => new Color(0.8f, 0.4f, 0f),
            VoiceInstructionType.Arrived => Color.green,
            VoiceInstructionType.UserStopped => Color.cyan,
            VoiceInstructionType.UserDeviated => new Color(1f, 0f, 0.5f),
            VoiceInstructionType.ObstacleWarning => new Color(1f, 0.3f, 0f),
            _ => Color.white,
        };
#endif

        // ─────────────────────────────────────────────────────────────────────
        //  CONTEXT MENU
        // ─────────────────────────────────────────────────────────────────────

        [ContextMenu("Estado v6.3")]
        private void DebugStatus() =>
            Debug.Log($"[VoiceGuide] v6.3 | ttsBusy={_ttsBusy} | guiding={_isGuiding} | " +
                      $"pendingDest=\"{_pendingDestinationId}\" | " +
                      $"events={_events.Count} nextIdx={_nextIdx} | " +
                      $"obstacle fired={_obstacleFired} last={Time.time - _lastObstacleWarningTime:F0}s ago");

        [ContextMenu("Detener")]
        private void DebugStop() => ResetSession();

        [ContextMenu("Simular TTS done")]
        private void DebugTTSDone() { ClearTTSBusy(); Debug.Log("[VoiceGuide] TTS done simulado."); }

        [ContextMenu("Simular Repeat")]
        private void DebugRepeat() => RepeatLastInstruction();

        [ContextMenu("Voice Status JSON")]
        private void DebugVoiceStatus() => Debug.Log(GetVoiceStatusJson());
    }
}