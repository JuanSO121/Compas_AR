// File: FlutterUnityBridge.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Integration/
//
// Parte receptora del bridge Flutter → Unity.
// La parte estática (Unity → Flutter) está en FlutterUnityBridgeStatic.cs.
//
// ✅ FIX OVERLAY (v_seg_fix):
//   case "toggle_seg_mask" ahora localiza SegmentationController y llama
//   SetOverlayVisible(!segCtrl.OverlayVisible) en lugar de ser un no-op.
//   Sin este fix el overlay nunca se activaba desde Flutter aunque se
//   enviaran múltiples toggle_seg_mask.
//
// ✅ FIX navigate_to (previo):
//   Añadido case "navigate_to" (acción que envía Flutter/UnityBridgeService v6.0)
//   para que sea equivalente a "navigate_to_waypoint" pero usando el campo "name"
//   en lugar de "waypointName". Sin este case el log mostraba:
//   W/Unity: [FlutterUnityBridge] Acción no soportada: navigate_to

using System;
using System.Threading.Tasks;
using UnityEngine;
using IndoorNavAR.Core;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;

namespace IndoorNavAR.Integration
{
    /// <summary>
    /// Bridge receptor para comandos enviados desde Flutter/Android hacia Unity.
    ///
    /// Uso esperado desde Android:
    ///   UnityPlayer.UnitySendMessage("FlutterBridge", "OnFlutterCommand", "{...json...}");
    ///
    /// Donde "FlutterBridge" es el nombre del GameObject que contiene este componente.
    ///
    /// Acciones soportadas:
    ///   navigate_to           → campo "name"         (UnityBridgeService v6.0)
    ///   navigate_to_waypoint  → campo "waypointName" (legado)
    ///   add_waypoint
    ///   create_waypoint       → campo "name"         (alias de add_waypoint desde posición actual)
    ///   clear_waypoints
    ///   remove_waypoint       → campo "name"
    ///   save_session
    ///   load_session
    ///   list_waypoints        → delega a VoiceCommandAPI
    ///   nav_status            → delega a VoiceCommandAPI
    ///   stop_navigation       → delega a VoiceCommandAPI
    ///   tts_speak             → delega a VoiceCommandAPI
    ///   voice_status          → delega a VoiceCommandAPI
    ///   repeat_instruction    → delega a VoiceCommandAPI (no-op local)
    ///   stop_voice            → delega a VoiceCommandAPI (no-op local)
    ///   session_status        → responde con estado actual de sesión
    ///   ping_scene            → responde con scene_ready si ya está lista
    ///   toggle_seg_mask       → ✅ FIX: llama SetOverlayVisible en SegmentationController
    /// </summary>
    public partial class FlutterUnityBridge : MonoBehaviour   // ← partial: comparte clase con FlutterUnityBridgeStatic.cs
    {
        [Header("Bridge")]
        [SerializeField] private bool _verboseLogs = true;

        [Header("Dependencias")]
        [SerializeField] private WaypointManager    _waypointManager;
        [SerializeField] private NavigationManager  _navigationManager;
        [SerializeField] private PersistenceManager _persistenceManager;

        // ── Modelo de comando entrante ────────────────────────────────────────

        [Serializable]
        private class FlutterCommand
        {
            // Acción solicitada
            public string action;

            // Campos de navegación por nombre (distintos según versión del bridge)
            public string waypointName;  // legado (navigate_to_waypoint)
            public string name;          // nuevo   (navigate_to, create_waypoint, remove_waypoint)

            // Campos de posición (add_waypoint)
            public float x;
            public float y;
            public float z;

            // TTS (tts_speak)
            public string text;
            public int    priority;
            public bool   interrupt;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            _waypointManager    ??= FindFirstObjectByType<WaypointManager>();
            _navigationManager  ??= FindFirstObjectByType<NavigationManager>();
            _persistenceManager ??= FindFirstObjectByType<PersistenceManager>();
        }

        // ── Punto de entrada principal ────────────────────────────────────────

        /// <summary>
        /// Recibe comandos JSON desde Flutter vía UnitySendMessage.
        /// </summary>
        public void OnFlutterCommand(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                LogWarn("OnFlutterCommand recibió JSON vacío.");
                return;
            }

            FlutterCommand cmd;
            try
            {
                cmd = JsonUtility.FromJson<FlutterCommand>(json);
            }
            catch (Exception ex)
            {
                LogError($"JSON inválido: {ex.Message}");
                return;
            }

            if (cmd == null || string.IsNullOrWhiteSpace(cmd.action))
            {
                LogWarn("Comando inválido: action no definida.");
                return;
            }

            Log($"← action='{cmd.action}'");

