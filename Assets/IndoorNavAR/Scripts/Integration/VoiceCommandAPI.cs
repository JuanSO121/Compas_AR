// File: VoiceCommandAPI.cs
// ✅ v8.1 — FIX: Timing de load_session → listWaypoints
//
// ============================================================================
//  CAMBIOS v8 → v8.1
// ============================================================================
//
//  BUG CORREGIDO — Flutter recibía list_waypoints con 0 waypoints tras cargar sesión:
//
//    CAUSA:
//      LoadAsync() respondía Ok("load_session", ...) inmediatamente después de que
//      PersistenceManager.LoadSession() retornaba true. Sin embargo, LoadSession()
//      retorna true en cuanto la deserialización del JSON es exitosa — ANTES de que
//      LoadSessionData() → WaypointManager.LoadWaypoints() → Instantiate() hayan
//      terminado de crear los GameObjects en escena.
//
//      Flutter recibía "session_loaded" y llamaba listWaypoints() de inmediato.
//      En ese momento _waypointManager.WaypointCount era 0 porque los Instantiate()
//      aún no habían corrido en el hilo principal de Unity.
//
//    FIX v8.1 en LoadAsync():
//      1. await Task.Yield() — ceder al hilo principal para que los Instantiate()
//         pendientes en la cola del UnitySynchronizationContext se completen.
//      2. _waypointCacheDirty = true — forzar reconstrucción del cache aunque
//         WaypointsBatchLoadedEvent haya llegado antes de que los GOs estuvieran listos.
//      3. Log de verificación: loguear WaypointCount justo antes de responder
//         para confirmar que los waypoints están en memoria.
//
//  TODOS LOS CAMBIOS DE v8 SE CONSERVAN ÍNTEGRAMENTE.

using System;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using IndoorNavAR.Core;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Navigation;

namespace IndoorNavAR.Integration
{
    public class VoiceCommandAPI : MonoBehaviour
    {
        public static VoiceCommandAPI Instance { get; private set; }

        [Header("Dependencias (auto-detectadas si quedan vacías)")]
        [SerializeField] private WaypointManager    _waypointManager;
        [SerializeField] private NavigationManager  _navigationManager;
        [SerializeField] private PersistenceManager _persistenceManager;

        [Header("Canal de respuesta a Flutter")]
        [SerializeField] private string _flutterGameObject = "FlutterBridge";
        [SerializeField] private string _responseMethod    = "OnUnityResponse";

        [Header("─── Debug ───────────────────────────────────────────────────")]
        [SerializeField] private bool _logTTSSync  = true;
        [SerializeField] private bool _logTracking = true;

        [Header("─── Tracking State ──────────────────────────────────────────")]
        [Tooltip("Intervalo mínimo (s) entre mensajes de tracking_state para cambios " +
                 "de mismo estado. Cambios stable→unstable o viceversa siempre se envían.")]
        [SerializeField] private float _trackingNotifyInterval = 1.0f;

        private bool   _waypointCacheDirty = true;
        private string _waypointListCache  = "[]";

        // Dedup de GuideAnnouncementEvent
        private GuideAnnouncementType? _lastAnnouncementType = null;
        private string                 _lastAnnouncementMsg  = null;

        // Throttle de tracking state
        private float _lastTrackingNotifyTime = -999f;
        private bool  _lastTrackingStable     = true;

        // StringBuilder compartido para operaciones de un solo punto de entrada
        private readonly StringBuilder _sb = new StringBuilder(512);

        // ✅ v8 BUG C FIX: StringBuilder dedicado para GuideAnnouncement,
        // evita corrupción si se llama desde un evento encadenado síncronamente
        // mientras _sb está siendo usado por otro método.
        private readonly StringBuilder _announceSb = new StringBuilder(512);

        #region Lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            _waypointManager    ??= FindFirstObjectByType<WaypointManager>();
            _navigationManager  ??= FindFirstObjectByType<NavigationManager>();
            _persistenceManager ??= FindFirstObjectByType<PersistenceManager>();
        }

