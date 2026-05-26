// File: SceneReadyNotifier.cs
// ✅ v5.2 — FIX: timeout insuficiente + diagnóstico mejorado + guard doble Ready
//
// ════════════════════════════════════════════════════════════════════════════
// CAMBIOS v5.1 → v5.2
// ════════════════════════════════════════════════════════════════════════════
//
//  BUG #1 — _sessionLoadTimeout demasiado corto (12s):
//  ─────────────────────────────────────────────────────────────────────────
//  En v5.1, el timeout se redujo de 20s a 12s. Con el Infinix X6887
//  (dispositivo lento), el flujo completo de sesión restaurada toma:
//    - GC compacting:           ~2s
//    - LoadSession:             ~1s
//    - WaitForAlignmentOrTimeout: hasta 6s (FIX #15)
//    - ReparentWaypointsAfterAlignment: ~1s
//    = ~10s mínimo, hasta 12s en días lentos
//  Con timeout=12s, el margen era cero. Con 25s hay margen suficiente.
//
//  BUG #2 — WaitForSessionLoad() no distingue entre "bridge Ready por
//  NavigationManager" y "bridge Ready por timeout anterior":
//  ─────────────────────────────────────────────────────────────────────────
//  Si SceneReadyNotifier se reiniciaba tras Resume (ResetForResume() →
//  BridgeState.Idle), el polling verificaba BridgeState.Ready, pero si
//  NavigationManager nunca llegó a llamar NotifySceneReady(), el bridge
//  quedaba en SessionLoading para siempre. Añadido log de diagnóstico
//  que permite ver exactamente en qué estado está el bridge en cada poll.
//
//  BUG #3 — _subsystemsNotified no se reseteaba en RenotifyAfterResume()
//  antes de FlutterUnityBridge.ResetForResume(), causando que la coroutine
//  principal se saltara el ciclo de espera si _subsystemsNotified=true.
//  Ahora el reset se hace en el orden correcto.
//
//  TODOS LOS COMPORTAMIENTOS DE v5.1 SE CONSERVAN ÍNTEGRAMENTE.

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

        [Tooltip("Segundos máximos esperando que LoadSession() complete.\n\n" +
                 "v5.2: aumentado a 25s (era 12s).\n" +
                 "Flujo en Infinix X6887 con sesión restaurada:\n" +
                 "  GC (~2s) + LoadSession (~1s) + WaitAlign (~6s) + Reparent (~1s) = ~10s\n" +
                 "Con 25s hay margen suficiente para dispositivos lentos.\n" +
                 "Este timeout es último fallback: NavigationManager llama\n" +
                 "NotifySceneReady() directamente, por lo que el polling\n" +
                 "normalmente sale antes de los 10s.")]
        [SerializeField] private float _sessionLoadTimeout  = 25f;

        [Tooltip("Log de progreso.")]
        [SerializeField] private bool  _logProgress = true;

        [Header("Configuración Resume")]
        [Tooltip("Delay (s) tras resume antes de re-notificar.")]
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
            // ✅ FIX #15 BUG #3: resetear _subsystemsNotified ANTES de
            // FlutterUnityBridge.ResetForResume() para evitar que la coroutine
            // principal detecte _subsystemsNotified=true y se salte el ciclo.
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
                    FlutterUnityBridge.NotifySubsystemsReady(detail);

                    yield return StartCoroutine(WaitForSessionLoad());
                    yield break;
                }

                yield return new WaitForSeconds(_pollIntervalSeconds);
                elapsed += _pollIntervalSeconds;
            }

            // Timeout de subsistemas
            Log($"⚠️ Timeout {_maxWaitSeconds}s — forzando NotifySubsystemsReady().");
            _subsystemsNotified = true;
            FlutterUnityBridge.NotifySubsystemsReady(
                $"Timeout {_maxWaitSeconds}s — subsistemas parcialmente listos");
            yield return StartCoroutine(WaitForSessionLoad());
        }

        // ─── Coroutine: esperar que LoadSession() complete ────────────────────

        /// <summary>
        /// ✅ v5.2 — Espera que el bridge llegue a Ready por cualquiera de estas vías:
        ///
        ///   A) pm.IsSessionLoadCompleted=true → NotifySceneReady() desde aquí
        ///   B) BridgeState.Ready ya alcanzado → NavigationManager lo llamó directamente
        ///   C) pm == null o sin sesión guardada → NotifySceneReady() inmediato
        ///   D) Timeout _sessionLoadTimeout (25s) → NotifySceneReady() forzado
        ///
        /// LOG DE DIAGNÓSTICO: cada 2s imprime el estado exacto del bridge y del
        /// PersistenceManager para facilitar la depuración en dispositivos lentos.
        /// </summary>
        private IEnumerator WaitForSessionLoad()
        {
            // Ruta B: bridge ya en Ready al entrar
            if (FlutterUnityBridge.State == BridgeState.Ready)
            {
                Log("WaitForSessionLoad: bridge ya Ready al entrar — OK. (ruta B)");
                yield break;
            }

            if (_persistenceManager == null)
            {
                Log("WaitForSessionLoad: sin PersistenceManager — Ready inmediato. (ruta C)");
                FlutterUnityBridge.NotifySceneReady("Sin PersistenceManager");
                yield break;
            }

            if (!_persistenceManager.HasSavedSession())
            {
                Log("WaitForSessionLoad: sin sesión previa — Ready inmediato. (ruta C)");
                FlutterUnityBridge.NotifySceneReady("Sin sesión previa guardada");
                yield break;
            }

            float elapsed = 0f;
            Log($"⏳ Esperando IsSessionLoadCompleted o BridgeState.Ready " +
                $"(max {_sessionLoadTimeout}s)...");

            while (elapsed < _sessionLoadTimeout)
            {
                // ✅ Ruta B: salida temprana si NavigationManager ya llamó NotifySceneReady
                if (FlutterUnityBridge.State == BridgeState.Ready)
                {
                    Log($"✅ WaitForSessionLoad: BridgeState=Ready en {elapsed:F2}s. (ruta B)");
                    yield break;
                }

                // ✅ Ruta A: PersistenceManager reporta carga completa
                if (_persistenceManager.IsSessionLoadCompleted)
                {
                    Log($"✅ WaitForSessionLoad: IsSessionLoadCompleted=true en {elapsed:F2}s. (ruta A)");
                    FlutterUnityBridge.NotifySceneReady(
                        $"Sesión {(_persistenceManager.SessionWasRestored ? "restaurada" : "nueva")}");
                    yield break;
                }

                // ✅ v5.2: Log de diagnóstico cada 2s para depuración en device
                if (Mathf.RoundToInt(elapsed * 10f) % 20 == 0)
                {
                    Log($"  [{elapsed:F1}s/{_sessionLoadTimeout}s] " +
                        $"BridgeState={FlutterUnityBridge.State} | " +
                        $"IsSessionLoadCompleted={_persistenceManager.IsSessionLoadCompleted} | " +
                        $"SessionWasRestored={_persistenceManager.SessionWasRestored} | " +
                        $"HasSavedSession={_persistenceManager.HasSavedSession()}");
                }

                yield return new WaitForSeconds(_pollIntervalSeconds);
                elapsed += _pollIntervalSeconds;
            }

            // Ruta D: último fallback
            Log($"⚠️ Timeout {_sessionLoadTimeout}s — forzando Ready. (ruta D)\n" +
                $"  Estado final: BridgeState={FlutterUnityBridge.State} | " +
                $"IsSessionLoadCompleted={_persistenceManager.IsSessionLoadCompleted}");
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
            Debug.Log($"  SceneReadyNotifier v5.2 — Estado");
            Debug.Log("══════════════════════════════════════════════");
            Debug.Log($"  BridgeState:              {FlutterUnityBridge.State}");
            Debug.Log($"  IsSceneReady (compat):    {FlutterUnityBridge.IsSceneReady}");
            Debug.Log($"  _subsystemsNotified:      {_subsystemsNotified}");
            Debug.Log($"  _sessionLoadTimeout:      {_sessionLoadTimeout}s (v5.2: 25s)");
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