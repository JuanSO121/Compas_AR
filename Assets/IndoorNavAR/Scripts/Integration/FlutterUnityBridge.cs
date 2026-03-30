// File: FlutterUnityBridge.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Integration/
// ✅ v4 — Comandos de voz ejecutables desde Flutter + COMPAS classifier
//
// ════════════════════════════════════════════════════════════════════════════
// CAMBIOS v3 → v4
// ════════════════════════════════════════════════════════════════════════════
//
//  NUEVOS COMANDOS (Flutter → Unity):
//
//  1. repeat_instruction
//     { "action": "repeat_instruction" }
//     Repite la última instrucción de navegación hablada.
//     Llama NavigationVoiceGuide.RepeatLastInstruction().
//     Usa p=1 e interrupt=false: se encola sin cortar lo que haya en curso.
//
//  2. stop_voice
//     { "action": "stop_voice" }
//     Detiene la guía de voz (sin cancelar la navegación física).
//     Llama NavigationVoiceGuide.StopVoiceGuideFromBridge().
//
//  3. voice_status
//     { "action": "voice_status" }
//     Solicita el estado actual de la guía de voz.
//     Responde con JSON via VoiceCommandAPI: isGuiding, ttsBusy,
//     destination, remainingSteps, nextInstruction.
//
//  4. tts_speak
//     { "action": "tts_speak", "text": "Texto libre", "priority": 1, "interrupt": false }
//     Permite a Flutter disparar un TTS arbitrario (ej. respuesta del
//     clasificador COMPAS conversacional) sin pasar por el bus de eventos.
//     Útil para el prompt conversacional de COMPAS.
//
//  CONTEXT — integración con COMPAS classifier:
//
//     Flutter clasifica el comando de voz del usuario con el LLM:
//       label=START_NAVIGATION → action="navigate_to"
//       label=STOP             → action="stop_navigation"
//       label=REPEAT           → action="repeat_instruction"
//       label=STATUS           → action="voice_status"
//       label=HELP             → action="tts_speak" con texto empático de COMPAS
//       label=<conversacional> → action="tts_speak" con respuesta generada
//
//     Unity no necesita conocer el prompt del clasificador. Solo procesa
//     el JSON resultante. La arquitectura es Flutter-first para el NLU.
//
//  TODOS LOS COMANDOS DE v3 SE CONSERVAN ÍNTEGRAMENTE.

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

                // ── Control de guía de voz (v4 NUEVOS) ────────────────────────

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
                // Prioridad recomendada:
                //   p=1 para respuestas informativas (encola detrás del TTS actual)
                //   p=2 para instrucciones importantes (puede preemptir p≤1)
                //   p=3 SOLO para urgencias (obstáculo, emergencia) — usar con cuidado
                case "tts_speak":
                    if (!string.IsNullOrWhiteSpace(cmd.text))
                        api.SpeakArbitraryText(cmd.text, cmd.priority, cmd.interrupt);
                    else
                        Debug.LogWarning("[Bridge] tts_speak: campo 'text' vacío.");
                    break;

                default:
                    Debug.LogWarning($"[Bridge] Acción desconocida: {cmd.action}");
                    break;
            }
        }
    }
}