        private void OnEnable()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Subscribe<WaypointPlacedEvent>      (OnWaypointPlaced);
            bus.Subscribe<WaypointRemovedEvent>     (OnWaypointRemoved);
            bus.Subscribe<WaypointsBatchLoadedEvent>(OnWaypointsBatchLoaded);
            bus.Subscribe<NavigationArrivedEvent>   (OnNavigationArrived);
            bus.Subscribe<GuideAnnouncementEvent>   (OnGuideAnnouncement);
            bus.Subscribe<FloorTransitionEvent>     (OnFloorTransition);
        }

        private void OnDisable()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Unsubscribe<WaypointPlacedEvent>      (OnWaypointPlaced);
            bus.Unsubscribe<WaypointRemovedEvent>     (OnWaypointRemoved);
            bus.Unsubscribe<WaypointsBatchLoadedEvent>(OnWaypointsBatchLoaded);
            bus.Unsubscribe<NavigationArrivedEvent>   (OnNavigationArrived);
            bus.Unsubscribe<GuideAnnouncementEvent>   (OnGuideAnnouncement);
            bus.Unsubscribe<FloorTransitionEvent>     (OnFloorTransition);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        #endregion

        // =====================================================================
        //  TRACKING STATE
        // =====================================================================

        #region Tracking State

        /// <summary>
        /// Llamado por AROriginAligner cuando ARSession.state cambia.
        ///
        /// stateStr puede llegar en dos formatos:
        ///   - Simple:    "SessionTracking"
        ///   - Compuesto: "SessionInitializing|ExcessiveMotion"
        ///
        /// ✅ v8 BUG B FIX: Throttle unificado.
        ///   - Cambio de estabilidad (stable↔unstable): SIEMPRE pasa, sin throttle.
        ///     Un cambio a unstable es crítico para el usuario ciego — debe
        ///     recibir el aviso inmediatamente aunque haya pasado hace 0.1s.
        ///   - Mismo estado repetido: throttle de _trackingNotifyInterval segundos.
        ///     Evita saturar el canal cuando ARCore oscila (ej. SessionInitializing
        ///     se emite cada frame durante VIO fault).
        /// </summary>
        public void NotifyTrackingState(bool isStable, string stateStr)
        {
            bool stateChanged = isStable != _lastTrackingStable;
            bool throttled    = Time.unscaledTime - _lastTrackingNotifyTime < _trackingNotifyInterval;

            // ✅ v8 FIX B: cambio de estado siempre pasa; mismo estado respeta throttle
            if (!stateChanged && throttled)
                return;

            _lastTrackingStable     = isStable;
            _lastTrackingNotifyTime = Time.unscaledTime;

            // Desempaquetar formato "State|Reason"
            string state  = stateStr ?? "Unknown";
            string reason = "None";
            int pipeIdx = state.IndexOf('|');
            if (pipeIdx >= 0)
            {
                reason = state.Substring(pipeIdx + 1);
                state  = state.Substring(0, pipeIdx);
            }

            _sb.Clear();
            _sb.Append("{\"action\":\"tracking_state\",\"ok\":true,\"stable\":");
            _sb.Append(isStable ? "true" : "false");
            _sb.Append(",\"state\":\"");
            _sb.Append(EscapeJson(state));
            _sb.Append("\",\"reason\":\"");
            _sb.Append(EscapeJson(reason));
            _sb.Append("\"}");

            Reply(_sb.ToString());

            if (_logTracking)
                Debug.Log($"[VoiceAPI] 📡 TrackingState → Flutter: " +
                          $"stable={isStable} state={state} reason={reason}" +
                          (stateChanged ? " [CAMBIO]" : " [throttled repeat]"));
        }

        #endregion

        // =====================================================================
        //  TTS SYNC
        // =====================================================================

        public void OnTTSStatus(string json)
        {
            if (_logTTSSync)
                Debug.Log($"[VoiceAPI] 📡 OnTTSStatus: {json}");

            try
            {
                var data = JsonUtility.FromJson<TTSStatusPayload>(json);
                EventBus.Instance?.Publish(new TTSSpeakingEvent
                {
                    IsSpeaking = data.isSpeaking,
                    Priority   = data.priority,
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VoiceAPI] ⚠️ OnTTSStatus parse error: {ex.Message}");
            }
        }

        [System.Serializable]
        private class TTSStatusPayload
        {
            public bool isSpeaking;
            public int  priority;
        }

        // =====================================================================
        //  Event handlers — Waypoints
        // =====================================================================

        #region Event handlers — Waypoints

        private void OnWaypointPlaced(WaypointPlacedEvent _)            => _waypointCacheDirty = true;
        private void OnWaypointRemoved(WaypointRemovedEvent _)          => _waypointCacheDirty = true;
        private void OnWaypointsBatchLoaded(WaypointsBatchLoadedEvent _) => _waypointCacheDirty = true;

        #endregion

        // =====================================================================
        //  Event handler — Cambio de piso → reset dedup
        // =====================================================================

        #region Event handler — Cambio de piso

        private void OnFloorTransition(FloorTransitionEvent evt)
        {
            _lastAnnouncementType = null;
            _lastAnnouncementMsg  = null;
            Debug.Log($"[VoiceAPI] 🔄 FloorTransition {evt.FromLevel}→{evt.ToLevel}: " +
                      "dedup de GuideAnnouncement reseteado.");
        }

        #endregion

        // =====================================================================
        //  Event handler — Guía / VoiceGuide
        // =====================================================================

        #region Event handler — GuideAnnouncement

        /// <summary>
        /// Recibe instrucciones de NavigationVoiceGuide y las reenvía a Flutter.
        ///
        /// ✅ v8 BUG A FIX: AnnouncementType.ToString() para nombre del enum,
        ///    no el valor numérico que enviaba v7.5.
        ///
        /// ✅ v8 BUG C FIX: Usa _announceSb dedicado, no _sb compartido.
        ///
        /// ✅ v8 MEJORA D: Incluye "clock_hour" cuando el tipo es un giro.
        ///    Flutter puede mostrar un indicador visual del reloj.
        ///    Valor 0 = no aplica (no es un giro).
        ///
        /// ✅ v8 MEJORA E: Incluye "priority" numérico para que Flutter
        ///    decida si interrumpir el TTS actual o encolar el mensaje:
        ///      3 = urgente (obstáculo, UTurn) — interrumpir siempre
        ///      2 = navegación (giros, escaleras, llegada) — interrumpir si libre
        ///      1 = informativo (recto, parado, progreso) — encolar
        ///
        /// JSON enviado a Flutter:
        /// {
        ///   "action":     "guide_announcement",
        ///   "ok":         true,
        ///   "message":    "El destino está a las 10. Gira...",
        ///   "type":       "StartNavigation",      ← nombre del enum, no número
        ///   "floor":      0,
        ///   "priority":   2,
        ///   "clock_hour": 10                      ← 0 si no es giro
        /// }
        /// </summary>
        private void OnGuideAnnouncement(GuideAnnouncementEvent evt)
        {
            // Dedup: mismo tipo + mismo mensaje → ignorar
            if (_lastAnnouncementType == evt.AnnouncementType &&
                _lastAnnouncementMsg  == evt.Message)
                return;

            _lastAnnouncementType = evt.AnnouncementType;
            _lastAnnouncementMsg  = evt.Message;

            // ✅ v8 MEJORA E: Prioridad según tipo
            int priority = GetFlutterPriority(evt.AnnouncementType);

            // ✅ v8 MEJORA D: Extraer hora del reloj del mensaje si es un giro
            int clockHour = IsDirectionalType(evt.AnnouncementType)
                ? ExtractClockHourFromMessage(evt.Message)
                : 0;

            // ✅ v8 BUG C FIX: StringBuilder dedicado para este evento
            _announceSb.Clear();
            _announceSb.Append("{\"action\":\"guide_announcement\",\"ok\":true,\"message\":\"");
            _announceSb.Append(EscapeJson(evt.Message));
            _announceSb.Append("\",\"type\":\"");
            _announceSb.Append(evt.AnnouncementType.ToString()); // ← v8 BUG A FIX
            _announceSb.Append("\",\"floor\":");
            _announceSb.Append(evt.CurrentFloor);
            _announceSb.Append(",\"priority\":");
            _announceSb.Append(priority);
            _announceSb.Append(",\"clock_hour\":");
            _announceSb.Append(clockHour);
            _announceSb.Append('}');

            Reply(_announceSb.ToString());

            Debug.Log($"[VoiceAPI] 🔊 GuideAnnouncement → Flutter: " +
                      $"[{evt.AnnouncementType}] p={priority} clock={clockHour} \"{evt.Message}\"");
        }

        /// <summary>
        /// ✅ v8 MEJORA E: Mapa de prioridad por tipo de anuncio.
        ///
        /// Prioridad 3 — URGENTE: debe interrumpir TTS actual inmediatamente.
        ///   Obstáculo, UTurn (media vuelta inesperada).
        ///
        /// Prioridad 2 — NAVEGACIÓN: interrumpir si el TTS actual es de menor prioridad.
        ///   Giros, escaleras, llegada, inicio de navegación, desviación.
        ///
        /// Prioridad 1 — INFORMATIVO: encolar detrás del TTS actual.
        ///   Recto, usuario parado, progreso, resumen de ruta.
        /// </summary>
        private static int GetFlutterPriority(GuideAnnouncementType type) => type switch
        {
            GuideAnnouncementType.ObstacleWarning      => 3,
            GuideAnnouncementType.UTurn                => 3,

            GuideAnnouncementType.TurnLeft             => 2,
            GuideAnnouncementType.TurnRight            => 2,
            GuideAnnouncementType.SlightLeft           => 2,
            GuideAnnouncementType.SlightRight          => 2,
            GuideAnnouncementType.ApproachingStairs    => 2,
            GuideAnnouncementType.StartingClimb        => 2,
            GuideAnnouncementType.StartingDescent      => 2,
            GuideAnnouncementType.StairsComplete       => 2,
            GuideAnnouncementType.Arrived              => 2,
            GuideAnnouncementType.StartNavigation      => 2,
            GuideAnnouncementType.UserDeviated         => 2,
            GuideAnnouncementType.ResumeAfterSeparation => 2,

            GuideAnnouncementType.GoStraight           => 1,
            GuideAnnouncementType.WaitingForUser       => 1,
            GuideAnnouncementType.ProgressUpdate       => 1,
            GuideAnnouncementType.ResumeGuide          => 1,
            _                                          => 1,
        };

        /// <summary>
        /// Retorna true si el tipo de anuncio es un giro direccional
        /// y por tanto puede contener una posición de reloj en el mensaje.
        /// </summary>
        private static bool IsDirectionalType(GuideAnnouncementType type) => type switch
        {
            GuideAnnouncementType.TurnLeft        => true,
            GuideAnnouncementType.TurnRight       => true,
            GuideAnnouncementType.SlightLeft      => true,
            GuideAnnouncementType.SlightRight     => true,
            GuideAnnouncementType.UTurn           => false, // UTurn no tiene hora de reloj
            GuideAnnouncementType.StartNavigation => true,  // puede tener hora inicial
            _                                     => false,
        };

        /// <summary>
        /// ✅ v8 MEJORA D: Extrae la hora del reloj del mensaje de voz.
        ///
        /// NavigationVoiceGuide v5.4e genera mensajes con el patrón:
        ///   "...a las 10..." → 10
        ///   "...a la 1..."  → 1
        ///   "...a las 12..."→ 12
        ///
        /// Retorna 0 si no se encuentra ninguna hora válida (1-12).
        /// </summary>
        private static int ExtractClockHourFromMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return 0;

            int searchFrom = 0;
            while (searchFrom < message.Length - 5)
            {
                int found = message.IndexOf("a la", searchFrom, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                searchFrom = found + 4;

                // Saltar "s" opcional (para "las")
                int numStart = found + 4;
                if (numStart < message.Length && message[numStart] == 's') numStart++;

                // Saltar espacio
                if (numStart < message.Length && message[numStart] == ' ') numStart++;

                // Leer dígitos (1 o 2)
                int numEnd = numStart;
                while (numEnd < message.Length && char.IsDigit(message[numEnd])) numEnd++;

                if (numEnd > numStart)
                {
                    if (int.TryParse(message.Substring(numStart, numEnd - numStart), out int hour))
                    {
                        if (hour >= 1 && hour <= 12)
                            return hour;
                    }
                }
            }
            return 0;
        }

        #endregion

        // =====================================================================
        //  Navegación
        // =====================================================================

        #region Navegación

        public void NavigateTo(string waypointName)
        {
            if (_waypointManager == null || _navigationManager == null)
            { Reply(Err("navigate", "Managers no disponibles")); return; }

            var matches = _waypointManager.SearchWaypointsByName(waypointName);
            if (matches == null || matches.Count == 0)
            { Reply(Err("navigate", $"No encontré '{waypointName}'")); return; }

            WaypointData target = matches.Find(w =>
                w.WaypointName.Equals(waypointName, StringComparison.OrdinalIgnoreCase))
                ?? matches[0];

            bool ok = _navigationManager.NavigateToWaypoint(target);
            Reply(ok
                ? Ok("navigate", $"Navegando a {target.WaypointName}",
                     new Arg("destination", target.WaypointName))
                : Err("navigate", $"No se pudo iniciar ruta a {target.WaypointName}"));
        }

        public void StopNavigation()
        {
            _navigationManager?.StopNavigation();
            Reply(Ok("stop_navigation", "Navegación detenida"));
        }

        public void GetNavigationStatus()
        {
            var agent = _navigationManager?.Agent;
            if (agent == null)
            { Reply(Err("nav_status", "NavigationAgent no disponible")); return; }

            Reply(Ok("nav_status", "ok",
                new Arg("is_navigating",  agent.IsNavigating.ToString()),
                new Arg("remaining_m",    agent.RemainingDistance.ToString("F1")),
                new Arg("progress_pct",   (agent.ProgressPercent * 100f).ToString("F0")),
                new Arg("current_level",  agent.CurrentLevel.ToString()),
                new Arg("destination",    agent.LastDestination.ToString())
            ));
        }

        #endregion

        // =====================================================================
        //  Waypoints
        // =====================================================================

        #region Waypoints

        public void ListWaypoints()
        {
            if (_waypointManager == null)
            { Reply(Err("list_waypoints", "WaypointManager no disponible")); return; }

            // ✅ v8.1 DEBUG: verificar cuántos waypoints hay en memoria al momento
            // de la llamada. Si este log muestra 0 justo después de load_session,
            // confirma el bug de timing. Eliminar en producción si se desea.
            Debug.Log($"[VoiceAPI] ListWaypoints — WaypointCount={_waypointManager.WaypointCount} | dirty={_waypointCacheDirty}");

            if (_waypointCacheDirty) RebuildWaypointCache();

            _sb.Clear();
            _sb.Append("{\"action\":\"list_waypoints\",\"ok\":true,\"count\":");
            _sb.Append(_waypointManager.WaypointCount);
            _sb.Append(",\"waypoints\":");
            _sb.Append(_waypointListCache);
            _sb.Append('}');
            Reply(_sb.ToString());
        }

        public void CreateWaypointAtAgent(string name)
        {
            if (_waypointManager == null || _navigationManager == null)
            { Reply(Err("create_waypoint", "Managers no disponibles")); return; }

            Vector3 pos = _navigationManager.Agent != null
                ? _navigationManager.Agent.transform.position + Vector3.up * 0.05f
                : Vector3.zero;

            var wp = _waypointManager.CreateWaypoint(pos, Quaternion.identity);
            if (wp == null)
            { Reply(Err("create_waypoint", "Límite de waypoints alcanzado")); return; }

            if (!string.IsNullOrWhiteSpace(name)) wp.WaypointName = name;

            Reply(Ok("create_waypoint", $"Baliza '{wp.WaypointName}' creada",
                new Arg("id",   wp.WaypointId),
                new Arg("name", wp.WaypointName),
                new Arg("x",    SafeFloat(wp.Position.x)),
                new Arg("y",    SafeFloat(wp.Position.y)),
                new Arg("z",    SafeFloat(wp.Position.z))
            ));
        }

        public void RemoveWaypoint(string waypointName)
        {
            if (_waypointManager == null)
            { Reply(Err("remove_waypoint", "WaypointManager no disponible")); return; }

            var matches = _waypointManager.SearchWaypointsByName(waypointName);
            if (matches == null || matches.Count == 0)
            { Reply(Err("remove_waypoint", $"No encontré '{waypointName}'")); return; }

            WaypointData target = matches.Find(w =>
                w.WaypointName.Equals(waypointName, StringComparison.OrdinalIgnoreCase))
                ?? matches[0];

            bool ok = _waypointManager.RemoveWaypoint(target.WaypointId);
            Reply(ok
                ? Ok("remove_waypoint", $"Baliza '{target.WaypointName}' eliminada")
                : Err("remove_waypoint", "No se pudo eliminar la baliza"));
        }

        public void ClearWaypoints()
        {
            _waypointManager?.ClearAllWaypoints();
            Reply(Ok("clear_waypoints", "Todas las balizas eliminadas"));
        }

        #endregion

        // =====================================================================
        //  Sesión
        // =====================================================================

        #region Sesión

        public void SaveSession() => _ = SaveAsync();
        public void LoadSession() => _ = LoadAsync();

        private async System.Threading.Tasks.Task SaveAsync()
        {
            if (_persistenceManager == null)
            { Reply(Err("save_session", "PersistenceManager no disponible")); return; }
            bool ok = await _persistenceManager.SaveSession();
            Reply(ok
                ? Ok("save_session", "Sesión guardada")
                : Err("save_session", "Error al guardar"));
        }

        /// <summary>
        /// ✅ v8.1 FIX — Timing entre load_session y list_waypoints.
        ///
        /// PROBLEMA:
        ///   PersistenceManager.LoadSession() retorna true en cuanto termina la
        ///   deserialización del JSON y antes de que LoadSessionData() →
        ///   WaypointManager.LoadWaypoints() → Instantiate() hayan completado
        ///   la creación de GameObjects en el hilo principal.
        ///
        ///   Flutter recibía "session_loaded" y llamaba list_waypoints de inmediato,
        ///   encontrando WaypointCount = 0 porque los Instantiate() aún no corrían.
        ///
        /// FIX:
        ///   1. await Task.Yield() — cede el control al UnitySynchronizationContext
        ///      para que los Instantiate() pendientes se procesen antes de responder.
        ///   2. _waypointCacheDirty = true — fuerza reconstrucción del cache aunque
        ///      WaypointsBatchLoadedEvent haya llegado antes de que los GOs existieran.
        ///   3. Log de verificación con WaypointCount final antes de enviar a Flutter.
        /// </summary>
        private async System.Threading.Tasks.Task LoadAsync()
        {
            if (_persistenceManager == null)
            { Reply(Err("load_session", "PersistenceManager no disponible")); return; }

            bool ok = await _persistenceManager.LoadSession();

            if (ok)
            {
                // ✅ v8.1 FIX: Ceder al hilo principal para que los Instantiate()
                // de WaypointManager.LoadWaypoints() se completen antes de responder
                // a Flutter. Sin esto, Flutter recibe "session_loaded" y llama
                // list_waypoints antes de que los waypoints existan en memoria.
                await System.Threading.Tasks.Task.Yield();

                // ✅ v8.1 FIX: Forzar reconstrucción del cache aunque
                // WaypointsBatchLoadedEvent ya haya llegado antes.
                _waypointCacheDirty = true;

                Debug.Log($"[VoiceAPI] ✅ LoadAsync completo — " +
                          $"WaypointCount={_waypointManager?.WaypointCount ?? -1} " +
                          $"(listo para list_waypoints de Flutter)");
            }

            Reply(ok
                ? Ok("load_session", "Sesión cargada")
                : Err("load_session", "Error al cargar"));
        }

        #endregion

        // =====================================================================
        //  Evento llegada
        // =====================================================================

        #region Evento llegada

        private void OnNavigationArrived(NavigationArrivedEvent evt)
        {
            Reply(Ok("navigation_arrived",
                     string.IsNullOrEmpty(evt.WaypointName)
                         ? "Has llegado a tu destino"
                         : $"Has llegado a {evt.WaypointName}",
                     new Arg("waypoint_name", evt.WaypointName ?? ""),
                     new Arg("x", SafeFloat(evt.Position.x)),
                     new Arg("y", SafeFloat(evt.Position.y)),
                     new Arg("z", SafeFloat(evt.Position.z))
            ));
        }

        #endregion

        // =====================================================================
        //  Cache de waypoints
        // =====================================================================

        #region Cache de waypoints

        private void RebuildWaypointCache()
        {
            var list = _waypointManager.Waypoints;
            if (list == null || list.Count == 0)
            {
                _waypointListCache  = "[]";
                _waypointCacheDirty = false;
                return;
            }

            _sb.Clear();
            _sb.Append('[');
            bool first = true;
            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (w == null) continue;
                if (!first) _sb.Append(',');
                first = false;

                _sb.Append("{\"id\":\"");       _sb.Append(w.WaypointId);
                _sb.Append("\",\"name\":\"");   _sb.Append(EscapeJson(w.WaypointName));
                _sb.Append("\",\"type\":\"");   _sb.Append(w.Type);
                _sb.Append("\",\"navigable\":"); _sb.Append(w.IsNavigable ? "true" : "false");
                _sb.Append(",\"pos\":{\"x\":"); _sb.Append(SafeFloat(w.Position.x));
                _sb.Append(",\"y\":");          _sb.Append(SafeFloat(w.Position.y));
                _sb.Append(",\"z\":");          _sb.Append(SafeFloat(w.Position.z));
                _sb.Append("}}");
            }
            _sb.Append(']');

            _waypointListCache  = _sb.ToString();
            _waypointCacheDirty = false;

            Debug.Log($"[VoiceAPI] RebuildCache OK: {list.Count} waypoints.");
        }

        #endregion

        // =====================================================================
        //  Envío a Flutter
        // =====================================================================

        #region Envío a Flutter

        private void Reply(string json)
        {
            SendUnityMessageToFlutter(_flutterGameObject, _responseMethod, json);
            Debug.Log($"[VoiceAPI→Flutter] {json}");
        }

        private static void SendUnityMessageToFlutter(string go, string method, string msg)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJavaClass cls = null;
            try
            {
                cls = new AndroidJavaClass(
                    "com.xraph.plugin.flutter_unity_widget.UnityPlayerUtils");
                cls.CallStatic("onUnityMessage", msg);
                return;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VoiceAPI] onUnityMessage falló: {ex.Message}");
            }
            finally { cls?.Dispose(); cls = null; }

            AndroidJavaClass cls2 = null;
            try
            {
                cls2 = new AndroidJavaClass(
                    "com.xraph.plugin.flutter_unity_widget.UnityPlayerUtils");
                cls2.CallStatic("sendMessageToFlutter", go, method, msg);
                return;
            }
            catch (Exception ex2)
            {
                Debug.LogWarning($"[VoiceAPI] sendMessageToFlutter falló: {ex2.Message}");
            }
            finally { cls2?.Dispose(); cls2 = null; }

            AndroidJavaClass cls3 = null;
            try
            {
                cls3 = new AndroidJavaClass(
                    "com.xraph.plugin.flutter_unity_widget.UnityUtils");
                cls3.CallStatic("onUnityMessage", msg);
            }
            catch (Exception ex3)
            {
                Debug.LogError($"[VoiceAPI] ❌ Todos los métodos fallaron: {ex3.Message}");
            }
            finally { cls3?.Dispose(); }
