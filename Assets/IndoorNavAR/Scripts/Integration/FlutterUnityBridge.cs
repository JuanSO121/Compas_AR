// File: FlutterUnityBridge.cs
// ✅ v4.7 — Fix navigate_to encolado cuando IsSceneReady=false tras resume,
//           aunque la sesión ya esté cargada y Flutter esté en estado ready.
//
// ════════════════════════════════════════════════════════════════════════════
// CAMBIOS v4.6 → v4.7
// ════════════════════════════════════════════════════════════════════════════
//
//  PROBLEMA RAÍZ (Bug 2 — segunda apertura):
//    Al cerrar y reabrir la app, SceneReadyNotifier.OnApplicationPause(false)
//    llama ResetSceneReadyForResume() → IsSceneReady = false.
//    Luego espera 0.5s y re-envía scene_ready.
//
//    Si el usuario da un comando de voz ANTES de que ese scene_ready llegue
//    a ser procesado por Unity, el comando se encola:
//      [Bridge] ⏳ Escena no lista — encolando: {"action":"navigate_to",...}
//
//    El problema es que PersistenceManager ya cargó la sesión, Flutter
//    ya está en estado ready, todo funciona — solo falta ese scene_ready
//    de 0.5s. El usuario ve que su comando "no funcionó".
//
//  SOLUCIÓN v4.7:
//    En OnFlutterCommand(), si IsSceneReady=false pero PersistenceManager
//    ya completó la carga de sesión (IsSessionLoadCompleted=true),
//    auto-reparar IsSceneReady=true y procesar el comando directamente.
//
//    Esto es seguro porque:
//      a) PersistenceManager.IsSessionLoadCompleted=true implica que ya
//         se envió scene_ready y session_loaded al menos una vez.
//      b) El estado IsSceneReady=false en este contexto es temporal —
//         el scene_ready del resume va a llegar de todos modos en 0.5s.
//      c) Auto-reparar IsSceneReady evita comandos perdidos sin side effects.
//
//  TODOS LOS COMPORTAMIENTOS DE v4.6 SE CONSERVAN ÍNTEGRAMENTE.

using System.Collections.Generic;
using UnityEngine;
using IndoorNavAR.Segmentation;
using IndoorNavAR.Core;

namespace IndoorNavAR.Integration
{
    public class FlutterUnityBridge : MonoBehaviour
    {
        [System.Serializable]
        private class Cmd
        {
            public string action;
            public string name;
            public bool   isSpeaking;
            public int    priority;
            public string text;
            public bool   interrupt;
        }

        // ── Estado del handshake ──────────────────────────────────────────────

        public static bool IsSceneReady { get; private set; } = false;
        private static readonly Queue<string> _pendingCommands = new Queue<string>();

        // ── ✅ v4.6 — Reset para ciclo pause/resume ───────────────────────────

        public static void ResetSceneReadyForResume()
        {
            IsSceneReady = false;
            _pendingCommands.Clear();
            Debug.Log("[Bridge] 🔄 ResetSceneReadyForResume() — IsSceneReady=false, cola limpia.");
        }

        // ── Llamado por SceneReadyNotifier cuando todo está listo ─────────────

        public static void NotifySceneReady(string detail = "")
        {
            if (IsSceneReady)
            {
                Debug.Log("[Bridge] NotifySceneReady() llamado dos veces — ignorado.");
                return;
            }

            IsSceneReady = true;

            string msg  = string.IsNullOrEmpty(detail) ? "Escena AR lista" : detail;
            string json = $"{{\"action\":\"scene_ready\",\"ok\":true," +
                          $"\"message\":\"{EscapeJson(msg)}\"}}";
            VoiceCommandAPI.Instance?.ReplyPublic(json);
            Debug.Log($"[Bridge] ✅ scene_ready enviado a Flutter: {msg}");

            HandleSessionStatus();
            VoiceCommandAPI.Instance?.ListWaypoints();

            if (_pendingCommands.Count > 0)
            {
                Debug.Log($"[Bridge] Procesando {_pendingCommands.Count} comando(s) en cola...");
                while (_pendingCommands.Count > 0)
                {
                    string pending = _pendingCommands.Dequeue();
                    Debug.Log($"[Bridge] ⏩ Ejecutando comando pendiente: {pending}");
                    ProcessCommand(pending);
                }
            }
        }

