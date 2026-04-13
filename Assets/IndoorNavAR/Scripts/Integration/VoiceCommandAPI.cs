// File: VoiceCommandAPI.cs
// ✅ v8.7 — Añade ReplyPublic() para que FlutterUnityBridge.NotifySceneReady()
//           pueda enviar scene_ready sin romper encapsulación.
//
// ════════════════════════════════════════════════════════════════════════════
// CAMBIOS v8.6 → v8.7
// ════════════════════════════════════════════════════════════════════════════
//
//  ÚNICO CAMBIO: nuevo método público ReplyPublic(string json).
//
//  CONTEXTO:
//    FlutterUnityBridge.NotifySceneReady() (v4.3) necesita enviar el mensaje
//    { "action": "scene_ready", "ok": true } a Flutter. Reply() es private,
//    por lo que se expone una versión pública con el mismo comportamiento.
//    Igual que SendSegmentationRatio(), encapsula la llamada sin exponer
//    la implementación interna de SendUnityMessageToFlutter().
//
//  TODO LO DEMÁS ES IDÉNTICO A v8.6.

using System;
using System.Text;
using UnityEngine;
using IndoorNavAR.Core;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Navigation;
using IndoorNavAR.Navigation.Voice;

namespace IndoorNavAR.Integration
{
    public class VoiceCommandAPI : MonoBehaviour
    {
        public static VoiceCommandAPI Instance { get; private set; }

        [Header("Dependencias (auto-detectadas si quedan vacías)")]
        [SerializeField] private WaypointManager _waypointManager;
        [SerializeField] private NavigationManager _navigationManager;
        [SerializeField] private PersistenceManager _persistenceManager;

        [Header("Canal de respuesta a Flutter")]
        [SerializeField] private string _flutterGameObject = "FlutterBridge";
        [SerializeField] private string _responseMethod = "OnUnityResponse";

        [Header("─── Debug ───────────────────────────────────────────────────")]
        [SerializeField] private bool _logTTSSync = true;
        [SerializeField] private bool _logTracking = true;

        [Header("─── Tracking State ──────────────────────────────────────────")]
        [Tooltip("Intervalo mínimo (s) entre mensajes de tracking_state para cambios " +
                 "de mismo estado. Cambios stable→unstable o viceversa siempre se envían.")]
        [SerializeField] private float _trackingNotifyInterval = 1.0f;

        [Header("─── TTS Speak throttle (v8.3) ─────────────────────────────")]
        [Tooltip("Tiempo mínimo (s) entre llamadas a SpeakArbitraryText desde Flutter.")]
        [SerializeField] private float _ttsRequestThrottle = 3.0f;

        [Header("─── Frame sender (v8.5) ──────────────────────────────────")]
        [Tooltip("Log de cada frame enviado. Desactivar en producción — " +
                 "a 10 fps satura la consola.")]
        [SerializeField] private bool _logFrames = false;

        private bool _waypointCacheDirty = true;
        private string _waypointListCache = "[]";

        private float _lastTrackingNotifyTime = -999f;
        private bool _lastTrackingStable = true;
        private float _lastArbitraryTTSTime = -999f;

        private readonly StringBuilder _sb = new StringBuilder(512);
        private readonly StringBuilder _ttsSb = new StringBuilder(256);

        #region Lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            _waypointManager ??= FindFirstObjectByType<WaypointManager>();
            _navigationManager ??= FindFirstObjectByType<NavigationManager>();
            _persistenceManager ??= FindFirstObjectByType<PersistenceManager>();
        }

