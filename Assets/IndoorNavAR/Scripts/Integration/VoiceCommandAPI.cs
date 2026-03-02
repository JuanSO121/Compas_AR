// File: VoiceCommandAPI.cs
// ✅ v7.1 — Fix TLS ALLOC_TEMP_TLS: deduplicación de GuideAnnouncementEvent
//
//  DIAGNÓSTICO:
//  ──────────────────────────────────────────────────────────────────────────
//  El error "TLS Allocator ALLOC_TEMP_TLS, underlying allocator
//  ALLOC_TEMP_MAIN has unfreed allocations, size 646" fue introducido en v7.
//
//  Cadena causal:
//    ARGuideController.EvaluateState()  [InvokeRepeating, cada 0.25s]
//      → Announce()
//        → EventBus.Publish(GuideAnnouncementEvent)
//          → OnGuideAnnouncement()
//            → Reply()
//              → SendUnityMessageToFlutter()
//                → new AndroidJavaClass(...)   ← ALLOC en ALLOC_TEMP_TLS
//
//  EvaluateState() corre a 4/s. Los estados del guía NO cambian en cada
//  tick, pero Announce() se llama de todas formas mientras se está en el
//  estado (p.ej. Waiting llama Announce(WaitingForUser) → nunca cambia
//  mientras el usuario sigue lejos).
//
//  AndroidJavaClass alloca en el TLS del JNI bridge (IL2CPP/arm64).
//  Con 4 instancias/s el allocator no alcanza a liberar entre ciclos.
//
//  FIX (solo en este archivo — ARGuideController y EventBus sin cambios):
//  ──────────────────────────────────────────────────────────────────────────
//  + _lastAnnouncementType (GuideAnnouncementType?): tipo del último anuncio
//    enviado a Flutter.
//  + _lastAnnouncementMsg (string): mensaje del último anuncio enviado.
//  + OnGuideAnnouncement() descarta el evento si tipo Y mensaje son
//    idénticos al último enviado → Reply() no se llama → AndroidJavaClass
//    no se instancia → ALLOC_TEMP_TLS no se satura.
//  + Se usan ambos discriminadores (tipo + mensaje) porque el mismo tipo
//    puede tener mensajes distintos (p.ej. FloorReached: "piso 1" vs "piso 2").
//
//  HEREDADOS sin cambios:
//  - SafeFloat()          — bug IL2CPP NaN/Infinity en JSON (v6)
//  - onUnityMessage()     — rama unity_6000 (v5)
//  - try/finally          — bug IL2CPP Dispose prematuro arm64 (v5)
//  - GuideAnnouncementEvent suscripción — mantenida, con deduplicación (v7)

