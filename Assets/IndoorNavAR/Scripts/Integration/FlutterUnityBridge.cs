// File: FlutterUnityBridge.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Integration/
// ✅ v4.2 — Añade case "toggle_seg_mask" para botón Mask del panel Flutter
//
// ════════════════════════════════════════════════════════════════════════════
// CAMBIOS v4.1 → v4.2
// ════════════════════════════════════════════════════════════════════════════
//
//  ÚNICO CAMBIO: nuevo case "toggle_seg_mask".
//
//  CONTEXTO:
//    ArNavigationScreen v8.5 añade un botón "Mask" en el panel de testing
//    que envía { "action": "toggle_seg_mask" } a Unity.
//    SegmentationController.cs v9.2 expone el método público
//    SetOverlayVisible(bool) y la propiedad OverlayVisible para que este
//    case pueda leer el estado actual y alternarlo correctamente.
//
//  DISEÑO:
//    FindFirstObjectByType<SegmentationController>() es correcto aquí porque
//    SegmentationController SÍ hereda de MonoBehaviour (a diferencia de
//    ObstacleSegmentationWorker que no hereda de UnityEngine.Object).
//
//  TODOS LOS COMPORTAMIENTOS DE v4.1 SE CONSERVAN ÍNTEGRAMENTE.

using UnityEngine;
using IndoorNavAR.Segmentation;

namespace IndoorNavAR.Integration
{
    public class FlutterUnityBridge : MonoBehaviour
    {
        // ── Esquema completo de comandos ──────────────────────────────────────
        //
        //  Navegación:
        //  { "action": "navigate_to",     "name": "Sala 101" }
        //  { "action": "stop_navigation" }
        //  { "action": "nav_status" }
        //  { "action": "list_waypoints" }
        //  { "action": "create_waypoint", "name": "Entrada" }
        //  { "action": "remove_waypoint", "name": "Entrada" }
        //  { "action": "clear_waypoints" }
        //
        //  Sesión:
        //  { "action": "save_session" }
        //  { "action": "load_session" }
        //
        //  TTS canal de vuelta (v3):
        //  { "action": "tts_status", "isSpeaking": false, "priority": 0 }
        //
        //  v4 — Control de guía de voz desde Flutter:
        //  { "action": "repeat_instruction" }
        //  { "action": "stop_voice" }
        //  { "action": "voice_status" }
        //  { "action": "tts_speak", "text": "...", "priority": 1, "interrupt": false }
        //  { "action": "reroute_obstacle" }
        //  { "action": "segmentation_ratio" }
        //
        //  ✅ v4.2 NUEVO:
        //  { "action": "toggle_seg_mask" }

        [System.Serializable]
        private class Cmd
        {
            public string action;
            public string name;        // waypoint name / destino
            public bool   isSpeaking;  // para tts_status
            public int    priority;    // para tts_status / tts_speak
            public string text;        // para tts_speak (respuesta COMPAS)
            public bool   interrupt;   // para tts_speak
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

                // ── TTS canal de vuelta (v3) ───────────────────────────────────
                case "tts_status":
                    string ttsJson = $"{{\"isSpeaking\":{(cmd.isSpeaking ? "true" : "false")}," +
                                     $"\"priority\":{cmd.priority}}}";
                    api.OnTTSStatus(ttsJson);
                    break;

                // ── Control de guía de voz (v4) ────────────────────────────────

                case "repeat_instruction":
                    var guide = IndoorNavAR.Navigation.Voice.NavigationVoiceGuide.Instance;
                    if (guide != null)
                        guide.RepeatLastInstruction();
                    else
                        Debug.LogWarning("[Bridge] repeat_instruction: NavigationVoiceGuide no disponible.");
                    break;

                case "stop_voice":
                    var guideStop = IndoorNavAR.Navigation.Voice.NavigationVoiceGuide.Instance;
                    if (guideStop != null)
                        guideStop.StopVoiceGuideFromBridge();
                    else
                        Debug.LogWarning("[Bridge] stop_voice: NavigationVoiceGuide no disponible.");
                    break;

                case "voice_status":
                    api.GetVoiceStatus();
                    break;

                case "tts_speak":
                    if (!string.IsNullOrWhiteSpace(cmd.text))
                        api.SpeakArbitraryText(cmd.text, cmd.priority, cmd.interrupt);
                    else
                        Debug.LogWarning("[Bridge] tts_speak: campo 'text' vacío.");
                    break;

                case "reroute_obstacle":
                    var mediator = FindFirstObjectByType<IndoorNavAR.Navigation.ObstacleRerouteMediator>();
                    if (mediator != null)
                        mediator.SimulateObstacleFromFlutter();
                    else
                        Debug.LogWarning("[Bridge] reroute_obstacle: ObstacleRerouteMediator no disponible.");
                    break;

                // ── Segmentación — consulta puntual (v4.1) ────────────────────
                //
                // Nota: desde v9.2 el push es automático desde SegmentationController.
                // Este case mantiene compatibilidad para consultas manuales.
                case "segmentation_ratio":
                {
                    var worker = IndoorNavAR.Segmentation.ObstacleSegmentationWorker.Instance;
                    if (worker != null)
                        api.SendSegmentationRatio(worker.ObstacleRatio, worker.FloorRatio);
                    else
                        Debug.LogWarning("[Bridge] segmentation_ratio: ObstacleSegmentationWorker.Instance es null.");
                    break;
                }

                // ── Toggle máscara de segmentación (v4.2) ─────────────────────
                //
                // Flutter envía { "action": "toggle_seg_mask" } cuando el usuario
                // pulsa el botón "Mask" / "Sin máscara" del panel de testing.
                // SegmentationController hereda de MonoBehaviour → FindFirstObjectByType es válido.
                // Se lee OverlayVisible para alternar el estado actual.
                case "toggle_seg_mask":
                {
                    var segCtrl = FindFirstObjectByType<SegmentationController>();
                    if (segCtrl != null)
                    {
                        bool newState = !segCtrl.OverlayVisible;
                        segCtrl.SetOverlayVisible(newState);
                        Debug.Log($"[Bridge] toggle_seg_mask → overlay={newState}");
                    }
                    else
                    {
                        Debug.LogWarning("[Bridge] toggle_seg_mask: SegmentationController no encontrado.");
                    }
                    break;
                }

                default:
                    Debug.LogWarning($"[Bridge] Acción desconocida: {cmd.action}");
                    break;
            }
        }
    }
}