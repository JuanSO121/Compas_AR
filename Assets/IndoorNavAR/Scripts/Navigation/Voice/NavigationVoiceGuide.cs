// File: NavigationVoiceGuide.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Navigation/Voice/
//
// ============================================================================
//  SISTEMA DE INSTRUCCIONES DE NAVEGACIÓN POR VOZ  — IndoorNavAR  v4.0
// ============================================================================
//
//  CAMBIO ARQUITECTÓNICO CENTRAL (v3.0 → v4.0):
//  ─────────────────────────────────────────────────────────────────────────
//
//  PROBLEMA (scena confirmada):
//    XR Origin (Mobile AR) → Main Camera
//      FullAR: ARCore mueve la cámara con el usuario físico real
//      No-AR:  AROriginAligner.FollowAgent() mueve la cámara con el NPC
//    VirtualAssistant: NPC guía que camina por NavMesh, va 2-4m adelante
//
//    v3.0 usaba transform.position del NPC (NavigationManager GO) para
//    todos los triggers de voz. En FullAR el NPC está adelante del usuario
//    → los "X pasos" y los triggers de giro eran incorrectos para usuarios ciegos.
//
//  SOLUCIÓN:
//    Todas las mediciones usan UserPositionBridge.UserPosition:
//      FullAR → XROrigin.Camera.transform.position = posición real del usuario
//      No-AR  → XROrigin.Camera.transform.position = posición del NPC (igual que antes)
//    El NPC (VirtualAssistant) sigue funcionando igual — ARGuideController no cambia.
//
//  ESCENARIOS DE ACCESIBILIDAD (usuarios ciegos / baja visión):
//  ─────────────────────────────────────────────────────────────────────────
//
//  [E1] PARADA: usuario quieto N segundos
//       → "Cuando estés listo, continúa." + recordatorio periódico con pasos restantes
//
//  [E2] DESVIACIÓN: usuario se aparta lateralmente de la ruta > umbral durante N segundos
//       → "Te has desviado. Busca al guía."
//       NavigationPathController recalculará → OnPathRecalculated → re-sync automático
//
//  [E3] OBSTÁCULO: quieto + sin reducir distancia al siguiente waypoint
//       → "Puede haber un obstáculo. Rodéalo hacia izquierda o derecha."
//
//  [E4] ORIENTACIÓN RELATIVA AL MOMENTO:
//       "Gira a tu izquierda" se calcula con UserForward ACTUAL en el disparo,
//       no con la orientación precomputada al generar la ruta hace N segundos.
//
//  [E5] PROGRESO: confirmación periódica mientras camina activamente
//       → "Vas bien. Quedan X pasos para llegar a Y."
//
//  [E6] SEPARACIÓN LARGA (Returning > N segundos):
//       → Resumen completo de ruta restante para reorientar al usuario ciego
//
//  DEPENDENCIAS:
//    UserPositionBridge.cs   — resuelve posición correcta según modo AR/NoAR
//    AROriginAligner.cs      — requiere añadir: public bool IsNoArMode => _noArMode;
//    NavigationPathController — CurrentPath (OptimizedPath), OnPathRecalculated
//    NavigationManager       — llama TriggerFromWaypoint() en NavigateToWaypoint()
//    EventBus                — NavigationStartedEvent, NavigationCompletedEvent, etc.

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
        StartNavigation,
        GoStraight,
        TurnLeft,
        TurnRight,
        SlightLeft,
        SlightRight,
        UTurn,
        StairsWarning,
        StairsClimb,
        StairsDescent,
        StairsComplete,
        Arrived,
        UserStopped,
        UserDeviated,
        ObstacleWarning,
        ProgressUpdate,
        ResumeAfterSeparation,
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
        [Tooltip("Expone UserPosition/UserForward según modo AR. Auto-detectado.")]
        [SerializeField] private UserPositionBridge       _userBridge;

        [Tooltip("NavigationPathController del NavigationManager GO. Auto-detectado.")]
        [SerializeField] private NavigationPathController _pathController;

        // ── Inspector — Triggers (medidos desde USUARIO) ──────────────────────
        //
        //  NOTA SOBRE VALORES:
        //    En FullAR el usuario está ~2-4m detrás del NPC.
        //    Los triggers deben ser suficientemente grandes para que el USUARIO
        //    reciba la instrucción cuando todavía le queden esos N pasos.
        //    Ejemplo: si el NPC ya pasó el giro pero el usuario está a 3m todavía,
        //    con _turnTriggerDistance = 5m el trigger se dispara cuando el usuario
        //    llega a 5m del waypoint → correcto.

        [Header("─── Triggers (desde posición del USUARIO, no del NPC) ──────")]
        [Tooltip("Distancia USUARIO→waypoint de giro para anunciar (m).")]
        [SerializeField] private float _turnTriggerDist      = 5.0f;

        [Tooltip("Distancia USUARIO→inicio de escalera para advertir (m).")]
        [SerializeField] private float _stairTriggerDist     = 6.0f;

        [Tooltip("Distancia USUARIO→destino final para anunciar llegada (m).")]
        [SerializeField] private float _arrivalTriggerDist   = 1.5f;

        [Tooltip("Distancia mínima USUARIO→próximo waypoint para lanzar recordatorio recto (m).")]
        [SerializeField] private float _straightReminderDist = 12.0f;

        // ── Inspector — Espera de ruta ────────────────────────────────────────

        [Header("─── Espera de Ruta ──────────────────────────────────────────")]
        [SerializeField] private float _pathWaitTimeout            = 3.0f;
        [SerializeField] private float _pathPollInterval           = 0.1f;
        [SerializeField] private float _destinationChangeThreshold = 0.5f;

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
        [SerializeField] private float _walkSpeedFlat   = 0.8f;  // m/s
        [SerializeField] private float _walkSpeedStairs = 0.4f;  // m/s
        [SerializeField] private float _stepLength      = 0.7f;  // m/paso

        // ── Inspector — Recordatorios ─────────────────────────────────────────

        [Header("─── Recordatorios ───────────────────────────────────────────")]
        [SerializeField] private float _straightReminderInterval  = 20f;
        [SerializeField] private float _progressInterval          = 45f;

        // ── Inspector — [E1] Parada ───────────────────────────────────────────

        [Header("─── [E1] Parada del usuario ─────────────────────────────────")]
        [Tooltip("Segundos sin moverse para detectar parada.")]
        [SerializeField] private float _stopTimeout          = 4.0f;
        [Tooltip("Movimiento mínimo (m) en ese tiempo para NO considerar parada.")]
        [SerializeField] private float _stopMinMovement      = 0.25f;
        [Tooltip("Intervalo de recordatorios mientras sigue parado (s).")]
        [SerializeField] private float _stopReminderInterval = 15.0f;

        // ── Inspector — [E2] Desviación ───────────────────────────────────────

        [Header("─── [E2] Desviación del camino ────────────────────────────")]
        [Tooltip("Distancia lateral (m) a la ruta para detectar desviación.")]
        [SerializeField] private float _deviationDist  = 2.0f;
        [Tooltip("Segundos con desviación antes de avisar.")]
        [SerializeField] private float _deviationDelay = 2.5f;

        // ── Inspector — [E3] Obstáculo ────────────────────────────────────────

        [Header("─── [E3] Obstáculo ─────────────────────────────────────────")]
        [Tooltip("Segundos parado sin avanzar al siguiente waypoint para detectar obstáculo.")]
        [SerializeField] private float _obstacleCheckTime = 6.0f;

        // ── Inspector — [E6] Separación larga ────────────────────────────────

        [Header("─── [E6] Separación larga (Returning) ───────────────────────")]
        [Tooltip("Segundos en Returning para dar resumen completo al retomar.")]
        [SerializeField] private float _longSeparationTime = 12.0f;

        // ── Inspector — Debug ─────────────────────────────────────────────────

        [Header("─── Debug ────────────────────────────────────────────────────")]
        [SerializeField] private bool _logInstructions  = true;
        [SerializeField] private bool _logPreprocessing = true;

        // ── Estado de sesión ──────────────────────────────────────────────────

        private readonly List<NavigationInstructionEvent> _events = new(32);
        private int     _nextIdx             = 0;
        private bool    _isGuiding           = false;
        private bool    _isPreprocessing     = false;
        private string  _destName            = string.Empty;
        private Vector3 _destPos             = new(float.PositiveInfinity, 0, 0);

        // ── Recordatorios ─────────────────────────────────────────────────────
        private float _lastStraightTime      = -999f;
        private int   _lastStraightIdx       = -1;
        private float _lastProgressTime      = -999f;

        // ── [E1] Parada ───────────────────────────────────────────────────────
        private Vector3 _stopRefPos;
        private float   _stopTimer           = 0f;
        private bool    _isStopped           = false;
        private float   _lastStopReminder    = -999f;

        // ── [E2] Desviación ───────────────────────────────────────────────────
        private float   _deviationTimer      = 0f;
        private bool    _deviationFired      = false;

        // ── [E3] Obstáculo ────────────────────────────────────────────────────
        private float   _obstacleTimer       = 0f;
        private float   _lastDistToNext      = float.MaxValue;
        private bool    _obstacleFired       = false;

        // ── [E6] Separación ───────────────────────────────────────────────────
        private float   _returningTimer      = 0f;

        private int       _currentFloor      = 0;
        private Coroutine _waitCoroutine      = null;

        // ── Shortcuts de posición del usuario ────────────────────────────────
        private Vector3 UserPos => _userBridge != null
            ? _userBridge.UserPosition
            : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);

        private Vector3 UserFwd => _userBridge != null
            ? _userBridge.UserForward
            : FlatFwd(Camera.main != null ? Camera.main.transform.forward : Vector3.forward);

        private float UserSpeed => _userBridge?.UserSpeed ?? 0f;

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
                _userBridge = FindFirstObjectByType<UserPositionBridge>();

            if (_userBridge == null)
                Debug.LogWarning("[VoiceGuide] ⚠️ UserPositionBridge no encontrado. " +
                                 "Usando Camera.main como fallback (FullAR asumido).");

            if (_pathController == null)
                _pathController = FindFirstObjectByType<NavigationPathController>();

            if (_pathController != null)
            {
                _pathController.OnPathRecalculated -= OnPathRecalculated;
                _pathController.OnPathRecalculated += OnPathRecalculated;
                if (_logPreprocessing)
                    Debug.Log($"[VoiceGuide] ✅ PathController: '{_pathController.gameObject.name}'");
            }
            else
                Debug.LogWarning("[VoiceGuide] ⚠️ NavigationPathController no encontrado.");
        }

        private void OnEnable()
        {
            var bus = EventBus.Instance; if (bus == null) return;
            bus.Subscribe<NavigationStartedEvent>  (OnNavStarted);
            bus.Subscribe<NavigationCompletedEvent>(OnNavCompleted);
            bus.Subscribe<NavigationCancelledEvent>(OnNavCancelled);
            bus.Subscribe<FloorTransitionEvent>    (OnFloorTransition);
        }

        private void OnDisable()
        {
            var bus = EventBus.Instance; if (bus == null) return;
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
        //  UPDATE — todas las evaluaciones usan UserPos (posición del USUARIO)
        // ─────────────────────────────────────────────────────────────────────

        private void Update()
        {
            if (!_isGuiding) return;
            EvaluateInstructions();  // triggers de giro/escalera/llegada
            EvaluateUserStop();      // [E1]
            EvaluateDeviation();     // [E2]
            EvaluateObstacle();      // [E3]
            EvaluateProgress();      // [E5]
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVALUACIÓN DE INSTRUCCIONES
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateInstructions()
        {
            if (_nextIdx >= _events.Count) return;

            Vector3 userPos = UserPos; // posición del USUARIO, no del NPC

            for (int i = _nextIdx; i < _events.Count; i++)
            {
                var evt = _events[i];
                if (evt.HasFired) { _nextIdx = i + 1; continue; }

                if (Vector3.Distance(userPos, evt.WorldPosition) <= evt.TriggerDistance)
                {
                    FireEvent(evt);
                    evt.HasFired = true;
                    _nextIdx = i + 1;
                }
                else break; // eventos ordenados por posición en la ruta
            }

            EvaluateStraightReminder();
        }

        private void EvaluateStraightReminder()
        {
            if (_nextIdx >= _events.Count || _nextIdx == _lastStraightIdx) return;
            var next = _events[_nextIdx];
            if (next.HasFired) return;

            float dist = Vector3.Distance(UserPos, next.WorldPosition);
            if (dist < _straightReminderDist) return;
            if (Time.time - _lastStraightTime < _straightReminderInterval) return;

            int steps = Mathf.Max(1, Mathf.RoundToInt(dist / _stepLength));
            Speak(VoiceInstructionType.GoStraight,
                $"Continúa recto. En aproximadamente {steps} pasos llegará la siguiente indicación.");
            _lastStraightTime = Time.time;
            _lastStraightIdx  = _nextIdx;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  [E1] USUARIO SE DETIENE
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateUserStop()
        {
            float moved = Vector3.Distance(UserPos, _stopRefPos);

            if (moved >= _stopMinMovement)
            {
                _stopRefPos  = UserPos;
                _stopTimer   = 0f;

                if (_isStopped)
                {
                    _isStopped     = false;
                    _obstacleFired = false;
                    _obstacleTimer = 0f;
                    _lastDistToNext = float.MaxValue;
                    if (_logPreprocessing)
                        Debug.Log("[VoiceGuide] ▶️ Usuario retomó movimiento.");
                }
                return;
            }

            _stopTimer += Time.deltaTime;

            if (_stopTimer >= _stopTimeout && !_isStopped)
            {
                _isStopped      = true;
                _lastStopReminder = Time.time;
                _obstacleTimer  = 0f;
                _lastDistToNext = DistUserToNextWp();

                int steps = Mathf.Max(1, Mathf.RoundToInt(_lastDistToNext / _stepLength));
                Speak(VoiceInstructionType.UserStopped,
                    $"Parece que te detuviste. " +
                    $"Cuando estés listo, continúa. La próxima indicación está en {steps} pasos.");
                if (_logPreprocessing)
                    Debug.Log($"[VoiceGuide] 🛑 Parada detectada. {steps} pasos al próximo WP.");
                return;
            }

            if (_isStopped && Time.time - _lastStopReminder >= _stopReminderInterval)
            {
                _lastStopReminder = Time.time;
                float rem   = RemainingDistFromUser();
                int   steps = Mathf.Max(1, Mathf.RoundToInt(rem / _stepLength));
                Speak(VoiceInstructionType.UserStopped,
                    $"Tómate tu tiempo. El destino está a {steps} pasos. Sigue al guía cuando estés listo.");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  [E2] DESVIACIÓN DEL CAMINO
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateDeviation()
        {
            if (_isStopped || UserSpeed < 0.2f) return;

            float lateral = LateralDeviationFromRoute();

            if (lateral > _deviationDist)
            {
                _deviationTimer += Time.deltaTime;
                if (_deviationTimer >= _deviationDelay && !_deviationFired)
                {
                    _deviationFired = true;
                    Speak(VoiceInstructionType.UserDeviated,
                        "Te has desviado del camino. Detente y busca al guía virtual. " +
                        "Camina hacia él para retomar la ruta.");
                    if (_logPreprocessing)
                        Debug.Log($"[VoiceGuide] ⚠️ Desviación: {lateral:F1}m del camino.");
                }
            }
            else
            {
                if (_deviationFired && lateral < _deviationDist * 0.5f)
                {
                    _deviationFired = false;
                    if (_logPreprocessing)
                        Debug.Log("[VoiceGuide] ✅ Usuario volvió al camino.");
                }
                _deviationTimer = 0f;
            }
        }

        private float LateralDeviationFromRoute()
        {
            var wp = _pathController?.CurrentPath?.Waypoints;
            if (wp == null || wp.Count < 2) return 0f;
            float min = float.MaxValue;
            for (int i = 0; i < wp.Count - 1; i++)
            {
                float d = SegDistXZ(UserPos, wp[i], wp[i + 1]);
                if (d < min) min = d;
            }
            return min < float.MaxValue ? min : 0f;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  [E3] POSIBLE OBSTÁCULO
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateObstacle()
        {
            if (!_isStopped || _obstacleFired) return;
            _obstacleTimer += Time.deltaTime;
            if (_obstacleTimer < _obstacleCheckTime) return;

            float current   = DistUserToNextWp();
            float reduction = _lastDistToNext - current;

            if (reduction < 0.4f)
            {
                _obstacleFired = true;
                Speak(VoiceInstructionType.ObstacleWarning,
                    "Puede haber un obstáculo en tu camino. " +
                    "Intenta rodearlo con cuidado hacia tu izquierda o tu derecha, " +
                    "manteniendo una mano en la pared si es posible.");
                if (_logPreprocessing)
                    Debug.Log("[VoiceGuide] 🚧 Posible obstáculo.");
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
                $"Vas bien. Quedan aproximadamente {steps} pasos para llegar a {_destName}.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVENTOS DEL BUS
        // ─────────────────────────────────────────────────────────────────────

        private void OnNavStarted(NavigationStartedEvent evt)
        {
            // Ignorar navegaciones internas del ARGuideController (sin WaypointId)
            if (string.IsNullOrEmpty(evt.DestinationWaypointId))
            {
                if (_logPreprocessing)
                    Debug.Log("[VoiceGuide] OnNavStarted ignorado — sin WaypointId (navegación interna).");
                return;
            }

            float delta = Vector3.Distance(evt.DestinationPosition, _destPos);
            if (delta < _destinationChangeThreshold && (_isGuiding || _isPreprocessing))
            {
                if (_logPreprocessing)
                    Debug.Log($"[VoiceGuide] OnNavStarted ignorado — misma sesión (Δ={delta:F2}m).");
                return;
            }

            StartSession(evt.DestinationWaypointId, evt.DestinationPosition);
        }

        private void OnNavCompleted(NavigationCompletedEvent _)
        {
            if (!_isGuiding) return;

            bool arrivedFired = _events.Exists(e => e.Type == VoiceInstructionType.Arrived && e.HasFired);
            if (!arrivedFired)
                Speak(VoiceInstructionType.Arrived,
                    string.IsNullOrEmpty(_destName)
                        ? "Has llegado a tu destino. ¡Bien hecho!"
                        : $"Has llegado a {_destName}. ¡Bien hecho!");

            ResetSession();
        }

        private void OnNavCancelled(NavigationCancelledEvent _) => ResetSession();

        private void OnFloorTransition(FloorTransitionEvent e)
        {
            _currentFloor  = e.ToLevel;
            _obstacleFired = false;
            _isStopped     = false;
            _stopTimer     = 0f;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  RECÁLCULO MID-ROUTE + [E6]
        // ─────────────────────────────────────────────────────────────────────

        private void OnPathRecalculated(OptimizedPath newPath)
        {
            if (!_isGuiding)
            {
                if (_logPreprocessing)
                    Debug.Log("[VoiceGuide] OnPathRecalculated ignorado — guía no activo.");
                return;
            }

            if (newPath == null || !newPath.IsValid || newPath.Waypoints.Count < 2)
                return;

            // Verificar que el path apunta al destino activo (no al usuario en Returning)
            Vector3 pathEnd   = newPath.Waypoints[newPath.Waypoints.Count - 1];
            float   destDelta = Vector3.Distance(pathEnd, _destPos);

            if (destDelta > 2.0f)
            {
                // Path hacia el usuario (Returning) → acumular tiempo de separación
                _returningTimer += Time.deltaTime;
                if (_logPreprocessing)
                    Debug.Log($"[VoiceGuide] OnPathRecalculated: path a destino diferente " +
                              $"(Δ={destDelta:F2}m). Returning={_returningTimer:F1}s.");
                return;
            }

            bool longSep    = _returningTimer >= _longSeparationTime;
            _returningTimer = 0f;

            if (_logPreprocessing)
                Debug.Log($"[VoiceGuide] 🔄 Re-sync. SeparaciónLarga={longSep}");

            if (_waitCoroutine != null)
            {
                StopCoroutine(_waitCoroutine);
                _waitCoroutine   = null;
                _isPreprocessing = false;
            }

            // Reset flags de accesibilidad al retomar ruta
            _obstacleFired  = false;
            _deviationFired = false;
            _isStopped      = false;
            _stopTimer      = 0f;
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
                // [E6]: resumen completo para reorientar al usuario ciego
                int secs = Mathf.RoundToInt(rem / _walkSpeedFlat);
                Speak(VoiceInstructionType.ResumeAfterSeparation,
                    $"El guía te encontró. Retomamos hacia {_destName}. " +
                    $"Quedan {steps} pasos, aproximadamente {secs} segundos. " +
                    $"Continúa siguiendo al guía.");
            }
            else
            {
                Speak(VoiceInstructionType.GoStraight,
                    $"Ruta actualizada. {steps} pasos restantes hasta {_destName}.");
            }

            if (_logPreprocessing)
                Debug.Log($"[VoiceGuide] ✅ Re-sync: {_events.Count} instrucciones, " +
                          $"~{steps} pasos restantes desde usuario.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  API PÚBLICA
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Llamar desde NavigationManager.NavigateToWaypoint() después de
        /// _navigationAgent.NavigateToWaypoint(waypoint).
        /// Solo inicia sesión nueva si el destino es diferente al activo.
        /// </summary>
        public void TriggerFromWaypoint(IndoorNavAR.Core.Data.WaypointData waypoint)
        {
            if (waypoint == null) return;

            float delta = Vector3.Distance(waypoint.Position, _destPos);
            if (delta < _destinationChangeThreshold && (_isGuiding || _isPreprocessing))
            {
                if (_logPreprocessing)
                    Debug.Log($"[VoiceGuide] TriggerFromWaypoint ignorado — misma sesión (Δ={delta:F2}m).");
                return;
            }

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

            _destName         = destName;
            _destPos          = destPosition;
            _isPreprocessing  = true;
            _stopRefPos       = UserPos;
            _stopTimer        = 0f;
            _returningTimer   = 0f;

            _waitCoroutine = StartCoroutine(WaitForPath());
        }

        /// <summary>
        /// Espera hasta que CurrentPath sea válido y apunte al destino correcto.
        /// Solo entonces genera instrucciones con mensaje de inicio.
        /// No habla hasta tener la ruta real (FIX #4 heredado).
        /// Usa CurrentPath del NavigationPathController, no NavMeshAgent (FIX #2/#3 heredado).
        /// </summary>
        private IEnumerator WaitForPath()
        {
            if (_logPreprocessing)
                Debug.Log($"[VoiceGuide] ⏳ Esperando ruta hacia '{_destName}'...");

            float elapsed = 0f;
            OptimizedPath path = null;

            while (elapsed < _pathWaitTimeout)
            {
                path = _pathController?.CurrentPath;

                if (path != null && path.IsValid && path.Waypoints.Count >= 2)
                {
                    Vector3 end   = path.Waypoints[path.Waypoints.Count - 1];
                    float   delta = Vector3.Distance(end, _destPos);

                    if (delta <= 1.5f)
                    {
                        if (_logPreprocessing)
                            Debug.Log($"[VoiceGuide] ✅ Ruta lista en {elapsed:F2}s — " +
                                      $"{path.Waypoints.Count} wp, {path.TotalLength:F1}m");
                        break;
                    }
                    // Path hacia el usuario en Returning → ignorar, seguir esperando
                    path = null;
                }

                yield return new WaitForSeconds(_pathPollInterval);
                elapsed += _pathPollInterval;
            }

            _waitCoroutine   = null;
            _isPreprocessing = false;

            if (path == null || !path.IsValid || path.Waypoints.Count < 2)
            {
                Debug.LogWarning($"[VoiceGuide] ⚠️ Timeout ({elapsed:F2}s). Inicio sin ruta.");
                Speak(VoiceInstructionType.StartNavigation,
                    $"Iniciando navegación a {_destName}. Sigue al guía hacia adelante.");
                yield break;
            }

            _events.Clear();
            _nextIdx          = 0;
            _lastStraightTime = Time.time;
            _lastStraightIdx  = -1;
            _lastProgressTime = Time.time;

            BuildInstructions(path.Waypoints, startMessage: true);
            _isGuiding = true;

            if (_logPreprocessing)
                Debug.Log($"[VoiceGuide] ✅ {_events.Count} instrucciones generadas.");

            if (_events.Count > 0)
            {
                var start = _events[0];
                FireEvent(start);
                start.HasFired = true;
                _nextIdx = 1;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CONSTRUCCIÓN DE INSTRUCCIONES
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Construye _events a partir de los waypoints del path.
        /// startMessage=true  → sesión nueva (incluye resumen inicial con pasos/segundos)
        /// startMessage=false → re-sync mid-route (omite mensaje de inicio)
        ///
        /// CLAVE: todos los "X pasos" se calculan desde UserPos (posición del USUARIO),
        /// no desde el inicio del path ni desde el NPC.
        /// </summary>
        private void BuildInstructions(IReadOnlyList<Vector3> wp, bool startMessage)
        {
            int count = wp.Count;
            if (count < 2) return;

            // Distancia total desde USUARIO (no desde inicio del path del NPC)
            float totalDist = RemainingDistFromUser(wp);
            bool  hasStairs = false;
            for (int i = 0; i < count - 1; i++)
                if (Mathf.Abs(wp[i + 1].y - wp[i].y) >= _stairHeightThreshold)
                    hasStairs = true;

            // ── Instrucción de inicio ────────────────────────────────────────
            if (startMessage)
            {
                int    steps   = Mathf.Max(1, Mathf.RoundToInt(totalDist / _stepLength));
                int    secs    = Mathf.RoundToInt(totalDist / _walkSpeedFlat);
                string stairs  = hasStairs
                    ? " La ruta incluye escaleras, te avisaré con tiempo para que reduzcas el paso."
                    : string.Empty;

                _events.Add(new NavigationInstructionEvent(
                    wp[0], VoiceInstructionType.StartNavigation, 0.5f,
                    $"Iniciando navegación a {_destName}. " +
                    $"Aproximadamente {steps} pasos, {secs} segundos.{stairs} " +
                    $"Sigue al guía hacia adelante.",
                    0));
            }

            // ── Segmentos intermedios ────────────────────────────────────────
            for (int i = 1; i < count - 1; i++)
            {
                Vector3 prev    = wp[i - 1];
                Vector3 current = wp[i];
                Vector3 next    = wp[i + 1];
                float   deltaY  = next.y - current.y;

                // Escaleras
                if (Mathf.Abs(deltaY) >= _stairHeightThreshold)
                {
                    float distUserToStair = Vector3.Distance(UserPos, current);
                    int   warnSteps       = Mathf.Max(1, Mathf.RoundToInt(distUserToStair / _stepLength));

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

                // Giros — precomputamos el tipo pero el texto FINAL se recalcula
                // en FireEvent con la orientación ACTUAL del usuario al disparar [E4]
                Vector3 dirIn  = current - prev;  dirIn.y  = 0f;
                Vector3 dirOut = next - current;   dirOut.y = 0f;
                if (dirIn.sqrMagnitude < 0.001f || dirOut.sqrMagnitude < 0.001f) continue;
                dirIn.Normalize(); dirOut.Normalize();

                float angle = Vector3.Angle(dirIn, dirOut);
                if (angle < _slightTurnAngle) continue;

                float cross = dirIn.x * dirOut.z - dirIn.z * dirOut.x;
                bool  left  = cross < 0f;

                // "X pasos" desde USUARIO al punto de giro
                float distUserToTurn = Vector3.Distance(UserPos, current);
                int   steps2         = Mathf.Max(1, Mathf.RoundToInt(distUserToTurn / _stepLength));
                var   ttype          = ClassifyTurn(angle, left);

                _events.Add(new NavigationInstructionEvent(
                    current, ttype, TriggerDist(ttype),
                    BuildTurnText(ttype, steps2), // texto provisional, se recalcula en FireEvent
                    i));

                if (_logPreprocessing)
                    Debug.Log($"[VoiceGuide] [wp {i}] {ttype} {angle:F1}° ~{steps2} pasos (desde usuario)");
            }

            // ── Llegada ──────────────────────────────────────────────────────
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

            _isGuiding      = false;
            _isPreprocessing = false;
            _destPos        = new(float.PositiveInfinity, 0, 0);
            _events.Clear();
            _nextIdx        = 0;

            _isStopped      = false;
            _stopTimer      = 0f;
            _deviationTimer = 0f;
            _deviationFired = false;
            _obstacleFired  = false;
            _obstacleTimer  = 0f;
            _returningTimer = 0f;

            if (!silent && _logPreprocessing)
                Debug.Log("[VoiceGuide] Sesión detenida.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS — DISTANCIA DESDE USUARIO
        // ─────────────────────────────────────────────────────────────────────

        private float DistUserToNextWp()
        {
            if (_nextIdx >= _events.Count) return 0f;
            return Vector3.Distance(UserPos, _events[_nextIdx].WorldPosition);
        }

        /// <summary>
        /// Distancia total restante medida desde la posición ACTUAL del USUARIO,
        /// proyectando al segmento de ruta más cercano y acumulando desde ahí.
        /// Independiente de dónde esté el NPC en ese momento.
        /// </summary>
        private float RemainingDistFromUser(IReadOnlyList<Vector3> waypoints = null)
        {
            var wp = waypoints ?? _pathController?.CurrentPath?.Waypoints;
            if (wp == null || wp.Count < 2) return 0f;

            Vector3 upos = UserPos;

            // Segmento más cercano al usuario
            int   closestSeg = 0;
            float minDist    = float.MaxValue;
            for (int i = 0; i < wp.Count - 1; i++)
            {
                float d = SegDistXZ(upos, wp[i], wp[i + 1]);
                if (d < minDist) { minDist = d; closestSeg = i; }
            }

            // Proyección del usuario sobre ese segmento
            Vector3 a  = wp[closestSeg];
            Vector3 b  = wp[closestSeg + 1];
            Vector3 ab = b - a;
            float   t  = ab.sqrMagnitude > 0.001f
                ? Mathf.Clamp01(Vector3.Dot(upos - a, ab) / ab.sqrMagnitude) : 0f;
            Vector3 proj = a + t * ab;

            // usuario→proyección + proyección→b + b→fin
            float rem = Vector3.Distance(upos, proj) + Vector3.Distance(proj, b);
            for (int i = closestSeg + 1; i < wp.Count - 1; i++)
                rem += Vector3.Distance(wp[i], wp[i + 1]);

            return rem;
        }

        private static float SegDistXZ(Vector3 pt, Vector3 a, Vector3 b)
        {
            var p   = new Vector2(pt.x, pt.z);
            var p1  = new Vector2(a.x,  a.z);
            var p2  = new Vector2(b.x,  b.z);
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
                evt.Type == VoiceInstructionType.TurnLeft   ||
                evt.Type == VoiceInstructionType.TurnRight  ||
                evt.Type == VoiceInstructionType.SlightLeft ||
                evt.Type == VoiceInstructionType.SlightRight;

            // [E4]: para giros, recalcular texto con orientación ACTUAL del usuario
            string text = isDirectional ? RecalcTurnText(evt) : evt.InstructionText;
            Speak(evt.Type, text);
        }

        /// <summary>
        /// [E4] Recalcula "gira a tu izquierda/derecha" con UserFwd ACTUAL en el disparo.
        /// Crítico para ciegos: "izquierda" es la izquierda del usuario AHORA,
        /// calculada desde XROrigin.Camera.forward, no desde el NPC ni precomputada.
        /// También recalcula los "X pasos" desde UserPos actual al punto de giro.
        /// </summary>
        private string RecalcTurnText(NavigationInstructionEvent evt)
        {
            int nextIdx = evt.CornerIndex + 1;
            if (nextIdx >= _events.Count) return evt.InstructionText;

            Vector3 toNext = _events[nextIdx].WorldPosition - evt.WorldPosition;
            toNext.y = 0f;
            if (toNext.sqrMagnitude < 0.001f) return evt.InstructionText;
            toNext.Normalize();

            // UserFwd = Camera.forward del USUARIO (XROrigin), no del NPC
            Vector3 fwd   = UserFwd;
            float   cross = fwd.x * toNext.z - fwd.z * toNext.x;
            float   dot   = fwd.x * toNext.x + fwd.z * toNext.z;
            float   angle = Mathf.Atan2(Mathf.Abs(cross), dot) * Mathf.Rad2Deg;
            bool    left  = cross < 0f;

            // Pasos desde UserPos ACTUAL al punto de giro
            float dist  = Vector3.Distance(UserPos, evt.WorldPosition);
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
        //  FLUTTER / EVENTBUS
        // ─────────────────────────────────────────────────────────────────────

        private void Speak(VoiceInstructionType type, string text)
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

            if (_logInstructions)
                Debug.Log($"[VoiceGuide] 🔊 [{type}] \"{text}\"");
        }

        private static Vector3 FlatFwd(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.001f ? v.normalized : Vector3.forward;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  GIZMOS
        // ─────────────────────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || !_isGuiding) return;

            // Posición del USUARIO (azul brillante) — distinta del NPC (amarillo)
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(UserPos, 0.25f);
            Gizmos.color = Color.blue;
            Gizmos.DrawLine(UserPos, UserPos + UserFwd * 0.7f);

            foreach (var evt in _events)
            {
                Gizmos.color = evt.HasFired
                    ? new Color(0.3f, 0.3f, 0.3f, 0.4f)
                    : GizmoColor(evt.Type);
                Gizmos.DrawWireSphere(evt.WorldPosition, evt.TriggerDistance);
                Gizmos.DrawSphere(evt.WorldPosition, 0.08f);

                if (!evt.HasFired)
                {
                    Gizmos.color = new Color(Gizmos.color.r, Gizmos.color.g, Gizmos.color.b, 0.2f);
                    Gizmos.DrawLine(UserPos, evt.WorldPosition); // desde USUARIO, no NPC
                }
            }
        }

        private static Color GizmoColor(VoiceInstructionType t) => t switch
        {
            VoiceInstructionType.TurnLeft          => Color.red,
            VoiceInstructionType.TurnRight         => Color.blue,
            VoiceInstructionType.SlightLeft        => new Color(1f, 0.5f, 0.5f),
            VoiceInstructionType.SlightRight       => new Color(0.5f, 0.5f, 1f),
            VoiceInstructionType.UTurn             => Color.magenta,
            VoiceInstructionType.StairsWarning     => Color.yellow,
            VoiceInstructionType.StairsClimb       => new Color(1f, 0.6f, 0f),
            VoiceInstructionType.StairsDescent     => new Color(0.8f, 0.4f, 0f),
            VoiceInstructionType.Arrived           => Color.green,
            VoiceInstructionType.UserStopped       => Color.cyan,
            VoiceInstructionType.UserDeviated      => new Color(1f, 0f, 0.5f),
            VoiceInstructionType.ObstacleWarning   => new Color(1f, 0.3f, 0f),
            _                                      => Color.white,
        };

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
                $"UserPos={UserPos:F2} | UserFwd={UserFwd:F2} | UserSpeed={UserSpeed:F2}m/s\n" +
                $"Modo={(_userBridge?.IsNoArMode == true ? "NoAR" : "FullAR")}\n" +
                $"RemainingDistFromUser={rem:F1}m (~{Mathf.RoundToInt(rem/_stepLength)} pasos)\n" +
                $"[E1] Stopped={_isStopped} StopTimer={_stopTimer:F1}s\n" +
                $"[E2] DeviationTimer={_deviationTimer:F1}s Fired={_deviationFired}\n" +
                $"[E3] ObstacleTimer={_obstacleTimer:F1}s Fired={_obstacleFired}\n" +
                $"[E6] ReturningTimer={_returningTimer:F1}s\n" +
                $"Path: valid={path?.IsValid} wp={path?.Waypoints.Count} len={path?.TotalLength:F1}m");
        }

        [ContextMenu("🔄 Test: Simular recálculo")]
        private void DebugResync()
        {
            var path = _pathController?.CurrentPath;
            if (path != null && path.IsValid) OnPathRecalculated(path);
            else Debug.LogWarning("[VoiceGuide] Sin path.");
        }

        [ContextMenu("🛑 Test: Simular parada")]
        private void DebugStopUser()
        {
            _stopTimer = _stopTimeout + 0.1f;
            _isStopped = false;
            EvaluateUserStop();
        }

        [ContextMenu("⚠️ Test: Simular desviación")]
        private void DebugDeviation()
        {
            _deviationTimer = _deviationDelay + 0.1f;
            _deviationFired = false;
            EvaluateDeviation();
        }

        [ContextMenu("🚧 Test: Simular obstáculo")]
        private void DebugObstacle()
        {
            _isStopped      = true;
            _obstacleTimer  = _obstacleCheckTime + 0.1f;
            _obstacleFired  = false;
            _lastDistToNext = float.MaxValue;
            EvaluateObstacle();
        }

        [ContextMenu("🛑 Detener guía")]
        private void DebugStop() => ResetSession();
    }
}