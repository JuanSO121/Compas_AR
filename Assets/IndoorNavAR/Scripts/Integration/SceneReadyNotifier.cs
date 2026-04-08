// File: SceneReadyNotifier.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Integration/
// ✅ v5.1 — FIX: WaitForSessionLoad() nunca completaba porque IsSessionLoadCompleted
//           siempre era false (bug en PersistenceManager v14.x).
//
// ════════════════════════════════════════════════════════════════════════════
// CAMBIOS v5.0 → v5.1
// ════════════════════════════════════════════════════════════════════════════
//
//  PROBLEMA EN v5.0:
//  ─────────────────────────────────────────────────────────────────────────
//  WaitForSessionLoad() hace polling sobre pm.IsSessionLoadCompleted, que
//  en PersistenceManager v14.x era _autoLoadAttempted && _autoLoadCompleted.
//  Esos flags nunca se ponían en true porque el auto-load fue eliminado pero
//  los flags quedaron huérfanos. Resultado: polling espera 20s hasta timeout,
//  y solo entonces llama NotifySceneReady(). El bridge llega a Ready con 20s
//  de delay, o nunca si el timeout tampoco era suficiente.
//
//  SOLUCIÓN v5.1:
//  ─────────────────────────────────────────────────────────────────────────
//  1. WaitForSessionLoad() ahora también verifica BridgeState.Ready como
//     condición de salida temprana — si PersistenceManager.ReparentWaypointsAfterAlignment()
//     ya llamó NotifySceneReady() directamente (fix en v14.3), el polling
//     termina de inmediato sin esperar el timeout.
//  2. Reducción del _sessionLoadTimeout default a 12s (era 20s). El timeout
//     sigue siendo el último fallback para casos donde PersistenceManager
//     falla completamente, pero ya no es el camino normal.
//  3. Log mejorado para diagnosticar la ruta de salida de WaitForSessionLoad().
//
//  TODOS LOS COMPORTAMIENTOS DE v5.0 SE CONSERVAN ÍNTEGRAMENTE.

using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using IndoorNavAR.Core;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Integration;

namespace IndoorNavAR.Integration
{
    public class SceneReadyNotifier : MonoBehaviour
    {
        // ─── Configuración ────────────────────────────────────────────────────

        [Header("Referencias (auto-detectadas si quedan vacías)")]
        [SerializeField] private PersistenceManager _persistenceManager;
        [SerializeField] private ARSession          _arSession;

        [Header("Configuración")]
        [Tooltip("Segundos máximos esperando VoiceCommandAPI + ARSession.")]
        [SerializeField] private float _maxWaitSeconds     = 10f;

        [Tooltip("Intervalo de polling para verificar subsistemas.")]
        [SerializeField] private float _pollIntervalSeconds = 0.1f;

        [Tooltip("Segundos máximos esperando que LoadSession() complete.\n" +
                 "v5.1: reducido a 12s (era 20s). En v14.3, PersistenceManager llama\n" +
                 "NotifySceneReady() directamente, así que este timeout es solo fallback.")]
        [SerializeField] private float _sessionLoadTimeout  = 12f;

        [Tooltip("Log de progreso.")]
        [SerializeField] private bool  _logProgress         = true;

        [Header("Configuración Resume")]
        [Tooltip("Delay (s) tras resume antes de re-notificar. " +
                 "Da tiempo a ARCore y VoiceCommandAPI para re-inicializarse.")]
        [SerializeField] private float _resumeDelay = 1.5f;

        // ─── Estado interno ───────────────────────────────────────────────────

        private bool _subsystemsNotified = false;

        // ─── Lifecycle ────────────────────────────────────────────────────────

        private void Start()
        {
            _persistenceManager ??= FindFirstObjectByType<PersistenceManager>();
            _arSession          ??= FindFirstObjectByType<ARSession>();

            if (_persistenceManager == null)
                Debug.LogWarning("[SceneReadyNotifier] ⚠️ PersistenceManager no encontrado.");
            if (_arSession == null)
                Debug.LogWarning("[SceneReadyNotifier] ⚠️ ARSession no encontrado.");

            StartCoroutine(WaitForSubsystems());
        }