            switch (cmd.action)
            {
                // ── Navegación ────────────────────────────────────────────────

                // ✅ FIX: "navigate_to" es la acción que envía UnityBridgeService v6.0
                //         con el campo "name". Era la causa del bug "Acción no soportada".
                case "navigate_to":
                    NavigateToWaypointByName(cmd.name);
                    break;

                // Legado: versiones anteriores del bridge enviaban "navigate_to_waypoint"
                // con el campo "waypointName".
                case "navigate_to_waypoint":
                    NavigateToWaypointByName(cmd.waypointName);
                    break;

                case "stop_navigation":
                    var api0 = VoiceCommandAPI.Instance;
                    if (api0 != null) api0.StopNavigation();
                    else _navigationManager?.StopNavigation();
                    break;

                case "nav_status":
                    VoiceCommandAPI.Instance?.GetNavigationStatus();
                    break;

                // ── Waypoints ─────────────────────────────────────────────────

                case "add_waypoint":
                    AddWaypoint(cmd.x, cmd.y, cmd.z);
                    break;

                // "create_waypoint" crea una baliza en la posición del agente con un nombre.
                case "create_waypoint":
                    VoiceCommandAPI.Instance?.CreateWaypointAtAgent(cmd.name);
                    break;

                case "remove_waypoint":
                    VoiceCommandAPI.Instance?.RemoveWaypoint(cmd.name);
                    break;

                case "clear_waypoints":
                    _waypointManager?.ClearAllWaypoints();
                    PublishInfo("Waypoints eliminados desde Flutter.");
                    break;

                case "list_waypoints":
                    VoiceCommandAPI.Instance?.ListWaypoints();
                    break;

                // ── Sesión ────────────────────────────────────────────────────

                case "save_session":
                    _ = SaveSessionAsync();
                    break;

                case "load_session":
                    _ = LoadSessionAsync();
                    break;

                case "session_status":
                    HandleSessionStatus();
                    break;

                // ── TTS ───────────────────────────────────────────────────────

                case "tts_speak":
                    if (!string.IsNullOrWhiteSpace(cmd.text))
                        VoiceCommandAPI.Instance?.SpeakArbitraryText(
                            cmd.text, cmd.priority, cmd.interrupt);
                    break;

                case "tts_status":
                    // Flutter envía tts_status para informar a Unity si el TTS de Flutter está activo.
                    // VoiceCommandAPI ya lo gestiona. Lo silenciamos aquí para no mostrar warning.
                    VoiceCommandAPI.Instance?.OnTTSStatus(json);
                    break;

                // ── Voz / estado ──────────────────────────────────────────────

                case "voice_status":
                    VoiceCommandAPI.Instance?.GetVoiceStatus();
                    break;

                case "repeat_instruction":
                    // La instrucción de repetición la gestiona NavigationVoiceGuide.
                    // VoiceCommandAPI no expone este método directamente; se puede
                    // extender en el futuro. Por ahora se registra y se ignora.
                    Log("repeat_instruction recibido — pendiente de implementación en VoiceCommandAPI.");
                    break;

                case "stop_voice":
                    Log("stop_voice recibido — pendiente de implementación en VoiceCommandAPI.");
                    break;

                // ── Handshake ─────────────────────────────────────────────────

                case "ping_scene":
                    // Flutter pregunta si la escena está lista.
                    // Si ya estamos en Ready, responder inmediatamente.
                    if (FlutterUnityBridge.State == BridgeState.Ready)
                    {
                        Log("ping_scene → respondiendo scene_ready (ya estamos en Ready).");
                        FlutterUnityBridge.NotifySceneReady("ping_scene reply");
                    }
                    else
                    {
                        Log($"ping_scene → estado actual: {FlutterUnityBridge.State} (no Ready aún).");
                    }
                    break;

                // ── Segmentación ──────────────────────────────────────────────

                // ✅ FIX OVERLAY: antes era un no-op (solo Log). Ahora delega al
                // SegmentationController para que el overlay realmente se active/oculte.
                case "toggle_seg_mask":
                    Log("toggle_seg_mask recibido.");
                    ToggleSegmentationOverlay();
                    break;

                default:
                    LogWarn($"Acción no soportada: {cmd.action}");
                    break;
            }
        }

        // ── Segmentación ──────────────────────────────────────────────────────

        /// <summary>
        /// ✅ FIX OVERLAY: Localiza el SegmentationController (incluyendo inactivos)
        /// y alterna la visibilidad del overlay de segmentación.
        ///
        /// Separado en método propio para poder llamarlo también desde
        /// ContextMenu en desarrollo sin pasar por el bridge.
        /// </summary>
        private void ToggleSegmentationOverlay()
        {
            var segCtrl = FindFirstObjectByType<IndoorNavAR.Segmentation.SegmentationController>(
                FindObjectsInactive.Include);

            if (segCtrl == null)
            {
                LogWarn("toggle_seg_mask: SegmentationController no encontrado en la escena.");
                return;
            }

            bool nextVisible = !segCtrl.OverlayVisible;
            segCtrl.SetOverlayVisible(nextVisible);
            Log($"toggle_seg_mask → overlay={nextVisible} segActive={segCtrl.IsSegmentationActive}");
        }

        // ── Navegación ────────────────────────────────────────────────────────

