// File: ARCapabilityDetector.cs
// ✅ v2.1 — AR Foundation 6.2.x / Unity 6 compatible.
//
// ============================================================================
//  CAMBIOS v2 → v2.1
// ============================================================================
//
//  BUG CORREGIDO: Detección falsa de NoAR en dispositivos con ARCore.
//
//  CAUSA RAÍZ:
//    El loop anterior esperaba a que ARSession SALIERA de SessionInitializing.
//    Si el timeout (8s) expiraba mientras ARCore seguía en SessionInitializing
//    (cargando su mapa VIO, configurando sensores, etc.), el estado NO estaba
//    incluido en sessionActive → detector concluía NoAR aunque ARCore SÍ
//    estuviera disponible.
//
//    Confirmado por logs: ARCore 1.45 inicializándose correctamente (300+ líneas
//    de config), pero el detector reportaba NoAR porque el timeout llegó primero.
//
//  FIX #1 — Loop invertido:
//    Antes: while (state == SessionInitializing) → esperar que salga
//    Ahora: salir del loop tan pronto como AR esté disponible en CUALQUIER forma
//           (SessionTracking, Ready, o SessionInitializing = ARCore cargando)
//
//  FIX #2 — sessionActive incluye SessionInitializing:
//    SessionInitializing significa que el dispositivo TIENE ARCore y está
//    inicializando el tracking. Es AR disponible, no AR ausente.
//
//  FIX #3 — Timeout ampliado de 8s a 15s:
//    Dispositivos lentos o con carga simultánea de sesión pueden tardar más.
//
// ============================================================================
//  CAMBIOS v1 → v2 (conservados íntegramente)
// ============================================================================
//
//  1. ARSession.CheckAvailability() y ARSession.Install() ELIMINADOS en AF 6.x.
//  2. ARSessionState.NeedsInstall ELIMINADO en AF 6.x.
//  3. ARSessionState.Unsupported sigue disponible en AF 6.x.
//  4. FindFirstObjectByType<T>() en lugar de FindAnyObjectByType<T>() (Unity 6).
//  5. Singleton con DontDestroyOnLoad.