        // ─── Resume ───────────────────────────────────────────────────────────

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus) return;
            if (!_subsystemsNotified) return;

            Log("▶️ Resume — preparando re-notificación...");
            StartCoroutine(RenotifyAfterResume());
        }

        private IEnumerator RenotifyAfterResume()
        {
            _subsystemsNotified = false;
            FlutterUnityBridge.ResetForResume();

            Log($"⏳ Resume delay: {_resumeDelay}s...");
            yield return new WaitForSeconds(_resumeDelay);

            yield return StartCoroutine(WaitForSubsystems(isResume: true));
        }

        // ─── Coroutine principal: esperar subsistemas ─────────────────────────

        private IEnumerator WaitForSubsystems(bool isResume = false)
        {
            float elapsed = 0f;
            Log($"⏳ Esperando subsistemas{(isResume ? " (resume)" : "")}...");

            while (elapsed < _maxWaitSeconds)
            {
                bool apiReady = VoiceCommandAPI.Instance != null;
                bool arReady  = _arSession != null && _arSession.enabled;

                if (_logProgress && elapsed > 0f && Mathf.RoundToInt(elapsed * 10f) % 10 == 0)
                    Log($"  [{elapsed:F1}s] API={apiReady} | AR={arReady}");

                if (apiReady && arReady)
                {
                    _subsystemsNotified = true;
                    string detail = isResume
                        ? "Escena AR lista — resume desde background"
                        : BuildInitialDetail();

                    Log($"✅ Subsistemas OK en {elapsed:F2}s — NotifySubsystemsReady()");

                    // Paso 1: bridge → SessionLoading (o Ready si sesión ya cargó)
                    FlutterUnityBridge.NotifySubsystemsReady(detail);

                    // Paso 2: esperar carga de sesión → bridge → Ready
                    yield return StartCoroutine(WaitForSessionLoad());
                    yield break;
                }

                yield return new WaitForSeconds(_pollIntervalSeconds);
                elapsed += _pollIntervalSeconds;
            }

            // Timeout
            Log($"⚠️ Timeout {_maxWaitSeconds}s — forzando NotifySubsystemsReady().");
            _subsystemsNotified = true;
            FlutterUnityBridge.NotifySubsystemsReady(
                $"Timeout {_maxWaitSeconds}s — subsistemas parcialmente listos");
            yield return StartCoroutine(WaitForSessionLoad());
        }

        // ─── Coroutine: esperar que LoadSession() complete ────────────────────

        /// <summary>
        /// Espera que el bridge llegue a Ready, lo cual ocurre por una de tres vías:
        ///   A) pm.IsSessionLoadCompleted=true   → NotifySceneReady() desde aquí (v5.0)
        ///   B) BridgeState.Ready ya alcanzado   → PersistenceManager.ReparentWaypointsAfterAlignment()
        ///                                          llamó NotifySceneReady() directamente (v14.3)
        ///   C) pm == null o sin sesión guardada → NotifySceneReady() inmediato
        ///   D) Timeout de _sessionLoadTimeout   → NotifySceneReady() forzado (último fallback)
        /// </summary>
        private IEnumerator WaitForSessionLoad()
        {
            // Salida rápida A: bridge ya en Ready (puede haber llegado por auto-repair
            // en NotifySubsystemsReady, o por llamada directa desde PersistenceManager v14.3)
            if (FlutterUnityBridge.State == BridgeState.Ready)
            {
                Log("WaitForSessionLoad: bridge ya Ready — saltando. (ruta A)");
                yield break;
            }

            if (_persistenceManager == null)
            {
                Log("WaitForSessionLoad: sin PersistenceManager — Ready inmediato. (ruta C)");
                FlutterUnityBridge.NotifySceneReady("Sin PersistenceManager");
                yield break;
            }

            // Si no hay sesión guardada → IsSessionLoadCompleted ya es true (v14.3 lo setea en Start())
            // pero verificamos ambas condiciones por si acaso
            if (!_persistenceManager.HasSavedSession())
            {
                Log("WaitForSessionLoad: sin sesión previa — Ready inmediato. (ruta C)");
                FlutterUnityBridge.NotifySceneReady("Sin sesión previa guardada");
                yield break;
            }

            float elapsed = 0f;
            Log($"⏳ Esperando IsSessionLoadCompleted o BridgeState.Ready (max {_sessionLoadTimeout}s)...");

            while (elapsed < _sessionLoadTimeout)
            {
                // ✅ v5.1 FIX: salida temprana si el bridge ya llegó a Ready.
                // En v14.3, PersistenceManager.ReparentWaypointsAfterAlignment() llama
                // NotifySceneReady() directamente, así que el bridge puede estar en Ready
                // antes de que IsSessionLoadCompleted sea true desde nuestra perspectiva.
                if (FlutterUnityBridge.State == BridgeState.Ready)
                {
                    Log($"WaitForSessionLoad: BridgeState=Ready en {elapsed:F2}s — OK. (ruta B)");
                    yield break;
                }

                if (_persistenceManager.IsSessionLoadCompleted)
                {
                    Log($"✅ Sesión cargada en {elapsed:F2}s — NotifySceneReady(). (ruta A)");
                    FlutterUnityBridge.NotifySceneReady(
                        $"Sesión cargada ({(_persistenceManager.SessionWasRestored ? "restaurada" : "nueva")})");
                    yield break;
                }

                yield return new WaitForSeconds(_pollIntervalSeconds);
                elapsed += _pollIntervalSeconds;
            }

            Log($"⚠️ Timeout {_sessionLoadTimeout}s esperando sesión — forzando Ready. (ruta D)");
            FlutterUnityBridge.NotifySceneReady($"Timeout sesión ({_sessionLoadTimeout}s)");
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private string BuildInitialDetail()
        {
            if (_persistenceManager == null)
                return "Escena AR lista (sin PersistenceManager)";

            bool hasSaved = _persistenceManager.HasSavedSession();
            return hasSaved
                ? "Escena AR lista — cargando sesión previa..."
                : "Escena AR lista — sin sesión previa";
        }

        private void Log(string msg)
        {
            if (_logProgress) Debug.Log($"[SceneReadyNotifier] {msg}");
        }

        // ─── ContextMenu debug ────────────────────────────────────────────────

        [ContextMenu("✅ Forzar Ready ahora")]
        private void DbgForceReady()
        {
            StopAllCoroutines();
            Log("🔧 Ready forzado manualmente.");
            FlutterUnityBridge.NotifySceneReady("Forzado desde ContextMenu");
        }

        [ContextMenu("🔄 Simular Resume")]
        private void DbgSimulateResume() => OnApplicationPause(false);

        [ContextMenu("📊 Estado actual")]
        private void DbgState()
        {
            Debug.Log("══════════════════════════════════════════════");
            Debug.Log($"  SceneReadyNotifier v5.1 — Estado");
            Debug.Log("══════════════════════════════════════════════");
            Debug.Log($"  BridgeState:              {FlutterUnityBridge.State}");
            Debug.Log($"  IsSceneReady (compat):    {FlutterUnityBridge.IsSceneReady}");
            Debug.Log($"  _subsystemsNotified:      {_subsystemsNotified}");
            Debug.Log($"  _resumeDelay:             {_resumeDelay}s");
            Debug.Log($"  VoiceCommandAPI OK:       {VoiceCommandAPI.Instance != null}");
            Debug.Log($"  ARSession OK:             {(_arSession != null && _arSession.enabled)}");
            Debug.Log($"  ARSession state:          {ARSession.state}");
            Debug.Log($"  PersistenceManager:       {(_persistenceManager != null ? "OK" : "NULL")}");
            Debug.Log($"  HasSavedSession:          {_persistenceManager?.HasSavedSession()}");
            Debug.Log($"  IsSessionLoadCompleted:   {_persistenceManager?.IsSessionLoadCompleted}");
            Debug.Log($"  SessionWasRestored:       {_persistenceManager?.SessionWasRestored}");
            Debug.Log("══════════════════════════════════════════════");
        }
    }
}