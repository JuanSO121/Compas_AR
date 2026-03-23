// File: NavigationVoiceGuide.cs
// ✅ v5.8 — Prioridades reordenadas + mensaje inicio simplificado
//
//  p=3 URGENTE  ObstacleWarning, Escaleras, Orientación inicial
//  p=2 ALTO     Giros (TurnLeft/Right/Slight/UTurn), UserDeviated
//  p=1 MEDIO    StartNavigation ("Listo, vamos a X"), Arrived, StairsComplete
//  p=0 BAJO     GoStraight, ProgressUpdate, UserStopped
//               → descartados en Speak() si _ttsBusy, nunca cruzan el bridge
//
//  Orientación inicial sube a p=3: activa _ttsBusy=true al instante.
//  Todos los p≤1 generados por Update() se descartan antes de llegar a Flutter.
//  La cola de Flutter nunca se llena con mensajes informativos.

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
        [SerializeField] private float _stairYTolerance      = 1.2f;

        [Header("─── Ángulos de Giro ─────────────────────────────────────────")]
        [SerializeField] private float _slightTurnAngle   = 20f;
        [SerializeField] private float _definiteTurnAngle = 50f;
        [SerializeField] private float _uTurnAngle        = 140f;

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

        [Header("─── [E1] Parada ─────────────────────────────────────────────")]
        [SerializeField] private float _stopTimeout          = 4.0f;
        [SerializeField] private float _stopMinMovement      = 0.25f;
        [SerializeField] private float _stopReminderInterval = 15.0f;

        [Header("─── [E2] Desviación ────────────────────────────────────────")]
        [SerializeField] private float _deviationDist  = 2.0f;
        [SerializeField] private float _deviationDelay = 2.5f;

        [Header("─── [E3] Obstáculo ─────────────────────────────────────────")]
        [SerializeField] private float _obstacleCheckTime       = 6.0f;
        [SerializeField] private float _obstacleWarningCooldown = 60f;

        [Header("─── [E6] Separación ────────────────────────────────────────")]
        [SerializeField] private float _longSeparationTime = 12.0f;

        [Header("─── [E7] Desorientación ────────────────────────────────────")]
        [SerializeField] private float _misalignAngleThreshold   = 45f;
        [SerializeField] private float _misalignConfirmTime      = 3.0f;
        [SerializeField] private float _misalignReminderInterval = 12f;
        [SerializeField] private float _misalignMinSpeed         = 0.2f;

        [Header("─── Anti-saturación ──────────────────────────────────────")]
        [SerializeField] private float _dedupWindow = 15.0f;
        [SerializeField] private float _minMessageInterval  = 3.5f;
        [SerializeField] private float _ttsFallbackTimeout  = 20f;

        [Header("─── Debug ────────────────────────────────────────────────────")]
        [SerializeField] private bool _logInstructions  = true;
        [SerializeField] private bool _logPreprocessing = true;

        // ── Estado ────────────────────────────────────────────────────────────
        private readonly List<NavigationInstructionEvent> _events = new(24);
        private int     _nextIdx         = 0;
        private bool    _isGuiding       = false;
        private bool    _isPreprocessing = false;
        private string  _destName        = string.Empty;
        private Vector3 _destPos         = new(float.PositiveInfinity, 0, 0);

        private float _lastStraightTime   = -999f;
        private int   _lastStraightIdx    = -1;
        private float _lastProgressTime   = -999f;
        private float _lastAnyMessageTime = -999f;

        private Vector3 _stopRefPos       = Vector3.zero;
        private float   _stopAccumTime    = 0f;
        private bool    _isStopped        = false;
        private float   _lastStopReminder = -999f;

        private float _deviationTimer = 0f;
        private bool  _deviationFired = false;

        private float _obstacleTimer           = 0f;
        private float _lastDistToNext          = float.MaxValue;
        private bool  _obstacleFired           = false;
        private float _lastObstacleWarningTime = -999f;

        private float _returningTimer = 0f;
        private int   _currentFloor   = 0;

        private Coroutine _waitCoroutine        = null;
        private Coroutine _ttsFallbackCoroutine = null;

        private float _evalAccum       = 0f;
        private string _lastSpokenText = string.Empty;
        private float  _lastSpokenTime = -999f;

        private float _misalignTimer    = 0f;
        private float _lastMisalignTime = -999f;

        private bool _ttsBusy = false;

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
            Debug.Log($"[VoiceGuide] ✅ v5.8");
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
            if (_nextIdx >= _events.Count || _ttsBusy) return;

            Vector3 evalPos = EvalPos;
            Vector3 userPos = UserPos;

            for (int i = _nextIdx; i < _events.Count; i++)
            {
                var evt = _events[i];
                if (evt.HasFired) { _nextIdx = i + 1; continue; }

                Vector3 checkPos = evt.Type == VoiceInstructionType.Arrived ? userPos : evalPos;
                if (!ShouldFireEvent(evt, checkPos)) break;

                FireEvent(evt);
                evt.HasFired = true;
                _nextIdx = i + 1;
                if (GetPriority(evt.Type) >= 2) return;
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

        public void ClearTTSBusy()
        {
            if (!_ttsBusy) return;
            _ttsBusy = false;
            if (_ttsFallbackCoroutine != null) { StopCoroutine(_ttsFallbackCoroutine); _ttsFallbackCoroutine = null; }
            if (_logInstructions) Debug.Log("[VoiceGuide] ✅ _ttsBusy liberado.");
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

            int steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));
            Speak(VoiceInstructionType.GoStraight, $"Sigue recto. {steps} pasos.", 0);
            _lastStraightTime = Time.time;
            _lastStraightIdx  = _nextIdx;
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
                    $"Cuando estés listo, continúa. Próxima indicación en {steps} pasos.", 0);
                return;
            }

            if (_isStopped && Time.time - _lastStopReminder >= _stopReminderInterval)
            {
                if (_ttsBusy || Time.time - _lastAnyMessageTime < _minMessageInterval) return;
                _lastStopReminder = Time.time;
                int steps = Mathf.Max(1, Mathf.RoundToInt(RemainingDistFromUser() / _stepLength));
                Speak(VoiceInstructionType.UserStopped, $"Tómate tu tiempo. {steps} pasos al destino.", 0);
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
                        ? "Te desviaste. Detente, gira hacia la ruta y retoma el camino."
                        : "Te desviaste. Busca al guía y vuelve a la ruta.";
                    Speak(VoiceInstructionType.UserDeviated, msg, 2);
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
                    "Posible obstáculo. Rodéalo con cuidado hacia un lado.", 3);
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
            Speak(VoiceInstructionType.ProgressUpdate, $"Vas bien. {steps} pasos para {_destName}.", 0);
        }

        private void EvaluateMisalignment(float dt)
        {
            if (!IsFullARMode || UserSpeed < _misalignMinSpeed || _isStopped) return;
            if (_nextIdx >= _events.Count || _ttsBusy) return;

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
                    string dir   = DirectionLabel(signedAngle);
                    float  dist  = Vector3.Distance(UserPos, _events[_nextIdx].WorldPosition);
                    int    steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));
                    string text  = absAngle <= 50f  ? $"El camino está {dir}. Gira levemente. {steps} pasos."
                                 : absAngle <= 130f ? $"Dirección equivocada. Gira {dir}. {steps} pasos."
                                                    : $"Estás al revés. Date la vuelta {dir}. {steps} pasos.";
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
            if (Vector3.Distance(evt.DestinationPosition, _destPos) < _destinationChangeThreshold
                && (_isGuiding || _isPreprocessing)) return;
            StartSession(evt.DestinationWaypointId, evt.DestinationPosition);
        }

        private void OnNavCompleted(NavigationCompletedEvent _)
        {
            if (!_isGuiding) return;
            bool  arrivedFired = _events.Exists(e => e.Type == VoiceInstructionType.Arrived && e.HasFired);
            float distToGoal   = Vector3.Distance(UserPos, _destPos);
            if (!arrivedFired && distToGoal > _arrivalTriggerDist * 2f) return;
            if (!arrivedFired)
                Speak(VoiceInstructionType.Arrived,
                    string.IsNullOrEmpty(_destName) ? "Llegaste. ¡Bien hecho!" : $"Llegaste a {_destName}. ¡Bien hecho!", 1);
            ResetSession();
        }

        private void OnNavCancelled(NavigationCancelledEvent _) => ResetSession();

        private void OnFloorTransition(FloorTransitionEvent e)
        {
            _currentFloor = e.ToLevel; _obstacleFired = false; _isStopped = false; _stopAccumTime = 0f;
            _lastSpokenText = string.Empty; _lastSpokenTime = -999f;
            _misalignTimer = 0f; _lastMisalignTime = -999f;
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
            float rem   = RemainingDistFromUser(subdivided);
            int   steps = Mathf.Max(1, Mathf.RoundToInt(rem / _stepLength));
            if (fullSummary)
                Speak(VoiceInstructionType.ResumeAfterSeparation,
                    IsFullARMode ? $"Ruta recalculada. {steps} pasos a {_destName}."
                                 : $"El guía te encontró. {steps} pasos a {_destName}.", 1);
            else
                Speak(VoiceInstructionType.GoStraight, $"Ruta actualizada. {steps} pasos a {_destName}.", 0);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  API PÚBLICA
        // ─────────────────────────────────────────────────────────────────────

        public void TriggerFromWaypoint(IndoorNavAR.Core.Data.WaypointData waypoint)
        {
            if (waypoint == null) return;
            if (Vector3.Distance(waypoint.Position, _destPos) < _destinationChangeThreshold
                && (_isGuiding || _isPreprocessing)) return;
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
        //  SESIÓN
        // ─────────────────────────────────────────────────────────────────────

        private void StartSession(string destName, Vector3 destPosition)
        {
            if (_waitCoroutine != null) { StopCoroutine(_waitCoroutine); _waitCoroutine = null; }
            ResetSession(silent: true);
            _destName = destName; _destPos = destPosition;
            _isPreprocessing = true; _stopRefPos = UserPos; _stopAccumTime = 0f; _returningTimer = 0f;
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
                // v5.8: mensaje corto incluso en timeout
                Speak(VoiceInstructionType.StartNavigation, $"Listo, vamos a {_destName}.", 1);
                _events.Clear(); _nextIdx = 0;
                _events.Add(new NavigationInstructionEvent(_destPos, VoiceInstructionType.Arrived,
                    _arrivalTriggerDist,
                    string.IsNullOrEmpty(_destName) ? "Llegaste. ¡Bien hecho!" : $"Llegaste a {_destName}. ¡Bien hecho!", 0));
                _isGuiding = true; _ttsBusy = false;
                yield break;
            }

            var subdivided = SubdivideWaypointSegments(path.Waypoints);
            _events.Clear(); _nextIdx = 0;
            _lastStraightTime = Time.time; _lastStraightIdx = -1; _lastProgressTime = Time.time;

            BuildInstructions(subdivided, startMessage: true);

            if (_events.Count > 0)
            {
                // ✅ v5.8: Primero el mensaje corto (p=1): "Listo, vamos a X"
                var startEvt = _events[0];
                FireEvent(startEvt);
                startEvt.HasFired = true;
                _nextIdx = 1;

                // ✅ v5.9: esperar a que el bridge procese el p=1 antes de enviar el p=3.
                // Sin este delay, ambos llegan al bridge en el mismo frame y el orden
                // no está garantizado. 200ms es suficiente para que Flutter los separe.
                yield return new WaitForSeconds(0.2f);

                AnnounceInitialOrientation(subdivided);
            }

            yield return new WaitForSeconds(_startDelay);

            // Esperar a que no haya eventos inminentes antes de soltar la guía
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
                Debug.Log($"[VoiceGuide] ✅ v5.8 activo. {_events.Count} instrucciones, nextIdx={_nextIdx}.");
        }

        private float GetDistToFirstActionEvent()
        {
            for (int i = 1; i < _events.Count; i++)
            {
                var t = _events[i].Type;
                if (t == VoiceInstructionType.TurnLeft  || t == VoiceInstructionType.TurnRight   ||
                    t == VoiceInstructionType.SlightLeft || t == VoiceInstructionType.SlightRight ||
                    t == VoiceInstructionType.UTurn      || t == VoiceInstructionType.StairsWarning)
                    return Vector3.Distance(EvalPos, _events[i].WorldPosition);
            }
            return float.MaxValue;
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
                // ✅ v5.8: mensaje de inicio corto
                _events.Add(new NavigationInstructionEvent(
                    wp[0], VoiceInstructionType.StartNavigation, 0.5f,
                    $"Listo, vamos a {_destName}.", 0));
            }

            for (int i = 1; i < count - 1; i++)
            {
                Vector3 prev = wp[i - 1], current = wp[i], next = wp[i + 1];
                float deltaY = next.y - current.y;

                if (Mathf.Abs(deltaY) >= _stairHeightThreshold)
                {
                    float pathDist = AccumDistAlongPath(wp, 0, i);
                    int   wSteps   = Mathf.Max(1, Mathf.RoundToInt(pathDist / _stepLength));
                    _events.Add(new NavigationInstructionEvent(current, VoiceInstructionType.StairsWarning,
                        _stairTriggerDist, wSteps > 5 ? $"En {wSteps} pasos, escaleras. Reduce el paso."
                                                      : "Escaleras muy cerca. Reduce el paso.", i));
                    float stairLen = Vector3.Distance(current, next);
                    int   stairSec = Mathf.Max(1, Mathf.RoundToInt(stairLen / _walkSpeedStairs));
                    bool  up = deltaY > 0f;
                    _events.Add(new NavigationInstructionEvent(current,
                        up ? VoiceInstructionType.StairsClimb : VoiceInstructionType.StairsDescent, 1.0f,
                        up ? $"Sube. Tómate tu tiempo. {stairSec}s." : $"Baja con cuidado. Agárrate al pasamanos. {stairSec}s.", i));
                    _events.Add(new NavigationInstructionEvent(next, VoiceInstructionType.StairsComplete,
                        0.8f, "Terminaste las escaleras. Continúa.", i));
                    continue;
                }

                Vector3 dirIn = current - prev; dirIn.y = 0f;
                Vector3 dirOut = next - current; dirOut.y = 0f;
                if (dirIn.sqrMagnitude < 0.001f || dirOut.sqrMagnitude < 0.001f) continue;
                dirIn.Normalize(); dirOut.Normalize();

                var (ttype, angle) = ClassifyTurnRelativeToUser(dirIn, dirOut, false);
                if (ttype == VoiceInstructionType.GoStraight) continue;

                int stepsToTurn = Mathf.Max(1, Mathf.RoundToInt(AccumDistAlongPath(wp, 0, i) / _stepLength));
                string turnText = BuildTurnTextWithContext(ttype, angle, stepsToTurn, wp, i, dirIn, dirOut);
                _events.Add(new NavigationInstructionEvent(current, ttype, TriggerDist(ttype), turnText, i));

                if (_logInstructions)
                    Debug.Log($"[VoiceGuide] 📍 wp[{i}]: {ttype} {angle:F1}° pasos={stepsToTurn}");
            }

            _events.Add(new NavigationInstructionEvent(wp[count - 1], VoiceInstructionType.Arrived,
                _arrivalTriggerDist,
                string.IsNullOrEmpty(_destName) ? "Llegaste. ¡Bien hecho!" : $"Llegaste a {_destName}. ¡Bien hecho!",
                count - 1));
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
                                              new Vector3(wp[cornerIdx].x, 0, wp[cornerIdx].z));
            }
            else if (cornerIdx >= 1)
            {
                prevSegLen = Vector3.Distance(new Vector3(wp[cornerIdx - 1].x, 0, wp[cornerIdx - 1].z),
                                              new Vector3(wp[cornerIdx].x, 0, wp[cornerIdx].z));
                prevIsStr  = true;
            }

            float distFromUser = Vector3.Distance(new Vector3(EvalPos.x, 0, EvalPos.z),
                                                   new Vector3(wp[cornerIdx].x, 0, wp[cornerIdx].z));
            int stepsFromUser = Mathf.Max(1, Mathf.RoundToInt(distFromUser / _stepLength));
            string turnLabel  = TurnLabel(ttype, signedAngle);

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
            if (_waitCoroutine != null)        { StopCoroutine(_waitCoroutine);        _waitCoroutine        = null; }
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

            if (!silent && _logPreprocessing) Debug.Log("[VoiceGuide] Sesión detenida.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SPEAK
        // ─────────────────────────────────────────────────────────────────────

        private void Speak(VoiceInstructionType type, string text, int priority)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (text == _lastSpokenText && Time.time - _lastSpokenTime < _dedupWindow)
            {
                if (_logInstructions) Debug.Log($"[VoiceGuide] 🔇 DEDUP [{type}]");
                return;
            }

            // p≤1 se descartan si _ttsBusy — nunca cruzan el bridge hacia Flutter
            if (priority <= 1 && _ttsBusy)
            {
                if (_logInstructions) Debug.Log($"[VoiceGuide] 🚫 DROP p={priority} (TTS busy): \"{text}\"");
                return;
            }

            _lastSpokenText = text; _lastSpokenTime = Time.time; _lastAnyMessageTime = Time.time;

            EventBus.Instance?.Publish(new TTSRequestEvent
            {
                Text = text, Priority = priority, Interrupt = priority >= 3,
            });

            EventBus.Instance?.Publish(new GuideAnnouncementEvent
            {
                AnnouncementType = MapToAnnouncementType(type),
                Message          = text,
                CurrentFloor     = _currentFloor,
            });

            // p≥2 activa _ttsBusy: orientación (p=3), giros (p=2), obstáculos (p=3)
            if (priority >= 2)
            {
                _ttsBusy = true;
                if (_ttsFallbackCoroutine != null) StopCoroutine(_ttsFallbackCoroutine);
                _ttsFallbackCoroutine = StartCoroutine(TTSFallbackTimeout(_ttsFallbackTimeout));
            }

            if (_logInstructions) Debug.Log($"[VoiceGuide] 🔊 [{type}] p={priority} \"{text}\"");
        }

        private IEnumerator TTSFallbackTimeout(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            _ttsFallbackCoroutine = null;
            if (_ttsBusy)
            {
                Debug.LogWarning($"[VoiceGuide] ⚠️ TTSFallbackTimeout ({seconds}s).");
                _ttsBusy = false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  MAPEO
        // ─────────────────────────────────────────────────────────────────────

        private static GuideAnnouncementType MapToAnnouncementType(VoiceInstructionType type) => type switch
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

        // ─────────────────────────────────────────────────────────────────────
        //  FIRE EVENT
        // ─────────────────────────────────────────────────────────────────────

        private void FireEvent(NavigationInstructionEvent evt)
        {
            bool isDirectional = evt.Type == VoiceInstructionType.TurnLeft   ||
                                 evt.Type == VoiceInstructionType.TurnRight  ||
                                 evt.Type == VoiceInstructionType.SlightLeft ||
                                 evt.Type == VoiceInstructionType.SlightRight ||
                                 evt.Type == VoiceInstructionType.UTurn;
            string text    = isDirectional ? RecalcTurnTextRelativeToUser(evt) : evt.InstructionText;
            int    priority = GetPriority(evt.Type);
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
            float dist  = Vector3.Distance(new Vector3(EvalPos.x, 0, EvalPos.z),
                                           new Vector3(evt.WorldPosition.x, 0, evt.WorldPosition.z));
            int   steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));
            if (steps <= 3) return $"{TurnLabelImperative(ttype, signedAngle)} ahora.";
            return $"En {steps} pasos, {TurnLabel(ttype, signedAngle)}.";
        }

        private float TriggerDist(VoiceInstructionType t) => t switch
        {
            VoiceInstructionType.UTurn       => _turnTriggerDist * 1.5f,
            VoiceInstructionType.SlightLeft  => _turnTriggerDist * 0.7f,
            VoiceInstructionType.SlightRight => _turnTriggerDist * 0.7f,
            _                                => _turnTriggerDist,
        };

        // ✅ v5.8: esquema de prioridades definitivo
        private static int GetPriority(VoiceInstructionType t) => t switch
        {
            VoiceInstructionType.ObstacleWarning => 3,  // urgente
            VoiceInstructionType.StairsWarning   => 3,
            VoiceInstructionType.StairsClimb     => 3,
            VoiceInstructionType.StairsDescent   => 3,
            VoiceInstructionType.TurnLeft        => 2,  // alto
            VoiceInstructionType.TurnRight       => 2,
            VoiceInstructionType.SlightLeft      => 2,
            VoiceInstructionType.SlightRight     => 2,
            VoiceInstructionType.UTurn           => 2,
            VoiceInstructionType.UserDeviated    => 2,
            VoiceInstructionType.StartNavigation       => 1,  // medio
            VoiceInstructionType.Arrived               => 1,
            VoiceInstructionType.StairsComplete        => 1,
            VoiceInstructionType.ResumeAfterSeparation => 1,
            _                                          => 0,  // bajo → descartado si _ttsBusy
        };

        // ─────────────────────────────────────────────────────────────────────
        //  ORIENTACIÓN INICIAL — p=3
        // ─────────────────────────────────────────────────────────────────────

        private void AnnounceInitialOrientation(IReadOnlyList<Vector3> waypoints)
        {
            if (!IsFullARMode || waypoints == null || waypoints.Count < 2) return;

            float totalDist = AccumDistAlongPath(waypoints, 0, waypoints.Count - 1);
            int totalSteps  = Mathf.Max(1, Mathf.RoundToInt(totalDist / _stepLength));

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

            float  initialAngle  = routeFirstDir.sqrMagnitude > 0.001f ? SignedAngleXZ(UserFwd, routeFirstDir) : 0f;
            int    clockHour     = ClockPosition(initialAngle);
            string clockStr      = ClockText(clockHour);
            int    straightSteps = Mathf.Max(1, Mathf.RoundToInt(straightDist / _stepLength));
            string text;

            if (firstTurnWpIdx < 0)
            {
                text = clockHour == 12 ? $"Destino al frente. {totalSteps} pasos en línea recta."
                     : clockHour == 6  ? $"Destino {clockStr}. Date la vuelta y camina {totalSteps} pasos."
                                       : $"Destino {clockStr}. Gira hasta tenerlo al frente y camina {totalSteps} pasos.";
            }
            else if (straightDist >= _minMentionableStraightDist)
            {
                var (turnType, _) = ClassifyTurnRelativeToUser(
                    (waypoints[firstTurnWpIdx]     - waypoints[firstTurnWpIdx - 1]).normalized,
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

            // ✅ v5.8: p=3 → _ttsBusy=true → GoStraight/UserStopped se descartan
            Speak(VoiceInstructionType.StartNavigation, text, priority: 3);

            if (_logInstructions)
                Debug.Log($"[VoiceGuide] 🧭 Orientación p=3: \"{text}\"");
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
            if (abs <= 15f)  return "recto";
            if (abs <= 50f)  return right ? "ligeramente a la derecha" : "ligeramente a la izquierda";
            if (abs <= 130f) return right ? "a la derecha" : "a la izquierda";
                             return right ? "casi detrás tuyo, hacia la derecha" : "casi detrás tuyo, hacia la izquierda";
        }

        private static Vector3 FlatFwd(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 0.001f ? v.normalized : Vector3.forward; }

        // ─────────────────────────────────────────────────────────────────────
        //  GIZMOS
        // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || !_isGuiding) return;
            Gizmos.color = Color.cyan;  Gizmos.DrawWireSphere(UserPos, 0.25f);
            Gizmos.color = Color.blue;  Gizmos.DrawLine(UserPos, UserPos + UserFwd * 0.7f);
            foreach (var evt in _events)
            {
                Gizmos.color = evt.HasFired ? new Color(0.3f, 0.3f, 0.3f, 0.4f) : GizmoColor(evt.Type);
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

        [ContextMenu("ℹ️ Estado")]
        private void DebugStatus() =>
            Debug.Log($"[VoiceGuide] v5.8 | ttsBusy={_ttsBusy} | guiding={_isGuiding} | " +
                      $"events={_events.Count} nextIdx={_nextIdx} | " +
                      $"obstacle fired={_obstacleFired} last={Time.time - _lastObstacleWarningTime:F0}s ago");

        [ContextMenu("🛑 Detener")]
        private void DebugStop() => ResetSession();

        [ContextMenu("🔊 Simular TTS done")]
        private void DebugTTSDone() { ClearTTSBusy(); Debug.Log("[VoiceGuide] TTS done simulado."); }
    }
}