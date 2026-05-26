// File: ARPerformanceManager.cs
// ✅ v2.0 — FIX_VIO: matchFrameRateRequested movido a Awake() + singleton robusto
//
// ============================================================================
//  CAMBIOS v1.x → v2.0  (FIX_VIO suite)
// ============================================================================
//
//  FIX CRÍTICO — matchFrameRateRequested debe setearse en Awake(), ANTES de Start()
//  ──────────────────────────────────────────────────────────────────────────────
//  PROBLEMA v1.x:
//    arSession.matchFrameRateRequested = true se seteaba en Start(). En Unity 6
//    con AR Foundation 6.x, ARCore inicializa su subsistema de sensores durante
//    Awake() del ARSession. Si matchFrameRateRequested llega en Start() (un frame
//    después), ARCore ya comenzó a correr con timestamps desincronizados.
//
//    Resultado directo en los logs:
//      Camera to IMU clock offset (-183ms) exceeds threshold (5ms)
//      → 36x el umbral permitido, desde el PRIMER frame, nunca se recupera.
//
//  FIX v2.0:
//    - Toda la configuración de matchFrameRateRequested y targetFrameRate se
//      hace en Awake() con [DefaultExecutionOrder(-100)] para garantizar que
//      ARPerformanceManager corre ANTES que ARSession y ARCameraManager.
//    - BeginHeavyLoad/EndHeavyLoad sin cambios funcionales.
//
//  SCRIPT EXECUTION ORDER:
//    Agregar en Project Settings → Script Execution Order:
//      ARPerformanceManager → -100  (antes de Default Time = 0)
//    O asegurarse de que el [DefaultExecutionOrder(-100)] en este archivo
//    tome efecto (Unity lo respeta automáticamente sin configuración manual).

