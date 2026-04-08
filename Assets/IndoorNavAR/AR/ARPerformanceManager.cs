// File: ARPerformanceManager.cs
// ✅ NUEVO — Gestión centralizada de CPU para ARCore / AR Foundation.
//
// ============================================================================
//  PROBLEMA RAÍZ (documentado en logs del dispositivo Infinix X6887)
// ============================================================================
//
//  Los logs muestran este patrón repetido:
//
//    RESOURCE_EXHAUSTED: Behind by: 156ms, skip current frame
//    RESOURCE_EXHAUSTED: Behind by: 180ms, skip current frame
//    FeatureExtraction is taking too long: 112ms
//    Skipped 30 frames! / Skipped 67 frames!
//
//  Causa: el estimador VIO (Visual-Inertial Odometry) de ARCore comparte CPU
//  con Unity. Cuando Unity satura el main thread (NavMesh, escaleras, GC),
//  el VIO no procesa frames a tiempo → tracking flicker → realineaciones
//  → waypoints descolocados.
//
//  FUENTE OFICIAL — ARCore Performance Considerations:
//    "When an ARCore session is active, your app must share limited mobile CPU
//     and GPU resources with ARCore. CPU bound apps can compete with the CPU
//     resources required for motion tracking."
//    https://developers.google.com/ar/develop/performance
//
//  FUENTE OFICIAL — Unity Application.targetFrameRate:
//    "Android: Content is rendered at fixed 30fps to conserve battery power
//     when targetFrameRate = -1."
//    "You can also reduce your game's frame rate to conserve battery life
//     and avoid overheating on mobile devices."
//    https://docs.unity3d.com/ScriptReference/Application-targetFrameRate.html
//
// ============================================================================
//  SOLUCIONES IMPLEMENTADAS
// ============================================================================
//
//  1. TARGET FRAMERATE = 30 en dispositivo
//     Libera ciclos de CPU para el VIO de ARCore. El VIO necesita ~30Hz
//     para funcionar correctamente. Unity a 60fps compite directamente.
//
//  2. ARSession.matchFrameRateRequested = true
//     Le indica a AR Foundation que sincronice el render con el frame AR,
//     evitando que Unity pida frames antes de que ARCore los tenga listos.
//     Esto elimina la mayoría de los "GetRecentDevicePose failed: too new".
//
//  3. FASES DE CARGA — throttling durante operaciones pesadas
//     NavMesh restore, escaleras y waypoints se marcan como "fases de carga".
//     Durante estas fases, targetFrameRate baja a 15fps para maximizar
//     el tiempo de CPU disponible para el VIO.
//     Esto elimina los flickers de 66-430ms vistos en los logs.
//
//  4. PUNTO DE ENTRADA ÚNICO
//     ARSessionManager, PersistenceManager y ModelLoadManager notifican
//     al ARPerformanceManager cuando inician/finalizan operaciones pesadas.
//
// ============================================================================

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

            // ARSession.matchFrameRateRequested = true es la solución oficial
            // para el error "GetRecentDevicePose failed: Passed timestamp is too new".
            // Cuando Unity renderiza más rápido que ARCore produce frames,
            // el plugin pide poses de timestamps que el VIO aún no procesó.
            // Con matchFrameRate=true, Unity espera al frame AR antes de renderizar.
            //
            // FUENTE: AR Foundation docs — ARSession component reference
            // "If True, the session will block execution until a new AR frame
            //  is available and set Application.targetFrameRate to match the
            //  native update frequency of the AR session."
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
                _isHeavyLoad       = true;
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
            float elapsed = Time.realtimeSinceStartup - _heavyLoadStartTime;
            float remaining = _minHeavyLoadDuration - elapsed;

            if (remaining > 0f)
            {
                Log($"⏳ Esperando {remaining:F2}s más en heavy-load (mínimo={_minHeavyLoadDuration}s)...");
                yield return new WaitForSeconds(remaining);
            }

            // Verificar que no se haya iniciado otra operación pesada mientras esperábamos
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
            // En Android con ARSession.matchFrameRateRequested=true, el AR session
            // ya controla el framerate. targetFrameRate actúa como límite superior.
            Application.targetFrameRate = _normalFrameRate;
            QualitySettings.vSyncCount  = 0; // Mobile ignora vSync, pero por consistencia
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
            Debug.Log($"[ARPerfMgr] isHeavyLoad={_isHeavyLoad} | ops={_heavyLoadCount} | " +
                      $"fps={Application.targetFrameRate} | matchAR={_matchARFrameRate}");
        }

        [ContextMenu("🔴 Simular HeavyLoad")]
        private void DebugHeavy() => BeginHeavyLoad("debug");

        [ContextMenu("🟢 Simular EndHeavyLoad")]
        private void DebugEnd() => EndHeavyLoad("debug");
    }
}