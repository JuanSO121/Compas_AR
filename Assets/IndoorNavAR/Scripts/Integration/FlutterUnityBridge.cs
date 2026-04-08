// File: FlutterUnityBridge.cs
// ✅ v6.1 — Integra parche v3.1 (navigate_to con ARSession stability check)
//
// ════════════════════════════════════════════════════════════════════════════
// CAMBIOS v6.0 → v6.1
// ════════════════════════════════════════════════════════════════════════════
//
//  INTEGRACIÓN DEL PARCHE v3.1:
//    El parche v3.1 existía como clase partial separada y referenciaba
//    SendMessageToFlutter() (inexistente en v6.0) y duplicaba EscapeJson().
//
//    Cambios aplicados:
//      1. ExecuteNavigateWithStabilityCheck() y ReplyNavigateError() movidos
//         a esta clase (ya no se necesita partial).
//      2. ReplyNavigateError() usa VoiceCommandAPI.Instance?.ReplyPublic()
//         igual que el resto del bridge (reemplaza SendMessageToFlutter).
//      3. EscapeJson() duplicado eliminado del parche — se usa el existente.
//      4. En ProcessCommand(), case "navigate_to" reemplaza la llamada
//         directa a api.NavigateTo(cmd.name) por StartCoroutine().
//      5. Constantes de stability check definidas en esta clase.
//
//  TODOS LOS COMPORTAMIENTOS DE v6.0 SE CONSERVAN ÍNTEGRAMENTE.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using IndoorNavAR.Segmentation;
using IndoorNavAR.Core;

