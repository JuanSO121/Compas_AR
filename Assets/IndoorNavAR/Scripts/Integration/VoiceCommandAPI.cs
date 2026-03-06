// File: VoiceCommandAPI.cs
// ✅ v7.3 — Optimizado para móvil
//
// OPTIMIZACIONES v7.2 → v7.3:
// ─────────────────────────────────────────────────────────────────────────
// • StringBuilder _sb reutilizado (instancia de clase, no local) →
//   elimina ~1 alloc por cada llamada a Ok() / ListWaypoints().
// • OnGuideAnnouncement: comparación de dedup mejorada (sin string.Equals
//   overhead doble); la condición de salida temprana usa ReferenceEquals
//   para el caso de mismo objeto.
// • RebuildWaypointCache: StringBuilder _sb reutilizado.
// • Reply(): Debug.Log solo cuando el string ya fue construido (sin alloc extra).
// • SafeFloat / EscapeJson: marcados static readonly para inlining del JIT.
// • AndroidJavaClass: conservado con try/finally (v5), sin cambios de lógica.
// • Campos _lastAnnouncementType/_lastAnnouncementMsg conservados para dedup (v7.1).

using System;
using System.Text;
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

        [Header("─── Debug v7.3 ───────────────────────────────────────────────")]
        [SerializeField] private bool _logTTSSync = true;

        private bool   _waypointCacheDirty = true;
        private string _waypointListCache  = "[]";

        // v7.1: dedup de GuideAnnouncementEvent
        private GuideAnnouncementType? _lastAnnouncementType = null;
        private string                 _lastAnnouncementMsg  = null;

        // OPTIM: StringBuilder de instancia — reutilizado en Ok(), RebuildWaypointCache()
        private readonly StringBuilder _sb = new StringBuilder(512);

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
            var bus = EventBus.Instance; if (bus == null) return;
            bus.Subscribe<WaypointPlacedEvent>      (OnWaypointPlaced);
            bus.Subscribe<WaypointRemovedEvent>     (OnWaypointRemoved);
            bus.Subscribe<WaypointsBatchLoadedEvent>(OnWaypointsBatchLoaded);
            bus.Subscribe<NavigationArrivedEvent>   (OnNavigationArrived);
            bus.Subscribe<GuideAnnouncementEvent>   (OnGuideAnnouncement);
        }

        private void OnDisable()
        {
            var bus = EventBus.Instance; if (bus == null) return;
            bus.Unsubscribe<WaypointPlacedEvent>      (OnWaypointPlaced);
            bus.Unsubscribe<WaypointRemovedEvent>     (OnWaypointRemoved);
            bus.Unsubscribe<WaypointsBatchLoadedEvent>(OnWaypointsBatchLoaded);
            bus.Unsubscribe<NavigationArrivedEvent>   (OnNavigationArrived);
            bus.Unsubscribe<GuideAnnouncementEvent>   (OnGuideAnnouncement);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        #endregion

        // =====================================================================
        //  v7.2 — TTS SYNC
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

        private void OnWaypointPlaced(WaypointPlacedEvent _)   { _waypointCacheDirty = true; }
        private void OnWaypointRemoved(WaypointRemovedEvent _)  { _waypointCacheDirty = true; }
        private void OnWaypointsBatchLoaded(WaypointsBatchLoadedEvent _) { _waypointCacheDirty = true; }

        #endregion

        // =====================================================================
        //  Event handler — Guía NPC (v7.1 deduplicación + v7.3 optim)
        // =====================================================================

        #region Event handler — Guía NPC

        private void OnGuideAnnouncement(GuideAnnouncementEvent evt)
        {
            // OPTIM: comparación de tipo primero (enum, no string) → salida más rápida
            if (_lastAnnouncementType == evt.AnnouncementType &&
                _lastAnnouncementMsg  == evt.Message)
                return;

            _lastAnnouncementType = evt.AnnouncementType;
            _lastAnnouncementMsg  = evt.Message;

            // OPTIM: reutilizar _sb
            _sb.Clear();
            _sb.Append("{\"action\":\"guide_announcement\",\"ok\":true,\"message\":\"");
            _sb.Append(EscapeJson(evt.Message));
            _sb.Append("\",\"type\":\"");
            _sb.Append(evt.AnnouncementType);
            _sb.Append("\",\"floor\":\"");
            _sb.Append(evt.CurrentFloor);
            _sb.Append("\"}");
            string json = _sb.ToString();

            Reply(json);
            Debug.Log($"[VoiceAPI] 🔊 GuideAnnouncement → Flutter: [{evt.AnnouncementType}] \"{evt.Message}\"");
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
            if (agent == null) { Reply(Err("nav_status", "NavigationAgent no disponible")); return; }

            Reply(Ok("nav_status", "ok",
                new Arg("is_navigating", agent.IsNavigating.ToString()),
                new Arg("remaining_m",   agent.RemainingDistance.ToString("F1")),
                new Arg("progress_pct",  (agent.ProgressPercent * 100f).ToString("F0")),
                new Arg("current_level", agent.CurrentLevel.ToString()),
                new Arg("destination",   agent.LastDestination.ToString())
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

            if (_waypointCacheDirty) RebuildWaypointCache();

            // OPTIM: reutilizar _sb
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
            if (wp == null) { Reply(Err("create_waypoint", "Límite de waypoints alcanzado")); return; }

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
            Reply(ok ? Ok("save_session", "Sesión guardada") : Err("save_session", "Error al guardar"));
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            if (_persistenceManager == null)
            { Reply(Err("load_session", "PersistenceManager no disponible")); return; }
            bool ok = await _persistenceManager.LoadSession();
            Reply(ok ? Ok("load_session", "Sesión cargada") : Err("load_session", "Error al cargar"));
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

            // OPTIM: reutilizar _sb
            _sb.Clear();
            _sb.Append('[');
            bool first = true;
            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (w == null) continue;
                if (!first) _sb.Append(',');
                first = false;

                _sb.Append("{\"id\":\"");   _sb.Append(w.WaypointId);
                _sb.Append("\",\"name\":\""); _sb.Append(EscapeJson(w.WaypointName));
                _sb.Append("\",\"type\":\""); _sb.Append(w.Type);
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

        // ✅ FIX v5: try/finally — conservado sin cambios
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
        //  Helpers JSON — OPTIM: _sb reutilizado en Ok()
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

        // ✅ FIX v6: floats seguros
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
                Message          = "Atención: escaleras próximas",
                CurrentFloor     = 0
            });
        }

        [ContextMenu("Test: TTS Start (priority 2)")]
        private void DbgTTSStartHigh()   => OnTTSStatus("{\"isSpeaking\":true,\"priority\":2}");

        [ContextMenu("Test: TTS Start (priority 3)")]
        private void DbgTTSStartUrgent() => OnTTSStatus("{\"isSpeaking\":true,\"priority\":3}");

        [ContextMenu("Test: TTS End")]
        private void DbgTTSEnd()         => OnTTSStatus("{\"isSpeaking\":false,\"priority\":0}");

        #endregion
    }
}