using System;
using System.Collections.Generic;
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

        private bool   _waypointCacheDirty = true;
        private string _waypointListCache  = "[]";

        // ✅ FIX v7.1 — Deduplicación de GuideAnnouncementEvent.
        // Reply() → AndroidJavaClass solo se instancia cuando el anuncio
        // es genuinamente distinto al último enviado.
        private GuideAnnouncementType? _lastAnnouncementType = null;
        private string                 _lastAnnouncementMsg  = null;

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
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        #endregion

        #region Event handlers — Waypoints

        private void OnWaypointPlaced(WaypointPlacedEvent _)
        {
            _waypointCacheDirty = true;
            Debug.Log($"[VoiceAPI] Cache dirty — WaypointPlaced. Total: {_waypointManager?.WaypointCount ?? -1}");
        }

        private void OnWaypointRemoved(WaypointRemovedEvent _)
        {
            _waypointCacheDirty = true;
            Debug.Log($"[VoiceAPI] Cache dirty — WaypointRemoved. Total: {_waypointManager?.WaypointCount ?? -1}");
        }

        private void OnWaypointsBatchLoaded(WaypointsBatchLoadedEvent evt)
        {
            _waypointCacheDirty = true;
            Debug.Log($"[VoiceAPI] ✅ BatchLoaded: {evt.Count} waypoint(s). " +
                      $"WaypointManager.Count={_waypointManager?.WaypointCount ?? -1}");
        }

        #endregion

        #region Event handler — Guía NPC (v7.1) ─────────────────────────────

        /// <summary>
        /// ✅ v7.1: Recibe avisos del NPC guía y los reenvía a Flutter como JSON,
        /// con deduplicación para evitar saturar ALLOC_TEMP_TLS.
        ///
        /// PROBLEMA (v7 original):
        ///   EvaluateState() corre a 0.25s. Mientras el guía esté en un estado
        ///   estable (p.ej. Waiting), Announce() se republica constantemente con
        ///   el mismo tipo y mensaje. Cada publicación llegaba aquí y llamaba
        ///   Reply() → new AndroidJavaClass() → alloc en ALLOC_TEMP_TLS → leak.
        ///
        /// FIX:
        ///   Comparar tipo + mensaje con el último enviado. Si son idénticos,
        ///   descartar silenciosamente. Solo llamar Reply() cuando el anuncio
        ///   cambia de verdad (nueva transición de estado, nuevo piso, etc.).
        ///
        /// Esquema JSON enviado a Flutter (sin cambios respecto a v7):
        /// {
        ///   "action":  "guide_announcement",
        ///   "ok":      true,
        ///   "message": "Atención: escaleras próximas",
        ///   "type":    "ApproachingStairs",
        ///   "floor":   "0"
        /// }
        /// </summary>
        private void OnGuideAnnouncement(GuideAnnouncementEvent evt)
        {
            // ✅ FIX v7.1: descartar duplicados antes de tocar AndroidJavaClass
            if (_lastAnnouncementType == evt.AnnouncementType &&
                _lastAnnouncementMsg  == evt.Message)
            {
                Debug.Log($"[VoiceAPI] 🔇 GuideAnnouncement deduplicado: [{evt.AnnouncementType}]");
                return;
            }

            _lastAnnouncementType = evt.AnnouncementType;
            _lastAnnouncementMsg  = evt.Message;

            string json = $"{{\"action\":\"guide_announcement\"," +
                          $"\"ok\":true," +
                          $"\"message\":\"{EscapeJson(evt.Message)}\"," +
                          $"\"type\":\"{evt.AnnouncementType}\"," +
                          $"\"floor\":\"{evt.CurrentFloor}\"}}";

            Reply(json);
            Debug.Log($"[VoiceAPI] 🔊 GuideAnnouncement → Flutter: [{evt.AnnouncementType}] \"{evt.Message}\" (piso {evt.CurrentFloor})");
        }

        #endregion

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

            if (matches.Count > 1)
                Debug.Log($"[VoiceAPI] NavigateTo '{waypointName}': {matches.Count} coincidencias, " +
                          $"usando '{target.WaypointName}'");

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

        #region Waypoints

        public void ListWaypoints()
        {
            if (_waypointManager == null)
            { Reply(Err("list_waypoints", "WaypointManager no disponible")); return; }

            Debug.Log($"[VoiceAPI] ListWaypoints: dirty={_waypointCacheDirty}, " +
                      $"Count={_waypointManager.WaypointCount}");

            if (_waypointCacheDirty) RebuildWaypointCache();

            Reply($"{{\"action\":\"list_waypoints\",\"ok\":true," +
                  $"\"count\":{_waypointManager.WaypointCount}," +
                  $"\"waypoints\":{_waypointListCache}}}");
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

        #region Sesión

        public void SaveSession() => _ = SaveAsync();
        public void LoadSession() => _ = LoadAsync();

        private async System.Threading.Tasks.Task SaveAsync()
        {
            if (_persistenceManager == null)
            { Reply(Err("save_session", "PersistenceManager no disponible")); return; }
            bool ok = await _persistenceManager.SaveSession();
            Reply(ok ? Ok("save_session", "Sesión guardada")
                     : Err("save_session", "Error al guardar"));
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            if (_persistenceManager == null)
            { Reply(Err("load_session", "PersistenceManager no disponible")); return; }
            bool ok = await _persistenceManager.LoadSession();
            Reply(ok ? Ok("load_session", "Sesión cargada")
                     : Err("load_session", "Error al cargar"));
        }

        #endregion

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

        #region Cache de waypoints

        private void RebuildWaypointCache()
        {
            var list = _waypointManager.Waypoints;
            if (list == null || list.Count == 0)
            {
                _waypointListCache  = "[]";
                _waypointCacheDirty = false;
                Debug.Log("[VoiceAPI] RebuildCache: lista vacía.");
                return;
            }

            var sb = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (w == null) continue;
                if (sb.Length > 1) sb.Append(',');

                sb.Append($"{{\"id\":\"{w.WaypointId}\"," +
                           $"\"name\":\"{EscapeJson(w.WaypointName)}\"," +
                           $"\"type\":\"{w.Type}\"," +
                           $"\"navigable\":{(w.IsNavigable ? "true" : "false")}," +
                           $"\"pos\":{{" +
                               $"\"x\":{SafeFloat(w.Position.x)}," +
                               $"\"y\":{SafeFloat(w.Position.y)}," +
                               $"\"z\":{SafeFloat(w.Position.z)}" +
                           $"}}}}");
            }
            sb.Append(']');
            _waypointListCache  = sb.ToString();
            _waypointCacheDirty = false;

            Debug.Log($"[VoiceAPI] RebuildCache OK: {list.Count} waypoint(s). " +
                      $"Preview: {_waypointListCache.Substring(0, Math.Min(150, _waypointListCache.Length))}");
        }

        #endregion

        #region Envío a Flutter

        private void Reply(string json)
        {
            SendUnityMessageToFlutter(_flutterGameObject, _responseMethod, json);
            Debug.Log($"[VoiceAPI→Flutter] {json}");
        }

        /// <summary>
        /// ✅ FIX v5: try/finally explícito — NO 'using'.
        /// En IL2CPP Unity6+arm64, 'using' llama Dispose() antes de que
        /// CallStatic complete → NoSuchMethodError en runtime.
        /// </summary>
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

        #region Helpers JSON

        private readonly struct Arg
        {
            public readonly string Key, Val;
            public Arg(string k, string v) { Key = k; Val = v; }
        }

        private static string Ok(string action, string message, params Arg[] extra)
        {
            var sb = new StringBuilder();
            sb.Append($"{{\"action\":\"{action}\",\"ok\":true,\"message\":\"{EscapeJson(message)}\"");
            foreach (var a in extra)
                sb.Append($",\"{a.Key}\":\"{EscapeJson(a.Val)}\"");
            sb.Append('}');
            return sb.ToString();
        }

        private static string Err(string action, string message) =>
            $"{{\"action\":\"{action}\",\"ok\":false,\"message\":\"{EscapeJson(message)}\"}}";

        private static string EscapeJson(string s) =>
            s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") ?? "";

        /// <summary>
        /// ✅ FIX v6: Serialización segura de floats.
        /// NaN/Infinity en IL2CPP → "0.00" para evitar FormatException en
        /// jsonDecode() de Flutter.
        /// </summary>
        private static string SafeFloat(float v) =>
            float.IsNaN(v) || float.IsInfinity(v) ? "0.00" : v.ToString("F2");

        #endregion

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

        #endregion
    }
}