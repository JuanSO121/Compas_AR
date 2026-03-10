// File: NavigationVoiceGuide.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Navigation/Voice/
// ✅ v5.4 — FIX: Instrucciones relativas a la orientación real del usuario (cámara XR)
//           FIX: Segmentación automática de waypoints en rutas diagonales
//
// ============================================================================
//  CAMBIOS v5.3 → v5.4
// ============================================================================
//
//  PROBLEMA 1 — Instrucciones no relativas a la orientación del usuario:
//
//    En v5.3, ClassifyTurn() calcula el ángulo entre dirIn (segmento anterior)
//    y dirOut (segmento siguiente). Esto da la curvatura de la ruta en el
//    NavMesh, NO la dirección relativa al usuario físico.
//
//    EJEMPLO DEL BUG:
//      Ruta: A → B → C donde A→B va al Norte y B→C va al Este.
//      El NavMesh dice "gira 90° a la derecha en B".
//      Pero si el usuario está mirando al Sur, "la derecha" del NavMesh
//      es en realidad "su izquierda". → Instrucción incorrecta.
//
//    FIX 1 — ClassifyTurnRelativeToUser():
//      En lugar de comparar dirIn vs dirOut, compara UserForward vs dirOut.
//      El ángulo resultante es el giro real que el usuario necesita hacer
//      desde su orientación actual para seguir la ruta.
//      Se usa SignedAngleXZ(UserFwd, dirOut) con signo para determinar
//      si el giro es izquierda (negativo) o derecha (positivo).
//
//  PROBLEMA 2 — Rutas diagonales generan instrucciones ambiguas:
//
//    Si hay un waypoint B que está, por ejemplo, a 45° de la posición del
//    usuario, el sistema dice "gira levemente a la izquierda" pero en
//    realidad el camino óptimo es: recto 5 pasos, luego curva suave.
//    El problema es que el NavMesh crea segmentos diagonales para optimizar
//    la longitud de la ruta pero estos segmentos no corresponden con
//    "caminar recto" desde la perspectiva del usuario.
//
//    FIX 2 — SubdivideWaypointSegments():
//      Antes de BuildInstructions(), los segmentos más largos que
//      _maxSegmentLength se subdividen en sub-waypoints intermedios.
//      En cada sub-waypoint se comprueba si hay un giro significativo
//      respecto a la trayectoria previa. Los sub-waypoints "rectos"
//      se insertan silenciosamente para mejorar la precisión de las
//      instrucciones de giro en los vértices reales de la ruta.
//
//    FIX 3 — InstructionBuilder con lookahead de segmento recto previo:
//      Al generar una instrucción de giro en el waypoint i, se calcula
//      cuántos pasos lleva el usuario caminando recto antes de ese giro.
//      Si el segmento previo era recto (< _slightTurnAngle de deflexión),
//      el mensaje incluye primero el segmento recto: 
//      "Continúa {n} pasos recto, luego gira a la derecha."
//      Esto da instrucciones mucho más naturales y precisas.
//
//  TODOS LOS FIXES DE v5.0 - v5.3 SE CONSERVAN ÍNTEGRAMENTE.

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

        [Header("─── Escaleras — Tolerancia Y (FIX D) ──────────────────────")]
        [Tooltip("Diferencia máxima en Y (metros) entre UserPos y el WorldPosition del evento " +
                 "StairsComplete o Arrived para que se disparen.")]
        [SerializeField] private float _stairYTolerance = 1.2f;

        [Header("─── Ángulos de Giro ─────────────────────────────────────────")]
        [SerializeField] private float _slightTurnAngle   = 20f;
        [SerializeField] private float _definiteTurnAngle = 50f;
        [SerializeField] private float _uTurnAngle        = 140f;

        [Header("─── v5.4: Subdivisión de segmentos diagonales ───────────────")]
        [Tooltip("Longitud máxima (m) de un segmento antes de subdividirlo para " +
                 "mejorar la precisión de las instrucciones. 0 = desactivado.")]
        [SerializeField] private float _maxSegmentLength = 3.0f;

        [Tooltip("Ángulo máximo (°) de deflexión para considerar un segmento como " +
                 "'recto' y añadir el prefijo 'X pasos recto, luego...' en el mensaje.")]
        [SerializeField] private float _straightSegmentAngle = 15f;

        [Tooltip("Longitud mínima (m) de un segmento recto para que valga la pena " +
                 "mencionar el trecho recto previo al giro.")]
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

        [Header("─── [E7] Desorientación / Instrucciones direccionales (v5.0) ─")]
        [SerializeField] private float _misalignAngleThreshold   = 45f;
        [SerializeField] private float _misalignConfirmTime      = 3.0f;
        [SerializeField] private float _misalignReminderInterval = 12f;
        [SerializeField] private float _misalignMinSpeed         = 0.2f;

        [Header("─── Dedup de instrucciones (FIX E) ──────────────────────────")]
        [Tooltip("Segundos durante los cuales un texto idéntico no se repite.")]
        [SerializeField] private float _dedupWindow = 3.0f;

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
            Debug.Log($"[VoiceGuide] ✅ v5.4 Iniciado. EventBus={EventBus.Instance != null}");
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
                string stopReminder = IsFullARMode
                    ? $"Tómate tu tiempo. El destino está a {steps} pasos. Continúa cuando estés listo."
                    : $"Tómate tu tiempo. El destino está a {steps} pasos. Sigue al guía cuando estés listo.";
                Speak(VoiceInstructionType.UserStopped, stopReminder, priority: 0);
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
                        ? "Te has desviado del camino. Detente y oriéntate. " +
                          "Gira hasta ver la ruta marcada y retoma el camino."
                        : "Te has desviado del camino. Detente y busca al guía virtual. " +
                          "Camina hacia él para retomar la ruta.";
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

                    if (absAngle <= 50f)
                        text = $"El camino está {dir}. Gira levemente y continúa. " +
                               $"En {steps} pasos llegarás a la siguiente indicación.";
                    else if (absAngle <= 130f)
                        text = $"Estás yendo en dirección equivocada. " +
                               $"El camino está {dir}. Gira y continúa. En {steps} pasos.";
                    else
                        text = $"Estás en sentido contrario. Date la vuelta — " +
                               $"el camino está {dir}. En {steps} pasos.";

                    Speak(VoiceInstructionType.UserDeviated, text, priority: 2);

                    if (_logInstructions)
                        Debug.Log($"[VoiceGuide] ⚠️ [E7] Desorientación: " +
                                  $"ángulo={signedAngle:F1}° ({dir}) | dist={dist:F1}m | " +
                                  $"nextWp={nextWpPos:F2}");
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

            // ✅ v5.4: Usar waypoints subdivididos para mejor precisión
            var subdivided = SubdivideWaypointSegments(waypoints);
            BuildInstructions(subdivided, startMessage: false);

            float rem   = RemainingDistFromUser(subdivided);
            int   steps = Mathf.Max(1, Mathf.RoundToInt(rem / _stepLength));

            if (fullSummary)
            {
                int secs = Mathf.RoundToInt(rem / _walkSpeedFlat);
                string resumeMsg = IsFullARMode
                    ? $"Ruta recalculada. Retomamos hacia {_destName}. " +
                      $"Quedan {steps} pasos, aproximadamente {secs} segundos. " +
                      $"Continúa siguiendo la ruta indicada."
                    : $"El guía te encontró. Retomamos hacia {_destName}. " +
                      $"Quedan {steps} pasos, aproximadamente {secs} segundos. " +
                      $"Continúa siguiendo al guía.";
                Speak(VoiceInstructionType.ResumeAfterSeparation, resumeMsg, priority: 1);
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
                    ? "Comienza a caminar hacia el destino indicado."
                    : "Sigue al guía hacia adelante.";

                Speak(VoiceInstructionType.StartNavigation,
                    $"Iniciando navegación a {_destName}. {timeoutCta}",
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

            // ✅ v5.4: Subdividir waypoints antes de construir instrucciones
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
                Debug.Log($"[VoiceGuide] ✅ v5.4 Guía activo. {_events.Count} instrucciones " +
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
        //  ✅ v5.4 — SUBDIVISIÓN DE SEGMENTOS DIAGONALES
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ✅ v5.4 FIX 2: Subdivide segmentos largos en sub-waypoints intermedios.
        ///
        /// PROBLEMA:
        ///   El NavMesh puede generar segmentos diagonales largos. Cuando el usuario
        ///   está en A y el siguiente waypoint B está a 10m en diagonal, el sistema
        ///   dice "gira 45° a la izquierda" en vez de "camina recto 6m, luego gira".
        ///   Esto ocurre porque el punto de giro real se mezcla con el segmento diagonal.
        ///
        /// SOLUCIÓN:
        ///   Subdividir cualquier segmento mayor que _maxSegmentLength en sub-puntos.
        ///   Esto permite que BuildInstructions() detecte giros más precisamente,
        ///   ya que los segmentos cortos reflejan mejor la dirección real de avance.
        ///
        ///   La subdivisión solo ocurre en segmentos planos (sin cambio de altura de
        ///   escaleras) — los segmentos de escaleras no se subdividen para no interferir
        ///   con la lógica de detección de escaleras.
        /// </summary>
        private List<Vector3> SubdivideWaypointSegments(IReadOnlyList<Vector3> waypoints)
        {
            var result = new List<Vector3>(waypoints.Count * 2);
            if (waypoints.Count == 0) return result;

            result.Add(waypoints[0]);

            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                Vector3 a = waypoints[i];
                Vector3 b = waypoints[i + 1];

                // No subdividir segmentos de escaleras
                float deltaY = Mathf.Abs(b.y - a.y);
                if (deltaY >= _stairHeightThreshold)
                {
                    result.Add(b);
                    continue;
                }

                float segLen = Vector3.Distance(a, b);

                // Si el segmento es corto, no subdividir
                if (_maxSegmentLength <= 0f || segLen <= _maxSegmentLength)
                {
                    result.Add(b);
                    continue;
                }

                // Subdividir en tramos de máximo _maxSegmentLength metros
                int subdivisions = Mathf.CeilToInt(segLen / _maxSegmentLength);
                for (int s = 1; s <= subdivisions; s++)
                {
                    float t = (float)s / subdivisions;
                    result.Add(Vector3.Lerp(a, b, t));
                }
                // El punto b ya se añadió en la última iteración de s
            }

            if (_logPreprocessing && result.Count != waypoints.Count)
                Debug.Log($"[VoiceGuide] 📐 SubdivideWaypointSegments: " +
                          $"{waypoints.Count} → {result.Count} waypoints.");

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CONSTRUCCIÓN DE INSTRUCCIONES
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ✅ v5.4: BuildInstructions ahora recibe la lista ya subdividida.
        /// Para la instrucción de giro, usa ClassifyTurnRelativeToUser() que
        /// compara la dirección del segmento saliente con la orientación real
        /// del usuario (cámara XR), no con el segmento entrante del NavMesh.
        /// Además, incluye el prefijo "N pasos recto, luego..." cuando el segmento
        /// previo al giro es suficientemente recto y largo.
        /// </summary>
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

                string startCta = IsFullARMode
                    ? "Comienza a caminar siguiendo la ruta indicada."
                    : "Sigue al guía hacia adelante.";

                _events.Add(new NavigationInstructionEvent(
                    wp[0], VoiceInstructionType.StartNavigation, 0.5f,
                    $"Iniciando navegación a {_destName}. " +
                    $"Aproximadamente {steps} pasos, {secs} segundos.{stairs} " +
                    $"{startCta}",
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

                // ✅ v5.4b: Durante la construcción, siempre usar dirIn (isImmediateTurn=false).
                // UserFwd solo aplica cuando el evento se DISPARA (ver RecalcTurnTextRelativeToUser).
                var (ttype, userRelativeAngle) = ClassifyTurnRelativeToUser(dirIn, dirOut, isImmediateTurn: false);

                // Ángulo de deflexión pura de la ruta (independiente del usuario)
                float routeDeflectionAngle = Vector3.Angle(dirIn, dirOut);

                // Ignorar microdeflexiones de la subdivisión (artefactos de lerp)
                if (ttype == VoiceInstructionType.GoStraight) continue;

                float pathDistToTurn = AccumDistAlongPath(wp, 0, i);
                int   stepsToTurn    = Mathf.Max(1, Mathf.RoundToInt(pathDistToTurn / _stepLength));

                // ✅ v5.4 FIX 3: Calcular si hay un trecho recto mensurable antes del giro.
                // Si el segmento previo era recto (deflexión < _straightSegmentAngle)
                // y suficientemente largo, prefijamos "X pasos recto, luego...".
                string turnText = BuildTurnTextWithContext(ttype, userRelativeAngle, stepsToTurn,
                                                          wp, i, dirIn, dirOut);

                _events.Add(new NavigationInstructionEvent(
                    current, ttype, TriggerDist(ttype),
                    turnText,
                    i));

                if (_logInstructions)
                    Debug.Log($"[VoiceGuide] 📍 [v5.4] Instrucción en wp[{i}]: " +
                              $"tipo={ttype} | ángulo usuario={userRelativeAngle:F1}° | " +
                              $"deflexión ruta={routeDeflectionAngle:F1}° | " +
                              $"pasos={stepsToTurn} | pos={current:F2}");
            }

            _events.Add(new NavigationInstructionEvent(
                wp[count - 1], VoiceInstructionType.Arrived, _arrivalTriggerDist,
                string.IsNullOrEmpty(_destName)
                    ? "Has llegado a tu destino. ¡Bien hecho!"
                    : $"Has llegado a {_destName}. ¡Bien hecho!",
                count - 1));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ✅ v5.4 — CLASIFICACIÓN RELATIVA AL USUARIO (FIX 1)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ✅ v5.4b FIX 1 (corregido): Clasifica el giro requerido desde la perspectiva del usuario.
        ///
        /// PROBLEMA DEL FIX ANTERIOR:
        ///   Usar UserFwd para TODOS los waypoints era incorrecto. UserFwd solo es válido
        ///   para el giro inmediatamente siguiente. Para waypoints futuros, el usuario
        ///   llegará a ese punto viniendo por dirIn, así que la referencia correcta es dirIn.
        ///
        ///   EJEMPLO DEL BUG:
        ///     wp[1]: deflexión=0° (segmento recto), pero usuario mira -71° → clasifica TurnLeft.
        ///     wp[6]: deflexión=6° (casi recto), pero usuario mira +83° → clasifica TurnRight.
        ///     Ambos son falsos positivos — la ruta es recta, el usuario simplemente no mira
        ///     en la misma dirección que el segmento todavía.
        ///
        /// SOLUCIÓN CORRECTA:
        ///   - Para el giro INMEDIATO (el que se va a disparar pronto, nextIdx == cornerIdx):
        ///     usar UserFwd como referencia en FullAR. El usuario ya está llegando a ese punto
        ///     y su orientación actual determina qué es "izquierda" y "derecha".
        ///   - Para giros FUTUROS (cornerIdx > _nextIdx):
        ///     usar dirIn. Cuando el usuario llegue a ese punto, vendrá siguiendo dirIn,
        ///     así que dirIn es la orientación correcta de referencia.
        ///   - En NoAR: siempre usar dirIn (el agente físico se orienta automáticamente).
        ///
        /// El parámetro isImmediateTurn indica si este es el giro del waypoint actualmente
        /// más próximo al usuario (se pasa true solo para el waypoint en _nextIdx).
        ///
        /// RETORNA: (tipo de instrucción, ángulo relativo al usuario con signo)
        /// </summary>
        private (VoiceInstructionType type, float signedAngle) ClassifyTurnRelativeToUser(
            Vector3 dirIn, Vector3 dirOut, bool isImmediateTurn = false)
        {
            Vector3 reference;

            if (IsFullARMode && isImmediateTurn)
            {
                // Giro inmediato en FullAR: usar la orientación real de la cámara XR.
                // El usuario ya está llegando a este punto y su orientación actual
                // determina correctamente qué es "su izquierda" y "su derecha".
                Vector3 userFwd = UserFwd;
                userFwd.y = 0f;
                reference = userFwd.sqrMagnitude > 0.001f ? userFwd.normalized : dirIn;
            }
            else
            {
                // Giro futuro (o NoAR): usar dirIn.
                // El usuario llegará a este waypoint viniendo por dirIn,
                // así que dirIn es la referencia de orientación correcta.
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
        //  ✅ v5.4 — TEXTO DE GIRO CON CONTEXTO DE SEGMENTO RECTO PREVIO (FIX 3)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ✅ v5.4 FIX 3: Genera texto de instrucción con información del trecho recto previo.
        ///
        /// ANTES: "En 6 pasos, gira a la derecha."
        /// AHORA: "Camina 4 pasos recto, luego gira a la derecha." (si hay trecho recto previo)
        ///        o "Gira a la derecha ya." (si el giro es inmediato)
        ///
        /// LÓGICA:
        ///   1. Busca el segmento de ruta previo al punto de giro (wp[i-1] → wp[i]).
        ///   2. Si ese segmento tiene deflexión < _straightSegmentAngle respecto a la
        ///      dirección de entrada (es "recto"), calcula su longitud.
        ///   3. Si la longitud es ≥ _minMentionableStraightDist, añade el prefijo
        ///      "Camina {n} pasos recto," antes de la instrucción de giro.
        ///   4. Si no hay trecho recto previo significativo, usa la forma estándar
        ///      "En {stepsToTurn} pasos, gira...".
        ///
        /// NOTA sobre stepsToTurn:
        ///   stepsToTurn es la distancia acumulada TOTAL desde el inicio de la ruta
        ///   hasta el punto de giro. El prefijo de "pasos rectos" se calcula desde
        ///   la posición actual del usuario, no desde el inicio de la ruta.
        /// </summary>
        private string BuildTurnTextWithContext(
            VoiceInstructionType ttype,
            float signedAngle,
            int stepsToTurn,
            IReadOnlyList<Vector3> wp,
            int cornerIdx,
            Vector3 dirIn,
            Vector3 dirOut)
        {
            // Calcular longitud del segmento recto previo
            // (el segmento que va de wp[cornerIdx-1] a wp[cornerIdx])
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
                // Primer giro: el segmento previo va desde el inicio de la ruta
                Vector3 prev = wp[cornerIdx - 1];
                Vector3 curr = wp[cornerIdx];
                prevSegLen   = Vector3.Distance(
                    new Vector3(prev.x, 0, prev.z),
                    new Vector3(curr.x, 0, curr.z));
                prevIsStr    = true; // El primer segmento siempre es "recto" (es el inicio)
            }

            // Pasos en el segmento recto previo desde la posición actual del usuario
            float distFromUser = Vector3.Distance(
                new Vector3(EvalPos.x, 0, EvalPos.z),
                new Vector3(wp[cornerIdx].x, 0, wp[cornerIdx].z));
            int stepsFromUser = Mathf.Max(1, Mathf.RoundToInt(distFromUser / _stepLength));

            // Determinar label de dirección con ángulo preciso
            string turnLabel = TurnLabel(ttype, signedAngle);

            // ✅ Generar texto según disponibilidad de trecho recto previo
            if (prevIsStr && prevSegLen >= _minMentionableStraightDist && stepsFromUser > 2)
            {
                int straightSteps = Mathf.Max(1, Mathf.RoundToInt(
                    (distFromUser - (distFromUser - prevSegLen)) / _stepLength));

                // Calcular pasos solo del trecho recto (distancia hasta el punto de giro
                // menos la longitud estimada del propio giro)
                int approachSteps = Mathf.Max(1, Mathf.RoundToInt(distFromUser / _stepLength));

                // Formato: "Camina N pasos recto y luego [giro]"
                return $"Camina {approachSteps} pasos recto y luego {turnLabel}.";
            }

            // Sin trecho recto previo significativo
            if (stepsFromUser <= 3)
                return $"{TurnLabelImperative(ttype, signedAngle)} ahora.";

            return $"En {stepsFromUser} pasos, {turnLabel}.";
        }

        /// <summary>
        /// ✅ v5.4e: Instrucción de giro usando posición de reloj.
        ///
        /// Para personas ciegas, el sistema de reloj es la referencia más precisa
        /// y universalmente entendida. Elimina ambigüedad de "levemente" / "fuertemente".
        ///
        ///   signedAngle positivo = derecha → horas 1-5
        ///   signedAngle negativo = izquierda → horas 7-11
        ///   0° = 12 (frente), 180° = 6 (atrás)
        ///
        /// Forma nominal: "gira hasta las 2" — para uso en frases como "en 5 pasos, gira hasta las 2"
        /// Forma imperativa: "Gira hasta las 2" — para giros inmediatos
        /// UTurn siempre es explícito: "date la vuelta completamente, hacia las 6"
        /// </summary>
        private static string TurnLabel(VoiceInstructionType ttype, float signedAngle)
        {
            if (ttype == VoiceInstructionType.UTurn)
                return "date la vuelta completamente";

            int    clock    = ClockPosition(signedAngle);
            string clockStr = ClockText(clock);

            // Para giros muy pequeños (SlightRight/SlightLeft) aclarar que es leve
            if (ttype == VoiceInstructionType.SlightRight || ttype == VoiceInstructionType.SlightLeft)
                return $"gira levemente {clockStr}";

            return $"gira {clockStr}";
        }

        /// <summary>
        /// Forma imperativa de TurnLabel para giros inmediatos (≤ 3 pasos).
        /// </summary>
        private static string TurnLabelImperative(VoiceInstructionType ttype, float signedAngle)
        {
            if (ttype == VoiceInstructionType.UTurn)
                return "Date la vuelta completamente";

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

            _lastSpokenText = string.Empty;
            _lastSpokenTime = -999f;

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

        /// <summary>
        /// ✅ v5.3 FIX: Distancia real al destino usando proyección 3D.
        /// </summary>
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

        /// <summary>v5.3: Distancia 3D de un punto a un segmento.</summary>
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

        /// <summary>
        /// ✅ v5.4e: Convierte un ángulo con signo a posición de reloj.
        ///
        /// Sistema de reloj: referencia estándar para orientación de personas ciegas.
        ///   0°   = 12 (frente)
        ///   90°  = 3  (derecha)
        ///   180° = 6  (atrás)
        ///  -90°  = 9  (izquierda)
        ///  -71°  = 10 (izquierda adelante — caso sala abierta del log)
        ///
        /// Cada hora = 30°. Se redondea al entero más cercano.
        /// </summary>
        private static int ClockPosition(float signedAngle)
        {
            float a = signedAngle % 360f;
            if (a < 0f) a += 360f;
            int hour = Mathf.RoundToInt(a / 30f) % 12;
            return hour == 0 ? 12 : hour;
        }

        /// <summary>
        /// Texto de posición de reloj para TTS.
        /// "a las 12", "a la 1", "a las 2", etc.
        /// En español: "la 1" pero "las 2, las 3..."
        /// </summary>
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
        /// v5.4d FIX: AnnounceInitialOrientation usando deflexión entre segmentos
        /// consecutivos (igual que BuildInstructions), no ángulo contra UserFwd.
        ///
        /// PROBLEMA DE v5.4c:
        ///   Iteraba segmentos comparando su ángulo contra UserFwd.
        ///   Si el primer segmento ya era 71° respecto a UserFwd → recto=0m → "Gira a la izquierda".
        ///   Pero ese segmento ES el pasillo recto. El usuario debe caminar por él.
        ///   UserFwd no tiene relación con la dirección del pasillo.
        ///
        /// SOLUCIÓN CORRECTA (v5.4d):
        ///   Usar la misma lógica que BuildInstructions: detectar giros por DEFLEXIÓN
        ///   entre segmentos CONSECUTIVOS de la ruta (ángulo dirIn vs dirOut).
        ///   Esto es independiente de hacia dónde mira el usuario ahora mismo.
        ///
        ///   Fase 1: Acumular segmentos iniciales con deflexión < _straightSegmentAngle (15°).
        ///           Estos son el "tramo recto inicial" de la ruta NavMesh.
        ///   Fase 2: El primer segmento con deflexión >= _straightSegmentAngle es el giro real.
        ///           Su dirección se calcula relativa a UserFwd para decir "izquierda/derecha".
        ///   Fase 3: Generar mensaje combinando el tramo recto y el giro.
        ///
        /// EJEMPLO (logs actuales):
        ///   wp[0]→wp[1]: 8m recto (pasillo) — deflexión entre segmentos ≈ 0°  → acumula
        ///   wp[1]→wp[2]: giro 36° → primer giro real → "Camina 8 pasos recto, luego gira X"
        ///
        /// NOTA: Para el mensaje de inicio el "tramo recto" es el primer segmento de la ruta
        ///       (la dirección en que el usuario debe comenzar a caminar). El ángulo para
        ///       "izquierda/derecha" se calcula comparando ESA dirección inicial contra UserFwd,
        ///       para que la instrucción de orientación inicial sea correcta.
        /// </summary>
        private void AnnounceInitialOrientation(IReadOnlyList<Vector3> waypoints)
        {
            if (!IsFullARMode) return;
            if (waypoints == null || waypoints.Count < 2) return;

            float totalDist  = AccumDistAlongPath(waypoints, 0, waypoints.Count - 1);
            int   totalSteps = Mathf.Max(1, Mathf.RoundToInt(totalDist / _stepLength));

            // ── FASE 1+2: Acumular tramo recto por DEFLEXIÓN entre segmentos ─────
            // Igual que BuildInstructions: el giro se detecta cuando la ruta dobla,
            // no cuando cambia respecto a UserFwd.
            float straightDist     = 0f;
            int   firstTurnWpIdx   = -1;   // índice del waypoint donde ocurre el primer giro
            float firstTurnDeflect = 0f;   // ángulo de deflexión de la ruta en ese punto
            Vector3 routeFirstDir  = Vector3.zero; // dirección del primer segmento (para orientar al usuario)

            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                Vector3 seg = waypoints[i + 1] - waypoints[i];
                seg.y = 0f;
                if (seg.sqrMagnitude < 0.001f) continue;

                float segLen = new Vector3(seg.x, 0, seg.z).magnitude;

                // Guardar la dirección del primer segmento (la que el usuario debe seguir al inicio)
                if (i == 0)
                    routeFirstDir = seg.normalized;

                if (i == 0)
                {
                    // El primer segmento no tiene segmento previo → siempre es "recto"
                    straightDist += segLen;
                    continue;
                }

                // Calcular deflexión respecto al segmento anterior
                Vector3 prevSeg = waypoints[i] - waypoints[i - 1];
                prevSeg.y = 0f;
                if (prevSeg.sqrMagnitude < 0.001f) { straightDist += segLen; continue; }

                float deflection = Vector3.Angle(prevSeg.normalized, seg.normalized);

                if (deflection < _straightSegmentAngle)
                {
                    // Segmento recto (poca deflexión respecto al anterior) → acumular
                    straightDist += segLen;
                }
                else
                {
                    // Primer giro real detectado
                    firstTurnWpIdx   = i;
                    firstTurnDeflect = deflection;
                    break;
                }
            }

            // ── FASE 3: Generar mensaje ──────────────────────────────────────────
            // Para decir "izquierda" o "derecha", comparar la dirección del primer
            // segmento de la ruta contra UserFwd (orientación actual del usuario).
            // El usuario debe orientarse hacia routeFirstDir para empezar a caminar.
            float  initialAngle = routeFirstDir.sqrMagnitude > 0.001f
                ? SignedAngleXZ(UserFwd, routeFirstDir)
                : 0f;
            float  absInitial   = Mathf.Abs(initialAngle);
            string initialDir   = DirectionLabel(initialAngle);

            int    straightSteps = Mathf.Max(1, Mathf.RoundToInt(straightDist / _stepLength));
            string text;

            // ── Sistema de reloj: referencia estándar para personas ciegas ──────
            // 12 = frente, 3 = derecha, 6 = atrás, 9 = izquierda
            // -71° → 10, -90° → 9, +45° → 1:30 redondeado a 2, etc.
            int    clockHour = ClockPosition(initialAngle);
            string clockStr  = ClockText(clockHour);

            if (firstTurnWpIdx < 0)
            {
                // ── Sala abierta: ruta en línea recta al destino (sin giros de deflexión) ──
                // Para persona ciega: posición de reloj + pasos es la instrucción más precisa
                // y accionable en espacio abierto sin referencias físicas de pared.
                if (clockHour == 12)
                    text = $"El destino está {clockStr}. " +
                           $"Camina {totalSteps} pasos en línea recta.";
                else if (clockHour == 6)
                    text = $"El destino está {clockStr}, directamente detrás tuyo. " +
                           $"Date la vuelta completamente y camina {totalSteps} pasos.";
                else
                    text = $"El destino está {clockStr}. " +
                           $"Gira hasta tener el destino al frente y camina {totalSteps} pasos en línea recta.";
            }
            else if (straightDist >= _minMentionableStraightDist)
            {
                // ── Pasillo: tramo recto seguido de un giro real ──────────────────
                // Orientación inicial en reloj + instrucción de pasillo
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

                // Si el usuario ya está alineado con el pasillo (reloj 12), no hace falta orientación
                if (clockHour == 12)
                    text = $"Camina {straightSteps} pasos recto y luego {giroLabel}. " +
                           $"El destino está a {totalSteps} pasos en total.";
                else
                    text = $"El pasillo está {clockStr}. " +
                           $"Oriéntate en esa dirección y camina {straightSteps} pasos recto, " +
                           $"luego {giroLabel}. El destino está a {totalSteps} pasos en total.";
            }
            else
            {
                // ── Giro casi inmediato desde la posición actual ──────────────────
                if (clockHour == 6)
                    text = $"El destino está {clockStr}. Date la vuelta completamente y " +
                           $"camina {totalSteps} pasos.";
                else if (clockHour == 12)
                    text = $"El destino está {clockStr}. Camina {totalSteps} pasos.";
                else
                    text = $"El destino está {clockStr}. " +
                           $"Gira hasta tener el destino al frente y camina {totalSteps} pasos.";
            }

            Speak(VoiceInstructionType.StartNavigation, text, priority: 1);

            if (_logInstructions)
                Debug.Log($"[VoiceGuide] 🧭 [v5.4d] Orientación inicial: " +
                          $"recto={straightDist:F1}m ({straightSteps}p) | " +
                          $"primerGiroWp={firstTurnWpIdx} deflexión={firstTurnDeflect:F1}° | " +
                          $"ángulo inicial={initialAngle:F1}° ({initialDir}) | " +
                          $"total={totalDist:F1}m ({totalSteps}p).");
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

            // ✅ v5.4: Para instrucciones direccionales, recalcular con la
            // orientación actual del usuario en el momento de disparar el evento,
            // no la que tenía cuando se construyó la ruta.
            string text     = isDirectional ? RecalcTurnTextRelativeToUser(evt) : evt.InstructionText;
            int    priority = GetPriority(evt.Type);
            Speak(evt.Type, text, priority);
        }

        /// <summary>
        /// ✅ v5.4: Recalcula el texto del giro usando la orientación ACTUAL del usuario.
        ///
        /// PROBLEMA que resuelve:
        ///   El texto se construyó cuando se calculó la ruta. Si el usuario giró
        ///   desde entonces (lo cual es muy probable — la ruta puede esperar segundos
        ///   antes de que el evento se dispare), el texto puede ser incorrecto.
        ///
        ///   Ejemplo: Se calcula la ruta mirando al Norte. El evento dice "gira derecha"
        ///   (hacia el Este). El usuario camina y ahora mira al Este. Cuando llega al
        ///   punto de giro, el destino está "recto" no "a la derecha".
        ///
        /// SOLUCIÓN:
        ///   En el momento en que el evento se dispara (el usuario ya está cerca del
        ///   punto de giro), recalcular el ángulo relativo a UserFwd actual y generar
        ///   el texto actualizado.
        /// </summary>
        private string RecalcTurnTextRelativeToUser(NavigationInstructionEvent evt)
        {
            // Necesitamos la dirección de salida (el próximo segmento tras el punto de giro)
            // Buscamos el siguiente evento de posición para obtener la dirección
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

            // dirIn: del waypoint anterior al waypoint del evento
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

        private void Speak(VoiceInstructionType type, string text, int priority)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (text == _lastSpokenText && Time.time - _lastSpokenTime < _dedupWindow)
            {
                if (_logInstructions)
                    Debug.Log($"[VoiceGuide] 🔇 DEDUP suprimido [{type}]: \"{text}\"");
                return;
            }
            _lastSpokenText = text;
            _lastSpokenTime = Time.time;

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

                    // ✅ v5.4: Dibujar también la dirección UserFwd
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
                $"[VoiceGuide] v5.4 — IsGuiding={_isGuiding} | IsPreprocessing={_isPreprocessing}\n" +
                $"Destino='{_destName}' | Events={_events.Count} | NextIdx={_nextIdx}\n" +
                $"Modo={( IsFullARMode ? "FullAR" : "NoAR")} \n" +
                $"UserPos={UserPos:F2} | EvalPos={EvalPos:F2}\n" +
                $"UserFwd={UserFwd:F2} | UserSpeed={UserSpeed:F2}m/s\n" +
                $"RemainingDist={rem:F1}m (~{Mathf.RoundToInt(rem / _stepLength)} pasos)\n" +
                $"[E1] Stopped={_isStopped} StopAccum={_stopAccumTime:F1}s\n" +
                $"[E2] DeviationTimer={_deviationTimer:F1}s Fired={_deviationFired}\n" +
                $"[E3] ObstacleTimer={_obstacleTimer:F1}s Fired={_obstacleFired}\n" +
                $"[E7] MisalignTimer={_misalignTimer:F1}s | LastMisalignTime={_lastMisalignTime:F1}s\n" +
                $"[Dedup] LastText='{_lastSpokenText?.Substring(0, Mathf.Min(40, _lastSpokenText?.Length ?? 0))}' " +
                $"hace {Time.time - _lastSpokenTime:F1}s\n" +
                $"Path: valid={path?.IsValid} wp={path?.Waypoints.Count} len={path?.TotalLength:F1}m");
        }

        [ContextMenu("🧭 Test: Orientación actual hacia siguiente waypoint")]
        private void DebugCurrentOrientation()
        {
            if (!_isGuiding || _nextIdx >= _events.Count)
            {
                Debug.Log("[VoiceGuide] Sin guía activa o sin waypoints pendientes.");
                return;
            }

            Vector3 nextWpPos = _events[_nextIdx].WorldPosition;
            Vector3 toNext    = nextWpPos - UserPos;
            toNext.y = 0f;

            if (toNext.sqrMagnitude < 0.001f)
            {
                Debug.Log("[VoiceGuide] Ya estás sobre el siguiente waypoint.");
                return;
            }

            toNext.Normalize();
            float angle = SignedAngleXZ(UserFwd, toNext);
            string dir  = DirectionLabel(angle);
            float dist  = Vector3.Distance(UserPos, nextWpPos);

            var (ttype, signedAngle) = ClassifyTurnRelativeToUser(toNext, toNext, isImmediateTurn: true);

            Debug.Log($"[VoiceGuide] 🧭 [v5.4] Orientación actual:\n" +
                      $"  UserFwd={UserFwd:F2}\n" +
                      $"  Dirección al wp#{_nextIdx}={toNext:F2}\n" +
                      $"  Ángulo={angle:F1}° → \"{dir}\"\n" +
                      $"  Instrucción tipo={ttype} ángulo={signedAngle:F1}°\n" +
                      $"  Distancia={dist:F1}m | MisalignTimer={_misalignTimer:F1}s");
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
            var original  = path.Waypoints;
            var subdivided = SubdivideWaypointSegments(original);
            Debug.Log($"[VoiceGuide] 📐 Subdivisión: {original.Count} → {subdivided.Count} waypoints.");
            for (int i = 0; i < subdivided.Count; i++)
                Debug.Log($"  [{i}] {subdivided[i]:F2}");
        }
    }
}