namespace IndoorNavAR.Integration
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Estado del bridge
    // ─────────────────────────────────────────────────────────────────────────

    public enum BridgeState
    {
        Initializing,   // Esperando VoiceCommandAPI + ARSession
        SessionLoading, // Subsistemas listos, cargando sesión
        Ready,          // scene_ready enviado, bridge operativo
        Error           // Timeout o excepción irrecuperable
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Prioridad de comandos en cola
    // ─────────────────────────────────────────────────────────────────────────

    internal enum CommandPriority
    {
        Critical   = 0,  // Siempre procesados, incluso antes de Ready
        Session    = 1,  // load_session, save_session
        Navigation = 2   // navigate_to, waypoints, voice, etc.
    }

    internal readonly struct QueuedCommand
    {
        public readonly string           Json;
        public readonly CommandPriority  Priority;
        public readonly DateTime         EnqueuedAt;

        public QueuedCommand(string json, CommandPriority priority)
        {
            Json       = json;
            Priority   = priority;
            EnqueuedAt = DateTime.UtcNow;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Bridge principal
    // ─────────────────────────────────────────────────────────────────────────

    public class FlutterUnityBridge : MonoBehaviour
    {
        // ── Constantes generales ─────────────────────────────────────────────

        private const int MaxQueueSize    = 64;
        private const int QueueTtlSeconds = 30;

        // ── Constantes de stability check (parche v3.1) ───────────────────────

        /// Tiempo máximo esperando ARSession estable antes de cancelar navigate_to.
        private const float NavStabilityMaxWait = 5f;

        /// Intervalo de polling del estado de ARSession.
        private const float NavStabilityPollInterval = 0.1f;

        /// Tiempo mínimo de ARSession estable antes de lanzar el NavMesh.
        /// Evita que un frame aislado de SessionTracking dispare la navegación
        /// justo antes de otro reset del VIO.
        private const float NavStabilityConfirmTime = 0.3f;

        // ── Estado de la máquina ──────────────────────────────────────────────

        private static BridgeState _state        = BridgeState.Initializing;
        private static readonly object _stateLock = new object();

        public static BridgeState State
        {
            get { lock (_stateLock) return _state; }
        }

        // Compatibilidad con código que lee IsSceneReady
        public static bool IsSceneReady => State == BridgeState.Ready;

        // ── Colas por prioridad ───────────────────────────────────────────────

        private static readonly Queue<QueuedCommand>[] _queues = {
            new Queue<QueuedCommand>(), // P0 Critical
            new Queue<QueuedCommand>(), // P1 Session
            new Queue<QueuedCommand>(), // P2 Navigation
        };

        private static int _flushPending = 0; // Interlocked guard

        // ── Serializable Cmd ─────────────────────────────────────────────────

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

        // ═════════════════════════════════════════════════════════════════════
        //  API pública — transiciones de estado
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Llamado por SceneReadyNotifier cuando VoiceCommandAPI + ARSession
        /// están listos. Transiciona Initializing→SessionLoading o
        /// SessionLoading→Ready si la sesión ya cargó.
        /// </summary>
        public static void NotifySubsystemsReady(string detail = "")
        {
            lock (_stateLock)
            {
                if (_state != BridgeState.Initializing)
                {
                    Debug.Log($"[Bridge] NotifySubsystemsReady ignorado — estado actual: {_state}");
                    return;
                }

                var pm = UnityEngine.Object.FindFirstObjectByType<PersistenceManager>();
                if (pm != null && pm.IsSessionLoadCompleted)
                {
                    // Sesión ya cargó antes de que llegáramos aquí (p.ej. re-arranque rápido)
                    TransitionToReady_NoLock(detail);
                }
                else
                {
                    _state = BridgeState.SessionLoading;
                    Debug.Log($"[Bridge] → SessionLoading: {detail}");
                }
            }

            FlushQueueIfReady();
        }

        /// <summary>
        /// Llamado por PersistenceManager (o SceneReadyNotifier) cuando
        /// LoadSession() completa. Transiciona SessionLoading→Ready.
        /// </summary>
        public static void NotifySceneReady(string detail = "")
        {
            lock (_stateLock)
            {
                if (_state == BridgeState.Ready)
                {
                    Debug.Log("[Bridge] NotifySceneReady() — ya Ready, ignorado.");
                    return;
                }
                TransitionToReady_NoLock(detail);
            }

            FlushQueueIfReady();
        }

        /// <summary>
        /// Llamado por SceneReadyNotifier en OnApplicationPause(false).
        /// Resetea el bridge para el ciclo de resume.
        /// </summary>
        public static void ResetForResume()
        {
            lock (_stateLock)
            {
                _state = BridgeState.Initializing;
                foreach (var q in _queues) q.Clear();
                _flushPending = 0;
            }
            Debug.Log("[Bridge] 🔄 ResetForResume() — estado=Initializing, colas limpias.");
        }

        // Alias de compatibilidad con SceneReadyNotifier v4.x
        public static void ResetSceneReadyForResume() => ResetForResume();

        // ═════════════════════════════════════════════════════════════════════
        //  Punto de entrada desde Flutter
        // ═════════════════════════════════════════════════════════════════════

        public void OnFlutterCommand(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;

            var priority = ClassifyCommand(json);

            // Comandos críticos: se procesan siempre, sin importar estado
            if (priority == CommandPriority.Critical)
            {
                ProcessCommand(json);
                return;
            }

            var currentState = State;

            if (currentState == BridgeState.Ready)
            {
                ProcessCommand(json);
                return;
            }

            // Intentar auto-repair si la sesión ya cargó pero el estado no avanzó
            if (TryAutoRepair(json)) return;

            // Encolar con TTL y límite de tamaño
            Enqueue(json, priority);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Clasificación de comandos
        // ═════════════════════════════════════════════════════════════════════

        private static CommandPriority ClassifyCommand(string json)
        {
            // P0 Critical — bypass total
            if (json.Contains("\"ping_scene\""))     return CommandPriority.Critical;
            if (json.Contains("\"tts_status\""))     return CommandPriority.Critical;
            if (json.Contains("\"tracking_state\"")) return CommandPriority.Critical;
            if (json.Contains("\"session_status\"")) return CommandPriority.Critical;

            // P1 Session
            if (json.Contains("\"load_session\""))   return CommandPriority.Session;
            if (json.Contains("\"save_session\""))   return CommandPriority.Session;

            // P2 Navigation (default)
            return CommandPriority.Navigation;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Auto-repair
        // ═════════════════════════════════════════════════════════════════════

        private static bool TryAutoRepair(string json)
        {
            var pm = UnityEngine.Object.FindFirstObjectByType<PersistenceManager>();
            if (pm == null || !pm.IsSessionLoadCompleted) return false;

            Debug.LogWarning($"[Bridge] ⚠️ [v6] Auto-repair: sesión cargada pero estado={State}. " +
                             $"Forzando Ready. Cmd: {json.Substring(0, Mathf.Min(80, json.Length))}...");

            lock (_stateLock)
            {
                if (_state != BridgeState.Ready)
                    TransitionToReady_NoLock("auto-repair desde IsSessionLoadCompleted");
            }

            ProcessCommand(json);
            FlushQueueIfReady();
            return true;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Cola inteligente
        // ═════════════════════════════════════════════════════════════════════

        private static void Enqueue(string json, CommandPriority priority)
        {
            lock (_stateLock)
            {
                var q = _queues[(int)priority];

                // Descartar comandos expirados antes de encolar
                PurgeExpired_NoLock(q);

                if (q.Count >= MaxQueueSize)
                {
                    Debug.LogWarning($"[Bridge] Cola P{(int)priority} llena ({MaxQueueSize}) — descartando más antiguo.");
                    q.Dequeue();
                }

                q.Enqueue(new QueuedCommand(json, priority));
                Debug.Log($"[Bridge] ⏳ Encolado P{(int)priority}: {json.Substring(0, Mathf.Min(60, json.Length))}...");
            }
        }

        private static void PurgeExpired_NoLock(Queue<QueuedCommand> q)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-QueueTtlSeconds);
            while (q.Count > 0 && q.Peek().EnqueuedAt < cutoff)
            {
                var expired = q.Dequeue();
                Debug.Log($"[Bridge] 🗑 Comando expirado descartado: {expired.Json.Substring(0, Mathf.Min(40, expired.Json.Length))}");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Drain de cola (orden P0→P1→P2)
        // ═════════════════════════════════════════════════════════════════════

        private static void FlushQueueIfReady()
        {
            if (State != BridgeState.Ready) return;

            // Garantía de un solo flush simultáneo
            if (Interlocked.CompareExchange(ref _flushPending, 1, 0) != 0) return;

            try
            {
                int processed = 0;
                for (int lane = 0; lane < _queues.Length; lane++)
                {
                    List<QueuedCommand> batch;
                    lock (_stateLock)
                    {
                        var q = _queues[lane];
                        PurgeExpired_NoLock(q);
                        if (q.Count == 0) continue;

                        batch = new List<QueuedCommand>(q.Count);
                        while (q.Count > 0) batch.Add(q.Dequeue());
                    }

                    foreach (var cmd in batch)
                    {
                        Debug.Log($"[Bridge] ⏩ Drain P{lane}: {cmd.Json.Substring(0, Mathf.Min(60, cmd.Json.Length))}...");
                        ProcessCommand(cmd.Json);
                        processed++;
                    }
                }

                if (processed > 0)
                    Debug.Log($"[Bridge] ✅ Drain completo: {processed} comando(s) procesado(s).");
            }
            finally
            {
                Interlocked.Exchange(ref _flushPending, 0);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Transición interna a Ready (debe llamarse con _stateLock tomado)
        // ═════════════════════════════════════════════════════════════════════

        private static void TransitionToReady_NoLock(string detail)
        {
            _state = BridgeState.Ready;

            string msg  = string.IsNullOrEmpty(detail) ? "Escena AR lista" : detail;
            string json = $"{{\"action\":\"scene_ready\",\"ok\":true," +
                          $"\"message\":\"{EscapeJson(msg)}\"}}";

            VoiceCommandAPI.Instance?.ReplyPublic(json);
            Debug.Log($"[Bridge] ✅ → Ready: {msg}");

            // Emitir estado de sesión y waypoints
            EmitSessionStatus_NoLock();
            VoiceCommandAPI.Instance?.ListWaypoints();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Handlers especializados
        // ═════════════════════════════════════════════════════════════════════

        private static void HandlePingScene()
        {
            var currentState = State;

            if (currentState == BridgeState.Ready)
            {
                VoiceCommandAPI.Instance?.ReplyPublic(
                    "{\"action\":\"scene_ready\",\"ok\":true," +
                    "\"message\":\"ping_scene — escena ya lista\"}");
                Debug.Log("[Bridge] ping_scene → scene_ready (Ready)");
                return;
            }

            // Intentar auto-repair
            var pm = UnityEngine.Object.FindFirstObjectByType<PersistenceManager>();
            if (pm != null && pm.IsSessionLoadCompleted)
            {
                lock (_stateLock)
                {
                    if (_state != BridgeState.Ready)
                        TransitionToReady_NoLock("ping_scene — auto-repair");
                }
                FlushQueueIfReady();
                return;
            }

            string stateMsg = currentState == BridgeState.SessionLoading
                ? "Escena inicializada, cargando sesión..."
                : "Escena AR inicializando...";

            VoiceCommandAPI.Instance?.ReplyPublic(
                $"{{\"action\":\"scene_loading\",\"ok\":false," +
                $"\"message\":\"{EscapeJson(stateMsg)}\"," +
                $"\"bridgeState\":\"{currentState}\"}}");

            Debug.Log($"[Bridge] ping_scene → scene_loading (estado={currentState})");
        }

        /// <summary>
        /// Emite session_status. Fuente de verdad unificada para
        /// HandleSessionStatus y HandleLoadSessionAlreadyLoaded de v4.x.
        /// </summary>
        private static void EmitSessionStatus_NoLock()
        {
            var pm  = UnityEngine.Object.FindFirstObjectByType<PersistenceManager>();
            var api = VoiceCommandAPI.Instance;
            if (pm == null || api == null) return;

            bool loaded     = pm.SessionWasRestored;
            bool hasNavMesh = pm.HasSavedNavMesh;

            int wpCount = 0;
            var wm = UnityEngine.Object.FindFirstObjectByType<IndoorNavAR.Core.Managers.WaypointManager>();
            if (wm != null) wpCount = wm.WaypointCount;

            // session_status (compatibilidad v5.x Flutter)
            api.ReplyPublic(
                $"{{\"action\":\"session_status\",\"ok\":true," +
                $"\"loaded\":{B(loaded)}," +
                $"\"waypointCount\":{wpCount}," +
                $"\"hasNavMesh\":{B(hasNavMesh)}}}");

            // session_loaded (compatibilidad v5.x Flutter)
            string msg = loaded
                ? (wpCount > 0 ? $"Sesión restaurada — {wpCount} baliza(s)" : "Sesión restaurada — sin balizas")
                : "Sin sesión previa guardada";

            api.ReplyPublic(
                $"{{\"action\":\"session_loaded\",\"ok\":true," +
                $"\"loaded\":{B(loaded)}," +
                $"\"waypointCount\":{wpCount}," +
                $"\"hasNavMesh\":{B(hasNavMesh)}," +
                $"\"message\":\"{EscapeJson(msg)}\"}}");

            Debug.Log($"[Bridge] EmitSessionStatus: loaded={loaded} wp={wpCount} navmesh={hasNavMesh}");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ✅ v6.1 (parche v3.1) — navigate_to con ARSession stability check
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Reemplaza la llamada directa a NavigateTo().
        /// Espera hasta <see cref="NavStabilityMaxWait"/> segundos a que
        /// ARSession esté en SessionTracking antes de calcular la ruta.
        /// Si no se estabiliza, responde con ok=false para que Flutter
        /// pueda informar al usuario.
        /// </summary>
        private IEnumerator ExecuteNavigateWithStabilityCheck(string waypointName)
        {
            float elapsed   = 0f;
            float stableFor = 0f;

            while (elapsed < NavStabilityMaxWait)
            {
                if (ARSession.state == ARSessionState.SessionTracking)
                {
                    stableFor += NavStabilityPollInterval;

                    if (stableFor >= NavStabilityConfirmTime)
                        break;
                }
                else
                {
                    // Reset del contador si pierde tracking
                    stableFor = 0f;
                }

                yield return new WaitForSeconds(NavStabilityPollInterval);
                elapsed += NavStabilityPollInterval;
            }

            if (ARSession.state != ARSessionState.SessionTracking)
            {
                Debug.LogWarning(
                    $"[Bridge] navigate_to '{waypointName}' cancelado — " +
                    $"ARSession={ARSession.state} tras {elapsed:F1}s de espera"
                );
                ReplyNavigateError(waypointName, "AR inestable — intenta de nuevo");
                yield break;
            }

            Debug.Log(
                $"[Bridge] navigate_to '{waypointName}' — " +
                $"ARSession estable tras {elapsed:F1}s, lanzando NavMesh"
            );

            VoiceCommandAPI.Instance?.NavigateTo(waypointName);
        }

        /// <summary>
        /// Envía una respuesta de error a Flutter para navigate_to.
        /// Usa VoiceCommandAPI.ReplyPublic() igual que el resto del bridge.
        /// </summary>
        private void ReplyNavigateError(string waypointName, string message)
        {
            var payload = $"{{" +
                          $"\"action\":\"navigate_to\"," +
                          $"\"ok\":false," +
                          $"\"name\":\"{EscapeJson(waypointName)}\"," +
                          $"\"message\":\"{EscapeJson(message)}\"" +
                          $"}}";

            VoiceCommandAPI.Instance?.ReplyPublic(payload);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Procesamiento de comandos
        // ═════════════════════════════════════════════════════════════════════

        private static void ProcessCommand(string json)
        {
            Cmd cmd;
            try { cmd = JsonUtility.FromJson<Cmd>(json); }
            catch { Debug.LogWarning($"[Bridge] JSON inválido: {json}"); return; }

            if (cmd == null || string.IsNullOrWhiteSpace(cmd.action)) return;

            var api = VoiceCommandAPI.Instance;
            if (api == null) { Debug.LogError("[Bridge] VoiceCommandAPI no disponible"); return; }

            // Necesitamos la instancia del MonoBehaviour para StartCoroutine
            var bridge = UnityEngine.Object.FindFirstObjectByType<FlutterUnityBridge>();

            switch (cmd.action)
            {
                case "ping_scene":
                    HandlePingScene();
                    break;

                case "session_status":
                    lock (_stateLock) EmitSessionStatus_NoLock();
                    break;

                // ✅ v6.1: navigate_to usa stability check antes de calcular ruta
                case "navigate_to":
                    if (bridge != null)
                        bridge.StartCoroutine(bridge.ExecuteNavigateWithStabilityCheck(cmd.name));
                    else
                    {
                        Debug.LogWarning("[Bridge] navigate_to: FlutterUnityBridge no encontrado, navegando sin stability check.");
                        api.NavigateTo(cmd.name);
                    }
                    break;

                case "stop_navigation": api.StopNavigation();                  break;
                case "nav_status":      api.GetNavigationStatus();             break;

                case "list_waypoints":  api.ListWaypoints();                   break;
                case "create_waypoint": api.CreateWaypointAtAgent(cmd.name);  break;
                case "remove_waypoint": api.RemoveWaypoint(cmd.name);         break;
                case "clear_waypoints": api.ClearWaypoints();                  break;

                case "save_session":
                    api.SaveSession();
                    break;

                case "load_session":
                {
                    var pmCheck = UnityEngine.Object.FindFirstObjectByType<PersistenceManager>();
                    if (pmCheck != null && pmCheck.IsSessionLoadCompleted)
                    {
                        Debug.Log("[Bridge] [v6] load_session — sesión ya cargada, emitiendo inmediato.");
                        lock (_stateLock) EmitSessionStatus_NoLock();
                        api.ListWaypoints();
                    }
                    else
                    {
                        Debug.Log("[Bridge] load_session — ejecutando LoadAsync().");
                        api.LoadSession();
                    }
                    break;
                }

                case "tts_status":
                {
                    string ttsJson = $"{{\"isSpeaking\":{B(cmd.isSpeaking)},\"priority\":{cmd.priority}}}";
                    api.OnTTSStatus(ttsJson);
                    break;
                }

                case "repeat_instruction":
                {
                    var guide = IndoorNavAR.Navigation.Voice.NavigationVoiceGuide.Instance;
                    if (guide != null) guide.RepeatLastInstruction();
                    else Debug.LogWarning("[Bridge] repeat_instruction: NavigationVoiceGuide no disponible.");
                    break;
                }

                case "stop_voice":
                {
                    var guide = IndoorNavAR.Navigation.Voice.NavigationVoiceGuide.Instance;
                    if (guide != null) guide.StopVoiceGuideFromBridge();
                    else Debug.LogWarning("[Bridge] stop_voice: NavigationVoiceGuide no disponible.");
                    break;
                }

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
                {
                    var med = UnityEngine.Object.FindFirstObjectByType<IndoorNavAR.Navigation.ObstacleRerouteMediator>();
                    if (med != null) med.SimulateObstacleFromFlutter();
                    else Debug.LogWarning("[Bridge] reroute_obstacle: ObstacleRerouteMediator no disponible.");
                    break;
                }

                case "segmentation_ratio":
                {
                    var worker = ObstacleSegmentationWorker.Instance;
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
                    else Debug.LogWarning("[Bridge] toggle_seg_mask: SegmentationController no encontrado.");
                    break;
                }

                default:
                    Debug.LogWarning($"[Bridge] Acción desconocida: {cmd.action}");
                    break;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Helpers
        // ═════════════════════════════════════════════════════════════════════

        private static string B(bool v) => v ? "true" : "false";

        private static string EscapeJson(string s) =>
            s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") ?? "";

        // ═════════════════════════════════════════════════════════════════════
        //  Reset en Editor
        // ═════════════════════════════════════════════════════════════════════

#if UNITY_EDITOR
        [UnityEngine.RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            lock (_stateLock)
            {
                _state = BridgeState.Initializing;
                foreach (var q in _queues) q.Clear();
                _flushPending = 0;
            }
            Debug.Log("[Bridge] Estado estático reseteado (Editor Play mode).");
        }
#endif

        // ═════════════════════════════════════════════════════════════════════
        //  ContextMenu debug
        // ═════════════════════════════════════════════════════════════════════

#if UNITY_EDITOR
        [ContextMenu("📊 Estado actual")]
        private void DbgState()
        {
            Debug.Log("══════════════════════════════════════════════");
            Debug.Log($"  FlutterUnityBridge v6.1 — Estado: {State}");
            Debug.Log("══════════════════════════════════════════════");
            for (int i = 0; i < _queues.Length; i++)
                Debug.Log($"  Cola P{i}: {_queues[i].Count} comandos");
            var pm = UnityEngine.Object.FindFirstObjectByType<PersistenceManager>();
            Debug.Log($"  SessionWasRestored:      {pm?.SessionWasRestored}");
            Debug.Log($"  IsSessionLoadCompleted:  {pm?.IsSessionLoadCompleted}");
            Debug.Log($"  ARSession.state:         {ARSession.state}");
            Debug.Log("══════════════════════════════════════════════");
        }

        [ContextMenu("✅ Simular NotifySceneReady")]
        private void DbgForceReady() => NotifySceneReady("Forzado desde ContextMenu");

        [ContextMenu("🔄 Simular Resume")]
        private void DbgResume() => ResetForResume();

        [ContextMenu("🚿 Forzar drain de cola")]
        private void DbgFlush() => FlushQueueIfReady();

        [ContextMenu("🧭 Test: navigate_to con stability check")]
        private void DbgNavigateTest() =>
            StartCoroutine(ExecuteNavigateWithStabilityCheck("TestWaypoint"));
#endif
    }
}