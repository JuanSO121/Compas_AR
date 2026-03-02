// File: VoiceCommandAPI.cs
// ✅ v6 — Fix JSON corrupto (NaN/Infinity en coordenadas z)
//
//  BUG CORREGIDO (v5 → v6):
//  ──────────────────────────────────────────────────────────────────────────
//  En Unity IL2CPP, float.ToString("F2") sobre NaN o Infinity produce
//  literales inválidos en JSON:
//
//    NaN           → "NaN"         ← no es JSON válido
//    Infinity      → "Infinity"    ← no es JSON válido
//    -Infinity     → "-Infinity"   ← no es JSON válido
//    (con formato compuesto {x:F2}) → a veces "-F2", "NaNF2"
//
//  Flutter llama jsonDecode() y lanza FormatException → cae al catch de
//  "Mensaje no-JSON" → list_waypoints / create_waypoint nunca se procesan.
//
//  FIX: SafeFloat() detecta NaN/Infinity y devuelve "0.00".
//       Se aplica en RebuildWaypointCache(), CreateWaypointAtAgent() y
//       OnNavigationArrived().
//
//  HEREDADOS de v5:
//  - onUnityMessage() en lugar de sendMessageToFlutter() (rama unity_6000)
//  - try/finally explícito — NO 'using' (bug IL2CPP Dispose prematuro en arm64)

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
        }

        private void OnDisable()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Unsubscribe<WaypointPlacedEvent>      (OnWaypointPlaced);
            bus.Unsubscribe<WaypointRemovedEvent>     (OnWaypointRemoved);
            bus.Unsubscribe<WaypointsBatchLoadedEvent>(OnWaypointsBatchLoaded);
            bus.Unsubscribe<NavigationArrivedEvent>   (OnNavigationArrived);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        #endregion

        #region Event handlers

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

            // ✅ FIX v6: SafeFloat en coordenadas
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
            // ✅ FIX v6: SafeFloat en coordenadas del evento
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

                // ✅ FIX v6: SafeFloat() en lugar de ":F2" directo
                // ":F2" sobre NaN o Infinity → "NaNF2" / "-F2" → JSON inválido
                // SafeFloat() → siempre un número decimal válido
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
        /// En IL2CPP Unity6+arm64, el bloque 'using' llama Dispose() antes
        /// de que CallStatic complete → objeto se resuelve como java.lang.Object
        /// → NoSuchMethodError en tiempo de ejecución.
        ///
        /// Rama experimental/unity_6000:
        ///   onUnityMessage(String)  ← método correcto (NO sendMessageToFlutter)
        /// </summary>
        private static void SendUnityMessageToFlutter(string go, string method, string msg)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // ── Intento 1: onUnityMessage (rama experimental/unity_6000) ─────
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

            // ── Intento 2: sendMessageToFlutter (versiones antiguas) ──────────
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

            // ── Intento 3: UnityUtils (nombre antiguo del plugin) ─────────────
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
        /// ✅ FIX v6: Serialización segura de floats para JSON.
        ///
        /// Problema raíz:
        ///   En IL2CPP, el interpolado $"{valor:F2}" equivale a
        ///   valor.ToString("F2", CultureInfo.InvariantCulture).
        ///   Sobre NaN produce   → "NaN"   (inválido en JSON)
        ///   Sobre Infinity      → "∞"     (inválido en JSON)
        ///   Sobre -Infinity     → "-∞"    (inválido en JSON)
        ///   Con formato erróneo → "-F2"   (inválido en JSON)
        ///
        ///   Flutter: jsonDecode lanza FormatException → catch "no-JSON".
        ///   Resultado: ningún mensaje de Unity se procesaba en Flutter.
        ///
        /// Solución: sustituir por "0.00" (válido JSON, neutro para Unity).
        /// El valor 0 es mucho mejor que crashear la comunicación.
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

        #endregion
    }
}