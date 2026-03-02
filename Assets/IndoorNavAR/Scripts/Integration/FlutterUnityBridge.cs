// File: FlutterUnityBridge.cs  — v2 (sin UI, optimizado para voz)
// Receptor único de comandos JSON desde Flutter.
// Delega TODO a VoiceCommandAPI — no tiene lógica propia.
// El GameObject que contenga este componente DEBE llamarse "FlutterBridge".

using UnityEngine;

namespace IndoorNavAR.Integration
{
    public class FlutterUnityBridge : MonoBehaviour
    {
        // ── Esquema de comando que envía Flutter ──────────────────────────────
        // {
        //   "action": "navigate_to" | "stop_navigation" | "nav_status"
        //           | "list_waypoints" | "create_waypoint" | "remove_waypoint"
        //           | "clear_waypoints" | "save_session" | "load_session",
        //   "name": "Sala 101",   // para navigate_to / create_waypoint / remove_waypoint
        // }
        // ─────────────────────────────────────────────────────────────────────

        [System.Serializable]
        private class Cmd
        {
            public string action;
            public string name;       // waypoint name / destino
        }

        /// <summary>
        /// Punto de entrada. Llamado por Flutter con:
        ///   UnityPlayer.UnitySendMessage("FlutterBridge", "OnFlutterCommand", "{...}")
        /// </summary>
        public void OnFlutterCommand(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;

            Cmd cmd;
            try { cmd = JsonUtility.FromJson<Cmd>(json); }
            catch { Debug.LogWarning($"[Bridge] JSON inválido: {json}"); return; }

            if (cmd == null || string.IsNullOrWhiteSpace(cmd.action)) return;

            var api = VoiceCommandAPI.Instance;
            if (api == null) { Debug.LogError("[Bridge] VoiceCommandAPI no disponible"); return; }

            switch (cmd.action)
            {
                case "navigate_to":       api.NavigateTo(cmd.name);               break;
                case "stop_navigation":   api.StopNavigation();                    break;
                case "nav_status":        api.GetNavigationStatus();               break;
                case "list_waypoints":    api.ListWaypoints();                     break;
                case "create_waypoint":   api.CreateWaypointAtAgent(cmd.name);    break;
                case "remove_waypoint":   api.RemoveWaypoint(cmd.name);           break;
                case "clear_waypoints":   api.ClearWaypoints();                   break;
                case "save_session":      api.SaveSession();                       break;
                case "load_session":      api.LoadSession();                       break;
                default:
                    Debug.LogWarning($"[Bridge] Acción desconocida: {cmd.action}");
                    break;
            }
        }
    }
}