using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace IndoorNavAR.AR
{
    public enum ARCapabilityLevel
    {
        FullAR,
        ARWithoutPlanes,
        NoAR
    }

    public class ARCapabilityDetector : MonoBehaviour
    {
        public static ARCapabilityDetector Instance { get; private set; }

        [Header("Referencias")]
        [SerializeField] private ARPlaneManager _planeManager;

        [Header("Debug")]
        [SerializeField] private bool _verbose = true;

        [Tooltip("-1 = detección automática. 0 = FullAR, 1 = ARWithoutPlanes, 2 = NoAR")]
        [SerializeField] private int _forceLevel = -1;

        [Header("Timeouts (AF 6.x)")]
        [Tooltip("Segundos máximos esperando que AR esté disponible. " +
                 "✅ v2.1: aumentado a 15s para cubrir inicialización lenta de ARCore.")]
        [SerializeField] private float _sessionStartTimeout = 15f;

        [Tooltip("Segundos de espera adicional antes de leer capacidades del planeManager.descriptor. " +
                 "AR Foundation 6.x puede tardar en reportar capacidades del subsistema.")]
        [SerializeField] private float _descriptorWaitSeconds = 2f;

        public ARCapabilityLevel Current { get; private set; } = ARCapabilityLevel.NoAR;
        public bool IsReady { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            StartCoroutine(DetectRoutine());
        }

        private IEnumerator DetectRoutine()
        {
            // ── Nivel forzado (testing / debug) ──────────────────────────────
            if (_forceLevel >= 0 && _forceLevel <= 2)
            {
                Current = (ARCapabilityLevel)_forceLevel;
                IsReady = true;
                Log($"Nivel AR FORZADO → {Current}");
                yield break;
            }

            // ── Editor: siempre FullAR ────────────────────────────────────────
#if UNITY_EDITOR
            Current = ARCapabilityLevel.FullAR;
            IsReady = true;
            Log($"[Editor] Capacidad AR detectada → {Current}");
            yield break;
#else
            // ── Dispositivo real ──────────────────────────────────────────────

            // Esperar a que ARSession tenga un estado definitivo
            // (puede estar en None brevemente al inicio).
            float waitNone = 3f;
            while (ARSession.state == ARSessionState.None && waitNone > 0f)
            {
                waitNone -= Time.deltaTime;
                yield return null;
            }

            // ✅ AF 6.x: Unsupported = el dispositivo no soporta AR en absoluto.
            if (ARSession.state == ARSessionState.Unsupported)
            {
                Log($"Dispositivo sin soporte AR (state={ARSession.state}) → NoAR");
                Current = ARCapabilityLevel.NoAR;
                IsReady = true;
                yield break;
            }

            // ✅ v2.1 FIX #1 + #3: Loop invertido con timeout ampliado a 15s.
            //
            // ANTES (v2 — BUGGY):
            //   while (state == SessionInitializing && timeout > 0)  ← esperaba que SALIERA
            //
            // AHORA (v2.1 — CORRECTO):
            //   Salir tan pronto como AR esté disponible en cualquier forma.
            //   SessionInitializing = ARCore está cargando = AR SÍ disponible.
            //   Solo seguir esperando si el estado es completamente inactivo.
            float timeout = _sessionStartTimeout;
            while (timeout > 0f)
            {
                var state = ARSession.state;

                // AR disponible en cualquier forma → salir del loop
                if (state == ARSessionState.SessionTracking ||
                    state == ARSessionState.Ready           ||
                    state == ARSessionState.SessionInitializing)
                {
                    break;
                }

                // Dispositivo sin soporte detectado tarde → NoAR inmediato
                if (state == ARSessionState.Unsupported)
                {
                    Log($"Dispositivo sin soporte AR detectado tarde (state={state}) → NoAR");
                    Current = ARCapabilityLevel.NoAR;
                    IsReady = true;
                    yield break;
                }

                timeout -= Time.deltaTime;
                yield return null;
            }

            // ✅ v2.1 FIX #2: SessionInitializing también es "sesión activa".
            //
            // SessionInitializing = el dispositivo TIENE ARCore y está cargando.
            // Solo es NoAR si el estado es completamente inactivo tras el timeout.
            bool sessionActive = ARSession.state == ARSessionState.SessionTracking
                              || ARSession.state == ARSessionState.Ready
                              || ARSession.state == ARSessionState.SessionInitializing;

            if (!sessionActive)
            {
                Log($"Sesión AR no activa tras timeout (state={ARSession.state}) → NoAR");
                Current = ARCapabilityLevel.NoAR;
                IsReady = true;
                yield break;
            }

            Log($"AR disponible (state={ARSession.state}) — verificando planos...");

            // ── Verificar soporte de detección de planos ──────────────────────
            _planeManager ??= FindFirstObjectByType<ARPlaneManager>();

            if (_planeManager == null)
            {
                Log("ARPlaneManager no encontrado → ARWithoutPlanes");
                Current = ARCapabilityLevel.ARWithoutPlanes;
                IsReady = true;
                yield break;
            }

            // ✅ AF 6.x: Esperar tiempo adicional para que el subsistema
            // reporte sus capacidades reales en descriptor.
            yield return new WaitForSeconds(_descriptorWaitSeconds);

            var descriptor = _planeManager.descriptor;

            bool supportsPlanes = descriptor != null
                               && (descriptor.supportsHorizontalPlaneDetection
                                || descriptor.supportsVerticalPlaneDetection);

            Current = supportsPlanes
                ? ARCapabilityLevel.FullAR
                : ARCapabilityLevel.ARWithoutPlanes;

            IsReady = true;
            Log($"Capacidad AR detectada → {Current}");
#endif
        }

        /// <summary>
        /// Coroutine que espera hasta que IsReady sea true.
        /// Usada por AROriginAligner y otros sistemas que dependen del detector.
        /// </summary>
        public IEnumerator WaitUntilReady()
        {
            while (!IsReady)
                yield return null;
        }

        private void Log(string msg)
        {
            if (_verbose)
                Debug.Log($"[ARCapability] {msg}");
        }

        // ─── Debug ────────────────────────────────────────────────────────

        [ContextMenu("ℹ️ Estado actual")]
        private void DebugStatus()
        {
            Debug.Log(
                $"[ARCapabilityDetector] v2.1\n" +
                $"  IsReady:       {IsReady}\n" +
                $"  Current:       {Current}\n" +
                $"  ARSession:     {ARSession.state}\n" +
                $"  ForceLevel:    {_forceLevel}\n" +
                $"  PlaneManager:  {(_planeManager != null ? "✓" : "✗")}");
        }

        [ContextMenu("🔄 Re-detectar (solo editor)")]
        private void DebugRedetect()
        {
#if UNITY_EDITOR
            IsReady = false;
            Current = ARCapabilityLevel.NoAR;
            StartCoroutine(DetectRoutine());
#else
            Debug.LogWarning("[ARCapabilityDetector] Re-detección solo disponible en editor.");
#endif
        }
    }
}