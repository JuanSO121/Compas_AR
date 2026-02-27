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
    /// </summary>
    public class FlutterUnityBridge : MonoBehaviour
    {
        [Header("Bridge")]
        [SerializeField] private bool _verboseLogs = true;

        [Header("Dependencias")]
        [SerializeField] private WaypointManager _waypointManager;
        [SerializeField] private NavigationManager _navigationManager;
        [SerializeField] private PersistenceManager _persistenceManager;

        [Serializable]
        private class FlutterCommand
        {
            public string action;
            public string waypointName;
            public float x;
            public float y;
            public float z;
        }

        private void Awake()
        {
            _waypointManager ??= FindFirstObjectByType<WaypointManager>();
            _navigationManager ??= FindFirstObjectByType<NavigationManager>();
            _persistenceManager ??= FindFirstObjectByType<PersistenceManager>();
        }

        /// <summary>
        /// Entrada principal para comandos JSON desde Flutter.
        /// Ejemplos de action:
        /// - navigate_to_waypoint
        /// - add_waypoint
        /// - clear_waypoints
        /// - save_session
        /// - load_session
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

            switch (cmd.action)
            {
                case "navigate_to_waypoint":
                    NavigateToWaypointByName(cmd.waypointName);
                    break;

                case "add_waypoint":
                    AddWaypoint(cmd.x, cmd.y, cmd.z);
                    break;

                case "clear_waypoints":
                    _waypointManager?.ClearAllWaypoints();
                    PublishInfo("Waypoints eliminados desde Flutter.");
                    break;

                case "save_session":
                    _ = SaveSessionAsync();
                    break;

                case "load_session":
                    _ = LoadSessionAsync();
                    break;

                case "stop_navigation":
                    _navigationManager?.StopNavigation();
                    PublishInfo("Navegación detenida desde Flutter.");
                    break;

                default:
                    LogWarn($"Acción no soportada: {cmd.action}");
                    break;
            }
        }

        public void NavigateToWaypointByName(string waypointName)
        {
            if (_waypointManager == null || _navigationManager == null)
            {
                LogError("Dependencias no disponibles para navegación.");
                return;
            }

            if (string.IsNullOrWhiteSpace(waypointName))
            {
                LogWarn("waypointName vacío.");
                return;
            }

            WaypointData waypoint = _waypointManager.SearchWaypointsByName(waypointName).Find(w =>
                w != null && w.WaypointName.Equals(waypointName, StringComparison.OrdinalIgnoreCase));

            if (waypoint == null)
            {
                LogWarn($"No se encontró waypoint: {waypointName}");
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

        private async Task SaveSessionAsync()
        {
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
            if (_persistenceManager == null)
            {
                LogWarn("PersistenceManager no disponible.");
                return;
            }

            bool ok = await _persistenceManager.LoadSession();
            Log(ok ? "Sesión cargada desde Flutter." : "Falló carga de sesión desde Flutter.");
        }

        private void PublishInfo(string message)
        {
            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message = message,
                Type = MessageType.Info,
                Duration = 2.5f
            });
        }

        private void Log(string msg)
        {
            if (_verboseLogs)
                Debug.Log($"[FlutterUnityBridge] {msg}");
        }

        private void LogWarn(string msg) => Debug.LogWarning($"[FlutterUnityBridge] {msg}");
        private void LogError(string msg) => Debug.LogError($"[FlutterUnityBridge] {msg}");
    }
}