        private void OnEnable()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Subscribe<WaypointPlacedEvent>(OnWaypointPlaced);
            bus.Subscribe<WaypointRemovedEvent>(OnWaypointRemoved);
            bus.Subscribe<WaypointsBatchLoadedEvent>(OnWaypointsBatchLoaded);
            bus.Subscribe<NavigationArrivedEvent>(OnNavigationArrived);
            bus.Subscribe<FloorTransitionEvent>(OnFloorTransition);
            bus.Subscribe<TTSRequestEvent>(OnTTSRequest);
            bus.Subscribe<GuideAnnouncementEvent>(OnGuideAnnouncement);
        }

        private void OnDisable()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Unsubscribe<WaypointPlacedEvent>(OnWaypointPlaced);
            bus.Unsubscribe<WaypointRemovedEvent>(OnWaypointRemoved);
            bus.Unsubscribe<WaypointsBatchLoadedEvent>(OnWaypointsBatchLoaded);
            bus.Unsubscribe<NavigationArrivedEvent>(OnNavigationArrived);
            bus.Unsubscribe<FloorTransitionEvent>(OnFloorTransition);
            bus.Unsubscribe<TTSRequestEvent>(OnTTSRequest);
            bus.Unsubscribe<GuideAnnouncementEvent>(OnGuideAnnouncement);
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

        public void NotifyTrackingState(bool isStable, string stateStr)
        {
            bool stateChanged = isStable != _lastTrackingStable;
            bool throttled = Time.unscaledTime - _lastTrackingNotifyTime < _trackingNotifyInterval;

            if (!stateChanged && throttled) return;

            _lastTrackingStable = isStable;
            _lastTrackingNotifyTime = Time.unscaledTime;

            string state = stateStr ?? "Unknown";
            string reason = "None";
            int pipeIdx = state.IndexOf('|');
            if (pipeIdx >= 0)
            {
                reason = state.Substring(pipeIdx + 1);
                state = state.Substring(0, pipeIdx);
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
        //  TTS — CANAL DE IDA (Unity → Flutter)
        // =====================================================================

        #region TTS Request (Unity → Flutter)

        private void OnTTSRequest(TTSRequestEvent evt)
        {
            if (string.IsNullOrEmpty(evt.Text)) return;

            _ttsSb.Clear();
            _ttsSb.Append("{\"action\":\"tts_request\",\"text\":\"");
            _ttsSb.Append(EscapeJson(evt.Text));
            _ttsSb.Append("\",\"priority\":");
            _ttsSb.Append(evt.Priority);
            _ttsSb.Append(",\"interrupt\":");
            _ttsSb.Append(evt.Interrupt ? "true" : "false");
            _ttsSb.Append('}');

            Reply(_ttsSb.ToString());

            if (_logTTSSync)
                Debug.Log($"[VoiceAPI] 🔊 tts_request → Flutter: " +
                          $"p={evt.Priority} interrupt={evt.Interrupt} \"{evt.Text}\"");
        }

        #endregion

        // =====================================================================
        //  TTS — CANAL DE VUELTA (Flutter → Unity)
        // =====================================================================

        #region TTS Status (Flutter → Unity)

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
                    Priority = data.priority,
                });

                if (!data.isSpeaking)
                {
                    var guide = NavigationVoiceGuide.Instance;
                    if (guide != null)
                        guide.ClearTTSBusy();
                    else
                        Debug.LogWarning("[VoiceAPI] OnTTSStatus: NavigationVoiceGuide.Instance es null.");
                }
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
            public int priority;
        }

        #endregion

        // =====================================================================
        //  GuideAnnouncement — SOLO DIAGNÓSTICO/ESTADO
        // =====================================================================

        #region GuideAnnouncement (estado, no TTS)

        private void OnGuideAnnouncement(GuideAnnouncementEvent evt)
        {
            if (_logTTSSync)
                Debug.Log($"[VoiceAPI] 📋 GuideAnnouncement (estado, sin TTS): " +
                          $"[{evt.AnnouncementType}] floor={evt.CurrentFloor} \"{evt.Message}\"");
        }

        #endregion

        // =====================================================================
        //  MÉTODOS v8.3 / v8.4 — Comandos desde Flutter
        // =====================================================================

        #region Métodos desde Flutter (v8.3/v8.4)

        /// <summary>
        /// Retorna el estado de la guía de voz a Flutter.
        /// Llamado por FlutterUnityBridge cuando recibe action="voice_status".
        /// </summary>
        public void GetVoiceStatus()
        {
            var guide = NavigationVoiceGuide.Instance;
            if (guide == null)
            {
                Reply("{\"action\":\"voice_status\",\"ok\":false," +
                      "\"message\":\"NavigationVoiceGuide no disponible\"}");
                return;
            }

            string statusJson = guide.GetVoiceStatusJson();
            Reply(statusJson);

            if (_logTTSSync)
                Debug.Log($"[VoiceAPI] 📊 voice_status → Flutter: {statusJson}");
        }

        /// <summary>
        /// ✅ v8.4 FIX — Habla texto libre generado por Flutter (COMPAS).
        /// Envía tts_request DIRECTAMENTE a Flutter via Reply(),
        /// sin pasar por EventBus.
        /// </summary>
        public void SpeakArbitraryText(string text, int priority, bool interrupt)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.LogWarning("[VoiceAPI] SpeakArbitraryText: text vacío.");
                return;
            }

            if (Time.unscaledTime - _lastArbitraryTTSTime < _ttsRequestThrottle)
            {
                Debug.Log($"[VoiceAPI] 🔇 SpeakArbitraryText throttled: \"{text}\"");
                return;
            }

            int clampedPriority = Mathf.Clamp(priority, 0, 2);
            bool clampedInterrupt = (clampedPriority >= 2) && interrupt;

            _lastArbitraryTTSTime = Time.unscaledTime;

            _ttsSb.Clear();
            _ttsSb.Append("{\"action\":\"tts_request\",\"text\":\"");
            _ttsSb.Append(EscapeJson(text));
            _ttsSb.Append("\",\"priority\":");
            _ttsSb.Append(clampedPriority);
            _ttsSb.Append(",\"interrupt\":");
            _ttsSb.Append(clampedInterrupt ? "true" : "false");
            _ttsSb.Append('}');

            Reply(_ttsSb.ToString());

            if (_logTTSSync)
                Debug.Log($"[VoiceAPI] 💬 tts_speak directo (COMPAS): " +
                          $"p={clampedPriority} interrupt={clampedInterrupt} \"{text}\"");
        }

        #endregion

        // =====================================================================
        //  ✅ v8.5 — Envío de frames de cámara a Flutter
        // =====================================================================

        #region Frame sender (v8.5)

        /// <summary>
        /// ✅ v8.5 — Envía un frame de cámara AR a Flutter para segmentación.
        /// Llamado por CameraFrameSender.cs a ~10 fps.
        /// </summary>
        public void SendFrameToFlutter(string frameJson)
        {
            if (string.IsNullOrEmpty(frameJson)) return;
            SendUnityMessageToFlutter(_flutterGameObject, _responseMethod, frameJson);
            if (_logFrames)
                Debug.Log($"[VoiceAPI] 📸 frame_data → Flutter ({frameJson.Length} chars)");
        }

        #endregion

        // =====================================================================
        //  ✅ v8.6 — Envío de ratios de segmentación a Flutter
        // =====================================================================

        #region Segmentation ratio sender (v8.6)

        /// <summary>
        /// ✅ v8.6 — Envía obstacle y floor ratio a Flutter.
        /// </summary>
        public void SendSegmentationRatio(float obstacleRatio, float floorRatio)
        {
            _sb.Clear();
            _sb.Append("{\"action\":\"segmentation_ratio\"");
            _sb.Append(",\"obstacle\":");
            _sb.Append(obstacleRatio.ToString("F3"));
            _sb.Append(",\"floor\":");
            _sb.Append(floorRatio.ToString("F3"));
            _sb.Append('}');

            Reply(_sb.ToString());
        }

        /// <summary>
        /// Sobrecarga con wallRatio.
        /// </summary>
        public void SendSegmentationRatio(float obstacleRatio, float floorRatio, float wallRatio)
        {
            _sb.Clear();
            _sb.Append("{\"action\":\"segmentation_ratio\"");
            _sb.Append(",\"obstacle\":");
            _sb.Append(obstacleRatio.ToString("F3"));
            _sb.Append(",\"floor\":");
            _sb.Append(floorRatio.ToString("F3"));
            _sb.Append(",\"wall\":");
            _sb.Append(wallRatio.ToString("F3"));
            _sb.Append('}');

            Reply(_sb.ToString());
        }

        #endregion

        // =====================================================================
        //  ✅ v8.7 NUEVO — ReplyPublic para handshake scene_ready
        // =====================================================================

        #region Reply público (v8.7)

        /// <summary>
        /// ✅ v8.7 — Versión pública de Reply() para uso exclusivo del handshake.
        ///
        /// Permite que FlutterUnityBridge.NotifySceneReady() envíe
        /// { "action": "scene_ready" } a Flutter sin romper la encapsulación
        /// de SendUnityMessageToFlutter().
        ///
        /// No usar para casos de uso distintos al handshake — el resto de
        /// mensajes deben pasar por los métodos específicos (Reply privado,
        /// SendSegmentationRatio, etc.).
        /// </summary>
        public void ReplyPublic(string json) => Reply(json);

        #endregion

        // =====================================================================
        //  Event handlers — Waypoints
        // =====================================================================

        #region Event handlers — Waypoints

        private void OnWaypointPlaced(WaypointPlacedEvent _) => _waypointCacheDirty = true;
        private void OnWaypointRemoved(WaypointRemovedEvent _) => _waypointCacheDirty = true;
        private void OnWaypointsBatchLoaded(WaypointsBatchLoadedEvent _) => _waypointCacheDirty = true;

        #endregion

        // =====================================================================
        //  Event handler — Cambio de piso
        // =====================================================================

        #region Event handler — Cambio de piso

        private void OnFloorTransition(FloorTransitionEvent evt)
        {
            Debug.Log($"[VoiceAPI] 🔄 FloorTransition {evt.FromLevel}→{evt.ToLevel}.");
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
                new Arg("is_navigating", agent.IsNavigating.ToString()),
                new Arg("remaining_m", agent.RemainingDistance.ToString("F1")),
                new Arg("progress_pct", (agent.ProgressPercent * 100f).ToString("F0")),
                new Arg("current_level", agent.CurrentLevel.ToString()),
                new Arg("destination", agent.LastDestination.ToString())
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

            Debug.Log($"[VoiceAPI] ListWaypoints — WaypointCount={_waypointManager.WaypointCount} " +
                      $"| dirty={_waypointCacheDirty}");

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
                new Arg("id", wp.WaypointId),
                new Arg("name", wp.WaypointName),
                new Arg("x", SafeFloat(wp.Position.x)),
                new Arg("y", SafeFloat(wp.Position.y)),
                new Arg("z", SafeFloat(wp.Position.z))
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

        private async System.Threading.Tasks.Task LoadAsync()
        {
            if (_persistenceManager == null)
            { Reply(Err("load_session", "PersistenceManager no disponible")); return; }

            bool ok = await _persistenceManager.LoadSession();

            if (ok)
            {
                await System.Threading.Tasks.Task.Yield();
                _waypointCacheDirty = true;
                Debug.Log($"[VoiceAPI] ✅ LoadAsync completo — " +
                          $"WaypointCount={_waypointManager?.WaypointCount ?? -1}");
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

        // ✅ NUEVO — Permite marcar el cache como sucio desde otros sistemas
        public void MarkWaypointCacheDirty() => _waypointCacheDirty = true;

        // ✅ NUEVO — Expone el JSON cacheado sin reconstruir innecesariamente
        public string GetWaypointListJson()
        {
            if (_waypointCacheDirty)
                RebuildWaypointCache();

            return _waypointListCache;
        }

        private void RebuildWaypointCache()
        {
            var list = _waypointManager.Waypoints;
            if (list == null || list.Count == 0)
            {
                _waypointListCache = "[]";
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

                _sb.Append("{\"id\":\""); _sb.Append(w.WaypointId);
                _sb.Append("\",\"name\":\""); _sb.Append(EscapeJson(w.WaypointName));
                _sb.Append("\",\"type\":\""); _sb.Append(w.Type);
                _sb.Append("\",\"navigable\":"); _sb.Append(w.IsNavigable ? "true" : "false");
                _sb.Append(",\"pos\":{\"x\":"); _sb.Append(SafeFloat(w.Position.x));
                _sb.Append(",\"y\":"); _sb.Append(SafeFloat(w.Position.y));
                _sb.Append(",\"z\":"); _sb.Append(SafeFloat(w.Position.z));
                _sb.Append("}}");
            }
            _sb.Append(']');

            _waypointListCache = _sb.ToString();
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

        [ContextMenu("Test: Simular TTS Done desde Flutter")]
        private void DbgSimulateTTSDone()
            => OnTTSStatus("{\"isSpeaking\":false,\"priority\":0}");

        [ContextMenu("Test: Simular TTS Start (priority 2)")]
        private void DbgTTSStartHigh()
            => OnTTSStatus("{\"isSpeaking\":true,\"priority\":2}");

        [ContextMenu("Test: Simular TTS Start (priority 3)")]
        private void DbgTTSStartUrgent()
            => OnTTSStatus("{\"isSpeaking\":true,\"priority\":3}");

        [ContextMenu("Test: Tracking estable")]
        private void DbgTrackingStable()
            => NotifyTrackingState(true, "SessionTracking");

        [ContextMenu("Test: Tracking perdido (ExcessiveMotion)")]
        private void DbgTrackingLost()
            => NotifyTrackingState(false, "SessionInitializing|ExcessiveMotion");

        [ContextMenu("Test: Voice Status")]
        private void DbgVoiceStatus() => GetVoiceStatus();

        [ContextMenu("Test: COMPAS tts_speak (p=1)")]
        private void DbgCompasSpeak()
            => SpeakArbitraryText("Claro, ¿en qué puedo ayudarte?", 1, false);

        [ContextMenu("Test: Frame sender — log toggle")]
        private void DbgToggleFrameLog() => _logFrames = !_logFrames;

        [ContextMenu("Test: SendSegmentationRatio (0.6 obstacle, 0.05 floor)")]
        private void DbgSegRatio() => SendSegmentationRatio(0.6f, 0.05f);

        [ContextMenu("Test: SendSegmentationRatio con wall (0.6 wall, 0.04 floor)")]
        private void DbgSegRatioWall() => SendSegmentationRatio(0.05f, 0.04f, 0.6f);

        [ContextMenu("Test: Simular scene_ready")]
        private void DbgSceneReady()
            => ReplyPublic("{\"action\":\"scene_ready\",\"ok\":true,\"message\":\"Test desde ContextMenu\"}");

        #endregion
    }
}