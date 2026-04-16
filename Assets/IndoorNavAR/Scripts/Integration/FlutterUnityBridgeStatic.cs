// File: FlutterUnityBridgeStatic.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Integration/
//
// Parte estática del bridge Unity → Flutter.
// Complementa FlutterUnityBridge.cs (receptor Flutter → Unity).
//
// Provee:
//   - BridgeState (enum)
//   - FlutterUnityBridge.State               (propiedad estática)
//   - FlutterUnityBridge.IsSceneReady        (propiedad estática, compat)
//   - FlutterUnityBridge.NotifySubsystemsReady()
//   - FlutterUnityBridge.NotifySceneReady()
//   - FlutterUnityBridge.ResetForResume()
//
// Todos los mensajes se envían a Flutter mediante VoiceCommandAPI.ReplyPublic()
// (mismo canal que usa PersistenceManager para session_loaded, etc.).
// Si VoiceCommandAPI no está disponible, el mensaje se encola y se reintenta
// en el próximo NotifySceneReady() o al llamar FlushPendingMessages().

using System.Collections.Generic;
using UnityEngine;
using IndoorNavAR.Core.Managers;

namespace IndoorNavAR.Integration
{
    // ═══════════════════════════════════════════════════════════════════════
    // Enum de estado del bridge
    // ═══════════════════════════════════════════════════════════════════════

    public enum BridgeState
    {
        /// <summary>Estado inicial — no se ha notificado nada.</summary>
        Idle,

        /// <summary>Subsistemas (VoiceCommandAPI + ARSession) listos.
        /// Se está esperando que PersistenceManager termine de cargar la sesión.</summary>
        SessionLoading,

        /// <summary>Todo listo — escena completamente inicializada.</summary>
        Ready
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Parte estática — se declara como partial para coexistir con
    // FlutterUnityBridge.cs (la parte MonoBehaviour) en el mismo namespace.
    // ═══════════════════════════════════════════════════════════════════════

    public partial class FlutterUnityBridge
    {
        // ─── Estado ───────────────────────────────────────────────────────

        private static BridgeState _state = BridgeState.Idle;

        /// <summary>Estado actual del bridge.</summary>
        public static BridgeState State => _state;

        /// <summary>Compatibilidad: true cuando State == Ready.</summary>
        public static bool IsSceneReady => _state == BridgeState.Ready;

        // ─── Cola de mensajes pendientes ──────────────────────────────────

        private static readonly Queue<string> _pendingMessages = new Queue<string>();

        // ─── API pública estática ─────────────────────────────────────────

        /// <summary>
        /// Llamado por SceneReadyNotifier cuando VoiceCommandAPI y ARSession
        /// están listos pero la sesión todavía se está cargando.
        /// Transiciona: Idle → SessionLoading (o se mantiene si ya está en Ready).
        /// </summary>
        public static void NotifySubsystemsReady(string detail = "")
        {
            // Auto-repair: si ya estamos en Ready no retrocedemos.
            if (_state == BridgeState.Ready)
            {
                Debug.Log($"[FlutterUnityBridge] NotifySubsystemsReady ignorado — ya en Ready. detail='{detail}'");
                return;
            }

            _state = BridgeState.SessionLoading;
            Debug.Log($"[FlutterUnityBridge] → SessionLoading. detail='{detail}'");

            string json = $"{{\"action\":\"subsystems_ready\"," +
                          $"\"detail\":\"{Escape(detail)}\"}}";
            Send(json);
        }

        /// <summary>
        /// Llamado cuando la escena está completamente lista:
        /// subsistemas OK + sesión cargada (o confirmada que no hay sesión).
        /// Transiciona: cualquier estado → Ready.
        /// </summary>
        public static void NotifySceneReady(string detail = "")
        {
            _state = BridgeState.Ready;
            Debug.Log($"[FlutterUnityBridge] → Ready. detail='{detail}'");

            string json = $"{{\"action\":\"scene_ready\"," +
                          $"\"detail\":\"{Escape(detail)}\"}}";
            Send(json);

            // Intentar vaciar cola de mensajes que no pudieron enviarse antes.
            FlushPendingMessages();
        }

        /// <summary>
        /// Llamado por SceneReadyNotifier.RenotifyAfterResume() antes de
        /// reiniciar el flujo de notificación tras volver del background.
        /// Resetea el estado a Idle para que el ciclo completo vuelva a correr.
        /// </summary>
        public static void ResetForResume()
        {
            _state = BridgeState.Idle;
            _pendingMessages.Clear();
            Debug.Log("[FlutterUnityBridge] ResetForResume — estado → Idle.");

            string json = "{\"action\":\"bridge_reset\",\"reason\":\"resume\"}";
            Send(json);
        }

        /// <summary>
        /// Reenvía mensajes encolados cuando VoiceCommandAPI no estaba disponible.
        /// Se llama automáticamente en NotifySceneReady(), pero también puede
        /// invocarse manualmente desde VoiceCommandAPI cuando termina de inicializarse.
        /// </summary>
        public static void FlushPendingMessages()
        {
            var api = VoiceCommandAPI.Instance;
            if (api == null) return;

            while (_pendingMessages.Count > 0)
            {
                string msg = _pendingMessages.Dequeue();
                api.ReplyPublic(msg);
                Debug.Log($"[FlutterUnityBridge] Flush → {msg}");
            }
        }

        // ─── Envío interno ────────────────────────────────────────────────

        private static void Send(string json)
        {
            var api = VoiceCommandAPI.Instance;
            if (api != null)
            {
                api.ReplyPublic(json);
                Debug.Log($"[FlutterUnityBridge] Sent: {json}");
            }
            else
            {
                // VoiceCommandAPI aún no listo — encolar para envío posterior.
                _pendingMessages.Enqueue(json);
                Debug.LogWarning($"[FlutterUnityBridge] VoiceCommandAPI no disponible — encolado: {json}");
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────

        /// <summary>Escapa caracteres problemáticos para JSON inline.</summary>
        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "");
        }
    }
}