        /// <summary>
        /// Navega al waypoint cuyo nombre coincide con <paramref name="waypointName"/>.
        /// Primero intenta delegarlo a VoiceCommandAPI (que envía la respuesta JSON a Flutter);
        /// si no está disponible, usa NavigationManager directamente.
        /// </summary>
        public void NavigateToWaypointByName(string waypointName)
        {
            if (string.IsNullOrWhiteSpace(waypointName))
            {
                LogWarn("NavigateToWaypointByName: waypointName vacío.");
                return;
            }

            Log($"Navegando a: '{waypointName}'");

            // Delegar a VoiceCommandAPI para que envíe la respuesta JSON a Flutter
            var api = VoiceCommandAPI.Instance;
            if (api != null)
            {
                api.NavigateTo(waypointName);
                return;
            }

            // Fallback: usar managers directamente (sin respuesta JSON a Flutter)
            if (_waypointManager == null || _navigationManager == null)
            {
                LogError("Dependencias no disponibles para navegación.");
                return;
            }

            WaypointData waypoint = _waypointManager
                .SearchWaypointsByName(waypointName)
                ?.Find(w => w != null &&
                            w.WaypointName.Equals(waypointName, StringComparison.OrdinalIgnoreCase));

            if (waypoint == null)
            {
                LogWarn($"No se encontró waypoint: '{waypointName}'");
                PublishInfo($"No se encontró destino: {waypointName}");
                return;
            }

            bool ok = _navigationManager.NavigateToWaypoint(waypoint);
            if (ok)
            {
                PublishInfo($"Navegando a {waypoint.WaypointName}");
                Log($"Navegación iniciada hacia: {waypoint.WaypointName}");
            }
            else
            {
                LogWarn($"No se pudo iniciar navegación a {waypoint.WaypointName}");
            }
        }

        // ── Waypoints ─────────────────────────────────────────────────────────

        public void AddWaypoint(float x, float y, float z)
        {
            if (_waypointManager == null)
            {
                LogError("WaypointManager no disponible.");
                return;
            }

            var wp = _waypointManager.CreateWaypoint(new Vector3(x, y, z), Quaternion.identity);
            if (wp != null)
            {
                PublishInfo($"Waypoint creado: {wp.WaypointName}");
                Log($"Waypoint creado desde Flutter en ({x:F2},{y:F2},{z:F2}).");
            }
            else
            {
                LogWarn("No se pudo crear waypoint desde Flutter.");
            }
        }

        // ── Sesión ────────────────────────────────────────────────────────────

        private async Task SaveSessionAsync()
        {
            var api = VoiceCommandAPI.Instance;
            if (api != null) { api.SaveSession(); return; }

            if (_persistenceManager == null)
            {
                LogWarn("PersistenceManager no disponible.");
                return;
            }
            bool ok = await _persistenceManager.SaveSession();
            Log(ok ? "Sesión guardada desde Flutter." : "Falló guardado de sesión desde Flutter.");
        }

        private async Task LoadSessionAsync()
        {
            var api = VoiceCommandAPI.Instance;
            if (api != null) { api.LoadSession(); return; }

            if (_persistenceManager == null)
            {
                LogWarn("PersistenceManager no disponible.");
                return;
            }
            bool ok = await _persistenceManager.LoadSession();
            Log(ok ? "Sesión cargada desde Flutter." : "Falló carga de sesión desde Flutter.");
        }

        /// <summary>
        /// Responde a "session_status" con el estado actual de la sesión.
        /// </summary>
        private void HandleSessionStatus()
        {
            var api = VoiceCommandAPI.Instance;
            if (api == null)
            {
                Log("session_status: VoiceCommandAPI no disponible.");
                return;
            }

            bool hasSession  = _persistenceManager != null && _persistenceManager.HasSavedSession();
            int  wpCount     = _waypointManager != null ? _waypointManager.WaypointCount : 0;
            bool hasNavMesh  = _navigationManager != null;

            string waypointsJson = api.GetWaypointListJson();

            string json =
                $"{{\"action\":\"session_status\"," +
                $"\"ok\":true," +
                $"\"loaded\":{(hasSession ? "true" : "false")}," +
                $"\"waypointCount\":{wpCount}," +
                $"\"hasNavMesh\":{(hasNavMesh ? "true" : "false")}," +
                $"\"waypoints\":{waypointsJson}," +
                $"\"message\":\"session_status reply\"}}";

            api.ReplyPublic(json);
            Log($"session_status → {json}");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void PublishInfo(string message)
        {
            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message  = message,
                Type     = MessageType.Info,
                Duration = 2.5f
            });
        }

        private void Log(string msg)
        {
            if (_verboseLogs)
                Debug.Log($"[FlutterUnityBridge] {msg}");
        }

        private void LogWarn(string msg)  => Debug.LogWarning($"[FlutterUnityBridge] {msg}");
        private void LogError(string msg) => Debug.LogError($"[FlutterUnityBridge] {msg}");
    }
}