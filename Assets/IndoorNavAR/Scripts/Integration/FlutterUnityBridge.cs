// File: FlutterUnityBridge.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Integration/
// ✅ v4.1 — Fix errores de compilación en case "segmentation_ratio"
//
// ════════════════════════════════════════════════════════════════════════════
// CAMBIOS v4 → v4.1
// ════════════════════════════════════════════════════════════════════════════
//
//  CORRECCIONES en case "segmentation_ratio":
//
//  FIX 1 — CS0311: ObstacleSegmentationWorker no hereda de UnityEngine.Object
//    ANTES: FindFirstObjectByType<ObstacleSegmentationWorker>()
//    AHORA: ObstacleSegmentationWorker.Instance
//    (el worker ya expone un singleton estático Instance)
//
//  FIX 2 — CS0136: variable local 'json' colisiona con parámetro del método
//    ANTES: string json = $"{{...}}"
//    AHORA: string segJson = $"{{...}}"
//
//  FIX 3 — CS0122: VoiceCommandAPI.Reply() es private
//    ANTES: VoiceCommandAPI.Instance?.Reply(json)
//    AHORA: VoiceCommandAPI.Instance?.SendSegmentationRatio(obstacle, floor)
//    Se añade el método público SendSegmentationRatio() en VoiceCommandAPI
//    (ver VoiceCommandAPI.cs v8.6).
//
//  TODOS LOS COMPORTAMIENTOS DE v4 SE CONSERVAN ÍNTEGRAMENTE.

using UnityEngine;

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
        //  ✅ v4 NUEVOS — Control de guía de voz desde Flutter:
        //  { "action": "repeat_instruction" }
        //  { "action": "stop_voice" }
        //  { "action": "voice_status" }
        //  { "action": "tts_speak", "text": "...", "priority": 1, "interrupt": false }
        //  { "action": "reroute_obstacle" }
        //  { "action": "segmentation_ratio" }

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

                // Repite la última instrucción. Diseñado para el comando
                // de voz "repetir" / "¿qué dijiste?" del usuario.
                // COMPAS classifier → label=REPEAT → action="repeat_instruction"
                case "repeat_instruction":
                    var guide = IndoorNavAR.Navigation.Voice.NavigationVoiceGuide.Instance;
                    if (guide != null)
                        guide.RepeatLastInstruction();
                    else
                        Debug.LogWarning("[Bridge] repeat_instruction: NavigationVoiceGuide no disponible.");
                    break;

                // Detiene la guía de voz sin cancelar la navegación física.
                // Útil si el usuario quiere silencio temporal.
                // COMPAS classifier → label=STOP con intent=voice_only → action="stop_voice"
                case "stop_voice":
                    var guideStop = IndoorNavAR.Navigation.Voice.NavigationVoiceGuide.Instance;
                    if (guideStop != null)
                        guideStop.StopVoiceGuideFromBridge();
                    else
                        Debug.LogWarning("[Bridge] stop_voice: NavigationVoiceGuide no disponible.");
                    break;

                // Solicita el estado de la guía de voz. La respuesta se envía
                // de vuelta a Flutter via VoiceCommandAPI.Reply().
                // COMPAS classifier → label=STATUS → action="voice_status"
                case "voice_status":
                    api.GetVoiceStatus();
                    break;

                // Habla texto libre generado por Flutter (respuesta COMPAS).
                // Permite que el clasificador conversacional envíe mensajes
                // empáticos sin pasar por el bus de eventos de Unity.
                // COMPAS classifier → label=HELP/conversacional → action="tts_speak"
                case "tts_speak":
                    if (!string.IsNullOrWhiteSpace(cmd.text))
                        api.SpeakArbitraryText(cmd.text, cmd.priority, cmd.interrupt);
                    else
                        Debug.LogWarning("[Bridge] tts_speak: campo 'text' vacío.");
                    break;

                // Simula un obstáculo desde Flutter para forzar recálculo de ruta.
                case "reroute_obstacle":
                    var mediator = FindFirstObjectByType<IndoorNavAR.Navigation.ObstacleRerouteMediator>();
                    if (mediator != null)
                        mediator.SimulateObstacleFromFlutter();
                    else
                        Debug.LogWarning("[Bridge] reroute_obstacle: ObstacleRerouteMediator no disponible.");
                    break;

                // ── Segmentación (v4.1 FIX) ───────────────────────────────────
                //
                // FIX 1: ObstacleSegmentationWorker no hereda de UnityEngine.Object,
                //        por lo que FindFirstObjectByType<T> no compila.
                //        Usamos el singleton estático Instance que ya expone el worker.
                //
                // FIX 2: renombramos la variable local de 'json' a 'segJson'
                //        para evitar la colisión con el parámetro del método.
                //
                // FIX 3: Reply() es private en VoiceCommandAPI. Se delega al
                //        nuevo método público SendSegmentationRatio() (v8.6).
                case "segmentation_ratio":
                {
                    var worker = IndoorNavAR.Segmentation.ObstacleSegmentationWorker.Instance;
                    if (worker != null)
                    {
                        // FIX 3: delegamos al método público — VoiceCommandAPI
                        //        construye el JSON internamente y llama Reply().
                        api.SendSegmentationRatio(worker.ObstacleRatio, worker.FloorRatio);
                    }
                    else
                    {
                        Debug.LogWarning("[Bridge] segmentation_ratio: ObstacleSegmentationWorker.Instance es null. " +
                                         "¿SegmentationController ya inicializó el worker?");
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