#else
            Debug.Log($"[VoiceAPI][EDITOR] {go}.{method}({msg})");
#endif
        }

        #endregion

        // =====================================================================
        //  Helpers JSON
        // =====================================================================

        #region Helpers JSON

        private readonly struct Arg
        {
            public readonly string Key, Val;
            public Arg(string k, string v) { Key = k; Val = v; }
        }

        private string Ok(string action, string message, params Arg[] extra)
        {
            _sb.Clear();
            _sb.Append("{\"action\":\""); _sb.Append(action);
            _sb.Append("\",\"ok\":true,\"message\":\""); _sb.Append(EscapeJson(message));
            _sb.Append('"');
            foreach (var a in extra)
            {
                _sb.Append(",\""); _sb.Append(a.Key);
                _sb.Append("\":\""); _sb.Append(EscapeJson(a.Val));
                _sb.Append('"');
            }
            _sb.Append('}');
            return _sb.ToString();
        }

        private static string Err(string action, string message) =>
            $"{{\"action\":\"{action}\",\"ok\":false,\"message\":\"{EscapeJson(message)}\"}}";

        private static string EscapeJson(string s) =>
            s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") ?? "";

        private static string SafeFloat(float v) =>
            float.IsNaN(v) || float.IsInfinity(v) ? "0.00" : v.ToString("F2");

        #endregion

        // =====================================================================
        //  ContextMenu debug
        // =====================================================================

        #region ContextMenu debug

        [ContextMenu("Test: ListWaypoints")]
        private void DbgList() => ListWaypoints();

        [ContextMenu("Test: NavStatus")]
        private void DbgStatus() => GetNavigationStatus();

        [ContextMenu("Test: Rebuild Cache")]
        private void DbgRebuildCache()
        {
            _waypointCacheDirty = true;
            RebuildWaypointCache();
            Debug.Log($"[VoiceAPI] Cache: {_waypointListCache}");
        }

        [ContextMenu("Test: GuideAnnouncement (ApproachingStairs)")]
        private void DbgGuideAnnouncement()
        {
            EventBus.Instance?.Publish(new GuideAnnouncementEvent
            {
                AnnouncementType = GuideAnnouncementType.ApproachingStairs,
                Message          = "En 6 pasos hay escaleras. Empieza a reducir el paso.",
                CurrentFloor     = 0
            });
        }

        [ContextMenu("Test: GuideAnnouncement (TurnLeft — a las 10)")]
        private void DbgGuideAnnouncementTurn()
        {
            EventBus.Instance?.Publish(new GuideAnnouncementEvent
            {
                AnnouncementType = GuideAnnouncementType.TurnLeft,
                Message          = "En 5 pasos, gira a las 10.",
                CurrentFloor     = 0
            });
        }

        [ContextMenu("Test: GuideAnnouncement (StartNavigation — sala abierta)")]
        private void DbgGuideAnnouncementStart()
        {
            EventBus.Instance?.Publish(new GuideAnnouncementEvent
            {
                AnnouncementType = GuideAnnouncementType.StartNavigation,
                Message          = "El destino está a las 10. Gira hasta tener el destino al frente y camina 24 pasos en línea recta.",
                CurrentFloor     = 0
            });
        }

        [ContextMenu("Test: Simular FloorTransition (0→1)")]
        private void DbgFloorTransition()
        {
            EventBus.Instance?.Publish(new FloorTransitionEvent
            {
                FromLevel     = 0,
                ToLevel       = 1,
                AgentPosition = Vector3.zero
            });
            Debug.Log("[VoiceAPI] FloorTransition simulado — dedup reseteado.");
        }

        [ContextMenu("Test: TTS Start (priority 2)")]
        private void DbgTTSStartHigh()   => OnTTSStatus("{\"isSpeaking\":true,\"priority\":2}");

        [ContextMenu("Test: TTS Start (priority 3)")]
        private void DbgTTSStartUrgent() => OnTTSStatus("{\"isSpeaking\":true,\"priority\":3}");

        [ContextMenu("Test: TTS End")]
        private void DbgTTSEnd()         => OnTTSStatus("{\"isSpeaking\":false,\"priority\":0}");

        [ContextMenu("Test: Tracking estable")]
        private void DbgTrackingStable()
            => NotifyTrackingState(true, "SessionTracking");

        [ContextMenu("Test: Tracking perdido (ExcessiveMotion)")]
        private void DbgTrackingLost()
            => NotifyTrackingState(false, "SessionInitializing|ExcessiveMotion");

        [ContextMenu("Test: Tracking perdido (InsufficientFeatures)")]
        private void DbgTrackingLostFeatures()
            => NotifyTrackingState(false, "SessionInitializing|InsufficientFeatures");

        [ContextMenu("Test: Tracking perdido (InsufficientLight)")]
        private void DbgTrackingLostLight()
            => NotifyTrackingState(false, "SessionInitializing|InsufficientLight");

        [ContextMenu("Test: ExtractClockHour — verificar parser")]
        private void DbgExtractClock()
        {
            string[] tests = {
                "El destino está a las 10. Gira...",
                "En 5 pasos, gira a las 3.",
                "Gira a la 1 ahora.",
                "Camina recto.",
                "Gira a las 12.",
                "El destino está a las 9."
            };
            foreach (var t in tests)
                Debug.Log($"[VoiceAPI] ExtractClock: \"{t}\" → hora={ExtractClockHourFromMessage(t)}");
        }

        [ContextMenu("ℹ️ Estado dedup")]
        private void DbgDedupState()
        {
            Debug.Log($"[VoiceAPI] Dedup actual:\n" +
                      $"  LastType:       {_lastAnnouncementType?.ToString() ?? "null"}\n" +
                      $"  LastMsg:        '{_lastAnnouncementMsg ?? "null"}'\n" +
                      $"  TrackingStable: {_lastTrackingStable}\n" +
                      $"  LastNotifyTime: {_lastTrackingNotifyTime:F1}s");
        }

        #endregion
    }
}