        // ── Punto de entrada ──────────────────────────────────────────────────

        public void OnFlutterCommand(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;

            if (!IsSceneReady)
            {
                // Comandos que siempre se procesan aunque la escena no esté lista
                if (json.Contains("\"ping_scene\""))
                {
                    HandlePingScene();
                    return;
                }

                if (json.Contains("\"tts_status\"") ||
                    json.Contains("\"tracking_state\""))
                {
                    ProcessCommand(json);
                    return;
                }

                // ✅ v4.7 FIX: Auto-reparar IsSceneReady si la sesión ya cargó.
                //
                // Contexto: flutter envió un comando (ej: navigate_to) pero Unity
                // tiene IsSceneReady=false porque está en el período de 0.5s del
                // resume cycle (ResetSceneReadyForResume → scene_ready aún no llegó).
                //
                // Si PersistenceManager ya terminó de cargar la sesión, significa
                // que scene_ready ya fue enviado al menos una vez en esta sesión.
                // Es seguro auto-reparar IsSceneReady=true y procesar el comando.
                var pm = UnityEngine.Object.FindFirstObjectByType<PersistenceManager>();
                if (pm != null && pm.IsSessionLoadCompleted)
                {
                    Debug.LogWarning("[Bridge] ⚠️ [v4.7] IsSceneReady=false pero sesión ya cargada. " +
                                     "Auto-reparando IsSceneReady=true y procesando directamente. " +
                                     $"Cmd: {json.Substring(0, Mathf.Min(80, json.Length))}...");
                    IsSceneReady = true;
                    ProcessCommand(json);
                    return;
                }

                Debug.LogWarning($"[Bridge] ⏳ Escena no lista — encolando: {json}");
                _pendingCommands.Enqueue(json);
                return;
            }

            ProcessCommand(json);
        }

        // ── ping_scene ────────────────────────────────────────────────────────

        private static void HandlePingScene()
        {
            if (IsSceneReady)
            {
                string ready = "{\"action\":\"scene_ready\",\"ok\":true," +
                               "\"message\":\"ping_scene — escena ya lista\"}";
                VoiceCommandAPI.Instance?.ReplyPublic(ready);
                Debug.Log("[Bridge] ping_scene → scene_ready (ya lista)");
            }
            else
            {
                // ✅ v4.7: Si la sesión ya cargó, responder como ready aunque
                // IsSceneReady sea false temporalmente por el resume cycle.
                var pm = UnityEngine.Object.FindFirstObjectByType<PersistenceManager>();
                if (pm != null && pm.IsSessionLoadCompleted)
                {
                    IsSceneReady = true;
                    string ready = "{\"action\":\"scene_ready\",\"ok\":true," +
                                   "\"message\":\"ping_scene — sesión cargada (auto-reparado)\"}";
                    VoiceCommandAPI.Instance?.ReplyPublic(ready);
                    Debug.Log("[Bridge] ping_scene → scene_ready (auto-reparado desde IsSessionLoadCompleted)");
                }
                else
                {
                    string loading = "{\"action\":\"scene_loading\",\"ok\":false," +
                                     "\"message\":\"Escena AR inicializando...\"}";
                    VoiceCommandAPI.Instance?.ReplyPublic(loading);
                    Debug.Log("[Bridge] ping_scene → scene_loading (aún cargando)");
                }
            }
        }

        // ── session_status ────────────────────────────────────────────────────