using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace IndoorNavAR.AR
{
    [DefaultExecutionOrder(-100)]   // ← Corre ANTES que ARSession (default order=0)
    public class ARPerformanceManager : MonoBehaviour
    {
        public static ARPerformanceManager Instance { get; private set; }

        [Header("AR Foundation")]
        [SerializeField] private ARSession _arSession;

        [Header("Frame Rate")]
        [Tooltip("Target FPS durante operación normal AR.\n" +
                 "30 fps = buena sincronía cámara-IMU en la mayoría de dispositivos Android.")]
        [SerializeField] private int _targetFrameRate = 30;

        [Tooltip("Target FPS reducido durante cargas pesadas (NavMesh bake, restauración).\n" +
                 "20 fps = reduce presión de framebuffers liberando ~15-30 MB RAM.")]
        [SerializeField] private int _heavyLoadFrameRate = 20;

        [Header("Debug")]
        [SerializeField] private bool _logPerformance = true;

        private int  _heavyLoadCount = 0;
        private bool _initialized    = false;

        private bool _trackingStabilized = false;
        private int  _savedFrameRate     = 30;

        // ─── Lifecycle ─────────────────────────────────────────────────────

        private void Awake()
        {
            // Singleton
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            // Auto-resolver ARSession si no está asignado en Inspector
            if (_arSession == null)
                _arSession = FindFirstObjectByType<ARSession>();

            // ✅ FIX_VIO CRÍTICO: matchFrameRateRequested DEBE setearse aquí en Awake(),
            // antes de que ARCore inicialice su pipeline de sensores.
            // Si se setea en Start(), los timestamps ya están desincronizados (-183ms).
            if (_arSession != null)
            {
                _arSession.matchFrameRateRequested = true;
                Log($"✅ [Awake] matchFrameRateRequested = true " +
                    $"(ARSession: {_arSession.gameObject.name})");
            }
            else
            {
                Debug.LogWarning(
                    "[ARPerformanceManager] ⚠️ ARSession no encontrado en Awake().\n" +
                    "matchFrameRateRequested NO pudo setearse antes de ARCore init.\n" +
                    "Verifica que ARPerformanceManager está en la misma escena que ARSession.");
            }

            // Configurar frame rate objetivo
            Application.targetFrameRate = _targetFrameRate;
            Log($"✅ [Awake] targetFrameRate = {_targetFrameRate}");

            _initialized = true;
        }

        private void Start()
        {
            // Verificación de seguridad en Start() — solo para detectar race conditions
            if (_arSession != null && !_arSession.matchFrameRateRequested)
            {
                Debug.LogWarning(
                    "[ARPerformanceManager] ⚠️ matchFrameRateRequested es false en Start().\n" +
                    "ARCore pudo haber iniciado sin frame rate matching. " +
                    "Intentando setear ahora (puede llegar tarde)...");
                _arSession.matchFrameRateRequested = true;
            }
        }

        // ─── API pública — Heavy Load ───────────────────────────────────────

        /// <summary>
        /// Llama cuando inicia una operación pesada (NavMesh bake, restauración de sesión).
        /// Reduce FPS para liberar RAM y CPU del pipeline de framebuffers.
        /// </summary>
        public void BeginHeavyLoad(string reason = "")
        {
            _heavyLoadCount++;
            if (_heavyLoadCount == 1)
            {
                Application.targetFrameRate = _heavyLoadFrameRate;
                Log($"🔴 BeginHeavyLoad [{reason}] — FPS → {_heavyLoadFrameRate}");
            }
        }

        /// <summary>
        /// Llama cuando termina la operación pesada para restaurar FPS normal.
        /// Usa un contador para soportar llamadas anidadas.
        /// </summary>
        public void EndHeavyLoad(string reason = "")
        {
            _heavyLoadCount = Mathf.Max(0, _heavyLoadCount - 1);
            if (_heavyLoadCount == 0)
            {
                Application.targetFrameRate = _targetFrameRate;
                Log($"🟢 EndHeavyLoad [{reason}] — FPS → {_targetFrameRate}");
            }
        }

        /// <summary>
        /// Fuerza restaurar FPS normal independientemente del contador.
        /// Usar si hay un desequilibrio Begin/End por excepción.
        /// </summary>
        public void ForceRestoreFrameRate()
        {
            _heavyLoadCount             = 0;
            Application.targetFrameRate = _targetFrameRate;
            Log($"🟢 ForceRestoreFrameRate — FPS → {_targetFrameRate}");
        }

        // ─── Propiedades ───────────────────────────────────────────────────

        public bool IsHeavyLoadActive    => _heavyLoadCount > 0;
        public int  CurrentTargetFPS     => Application.targetFrameRate;
        public bool IsMatchFrameRateOn   => _arSession != null && _arSession.matchFrameRateRequested;

        // ─── Helpers ───────────────────────────────────────────────────────

        private void Log(string msg)
        {
            if (_logPerformance)
                Debug.Log($"[ARPerformanceManager] {msg}");
        }

        // ─── Debug ─────────────────────────────────────────────────────────

        [ContextMenu("ℹ️ Estado")]
        private void DebugStatus()
        {
            Debug.Log(
                $"[ARPerformanceManager] v2.0\n" +
                $"  initialized:           {_initialized}\n" +
                $"  matchFrameRateOn:      {IsMatchFrameRateOn}\n" +
                $"  currentTargetFPS:      {CurrentTargetFPS}\n" +
                $"  heavyLoadCount:        {_heavyLoadCount}\n" +
                $"  targetFPS (normal):    {_targetFrameRate}\n" +
                $"  targetFPS (heavy):     {_heavyLoadFrameRate}\n" +
                $"  ARSession:             {(_arSession != null ? _arSession.gameObject.name : "NULL")}");
        }

        [ContextMenu("🔴 Simular BeginHeavyLoad")]
        private void DbgBeginHeavy() => BeginHeavyLoad("debug");

        [ContextMenu("🟢 Simular EndHeavyLoad")]
        private void DbgEndHeavy() => EndHeavyLoad("debug");
    }
}