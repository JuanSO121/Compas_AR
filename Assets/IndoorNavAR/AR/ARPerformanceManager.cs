// File: ARPerformanceManager.cs
// ✅ v1.1 — Sin cambios funcionales respecto a v1.0.
//           Bump de versión para consistencia con el resto del sistema (FIX_VIO suite).
//
// ============================================================================
//  NOTA v1.1
// ============================================================================
//
//  ARPerformanceManager no requiere cambios directos para el FIX_VIO.
//  El sistema ya implementa:
//    - targetFrameRate = 30 durante navegación normal (libera CPU al VIO).
//    - targetFrameRate = 15 durante HeavyLoad (cede CPU máxima al VIO).
//    - ARSession.matchFrameRateRequested = true (elimina timestamp conflicts).
//
//  Los tres fixes principales se aplican en archivos externos:
//    - PersistenceManager.cs  v14.4 (FIX_VIO — WaitForVIOStableBeforeHeavyWork)
//    - NavigationManager.cs   FIX#14 (FIX_RAM — ReleaseMemoryBeforeARStart)
//    - AROriginAligner.cs     v8.12  (FIX_TIMESTAMP — cooldown de pose-query)
//
//  TODO LO DEMÁS ES IDÉNTICO A v1.0.

using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace IndoorNavAR.AR
{
    public class ARPerformanceManager : MonoBehaviour
    {
        public static ARPerformanceManager Instance { get; private set; }

        [Header("─── Framerate targets ──────────────────────────────────────")]
        [Tooltip("FPS durante navegación normal. 30 es el valor recomendado por ARCore.\n" +
                 "Ref: developers.google.com/ar/develop/performance")]
        [SerializeField] private int _normalFrameRate = 30;

        [Tooltip("FPS durante operaciones pesadas (NavMesh, escaleras, waypoints).\n" +
                 "Reduce al mínimo para ceder CPU al VIO de ARCore.")]
        [SerializeField] private int _heavyLoadFrameRate = 15;

        [Tooltip("Si true, sincroniza Unity con el frame rate de ARCore.\n" +
                 "Evita que Unity pida poses antes de que el VIO las tenga.\n" +
                 "Ref: ARSession.matchFrameRateRequested docs.")]
        [SerializeField] private bool _matchARFrameRate = true;

        [Header("─── Duración mínima de fase pesada ──────────────────────────")]
        [Tooltip("Segundos mínimos en modo heavy-load antes de volver a normal.\n" +
                 "Evita oscilaciones si múltiples operaciones se solapan.")]
        [SerializeField] private float _minHeavyLoadDuration = 1.0f;

        [Header("─── Debug ───────────────────────────────────────────────────")]
        [SerializeField] private bool _logChanges = true;

        private int  _heavyLoadCount = 0;
        private bool _isHeavyLoad    = false;
        private float _heavyLoadStartTime = 0f;

        // ─── Lifecycle ────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            ApplyNormalFrameRate();

            if (_matchARFrameRate)
            {
                var arSession = FindFirstObjectByType<ARSession>();
                if (arSession != null)
                {
                    arSession.matchFrameRateRequested = true;
                    Log("✅ ARSession.matchFrameRateRequested = true " +
                        "(elimina GetRecentDevicePose 'too new')");
                }
                else
                {
                    Log("⚠️ ARSession no encontrado — matchFrameRate no aplicado.");
                }
            }
        }

        // ─── API pública ──────────────────────────────────────────────────

        /// <summary>
        /// Registra el inicio de una operación pesada de CPU.
        /// Llamar desde PersistenceManager, ModelLoadManager, etc.
        /// </summary>
        public void BeginHeavyLoad(string reason = "")
        {
            _heavyLoadCount++;

            if (!_isHeavyLoad)
            {
                _isHeavyLoad        = true;
                _heavyLoadStartTime = Time.realtimeSinceStartup;
                ApplyHeavyLoadFrameRate();
                Log($"🔴 HeavyLoad BEGIN [{_heavyLoadCount} ops]: {reason}");
            }
            else
            {
                Log($"🔴 HeavyLoad +1 [{_heavyLoadCount} ops]: {reason}");
            }
        }

        /// <summary>
        /// Registra el fin de una operación pesada de CPU.
        /// Cuando todas las operaciones terminan, vuelve a framerate normal.
        /// </summary>
        public void EndHeavyLoad(string reason = "")
        {
            _heavyLoadCount = Mathf.Max(0, _heavyLoadCount - 1);

            Log($"🟢 HeavyLoad END [{_heavyLoadCount} ops restantes]: {reason}");

            if (_heavyLoadCount == 0 && _isHeavyLoad)
            {
                StartCoroutine(ReturnToNormalAfterMinDuration());
            }
        }

        /// <summary>
        /// Fuerza retorno inmediato a framerate normal.
        /// Usar solo si una operación terminó abruptamente.
        /// </summary>
        public void ForceNormalLoad(string reason = "")
        {
            _heavyLoadCount = 0;
            if (_isHeavyLoad)
            {
                _isHeavyLoad = false;
                ApplyNormalFrameRate();
                Log($"🟢 ForceNormalLoad: {reason}");
            }
        }

        // ─── Internos ─────────────────────────────────────────────────────

        private IEnumerator ReturnToNormalAfterMinDuration()
        {
            float elapsed   = Time.realtimeSinceStartup - _heavyLoadStartTime;
            float remaining = _minHeavyLoadDuration - elapsed;

            if (remaining > 0f)
            {
                Log($"⏳ Esperando {remaining:F2}s más en heavy-load (mínimo={_minHeavyLoadDuration}s)...");
                yield return new WaitForSeconds(remaining);
            }

            if (_heavyLoadCount == 0)
            {
                _isHeavyLoad = false;
                ApplyNormalFrameRate();
            }
            else
            {
                Log($"⚠️ Nuevas operaciones pesadas durante espera [{_heavyLoadCount} ops] — manteniendo heavy-load.");
            }
        }

        private void ApplyNormalFrameRate()
        {
            Application.targetFrameRate = _normalFrameRate;
            QualitySettings.vSyncCount  = 0;
            Log($"✅ TargetFrameRate → {_normalFrameRate}fps (modo normal)");
        }

        private void ApplyHeavyLoadFrameRate()
        {
            Application.targetFrameRate = _heavyLoadFrameRate;
            QualitySettings.vSyncCount  = 0;
            Log($"🔴 TargetFrameRate → {_heavyLoadFrameRate}fps (modo heavy-load — cediendo CPU al VIO)");
        }

        private void Log(string msg)
        {
            if (_logChanges) Debug.Log($"[ARPerfMgr] {msg}");
        }

        // ─── Debug ────────────────────────────────────────────────────────

        [ContextMenu("ℹ️ Estado")]
        private void DebugState()
        {
            Debug.Log($"[ARPerfMgr] v1.1 | isHeavyLoad={_isHeavyLoad} | ops={_heavyLoadCount} | " +
                      $"fps={Application.targetFrameRate} | matchAR={_matchARFrameRate}");
        }

        [ContextMenu("🔴 Simular HeavyLoad")]
        private void DebugHeavy() => BeginHeavyLoad("debug");

        [ContextMenu("🟢 Simular EndHeavyLoad")]
        private void DebugEnd() => EndHeavyLoad("debug");
    }
}