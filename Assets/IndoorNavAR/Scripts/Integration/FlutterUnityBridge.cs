// File: FlutterUnityBridge.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Integration/
// ✅ v3 — Agrega action="tts_status" para canal de vuelta TTS Flutter → Unity
//
// ============================================================================
//  CAMBIOS v2 → v3
// ============================================================================
//
//  ÚNICO CAMBIO: case "tts_status" en el switch de OnFlutterCommand().
//
//  CONTEXTO:
//    En v8.2 de VoiceCommandAPI, Flutter confirma el fin real del TTS
//    enviando action="tts_status" con {isSpeaking: false, priority: 0}.
//    VoiceCommandAPI.OnTTSStatus() ya existía para recibir este JSON,
//    pero FlutterUnityBridge no tenía el case para enrutarlo.
//
//    Sin este case, Flutter no podía liberar _ttsBusy en Unity → la guía
//    se bloqueaba hasta que el TTSFallbackTimeout (20s) la liberaba.
//
//  TODO LO DEMÁS ES IDÉNTICO A v2.

using UnityEngine;

namespace IndoorNavAR.Integration
{
    public class FlutterUnityBridge : MonoBehaviour
    {
        // ── Esquema de comandos que envía Flutter ─────────────────────────────
        //
        //  Comandos de navegación y sesión (sin cambios desde v2):
        //  { "action": "navigate_to",     "name": "Sala 101" }
        //  { "action": "stop_navigation" }
        //  { "action": "nav_status" }
        //  { "action": "list_waypoints" }
        //  { "action": "create_waypoint", "name": "Entrada" }
        //  { "action": "remove_waypoint", "name": "Entrada" }
        //  { "action": "clear_waypoints" }
        //  { "action": "save_session" }
        //  { "action": "load_session" }
        //
        //  ✅ v3 NUEVO — Canal de vuelta TTS (Flutter → Unity):
        //  { "action": "tts_status", "isSpeaking": false, "priority": 0 }
        //    Enviado por VoiceNavigationService cuando el engine TTS termina
        //    de hablar. Libera _ttsBusy en NavigationVoiceGuide con el evento
        //    REAL de completion (no con estimación de palabras).
        //
        // ─────────────────────────────────────────────────────────────────────

        [System.Serializable]
        private class Cmd
        {
            public string action;
            public string name;        // waypoint name / destino
            public bool   isSpeaking;  // para tts_status
            public int    priority;    // para tts_status
        }

        /// <summary>
        /// Punto de entrada único. Llamado por Flutter con:
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
                // ── Navegación ─────────────────────────────────────────────────
                case "navigate_to":     api.NavigateTo(cmd.name);            break;
                case "stop_navigation": api.StopNavigation();                 break;
                case "nav_status":      api.GetNavigationStatus();            break;

                // ── Waypoints ──────────────────────────────────────────────────
                case "list_waypoints":  api.ListWaypoints();                  break;
                case "create_waypoint": api.CreateWaypointAtAgent(cmd.name); break;
                case "remove_waypoint": api.RemoveWaypoint(cmd.name);        break;
                case "clear_waypoints": api.ClearWaypoints();                 break;

                // ── Sesión ─────────────────────────────────────────────────────
                case "save_session":    api.SaveSession();                    break;
                case "load_session":    api.LoadSession();                    break;

                // ✅ v3: Canal de vuelta TTS ─────────────────────────────────────
                // Flutter envía este comando cuando VoiceNavigationService
                // confirma que el TTS terminó (o empezó). VoiceCommandAPI
                // lo reenvía al EventBus y notifica a NavigationVoiceGuide.
                case "tts_status":
                    // Serializar de vuelta a JSON para reusar OnTTSStatus()
                    // que ya tiene la lógica de parseo y notificación.
                    string ttsJson = $"{{\"isSpeaking\":{(cmd.isSpeaking ? "true" : "false")}," +
                                     $"\"priority\":{cmd.priority}}}";
                    api.OnTTSStatus(ttsJson);
                    break;

                default:
                    Debug.LogWarning($"[Bridge] Acción desconocida: {cmd.action}");
                    break;
            }
        }
    }
}