    // File: SceneReadyNotifier.cs
    // Carpeta: Assets/IndoorNavAR/Scripts/Integration/
    // ✅ v4.1 — Re-notifica scene_ready tras resume de background
    //
    // ════════════════════════════════════════════════════════════════════════════
    // CAMBIOS v4.0 → v4.1
    // ════════════════════════════════════════════════════════════════════════════
    //
    //  PROBLEMA RAÍZ (v4.0):
    //    IsSceneReady en FlutterUnityBridge es estático y nunca se reseteaba
    //    en builds de Android al volver del background.
    //
    //    Flujo problemático:
    //      App → background → resume
    //      Flutter: _sceneReady = false (espera nuevo scene_ready)
    //      Unity:   SceneReadyNotifier._notified = true → no re-notifica
    //      Unity:   NotifySceneReady() → IsSceneReady==true → ignorado
    //      Resultado: Flutter espera scene_ready que nunca llega.
    //               Todos los comandos (navigate_to, etc.) se encolan y
    //               nunca se ejecutan.
    //
    //    Evidencia en log:
    //      [Bridge] ⏳ Escena no lista — encolando: {"action":"navigate_to",...}
    //
    //  SOLUCIÓN v4.1:
    //    OnApplicationPause(false) detecta el resume y ejecuta el ciclo:
    //      1. ResetSceneReadyForResume() — IsSceneReady=false, cola limpia
    //      2. WaitForSeconds(0.5f)       — ARCore re-inicializa
    //      3. NotifySceneReady()         — scene_ready re-enviado a Flutter
    //
    //    _notified se resetea también para permitir el re-envío.
    //    El delay de 0.5s es suficiente para que VoiceCommandAPI y ARSession
    //    vuelvan a estar disponibles tras el resume.
    //
    //  FLUJO CORRECTO TRAS RESUME:
    //    t=0:    OnApplicationPause(false)
    //    t=0:    ResetSceneReadyForResume() → IsSceneReady=false
    //    t=0.5s: NotifySceneReady("Resume...") → scene_ready enviado
    //    t=0.5s: Flutter recibe scene_ready → _sceneReady=true
    //    t=0.5s+: Comandos se ejecutan normalmente
    //
    //  TODO LO DEMÁS ES IDÉNTICO A v4.0.

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
            [Header("Referencias (auto-detectadas si quedan vacías)")]
            [SerializeField] private PersistenceManager _persistenceManager;
            [SerializeField] private ARSession          _arSession;

            [Header("Configuración v4.0")]
            [Tooltip("Segundos máximos esperando que exista VoiceCommandAPI y ARSession.")]
            [SerializeField] private float _maxWaitSeconds = 10f;

            [Tooltip("Intervalo de polling para verificar subsistemas mínimos.")]
            [SerializeField] private float _pollIntervalSeconds = 0.1f;

            [Tooltip("Log del progreso de inicialización.")]
            [SerializeField] private bool _logProgress = true;

            [Header("Configuración v4.1 — Resume")]
            [Tooltip("Segundos de espera tras resume antes de re-enviar scene_ready. " +
                    "Da tiempo a ARCore y VoiceCommandAPI para re-inicializarse.")]
            [SerializeField] private float _resumeDelay = 1.5f;

            // Guard: evitar doble notificación en la misma sesión
            private bool _notified = false;

            // ─── Lifecycle ────────────────────────────────────────────────────────

            private void Start()
            {
                _persistenceManager ??= FindFirstObjectByType<PersistenceManager>();
                _arSession          ??= FindFirstObjectByType<ARSession>();

                if (_persistenceManager == null)
                    Debug.LogWarning("[SceneReadyNotifier] ⚠️ PersistenceManager no encontrado.");

                if (_arSession == null)
                    Debug.LogWarning("[SceneReadyNotifier] ⚠️ ARSession no encontrado.");

                StartCoroutine(WaitForMinimalSubsystems());
            }

            // ─── ✅ v4.1 — Resume desde background ───────────────────────────────

            /// <summary>
            /// ✅ v4.1 — Detecta cuando la app vuelve del background (pauseStatus=false)
            /// y re-envía scene_ready a Flutter para desbloquear el bridge.
            ///
            /// Sin esto, Flutter queda esperando scene_ready indefinidamente tras
            /// un ciclo background/foreground y todos los comandos quedan encolados.
            /// </summary>
            private void OnApplicationPause(bool pauseStatus)
            {
                if (pauseStatus) return; // Entrando en background — no hacer nada

                // Volviendo al foreground
                if (!_notified) return; // Nunca llegamos a notificar — WaitForMinimalSubsystems lo hará

                Log("▶️ Resume desde background — preparando re-notificación de scene_ready...");
                StartCoroutine(RenotifyAfterResume());
            }

            private IEnumerator RenotifyAfterResume()
            {
                _notified = false;
                FlutterUnityBridge.ResetSceneReadyForResume();

                // Reutilizar el mismo polling robusto del arranque inicial
                yield return StartCoroutine(WaitForMinimalSubsystems());
            }

            // ─── Coroutine principal (arranque inicial) ───────────────────────────

            private IEnumerator WaitForMinimalSubsystems()
            {
                float elapsed = 0f;
                Log("⏳ Esperando subsistemas mínimos para scene_ready...");

                while (elapsed < _maxWaitSeconds)
                {
                    bool apiReady = VoiceCommandAPI.Instance != null;
                    bool arReady  = _arSession != null && _arSession.enabled;

                    if (_logProgress && elapsed > 0f && Mathf.RoundToInt(elapsed * 10f) % 10 == 0)
                        Log($"  [{elapsed:F1}s] API={apiReady} | ARSession={arReady}");

                    if (apiReady && arReady)
                    {
                        if (_notified) yield break;
                        string detail = BuildInitialDetail();
                        Log($"✅ Subsistemas mínimos listos en {elapsed:F2}s — enviando scene_ready.");
                        NotifyReady(detail);
                        yield break;
                    }

                    yield return new WaitForSeconds(_pollIntervalSeconds);
                    elapsed += _pollIntervalSeconds;
                }

                if (_notified) yield break;
                Log($"⚠️ Timeout {_maxWaitSeconds}s — enviando scene_ready igualmente.");
                NotifyReady($"Timeout {_maxWaitSeconds}s — subsistemas parcialmente listos");
            }

            // ─── Detail builders ──────────────────────────────────────────────────

            private string BuildInitialDetail()
            {
                if (_persistenceManager == null)
                    return "Escena AR lista (sin PersistenceManager)";

                bool hasSaved = _persistenceManager.HasSavedSession();
                return hasSaved
                    ? "Escena AR lista — cargando sesión previa en segundo plano..."
                    : "Escena AR lista — sin sesión previa guardada";
            }

            private string BuildResumeDetail()
            {
                return "Escena AR lista — resume desde background";
            }

            // ─── Notificación ─────────────────────────────────────────────────────

            private void NotifyReady(string detail)
            {
                if (_notified) return;
                _notified = true;
                FlutterUnityBridge.NotifySceneReady(detail);
            }

            // ─── Helpers ──────────────────────────────────────────────────────────

            private void Log(string msg)
            {
                if (_logProgress) Debug.Log($"[SceneReadyNotifier] {msg}");
            }

            // ─── ContextMenu debug ────────────────────────────────────────────────

            [ContextMenu("✅ Forzar scene_ready ahora")]
            private void DbgForceReady()
            {
                StopAllCoroutines();
                Log("🔧 scene_ready forzado manualmente.");
                NotifyReady("Forzado manualmente desde ContextMenu");
            }

            [ContextMenu("🔄 Simular Resume desde background")]
            private void DbgSimulateResume()
            {
                Log("🔧 Resume simulado desde ContextMenu.");
                OnApplicationPause(false);
            }

            [ContextMenu("📊 Estado actual")]
            private void DbgState()
            {
                Debug.Log("══════════════════════════════════════════════");
                Debug.Log("  SceneReadyNotifier v4.1 — Estado");
                Debug.Log("══════════════════════════════════════════════");
                Debug.Log($"  IsSceneReady (Bridge):    {FlutterUnityBridge.IsSceneReady}");
                Debug.Log($"  _notified:                {_notified}");
                Debug.Log($"  VoiceCommandAPI OK:       {VoiceCommandAPI.Instance != null}");
                Debug.Log($"  ARSession OK:             {(_arSession != null && _arSession.enabled)}");
                Debug.Log($"  ARSession state:          {ARSession.state}");
                Debug.Log($"  PersistenceManager:       {(_persistenceManager != null ? "encontrado" : "NULL")}");
                Debug.Log($"  HasSavedSession:          {_persistenceManager?.HasSavedSession()}");
                Debug.Log($"  IsSessionLoadCompleted:   {_persistenceManager?.IsSessionLoadCompleted}");
                Debug.Log("══════════════════════════════════════════════");
            }
        }
    }