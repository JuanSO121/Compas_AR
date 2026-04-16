// File: NavigationVoiceGuide.cs
// ✅ v7.2 — FIX PASOS DINÁMICOS: RecalcTurnTextRelativeToUser() siempre usa
//           distancia real en el momento del disparo, nunca el texto estático.
//
// ══════════════════════════════════════════════════════════════════════════════
// CAMBIOS v7.1 → v7.2
// ══════════════════════════════════════════════════════════════════════════════
//
// PROBLEMA OBSERVADO EN v7.1:
// ────────────────────────────────────────────────────────────────────────────
//   El sistema anunciaba "14 pasos" cuando el usuario llegaba casi al waypoint,
//   en lugar de decir "4 pasos" o "gira ahora". La cuenta de pasos era incorrecta
//   porque se calculaba UNA SOLA VEZ durante BuildInstructions() / Resync() y
//   quedaba congelada en evt.InstructionText.
//
//   CAUSA RAÍZ:
//   BuildTurnTextWithContext() calcula los pasos usando EvalPos en el momento
//   de construcción de la instrucción (puede ser segundos/minutos antes del
//   disparo). El texto con "14 pasos" se guarda en evt.InstructionText y
//   NO se actualiza cuando el usuario avanza.
//
//   RecalcTurnTextRelativeToUser() existe y recalcula en tiempo real, pero
//   tenía dos rutas de escape que devolvían evt.InstructionText (texto estático):
//
//     1. if (nextEvtIdx < 0 || nextEvtIdx >= _events.Count)
//            return evt.InstructionText;   ← ❌ devuelve texto con pasos viejos
//
//     2. if (dirIn.sqrMagnitude < 0.001f) dirIn = UserFwd;
//        → Si dirIn es inválido, el texto de pasos sigue calculándose pero
//          con ángulo incorrecto, y en el fallback anterior no se calculaba
//          el conteo de pasos dinámico.
//
//   RESULTADO: El usuario escucha "En 14 pasos, gira a las 11" cuando ya está
//   a 2 pasos del waypoint, porque el texto se generó cuando estaba a 14 pasos.
//
// SOLUCIÓN v7.2:
// ────────────────────────────────────────────────────────────────────────────
//
//   FIX PRINCIPAL — RecalcTurnTextRelativeToUser() siempre recalcula pasos:
//     • Eliminadas las rutas de escape que devolvían evt.InstructionText.
//     • En TODOS los casos se calcula distFromUser en tiempo real desde EvalPos.
//     • Si nextEvtIdx es inválido (edge case): se usa el ángulo del evt original
//       pero con pasos recalculados dinámicamente.
//     • Si dirIn/dirOut son inválidos: fallback a UserFwd con pasos dinámicos.
//     • Nuevo helper privado RecalcStepsOnly(evt) para el fallback de emergencia.
//
//   FIX SECUNDARIO — BuildTurnTextWithContext() ahora es solo para construcción
//   inicial (preview). El texto real que escucha el usuario siempre viene de
//   RecalcTurnTextRelativeToUser() en FireEvent().
//     • Se añade comentario explícito: "Este texto es solo inicial/preview."
//     • FireEvent() ya llama RecalcTurnTextRelativeToUser() para tipos
//       direccionales — ese comportamiento se conserva íntegramente.
//
//   FIX TERCIARIO — Resync() fuerza ResetTypeCooldown para tipos direccionales
//   después de recalcular ruta, para que el primer giro post-Resync se anuncie
//   con pasos actualizados sin esperar el cooldown.
//
// TODOS LOS CAMBIOS DE v7.1 SE CONSERVAN ÍNTEGRAMENTE.

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
        StairsSafetyWarning,
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
            int floor = 0)
        {
            WorldPosition   = worldPosition;
            Type            = type;
            TriggerDistance = triggerDistance;
            InstructionText = instructionText;
            CornerIndex     = cornerIndex;
            Floor           = floor;
        }
    }

    public sealed class NavigationVoiceGuide : MonoBehaviour
    {
        public static NavigationVoiceGuide Instance { get; private set; }

        [Header("─── Referencias ─────────────────────────────────────────────")]
        [SerializeField] private UserPositionBridge _userBridge;
        [SerializeField] private NavigationPathController _pathController;

        [Header("─── Triggers de distancia ──────────────────────────────────")]
        [SerializeField] private float _turnTriggerDist    = 5.0f;
        [SerializeField] private float _stairTriggerDist   = 6.0f;
        [SerializeField] private float _arrivalTriggerDist = 1.5f;
        [SerializeField] private float _straightReminderDist = 12.0f;

        [Header("─── Rendimiento ─────────────────────────────────────────────")]
        [SerializeField, Range(0.05f, 0.5f)]
        private float _evalInterval = 0.10f;

        [Header("─── Espera de Ruta ──────────────────────────────────────────")]
        [SerializeField] private float _pathWaitTimeout      = 3.0f;
        [SerializeField] private float _pathPollInterval     = 0.1f;
        [SerializeField] private float _destinationChangeThreshold = 0.5f;

        [Header("─── Timing de inicio ───────────────────────────────────────")]
        [SerializeField] private float _startDelay = 2.5f;

        [Header("─── Escaleras ──────────────────────────────────────────────")]
        [SerializeField] private float _stairHeightThreshold = 0.3f;
        [SerializeField] private float _stairYTolerance      = 1.2f;

        [Header("─── Ángulos de Giro ─────────────────────────────────────────")]
        [SerializeField] private float _slightTurnAngle    = 20f;
        [SerializeField] private float _definiteTurnAngle  = 50f;
        [SerializeField] private float _uTurnAngle         = 140f;

        [Header("─── Subdivisión de segmentos ───────────────────────────────")]
        [SerializeField] private float _maxSegmentLength           = 3.0f;
        [SerializeField] private float _straightSegmentAngle       = 15f;
        [SerializeField] private float _minMentionableStraightDist = 1.5f;

        [Header("─── Física Humana ─────────────────────────────────────────")]
        [SerializeField] private float _walkSpeedFlat   = 0.8f;
        [SerializeField] private float _walkSpeedStairs = 0.4f;
        [SerializeField] private float _stepLength      = 0.7f;

        [Header("─── Recordatorios ───────────────────────────────────────────")]
        [SerializeField] private float _straightReminderInterval = 20f;
        [SerializeField] private float _progressInterval         = 45f;

        [Header("─── v7.1 FIX C — Delay mínimo post-Resync ───────────────────")]
        [Tooltip("✅ v7.1 FIX C — Segundos mínimos tras un Resync() antes de\n" +
                 "permitir el primer recordatorio 'Sigue recto'.\n" +
                 "Default: 3s.")]
        [SerializeField] private float _minStraightReminderDelay = 3f;
        private float _straightReminderBlockedUntil = -1f;

        [Header("─── [E1] Parada ─────────────────────────────────────────────")]
        [SerializeField] private float _stopTimeout         = 4.0f;
        [SerializeField] private float _stopMinMovement     = 0.25f;
        [SerializeField] private float _stopReminderInterval = 45.0f;

        [Header("─── [E2] Desviación ────────────────────────────────────────")]
        [SerializeField] private float _deviationDist  = 2.0f;
        [SerializeField] private float _deviationDelay = 2.5f;
        [Tooltip("Velocidad mínima del usuario para detectar desviación (evita falsos positivos parado).")]
        [SerializeField] private float _deviationMinSpeed = 0.15f;

        [Header("─── [E3] Obstáculo ─────────────────────────────────────────")]
        [SerializeField] private float _obstacleCheckTime      = 6.0f;
        [SerializeField] private float _obstacleWarningCooldown = 60f;

        [Header("─── [E6] Separación ────────────────────────────────────────")]
        [SerializeField] private float _longSeparationTime = 12.0f;

        [Header("─── [E7] Desorientación ────────────────────────────────────")]
        [SerializeField] private float _misalignAngleThreshold   = 45f;
        [SerializeField] private float _misalignConfirmTime      = 3.0f;
        [SerializeField] private float _misalignReminderInterval = 12f;
        [SerializeField] private float _misalignMinSpeed         = 0.2f;

        [Header("─── v7.1 FIX D — Orientación cuando usuario está parado ────")]
        [Tooltip("✅ v7.1 FIX D — Umbral de velocidad para misalignment cuando\n" +
                 "_isStopped=true. Default: 0.0f (sin requisito de velocidad).")]
        [SerializeField] private float _misalignStopSpeedThreshold = 0.0f;

        [Header("─── Anti-saturación ──────────────────────────────────────")]
        [SerializeField] private float _dedupWindow        = 6f;
        [SerializeField] private float _minMessageInterval = 3.5f;
        [SerializeField] private float _ttsFallbackTimeout = 8f;
        [SerializeField] private float _sameTypeCooldown   = 8.0f;

        [Header("─── v6.4: Control de secuencia ────────────────────────────")]
        [SerializeField] private float _exitRadiusFactor = 0.6f;
        [SerializeField] private float _minExitDistance = 1.5f;

        [Header("─── v7.0: Tracking de posición en ruta ─────────────────────")]
        [Tooltip("✅ v7.0 — Distancia XZ máxima al segmento de ruta para considerar\n" +
                 "que el usuario 'pasó' por ese waypoint sin disparar el trigger.\n" +
                 "Default: 3.0m")]
        [SerializeField] private float _passedWaypointXZThreshold = 3.0f;

        [Tooltip("✅ v7.0 — Cooldown reducido para recálculo por desviación.\n" +
                 "v6.5 usaba 8s, v7.0 usa 5s para mayor agilidad.")]
        [SerializeField] private float _deviationRerouteCooldown = 5.0f;

        [Header("─── Debug ────────────────────────────────────────────────────")]
        [SerializeField] private bool _logInstructions  = true;
        [SerializeField] private bool _logPreprocessing = true;

        // ── Estado ────────────────────────────────────────────────────────────
        private readonly List<NavigationInstructionEvent> _events = new(24);
        private int   _nextIdx        = 0;
        private bool  _isGuiding      = false;
        private bool  _isPreprocessing = false;
        private string _destName      = string.Empty;
        private Vector3 _destPos      = new(float.PositiveInfinity, 0, 0);

        private string _pendingDestinationId = string.Empty;

        private float _lastStraightTime   = -999f;
        private int   _lastStraightIdx    = -1;
        private float _lastProgressTime   = -999f;
        private float _lastAnyMessageTime = -999f;

        private Vector3 _stopRefPos        = Vector3.zero;
        private float   _stopAccumTime     = 0f;
        private bool    _isStopped         = false;
        private float   _lastStopReminder  = -999f;

        private float _deviationTimer = 0f;
        private bool  _deviationFired = false;
        private float _lastDeviationRerouteTime = -999f;

        private float _obstacleTimer    = 0f;
        private float _lastDistToNext   = float.MaxValue;
        private bool  _obstacleFired    = false;
        private float _lastObstacleWarningTime = -999f;

        private float _returningTimer = 0f;
        private int   _currentFloor  = 0;

        private Coroutine _waitCoroutine        = null;
        private Coroutine _ttsFallbackCoroutine = null;

        private float  _evalAccum       = 0f;
        private string _lastSpokenText  = string.Empty;
        private float  _lastSpokenTime  = -999f;
        private float  _misalignTimer   = 0f;
        private float  _lastMisalignTime = -999f;
        private bool   _ttsBusy        = false;

        private readonly Dictionary<VoiceInstructionType, float> _lastSpokenByType
            = new Dictionary<VoiceInstructionType, float>();

        // v6.4
        private string _lastSpokenTextForRepeat    = string.Empty;
        private int    _lastSpokenPriorityRepeat   = 0;

        // v6.4: secuenciador
        private bool    _waitingForExit     = false;
        private Vector3 _lastFiredEventPos  = Vector3.zero;
        private float   _lastFiredTriggerDist = 0f;

        // ─────────────────────────────────────────────────────────────────────
        //  POSICIONES
        // ─────────────────────────────────────────────────────────────────────

        private bool    IsFullARMode => _userBridge != null && !_userBridge.IsNoArMode;
        private Vector3 UserPos      => _userBridge != null ? _userBridge.UserPosition
                                         : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);
        private Vector3 UserFwd      => _userBridge != null ? _userBridge.UserForward
                                         : FlatFwd(Camera.main != null ? Camera.main.transform.forward : Vector3.forward);
        private float   UserSpeed    => _userBridge?.UserSpeed ?? 0f;
        private Vector3 EvalPos      => IsFullARMode ? UserPos : (_userBridge?.AgentPosition ?? UserPos);

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
            Debug.Log($"[VoiceGuide] ✅ v7.2");
        }

        private void OnEnable()  => SubscribeEvents();
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

            UpdateNextIdxByProximity();

            EvaluateInstructions();
            EvaluateUserStop(dt);
            EvaluateDeviation(dt);
            EvaluateObstacle(dt);
            EvaluateProgress();
            EvaluateMisalignment(dt);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ✅ v7.0 FIX 2 — Actualizar índice de ruta por proximidad al segmento
        // ─────────────────────────────────────────────────────────────────────

        private void UpdateNextIdxByProximity()
        {
            if (_events.Count < 2 || _nextIdx >= _events.Count) return;

            Vector3 userPosXZ = new Vector3(UserPos.x, 0f, UserPos.z);

            for (int i = _nextIdx; i < _events.Count - 1; i++)
            {
                var evt = _events[i];
                if (evt.HasFired) { _nextIdx = i + 1; continue; }

                if (GetPriority(evt.Type) >= 3) break;

                if (evt.Type == VoiceInstructionType.Arrived   ||
                    evt.Type == VoiceInstructionType.StairsClimb ||
                    evt.Type == VoiceInstructionType.StairsDescent) break;

                Vector3 evtPosXZ = new Vector3(evt.WorldPosition.x, 0f, evt.WorldPosition.z);
                float distToEvt = Vector3.Distance(userPosXZ, evtPosXZ);

                if (distToEvt > _passedWaypointXZThreshold && i + 1 < _events.Count)
                {
                    var nextEvt = _events[i + 1];
                    Vector3 nextPosXZ = new Vector3(nextEvt.WorldPosition.x, 0f, nextEvt.WorldPosition.z);
                    float distToNext = Vector3.Distance(userPosXZ, nextPosXZ);

                    if (distToNext < distToEvt * 0.7f)
                    {
                        if (_logInstructions)
                            Debug.Log($"[VoiceGuide] ⏭ v7.0 Proximity skip: evt[{i}] {evt.Type} " +
                                      $"(distToEvt={distToEvt:F1}m > distToNext={distToNext:F1}m)");
                        evt.HasFired = true;
                        _nextIdx = i + 1;
                    }
                    else break;
                }
                else break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVALUACIÓN — SECUENCIADOR v7.2
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateInstructions()
        {
            if (_nextIdx >= _events.Count) return;

            Vector3 evalPos = EvalPos;
            Vector3 userPos = UserPos;

            if (_waitingForExit)
            {
                float exitRadius = Mathf.Max(
                    _lastFiredTriggerDist * _exitRadiusFactor,
                    _minExitDistance);

                float distToLastFired = Vector3.Distance(userPos, _lastFiredEventPos);

                if (distToLastFired < exitRadius)
                {
                    bool emergencyAhead = false;
                    for (int i = _nextIdx; i < _events.Count; i++)
                    {
                        var ev = _events[i];
                        if (ev.HasFired) continue;
                        if (GetPriority(ev.Type) < 3) break;
                        if (Vector3.Distance(evalPos, ev.WorldPosition) <= ev.TriggerDistance)
                        { emergencyAhead = true; break; }
                    }

                    if (!emergencyAhead)
                    {
                        EvaluateStraightReminder();
                        return;
                    }
                }
                else
                {
                    _waitingForExit = false;
                }
            }

            for (int i = _nextIdx; i < _events.Count; i++)
            {
                var evt = _events[i];
                if (evt.HasFired) { _nextIdx = i + 1; continue; }

                bool isStairTransitionEvent =
                    evt.Type == VoiceInstructionType.StairsWarning        ||
                    evt.Type == VoiceInstructionType.StairsSafetyWarning  ||
                    evt.Type == VoiceInstructionType.StairsClimb          ||
                    evt.Type == VoiceInstructionType.StairsDescent        ||
                    evt.Type == VoiceInstructionType.StairsComplete;

                if (!isStairTransitionEvent && evt.Floor != _currentFloor)
                {
                    float yDist = Mathf.Abs(UserPos.y - evt.WorldPosition.y);
                    if (yDist > 1.5f)
                    {
                        evt.HasFired = true;
                        _nextIdx = i + 1;
                        if (_logInstructions)
                            Debug.Log($"[VoiceGuide] ⏭ Evento [{evt.Type}] floor={evt.Floor} " +
                                    $"saltado (usuario en floor={_currentFloor}).");
                    }
                    else break;
                    continue;
                }

                if (_ttsBusy && GetPriority(evt.Type) < 3) break;

                Vector3 checkPos = (evt.Type == VoiceInstructionType.Arrived) ? userPos : evalPos;
                if (!ShouldFireEvent(evt, checkPos)) break;

                FireEvent(evt);
                evt.HasFired = true;
                _nextIdx = i + 1;

                if (GetPriority(evt.Type) < 3)
                {
                    _waitingForExit    = true;
                    _lastFiredEventPos = evt.WorldPosition;

                    _lastFiredTriggerDist = evt.Type == VoiceInstructionType.StartNavigation
                        ? _minExitDistance / _exitRadiusFactor
                        : evt.TriggerDistance;
                }

                return;
            }

            EvaluateStraightReminder();
        }

        private bool ShouldFireEvent(NavigationInstructionEvent evt, Vector3 checkPos)
        {
            if (Vector3.Distance(checkPos, evt.WorldPosition) > evt.TriggerDistance) return false;

            if (evt.Type == VoiceInstructionType.StairsComplete || evt.Type == VoiceInstructionType.Arrived)
            {
                if (Mathf.Abs(UserPos.y - evt.WorldPosition.y) > _stairYTolerance) return false;

                if (evt.Type == VoiceInstructionType.Arrived && evt.Floor != _currentFloor)
                    return false;
            }

            return true;
        }

        public void ClearTTSBusy()
        {
            if (!_ttsBusy) return;
            _ttsBusy = false;
            if (_ttsFallbackCoroutine != null) { StopCoroutine(_ttsFallbackCoroutine); _ttsFallbackCoroutine = null; }
            if (_logInstructions) Debug.Log("[VoiceGuide] ✅ _ttsBusy liberado por Flutter.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ✅ v7.1 FIX A+B+C — Recordatorio "Sigue recto" con dirección
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateStraightReminder()
        {
            if (_nextIdx >= _events.Count || _nextIdx == _lastStraightIdx) return;
            var next = _events[_nextIdx];
            if (next.HasFired) return;

            float dist = Vector3.Distance(EvalPos, next.WorldPosition);
            if (dist < _straightReminderDist) return;
            if (Time.time - _lastStraightTime < _straightReminderInterval) return;

            // ✅ FIX C: Bloqueo mínimo post-Resync
            if (Time.time < _straightReminderBlockedUntil) return;

            if (_ttsBusy || Time.time - _lastAnyMessageTime < _minMessageInterval) return;

            int steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));

            // ✅ FIX A — Calcular dirección hacia el próximo waypoint
            string reminderText = BuildStraightReminderText(next.WorldPosition, steps);

            // ✅ FIX B — ResetTypeCooldown() DESPUÉS de intentar Speak(), solo si exitoso
            bool sent = Speak(VoiceInstructionType.GoStraight, reminderText, 0);

            if (sent)
            {
                ResetTypeCooldown(VoiceInstructionType.GoStraight);
                _lastStraightTime = Time.time;
                _lastStraightIdx  = _nextIdx;

                if (_logInstructions)
                    Debug.Log($"[VoiceGuide] 🔄 v7.1 StraightReminder: \"{reminderText}\"");
            }
        }

        private string BuildStraightReminderText(Vector3 targetWorldPos, int steps)
        {
            Vector3 toNext = targetWorldPos - UserPos;
            toNext.y = 0f;

            if (toNext.sqrMagnitude < 0.01f)
                return $"Sigue recto. {steps} pasos.";

            toNext.Normalize();

            float signedAngle = SignedAngleXZ(UserFwd, toNext);
            float absAngle    = Mathf.Abs(signedAngle);

            if (absAngle <= _slightTurnAngle)
                return $"Sigue recto. {steps} pasos.";

            int    clockH   = ClockPosition(signedAngle);
            string clockStr = ClockText(clockH);

            if (absAngle >= _uTurnAngle)
                return $"Date la vuelta y continúa. {steps} pasos.";

            return $"Gira {clockStr} y continúa. {steps} pasos.";
        }

        private string BuildStopReminderText(float distToNext, int steps, bool isRepeat = false)
        {
            string prefix = isRepeat ? "Tómate tu tiempo." : "Cuando estés listo,";

            if (_nextIdx >= _events.Count)
                return $"{prefix} continúa. {steps} pasos.";

            Vector3 nextPos = _events[_nextIdx].WorldPosition;
            Vector3 toNext  = nextPos - UserPos;
            toNext.y = 0f;

            if (toNext.sqrMagnitude < 0.01f)
                return $"{prefix} continúa. {steps} pasos.";

            toNext.Normalize();

            float signedAngle = SignedAngleXZ(UserFwd, toNext);
            float absAngle    = Mathf.Abs(signedAngle);

            if (absAngle <= _slightTurnAngle)
                return $"{prefix} continúa recto. {steps} pasos.";

            if (absAngle >= _uTurnAngle)
                return $"{prefix} date la vuelta y continúa. {steps} pasos.";

            string clockStr = ClockText(ClockPosition(signedAngle));
            return $"{prefix} gira {clockStr} y continúa. {steps} pasos.";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVALUACIONES SECUNDARIAS
        // ─────────────────────────────────────────────────────────────────────

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
                string stopText = BuildStopReminderText(_lastDistToNext, steps);
                Speak(VoiceInstructionType.UserStopped, stopText, 0);
                return;
            }

            if (_isStopped && Time.time - _lastStopReminder >= _stopReminderInterval)
            {
                if (_ttsBusy || Time.time - _lastAnyMessageTime < _minMessageInterval)
                { _lastStopReminder = Time.time; return; }
                _lastStopReminder = Time.time;
                float remDist = RemainingDistFromUser();
                int steps = Mathf.Max(1, Mathf.RoundToInt(remDist / _stepLength));
                string reminderText = BuildStopReminderText(remDist, steps, isRepeat: true);
                Speak(VoiceInstructionType.UserStopped, reminderText, 0);
            }
        }

        private void EvaluateDeviation(float dt)
        {
            if (_isStopped || UserSpeed < _deviationMinSpeed) return;

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

                    if (Time.time - _lastDeviationRerouteTime >= _deviationRerouteCooldown)
                    {
                        _lastDeviationRerouteTime = Time.time;
                        EventBus.Instance?.Publish(new RouteDeviatedEvent
                        {
                            UserPosition      = UserPos,
                            DeviationDistance = lateral,
                            Destination       = _destPos,
                        });
                        if (_logInstructions)
                            Debug.Log($"[VoiceGuide] 🔄 RouteDeviatedEvent publicado: " +
                                      $"lateral={lateral:F2}m dest={_destPos:F2}");
                    }
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

            float current   = DistUserToNextWp();
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
            if (!IsFullARMode || _nextIdx >= _events.Count || _ttsBusy) return;

            float speedThreshold = _isStopped ? _misalignStopSpeedThreshold : _misalignMinSpeed;
            if (UserSpeed < speedThreshold) return;

            Vector3 toNext = _events[_nextIdx].WorldPosition - UserPos; toNext.y = 0f;
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
                    _lastMisalignTime = Time.time; _misalignTimer = 0f;
                    float  dist = Vector3.Distance(UserPos, _events[_nextIdx].WorldPosition);
                    int    steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));

                    int    clockH = ClockPosition(signedAngle);
                    string clockS = ClockText(clockH);
                    string text = absAngle <= 50f  ? $"El camino está {clockS}. {steps} pasos."
                                 : absAngle <= 130f ? $"Gira {clockS}. {steps} pasos."
                                                    : $"Date la vuelta {clockS}. {steps} pasos.";
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
            if (arrivedFired) { ResetSession(); return; }

            Speak(VoiceInstructionType.Arrived,
                string.IsNullOrEmpty(_destName) ? "Llegaste." : $"Llegaste a {_destName}.", 1);
            ResetSession();
        }

        private void OnNavCancelled(NavigationCancelledEvent _) => ResetSession();

        private void OnFloorTransition(FloorTransitionEvent e)
        {
            _currentFloor  = e.ToLevel;
            _obstacleFired = false;
            _isStopped     = false;
            _stopAccumTime = 0f;

            ResetTypeCooldown(VoiceInstructionType.StairsWarning);
            ResetTypeCooldown(VoiceInstructionType.StairsSafetyWarning);
            ResetTypeCooldown(VoiceInstructionType.StairsClimb);
            ResetTypeCooldown(VoiceInstructionType.StairsDescent);
            ResetTypeCooldown(VoiceInstructionType.StairsComplete);

            _misalignTimer    = 0f;
            _lastMisalignTime = -999f;

            _waitingForExit = false;

            var currentPath = _pathController?.CurrentPath;
            if (currentPath != null && currentPath.IsValid && currentPath.Waypoints.Count >= 2)
            {
                Debug.Log($"[VoiceGuide] 🔄 FloorTransition → Nivel {e.ToLevel}: Resync.");
                Resync(currentPath.Waypoints, fullSummary: false);
            }
        }

        private void OnObstacleDetected(ObstacleDetectedEvent evt)
        {
            if (!_isGuiding) return;
            if (Time.time - _lastObstacleWarningTime < _obstacleWarningCooldown) return;

            _lastObstacleWarningTime = Time.time;
            _obstacleFired           = true;
            _obstacleTimer           = 0f;

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

            _obstacleFired  = false; _deviationFired = false; _isStopped = false;
            _stopAccumTime  = 0f;   _deviationTimer = 0f;    _misalignTimer = 0f;
            _lastMisalignTime = -999f;
            _waitingForExit = false;
            Resync(newPath.Waypoints, longSep);
        }

        /// <summary>
        /// ✅ v7.2 — Resync resetea cooldowns de giro para que el primer anuncio
        /// post-recálculo use pasos frescos sin quedar bloqueado por _sameTypeCooldown.
        /// ✅ v7.1 FIX C — Setea _straightReminderBlockedUntil.
        /// </summary>
        private void Resync(IReadOnlyList<Vector3> waypoints, bool fullSummary)
        {
            _events.Clear(); _nextIdx = 0;
            _waitingForExit   = false;
            _lastStraightTime = Time.time; _lastStraightIdx = -1;

            // ✅ v7.1 FIX C: Bloquear reminder por _minStraightReminderDelay segundos
            _straightReminderBlockedUntil = Time.time + _minStraightReminderDelay;

            _lastProgressTime = Time.time;
            var subdivided = SubdivideWaypointSegments(waypoints);
            BuildInstructions(subdivided, startMessage: false);
            float rem   = RemainingDistFromUser(subdivided);
            int   steps = Mathf.Max(1, Mathf.RoundToInt(rem / _stepLength));

            // ✅ v7.2: Reset cooldowns de tipos direccionales para que el primer giro
            // post-Resync siempre recalcule pasos sin bloqueo de cooldown.
            ResetTypeCooldown(VoiceInstructionType.TurnLeft);
            ResetTypeCooldown(VoiceInstructionType.TurnRight);
            ResetTypeCooldown(VoiceInstructionType.SlightLeft);
            ResetTypeCooldown(VoiceInstructionType.SlightRight);
            ResetTypeCooldown(VoiceInstructionType.UTurn);

            bool stairsRecentlyAnnounced = IsTypeCoolingDown(VoiceInstructionType.StairsWarning)
                                        || IsTypeCoolingDown(VoiceInstructionType.StairsSafetyWarning);

            if (stairsRecentlyAnnounced)
            {
                foreach (var evt in _events)
                {
                    if (evt.Type == VoiceInstructionType.StairsWarning ||
                        evt.Type == VoiceInstructionType.StairsSafetyWarning)
                    {
                        if (Vector3.Distance(EvalPos, evt.WorldPosition) <= evt.TriggerDistance)
                            evt.HasFired = true;
                    }
                }
            }

            if (fullSummary)
                Speak(VoiceInstructionType.ResumeAfterSeparation,
                    IsFullARMode ? $"Ruta recalculada. {steps} pasos a {_destName}."
                                : $"El guía te encontró. {steps} pasos.", 1);
            else
                Speak(VoiceInstructionType.GoStraight, $"Ruta actualizada. {steps} pasos.", 0);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  API PÚBLICA
        // ─────────────────────────────────────────────────────────────────────

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

        public void StopVoiceGuide()       => ResetSession();
        public IReadOnlyList<NavigationInstructionEvent> InstructionEvents => _events;
        public bool IsGuiding              => _isGuiding;

        public void SetPathController(NavigationPathController controller)
        {
            if (_pathController != null) _pathController.OnPathRecalculated -= OnPathRecalculated;
            _pathController = controller;
            if (_pathController != null) _pathController.OnPathRecalculated += OnPathRecalculated;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  REPEAT LAST INSTRUCTION
        // ─────────────────────────────────────────────────────────────────────

        public void RepeatLastInstruction()
        {
            if (string.IsNullOrEmpty(_lastSpokenTextForRepeat))
            {
                Debug.Log("[VoiceGuide] RepeatLastInstruction: sin instrucción previa para repetir.");
                return;
            }

            EventBus.Instance?.Publish(new TTSRequestEvent
            {
                Text      = _lastSpokenTextForRepeat,
                Priority  = Mathf.Max(1, _lastSpokenPriorityRepeat),
                Interrupt = false,
            });

            _lastAnyMessageTime = Time.time;
            Debug.Log($"[VoiceGuide] 🔁 RepeatLastInstruction: \"{_lastSpokenTextForRepeat}\"");
        }

        public void StopVoiceGuideFromBridge()
        {
            Debug.Log("[VoiceGuide] 🛑 StopVoiceGuideFromBridge().");
            ResetSession();
        }

        public string GetVoiceStatusJson()
        {
            float rem   = _isGuiding ? RemainingDistFromUser() : 0f;
            int   steps = Mathf.Max(0, Mathf.RoundToInt(rem / _stepLength));
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
            float elapsed = 0f;
            OptimizedPath path = null;
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

            _waitCoroutine   = null;
            _isPreprocessing = false;

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
            _waitingForExit   = false;
            _lastStraightTime = Time.time; _lastStraightIdx = -1; _lastProgressTime = Time.time;

            // ✅ v7.1 FIX C: Bloquear primer straight reminder durante el startup
            _straightReminderBlockedUntil = Time.time + _startDelay + _minStraightReminderDelay;

            BuildInstructions(subdivided, startMessage: true);

            if (_events.Count > 0)
            {
                var startEvt = _events[0];
                FireEvent(startEvt);
                startEvt.HasFired = true;
                _nextIdx = 1;

                yield return new WaitForSeconds(0.2f);

                ResetTypeCooldown(VoiceInstructionType.GoStraight);
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
                Debug.Log($"[VoiceGuide] ✅ v7.2 activo. {_events.Count} instrucciones, nextIdx={_nextIdx}.");
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
                    floor: ResolveFloorForY(wp[0].y)));
            }

            for (int i = 1; i < count - 1; i++)
            {
                Vector3 prev    = wp[i - 1];
                Vector3 current = wp[i];
                Vector3 next    = wp[i + 1];
                float   deltaY  = next.y - current.y;

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
                        _stairTriggerDist, warnText, i, floor: waypointFloor));

                    bool   up         = deltaY > 0f;
                    string safetyText = up ? "Reduce el paso. Escaleras inmediatas, sube con cuidado."
                                           : "Reduce el paso. Escaleras inmediatas, baja con cuidado.";
                    _events.Add(new NavigationInstructionEvent(
                        current, VoiceInstructionType.StairsSafetyWarning,
                        2.0f, safetyText, i, floor: waypointFloor));

                    string actionText = up ? "Sube." : "Baja.";
                    _events.Add(new NavigationInstructionEvent(
                        current,
                        up ? VoiceInstructionType.StairsClimb : VoiceInstructionType.StairsDescent,
                        1.0f, actionText, i, floor: waypointFloor));

                    int nextFloor = ResolveFloorForY(next.y);
                    _events.Add(new NavigationInstructionEvent(
                        next, VoiceInstructionType.StairsComplete,
                        0.8f, "Escaleras terminadas.", i, floor: nextFloor));
                    continue;
                }

                Vector3 dirIn  = current - prev;  dirIn.y  = 0f;
                Vector3 dirOut = next - current;   dirOut.y = 0f;
                if (dirIn.sqrMagnitude < 0.001f || dirOut.sqrMagnitude < 0.001f) continue;
                dirIn.Normalize(); dirOut.Normalize();

                var (ttype, angle) = ClassifyTurnRelativeToUser(dirIn, dirOut, false);
                if (ttype == VoiceInstructionType.GoStraight) continue;

                int stepsToTurn = Mathf.Max(1,
                    Mathf.RoundToInt(AccumDistAlongPath(wp, 0, i) / _stepLength));

                // NOTA v7.2: Este texto es solo inicial/preview.
                // El texto real que escucha el usuario se recalcula en FireEvent()
                // → RecalcTurnTextRelativeToUser(), que usa EvalPos en tiempo real.
                string turnText = BuildTurnTextWithContext(
                    ttype, angle, stepsToTurn, wp, i, dirIn, dirOut);

                _events.Add(new NavigationInstructionEvent(
                    current, ttype, TriggerDist(ttype), turnText, i, floor: waypointFloor));

                if (_logInstructions)
                    Debug.Log($"[VoiceGuide] 📍 wp[{i}] floor={waypointFloor}: " +
                            $"{ttype} {angle:F1}° pasos={stepsToTurn} → \"{turnText}\"");
            }

            int destFloor = ResolveFloorForY(wp[count - 1].y);
            _events.Add(new NavigationInstructionEvent(
                wp[count - 1], VoiceInstructionType.Arrived,
                _arrivalTriggerDist,
                string.IsNullOrEmpty(_destName) ? "Llegaste." : $"Llegaste a {_destName}.",
                count - 1, floor: destFloor));
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
            float absAngle    = Mathf.Abs(signedAngle);
            bool  isRight     = signedAngle >= 0f;

            VoiceInstructionType ttype =
                absAngle < _slightTurnAngle   ? VoiceInstructionType.GoStraight :
                absAngle >= _uTurnAngle       ? VoiceInstructionType.UTurn :
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
                Vector3 dPIn  = (wp[cornerIdx - 1] - wp[cornerIdx - 2]); dPIn.y  = 0f;
                Vector3 dPOut = (wp[cornerIdx]     - wp[cornerIdx - 1]); dPOut.y = 0f;
                if (dPIn.sqrMagnitude > 0.001f && dPOut.sqrMagnitude > 0.001f)
                    prevIsStr = Vector3.Angle(dPIn.normalized, dPOut.normalized) < _straightSegmentAngle;
                prevSegLen = Vector3.Distance(new Vector3(wp[cornerIdx - 1].x, 0, wp[cornerIdx - 1].z),
                                              new Vector3(wp[cornerIdx].x,     0, wp[cornerIdx].z));
            }
            else if (cornerIdx >= 1)
            {
                prevSegLen = Vector3.Distance(new Vector3(wp[cornerIdx - 1].x, 0, wp[cornerIdx - 1].z),
                                              new Vector3(wp[cornerIdx].x,     0, wp[cornerIdx].z));
                prevIsStr = true;
            }

            // NOTA v7.2: distFromUser aquí es solo para el texto inicial/preview.
            // RecalcTurnTextRelativeToUser() lo recalculará en tiempo real al disparar.
            float distFromUser = Vector3.Distance(new Vector3(EvalPos.x, 0, EvalPos.z),
                                                   new Vector3(wp[cornerIdx].x, 0, wp[cornerIdx].z));
            int    stepsFromUser = Mathf.Max(1, Mathf.RoundToInt(distFromUser / _stepLength));
            string turnLabel     = TurnLabel(ttype, signedAngle);

            if (prevIsStr && prevSegLen >= _minMentionableStraightDist && stepsFromUser > 2)
                return $"{stepsFromUser} pasos recto, luego {turnLabel}.";
            if (stepsFromUser <= 3)
                return $"{TurnLabelImperative(ttype, signedAngle)} ahora.";
            return $"En {stepsFromUser} pasos, {turnLabel}.";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ✅ v7.0 FIX 1 — Etiquetas de dirección con reloj
        // ─────────────────────────────────────────────────────────────────────

        private static string TurnLabel(VoiceInstructionType t, float angle)
        {
            if (t == VoiceInstructionType.UTurn) return "date la vuelta";
            string c = ClockText(ClockPosition(angle));
            return $"gira {c}";
        }

        private static string TurnLabelImperative(VoiceInstructionType t, float angle)
        {
            if (t == VoiceInstructionType.UTurn) return "Date la vuelta";
            string c = ClockText(ClockPosition(angle));
            return $"Gira {c}";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  RESET
        // ─────────────────────────────────────────────────────────────────────

        private void ResetSession(bool silent = false)
        {
            if (_waitCoroutine != null)        { StopCoroutine(_waitCoroutine);        _waitCoroutine = null; }
            if (_ttsFallbackCoroutine != null) { StopCoroutine(_ttsFallbackCoroutine); _ttsFallbackCoroutine = null; }

            _isGuiding = false; _isPreprocessing = false; _ttsBusy = false;
            _destPos   = new(float.PositiveInfinity, 0, 0);
            _events.Clear(); _nextIdx = 0; _evalAccum = 0f;
            _isStopped = false; _stopAccumTime  = 0f;
            _deviationTimer = 0f; _deviationFired = false;
            _obstacleFired  = false; _obstacleTimer  = 0f; _returningTimer = 0f;
            _lastObstacleWarningTime = -999f;
            _lastSpokenText = string.Empty; _lastSpokenTime = -999f; _lastAnyMessageTime = -999f;
            _misalignTimer  = 0f; _lastMisalignTime = -999f;
            _lastSpokenByType.Clear();
            _pendingDestinationId = string.Empty;
            _lastDeviationRerouteTime = -999f;
            _straightReminderBlockedUntil = -1f;

            _waitingForExit = false;

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

            if (priority == 0 && _ttsBusy)
            {
                if (_logInstructions) Debug.Log($"[VoiceGuide] 🚫 DROP p=0 (TTS busy): \"{text}\"");
                return false;
            }

            _lastSpokenText        = text;
            _lastSpokenTime        = Time.time;
            _lastAnyMessageTime    = Time.time;
            _lastSpokenByType[type] = Time.time;

            _lastSpokenTextForRepeat  = text;
            _lastSpokenPriorityRepeat = priority;

            EventBus.Instance?.Publish(new TTSRequestEvent
            {
                Text     = text,
                Priority = priority,
                Interrupt = priority >= 3,
            });

            EventBus.Instance?.Publish(new GuideAnnouncementEvent
            {
                AnnouncementType = MapToAnnouncementType(type),
                Message          = text,
                CurrentFloor     = _currentFloor,
            });

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
        //  COOLDOWN POR TIPO
        // ─────────────────────────────────────────────────────────────────────

        private bool IsTypeCoolingDown(VoiceInstructionType type)
        {
            if (!_lastSpokenByType.TryGetValue(type, out float lastTime)) return false;
            return Time.time - lastTime < _sameTypeCooldown;
        }

        private void ResetTypeCooldown(VoiceInstructionType type) =>
            _lastSpokenByType.Remove(type);

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
            VoiceInstructionType.StairsDescent        => GuideAnnouncementType.StartingDescent,
            VoiceInstructionType.StairsComplete       => GuideAnnouncementType.StairsComplete,
            VoiceInstructionType.ResumeAfterSeparation => GuideAnnouncementType.ResumeAfterSeparation,
            VoiceInstructionType.StartNavigation      => GuideAnnouncementType.StartNavigation,
            VoiceInstructionType.Arrived              => GuideAnnouncementType.Arrived,
            VoiceInstructionType.TurnLeft             => GuideAnnouncementType.TurnLeft,
            VoiceInstructionType.TurnRight            => GuideAnnouncementType.TurnRight,
            VoiceInstructionType.SlightLeft           => GuideAnnouncementType.SlightLeft,
            VoiceInstructionType.SlightRight          => GuideAnnouncementType.SlightRight,
            VoiceInstructionType.UTurn                => GuideAnnouncementType.UTurn,
            VoiceInstructionType.GoStraight           => GuideAnnouncementType.GoStraight,
            VoiceInstructionType.UserStopped          => GuideAnnouncementType.WaitingForUser,
            VoiceInstructionType.UserDeviated         => GuideAnnouncementType.UserDeviated,
            VoiceInstructionType.ObstacleWarning      => GuideAnnouncementType.ObstacleWarning,
            VoiceInstructionType.ProgressUpdate       => GuideAnnouncementType.ProgressUpdate,
            _ => GuideAnnouncementType.ResumeGuide,
        };

        // ─────────────────────────────────────────────────────────────────────
        //  FIRE EVENT
        // ─────────────────────────────────────────────────────────────────────

        private void FireEvent(NavigationInstructionEvent evt)
        {
            bool isDirectional = evt.Type == VoiceInstructionType.TurnLeft  ||
                                 evt.Type == VoiceInstructionType.TurnRight  ||
                                 evt.Type == VoiceInstructionType.SlightLeft ||
                                 evt.Type == VoiceInstructionType.SlightRight||
                                 evt.Type == VoiceInstructionType.UTurn;

            // ✅ v7.2: Para instrucciones direccionales SIEMPRE recalcular texto
            // con distancia real actual. Nunca usar evt.InstructionText para estos tipos.
            string text    = isDirectional ? RecalcTurnTextRelativeToUser(evt) : evt.InstructionText;
            int    priority = GetPriority(evt.Type);
            Speak(evt.Type, text, priority);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ✅ v7.2 FIX PRINCIPAL — RecalcTurnTextRelativeToUser con pasos dinámicos
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ✅ v7.2 FIX — Recalcula el texto del giro con distancia REAL al waypoint
        /// en el momento del disparo. Nunca devuelve evt.InstructionText (texto estático).
        ///
        /// ANTES (v7.1): Si nextEvtIdx era inválido, devolvía evt.InstructionText
        /// con los pasos calculados en BuildInstructions() (potencialmente minutos atrás).
        ///
        /// AHORA (v7.2): En TODOS los casos se recalcula la distancia desde EvalPos
        /// actual al waypoint del evento. El texto siempre refleja los pasos reales.
        ///
        /// Flujo:
        ///   1. Calcular distFromUser y stepsFromUser en tiempo real (siempre).
        ///   2. Intentar reconstruir dirIn/dirOut para clasificar el giro con UserFwd.
        ///   3. Si la geometría es válida → texto con dirección de reloj + pasos reales.
        ///   4. Si la geometría falla → texto de emergencia con pasos reales (nunca texto viejo).
        /// </summary>
        private string RecalcTurnTextRelativeToUser(NavigationInstructionEvent evt)
        {
            // ✅ v7.2: Calcular distancia real en tiempo real — SIEMPRE, antes de cualquier
            // early return. Este es el dato crítico que estaba siendo ignorado en v7.1.
            float distFromUser = Vector3.Distance(
                new Vector3(EvalPos.x, 0f, EvalPos.z),
                new Vector3(evt.WorldPosition.x, 0f, evt.WorldPosition.z));
            int stepsFromUser = Mathf.Max(1, Mathf.RoundToInt(distFromUser / _stepLength));

            if (_logInstructions)
                Debug.Log($"[VoiceGuide] 📐 v7.2 RecalcSteps: dist={distFromUser:F1}m → {stepsFromUser} pasos " +
                          $"(antes era: \"{evt.InstructionText}\")");

            // Buscar el índice del evento en la lista para obtener la geometría
            int evtIdx = -1;
            for (int i = 0; i < _events.Count; i++)
            {
                if (_events[i] == evt) { evtIdx = i; break; }
            }

            int nextEvtIdx = evtIdx + 1;

            // ✅ v7.2: Si no hay evento siguiente válido, construir texto de emergencia
            // con pasos reales. NUNCA devolver evt.InstructionText.
            if (evtIdx < 0 || nextEvtIdx >= _events.Count)
            {
                return BuildFallbackTurnText(evt.Type, stepsFromUser);
            }

            // Reconstruir dirOut desde la geometría de eventos
            Vector3 dirOut = _events[nextEvtIdx].WorldPosition - evt.WorldPosition;
            dirOut.y = 0f;

            if (dirOut.sqrMagnitude < 0.001f)
            {
                // ✅ v7.2: dirOut inválido → fallback con pasos reales
                return BuildFallbackTurnText(evt.Type, stepsFromUser);
            }

            dirOut.Normalize();

            // Reconstruir dirIn desde eventos anteriores
            Vector3 dirIn = Vector3.zero;
            for (int back = evtIdx - 1; back >= 0; back--)
            {
                Vector3 candidate = evt.WorldPosition - _events[back].WorldPosition;
                candidate.y = 0f;
                if (candidate.sqrMagnitude > 0.01f)
                {
                    dirIn = candidate.normalized;
                    break;
                }
            }

            // Si no se encontró dirIn válido, usar UserFwd como referencia
            if (dirIn.sqrMagnitude < 0.001f)
                dirIn = UserFwd;

            var (ttype, signedAngle) = ClassifyTurnRelativeToUser(dirIn, dirOut, true);

            // ✅ v7.2: Construir texto con pasos dinámicos recalculados
            if (stepsFromUser <= 3)
                return $"{TurnLabelImperative(ttype, signedAngle)} ahora.";

            return $"En {stepsFromUser} pasos, {TurnLabel(ttype, signedAngle)}.";
        }

        /// <summary>
        /// ✅ v7.2 — Texto de giro de emergencia cuando la geometría no está disponible.
        /// Usa los pasos recalculados en tiempo real, nunca texto estático.
        /// Se infiere la dirección a partir del tipo del evento original.
        /// </summary>
        private string BuildFallbackTurnText(VoiceInstructionType type, int steps)
        {
            // Estimar ángulo representativo del tipo de giro para construir el ClockText
            float estimatedAngle = type switch
            {
                VoiceInstructionType.TurnRight   => 90f,
                VoiceInstructionType.TurnLeft    => -90f,
                VoiceInstructionType.SlightRight => 35f,
                VoiceInstructionType.SlightLeft  => -35f,
                VoiceInstructionType.UTurn       => 180f,
                _                                => 0f,
            };

            if (type == VoiceInstructionType.UTurn)
                return steps <= 3 ? "Date la vuelta ahora." : $"En {steps} pasos, date la vuelta.";

            string clockStr = ClockText(ClockPosition(estimatedAngle));

            if (_logInstructions)
                Debug.Log($"[VoiceGuide] ⚠️ v7.2 FallbackTurnText: tipo={type} steps={steps} " +
                          $"ángulo estimado={estimatedAngle}° → {clockStr}");

            return steps <= 3
                ? $"Gira {clockStr} ahora."
                : $"En {steps} pasos, gira {clockStr}.";
        }

        private float TriggerDist(VoiceInstructionType t) => t switch
        {
            VoiceInstructionType.UTurn               => _turnTriggerDist * 1.5f,
            VoiceInstructionType.SlightLeft          => _turnTriggerDist * 0.7f,
            VoiceInstructionType.SlightRight         => _turnTriggerDist * 0.7f,
            VoiceInstructionType.StairsSafetyWarning => 2.0f,
            _ => _turnTriggerDist,
        };

        private static int GetPriority(VoiceInstructionType t) => t switch
        {
            VoiceInstructionType.ObstacleWarning      => 3,
            VoiceInstructionType.UTurn                => 3,
            VoiceInstructionType.TurnLeft             => 2,
            VoiceInstructionType.TurnRight            => 2,
            VoiceInstructionType.SlightLeft           => 2,
            VoiceInstructionType.SlightRight          => 2,
            VoiceInstructionType.UserDeviated         => 2,
            VoiceInstructionType.StairsWarning        => 2,
            VoiceInstructionType.StairsSafetyWarning  => 2,
            VoiceInstructionType.StairsClimb          => 2,
            VoiceInstructionType.StairsDescent        => 2,
            VoiceInstructionType.StartNavigation      => 1,
            VoiceInstructionType.Arrived              => 1,
            VoiceInstructionType.StairsComplete       => 1,
            VoiceInstructionType.ResumeAfterSeparation => 1,
            _ => 0,
        };

        // ─────────────────────────────────────────────────────────────────────
        //  ORIENTACIÓN INICIAL
        // ─────────────────────────────────────────────────────────────────────

        private void AnnounceInitialOrientation(IReadOnlyList<Vector3> waypoints)
        {
            if (!IsFullARMode || waypoints == null || waypoints.Count < 2) return;

            float totalDist  = AccumDistAlongPath(waypoints, 0, waypoints.Count - 1);
            int   totalSteps = Mathf.Max(1, Mathf.RoundToInt(totalDist / _stepLength));

            float   straightDist   = 0f;
            int     firstTurnWpIdx = -1;
            float   firstTurnAngle = 0f;
            Vector3 routeFirstDir  = Vector3.zero;

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

            float  initialAngle = routeFirstDir.sqrMagnitude > 0.001f ? SignedAngleXZ(UserFwd, routeFirstDir) : 0f;
            int    clockHour    = ClockPosition(initialAngle);
            string clockStr     = ClockText(clockHour);
            int    straightSteps = Mathf.Max(1, Mathf.RoundToInt(straightDist / _stepLength));
            string text;

            if (firstTurnWpIdx < 0)
            {
                text = clockHour == 12 ? $"Destino al frente. {totalSteps} pasos."
                     : clockHour == 6  ? $"Destino {clockStr}. Date la vuelta y camina {totalSteps} pasos."
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
                text = clockHour == 6  ? $"Destino {clockStr}. Date la vuelta y camina {totalSteps} pasos."
                     : clockHour == 12 ? $"Destino al frente. {totalSteps} pasos."
                                       : $"Destino {clockStr}. Gira al frente y camina {totalSteps} pasos.";
            }

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
            float   t = ab.sqrMagnitude > 0.001f
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
            var p  = new Vector2(pt.x, pt.z);
            var p1 = new Vector2(a.x,  a.z);
            var p2 = new Vector2(b.x,  b.z);
            var seg = p2 - p1;
            float lenSq = seg.sqrMagnitude;
            if (lenSq < 0.0001f) return Vector2.Distance(p, p1);
            return Vector2.Distance(p, p1 + Mathf.Clamp01(Vector2.Dot(p - p1, seg) / lenSq) * seg);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ✅ v7.0 FIX 1 + FIX 5 — ClockPosition y ClockText corregidos
        // ─────────────────────────────────────────────────────────────────────

        private static int ClockPosition(float signedAngle)
        {
            float normalized = ((signedAngle % 360f) + 360f) % 360f;
            int h = Mathf.RoundToInt(normalized / 30f) % 12;
            return h == 0 ? 12 : h;
        }

        private static string ClockText(int h) => h switch
        {
            12 => "al frente",
            6  => "atrás",
            3  => "a la derecha",
            9  => "a la izquierda",
            _  => $"a las {h}"
        };

        private static float SignedAngleXZ(Vector3 from, Vector3 to)
        {
            from.y = 0f; to.y = 0f;
            if (from.sqrMagnitude < 0.001f || to.sqrMagnitude < 0.001f) return 0f;
            return Vector3.SignedAngle(from, to, Vector3.up);
        }

        private static string DirectionLabel(float a)
        {
            int h = ClockPosition(a);
            return ClockText(h);
        }

        private static Vector3 FlatFwd(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.001f ? v.normalized : Vector3.forward;
        }

        private int ResolveFloorForY(float worldY)
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

        // ─────────────────────────────────────────────────────────────────────
        //  GIZMOS
        // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || !_isGuiding) return;
            Gizmos.color = Color.cyan;  Gizmos.DrawWireSphere(UserPos, 0.25f);
            Gizmos.color = Color.blue;  Gizmos.DrawLine(UserPos, UserPos + UserFwd * 0.7f);

            if (_waitingForExit)
            {
                float exitRadius = Mathf.Max(_lastFiredTriggerDist * _exitRadiusFactor, _minExitDistance);
                Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
                Gizmos.DrawWireSphere(_lastFiredEventPos, exitRadius);
            }

            foreach (var evt in _events)
            {
                Gizmos.color = evt.HasFired ? new Color(0.3f, 0.3f, 0.3f, 0.4f) : GizmoColor(evt.Type);
                Gizmos.DrawWireSphere(evt.WorldPosition, evt.TriggerDistance);
                Gizmos.DrawSphere(evt.WorldPosition, 0.08f);
            }
        }

        private static Color GizmoColor(VoiceInstructionType t) => t switch
        {
            VoiceInstructionType.TurnLeft            => Color.red,
            VoiceInstructionType.TurnRight           => Color.blue,
            VoiceInstructionType.SlightLeft          => new Color(1f, 0.5f, 0.5f),
            VoiceInstructionType.SlightRight         => new Color(0.5f, 0.5f, 1f),
            VoiceInstructionType.UTurn               => Color.magenta,
            VoiceInstructionType.StairsWarning       => Color.yellow,
            VoiceInstructionType.StairsClimb         => new Color(1f, 0.6f, 0f),
            VoiceInstructionType.StairsDescent       => new Color(0.8f, 0.4f, 0f),
            VoiceInstructionType.Arrived             => Color.green,
            VoiceInstructionType.UserStopped         => Color.cyan,
            VoiceInstructionType.UserDeviated        => new Color(1f, 0f, 0.5f),
            VoiceInstructionType.ObstacleWarning     => new Color(1f, 0.3f, 0f),
            _ => Color.white,
        };
#endif

        // ─────────────────────────────────────────────────────────────────────
        //  CONTEXT MENU
        // ─────────────────────────────────────────────────────────────────────

        [ContextMenu("Estado v7.2")]
        private void DebugStatus() =>
            Debug.Log($"[VoiceGuide] v7.2 | ttsBusy={_ttsBusy} | guiding={_isGuiding} | " +
                      $"pendingDest=\"{_pendingDestinationId}\" | " +
                      $"events={_events.Count} nextIdx={_nextIdx} | " +
                      $"waitingForExit={_waitingForExit} | " +
                      $"straightBlockedUntil={_straightReminderBlockedUntil:F1} (now={Time.time:F1}) | " +
                      $"lastStraightTime={_lastStraightTime:F1}");

        [ContextMenu("Detener")]
        private void DebugStop() => ResetSession();

        [ContextMenu("Simular TTS done")]
        private void DebugTTSDone() { ClearTTSBusy(); Debug.Log("[VoiceGuide] TTS done simulado."); }

        [ContextMenu("🔁 Repetir última instrucción")]
        private void DebugRepeat() => RepeatLastInstruction();

        [ContextMenu("Voice Status JSON")]
        private void DebugVoiceStatus() => Debug.Log(GetVoiceStatusJson());

        [ContextMenu("🔓 Forzar desbloqueo secuenciador")]
        private void DebugUnlockSequencer()
        {
            _waitingForExit = false;
            Debug.Log("[VoiceGuide] Secuenciador desbloqueado manualmente.");
        }

        [ContextMenu("🕐 Test ClockText")]
        private void DebugClockText()
        {
            for (int deg = -180; deg <= 180; deg += 30)
            {
                int h = ClockPosition(deg);
                Debug.Log($"  Ángulo {deg,5}° → hora {h,2} → \"{ClockText(h)}\"");
            }
        }

        [ContextMenu("📍 Log nextIdx y posición")]
        private void DebugNextIdx()
        {
            if (_nextIdx < _events.Count)
            {
                var next = _events[_nextIdx];
                float dist = Vector3.Distance(EvalPos, next.WorldPosition);
                Debug.Log($"[VoiceGuide] nextIdx={_nextIdx}/{_events.Count} | " +
                          $"tipo={next.Type} | dist={dist:F2}m | trigger={next.TriggerDistance:F2}m | " +
                          $"fired={next.HasFired}");
            }
            else Debug.Log($"[VoiceGuide] nextIdx={_nextIdx} >= events.Count={_events.Count} (completado)");
        }

        [ContextMenu("🔓 Desbloquear straight reminder (test)")]
        private void DebugUnlockStraightReminder()
        {
            _straightReminderBlockedUntil = -1f;
            _lastStraightTime = -999f;
            _lastStraightIdx = -1;
            ResetTypeCooldown(VoiceInstructionType.GoStraight);
            Debug.Log("[VoiceGuide] Straight reminder desbloqueado para test.");
        }

        [ContextMenu("📐 Test RecalcSteps (nextIdx)")]
        private void DebugRecalcSteps()
        {
            if (_nextIdx >= _events.Count)
            {
                Debug.Log("[VoiceGuide] Sin evento siguiente para testear.");
                return;
            }
            var evt = _events[_nextIdx];
            float dist = Vector3.Distance(
                new Vector3(EvalPos.x, 0f, EvalPos.z),
                new Vector3(evt.WorldPosition.x, 0f, evt.WorldPosition.z));
            int steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));
            Debug.Log($"[VoiceGuide] 📐 evt[{_nextIdx}] {evt.Type} | " +
                      $"dist={dist:F2}m | pasos dinámicos={steps} | " +
                      $"texto estático original=\"{evt.InstructionText}\"");
        }
    }
}