        private static void HandleSessionStatus()
        {
            var pm  = UnityEngine.Object.FindFirstObjectByType<PersistenceManager>();
            var api = VoiceCommandAPI.Instance;

            if (pm == null || api == null)
            {
                api?.ReplyPublic("{\"action\":\"session_status\",\"ok\":false," +
                                 "\"message\":\"PersistenceManager no disponible\"}");
                return;
            }

            bool loaded     = pm.AutoLoadResult;
            bool hasNavMesh = pm.HasSavedNavMesh;

            int wpCount = 0;
            var wm = UnityEngine.Object.FindFirstObjectByType<IndoorNavAR.Core.Managers.WaypointManager>();
            if (wm != null) wpCount = wm.WaypointCount;

            string json = $"{{\"action\":\"session_status\",\"ok\":true," +
                          $"\"loaded\":{(loaded ? "true" : "false")}," +
                          $"\"waypointCount\":{wpCount}," +
                          $"\"hasNavMesh\":{(hasNavMesh ? "true" : "false")}}}";

            api.ReplyPublic(json);
            Debug.Log($"[Bridge] session_status → Flutter: loaded={loaded} wp={wpCount} navmesh={hasNavMesh}");
        }

        // ── Procesamiento de comandos ─────────────────────────────────────────

        private static void ProcessCommand(string json)
        {
            Cmd cmd;
            try { cmd = JsonUtility.FromJson<Cmd>(json); }
            catch { Debug.LogWarning($"[Bridge] JSON inválido: {json}"); return; }

            if (cmd == null || string.IsNullOrWhiteSpace(cmd.action)) return;

            var api = VoiceCommandAPI.Instance;
            if (api == null) { Debug.LogError("[Bridge] VoiceCommandAPI no disponible"); return; }

            switch (cmd.action)
            {
                case "ping_scene":
                    HandlePingScene();
                    break;

                case "session_status":
                    HandleSessionStatus();
                    break;

                case "navigate_to":     api.NavigateTo(cmd.name);            break;
                case "stop_navigation": api.StopNavigation();                 break;
                case "nav_status":      api.GetNavigationStatus();            break;

                case "list_waypoints":  api.ListWaypoints();                  break;
                case "create_waypoint": api.CreateWaypointAtAgent(cmd.name); break;
                case "remove_waypoint": api.RemoveWaypoint(cmd.name);        break;
                case "clear_waypoints": api.ClearWaypoints();                 break;

                case "save_session":
                    api.SaveSession();
                    break;

                case "load_session":
                    var pmCheck = UnityEngine.Object.FindFirstObjectByType<PersistenceManager>();
                    if (pmCheck != null && pmCheck.IsSessionLoadCompleted)
                    {
                        Debug.LogWarning("[Bridge] ⚠️ load_session recibido pero la sesión ya fue " +
                                         "cargada automáticamente. Usa 'session_status' para consultar.");
                    }
                    api.LoadSession();
                    break;

                case "tts_status":
                    string ttsJson = $"{{\"isSpeaking\":{(cmd.isSpeaking ? "true" : "false")}," +
                                     $"\"priority\":{cmd.priority}}}";
                    api.OnTTSStatus(ttsJson);
                    break;

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
                    var mediator = UnityEngine.Object.FindFirstObjectByType<IndoorNavAR.Navigation.ObstacleRerouteMediator>();
                    if (mediator != null)
                        mediator.SimulateObstacleFromFlutter();
                    else
                        Debug.LogWarning("[Bridge] reroute_obstacle: ObstacleRerouteMediator no disponible.");
                    break;

                case "segmentation_ratio":
                {
                    var worker = IndoorNavAR.Segmentation.ObstacleSegmentationWorker.Instance;
                    if (worker != null)
                        api.SendSegmentationRatio(worker.ObstacleRatio, worker.FloorRatio);
                    else
                        Debug.LogWarning("[Bridge] segmentation_ratio: ObstacleSegmentationWorker.Instance es null.");
                    break;
                }

                case "toggle_seg_mask":
                {
                    var segCtrl = UnityEngine.Object.FindFirstObjectByType<SegmentationController>();
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

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string EscapeJson(string s) =>
            s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") ?? "";

        // ── Reset en Editor ───────────────────────────────────────────────────

#if UNITY_EDITOR
        [UnityEngine.RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            IsSceneReady = false;
            _pendingCommands.Clear();
            Debug.Log("[Bridge] Estado estático reseteado (Editor Play mode).");
        }
#endif
    }
}