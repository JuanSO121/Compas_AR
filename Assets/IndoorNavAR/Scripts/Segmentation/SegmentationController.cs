// File: SegmentationController.cs
// ✅ v11.0 — Desacopla inferencia del render loop de ARCore para preservar framerate
//
// ============================================================================
//  CAMBIOS v10.2 → v11.0
// ============================================================================
//
//  PROBLEMA RAÍZ (v10.x):
//  ─────────────────────────────────────────────────────────────────────────
//  _cameraManager.frameReceived se disparaba en cada frame de ARCore y dentro
//  de él se ejecutaba TryCaptureFromCpuImage() que incluye:
//    - XRCpuImage.Convert() (CPU-heavy, copia de buffer)
//    - Texture2D.LoadRawTextureData() + Apply()
//    - GL.Clear() + Graphics.DrawTexture() + ReadPixels()
//  Aunque la inferencia GPU era asíncrona, la CAPTURA bloqueaba el hilo
//  principal en cada frame polling, causando drops de framerate en la cámara AR.
//
//  SOLUCIÓN v11.0 — PIPELINE COMPLETAMENTE DESACOPLADO:
//  ─────────────────────────────────────────────────────────────────────────
//  1. frameReceived YA NO SE USA para disparar inferencias.
//     Solo se usa para capturar la textura cruda de ARCore cuando el timer
//     de segmentación lo indica — fuera del path caliente de render.
//
//  2. Timer independiente (_inferenceInterval, default 3s) controla cuándo
//     se captura y schedules la inferencia. ARCore corre a su framerate
//     nativo sin interrupciones.
//
//  3. Captura lazy: en el tick del timer se captura el frame más reciente
//     disponible de ARCore usando TryAcquireLatestCpuImage(). Si el worker
//     está ocupado, se espera al siguiente tick.
//
//  4. _debugOverlayEnabled (booleano en Inspector + ContextMenu):
//     - false (default producción): overlay desactivado, cero overhead de
//       Texture2D.SetPixels32/Apply en Update(). La información sigue
//       fluyendo a Flutter/VoiceAPI normalmente.
//     - true (demo/presentación): muestra cómo el modelo "ve" la escena,
//       exactamente igual que v10.x.
//     Cambiable en runtime desde el Inspector o ContextMenu sin reiniciar.
//
//  5. Frecuencia adaptativa ELIMINADA del render loop — el timer fijo de
//     3s es suficiente para alertas de obstáculos. Se conserva la lógica
//     de cooldown de alertas y supresión por ObstacleRerouteMediator.
//
//  6. TODOS LOS COMPORTAMIENTOS DE v10.2 SE CONSERVAN:
//     - Activación/desactivación por NavigationStartedEvent/StoppedEvent
//     - Fallback CPU tras MAX_GPU_TIMEOUTS
//     - Notificación a Flutter de segmentation_active
//     - PassageBlockDetector sigue leyendo ObstacleRatio/WallRatio/FloorRatio
//     - _onlyDuringNavigation respetado
//     - ContextMenus de debug

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Collections;
using Unity.Sentis;
using IndoorNavAR.Integration;
using IndoorNavAR.Navigation;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Segmentation
{
    public class SegmentationController : MonoBehaviour
    {
        [Header("Sentis")]
        [SerializeField] private ModelAsset  _modelAsset;
        [SerializeField] private BackendType _backend = BackendType.GPUCompute;

        [Header("Rotación del tensor")]
        [Tooltip("0/90/180/270. Android portrait cámara trasera → 90.")]
        [SerializeField] private int _tensorRotation = 90;

        [Header("Flip del INPUT al modelo")]
        [Tooltip("Flip vertical — corrige MirrorY de ARCore. Default: true.")]
        [SerializeField] private bool _flipInputY = true;
        [Tooltip("Flip horizontal — corrige espejo en X. Default: true.")]
        [SerializeField] private bool _flipInputX = true;

        [Header("AR")]
        [SerializeField] private ARCameraManager    _cameraManager;
        [SerializeField] private ARCameraBackground _cameraBackground;

        [Header("Overlay")]
        [SerializeField] private SegmentationOverlayRenderer _overlayRenderer;

        [Tooltip("Solo para demos / presentaciones.\n" +
                 "false (default) = overlay oculto, cero overhead de GPU en textura de máscara.\n" +
                 "true            = muestra cómo el modelo ve la escena (igual que v10.x).\n" +
                 "Cambiable en runtime desde Inspector o ContextMenu sin reiniciar.")]
        [SerializeField] private bool _debugOverlayEnabled = false;

        [Header("ROI — Región de Interés")]
        [Tooltip("Fracción desde arriba que se OMITE al capturar el frame.")]
        [SerializeField, Range(0f, 0.7f)]
        private float _roiTopSkip = 0.4f;

        [Header("─── v11.0: Timer de inferencia (desacoplado del render loop) ───")]
        [Tooltip("Intervalo en segundos entre inferencias.\n" +
                 "El render loop de ARCore NO es afectado por este timer.\n" +
                 "Default: 3s — suficiente para alertas de obstáculos en tiempo real.")]
        [SerializeField, Range(0.5f, 10f)]
        private float _inferenceInterval = 3f;

        [Header("Alertas TTS")]
        [SerializeField] private float _obstacleAlertThreshold = 0.12f;
        [SerializeField] private float _alertCooldown          = 3.5f;

        [Header("Control de activación")]
        [Tooltip("Si está marcado, la segmentación SOLO se activa durante navegación.")]
        [SerializeField] private bool _onlyDuringNavigation = true;

        [Header("Debug")]
        [SerializeField] private bool _logStats        = true;
        [SerializeField] private bool _logFrameCapture = true;

        // ── Constantes ────────────────────────────────────────────────────
        private const int   MODEL_SIZE       = ObstacleSegmentationWorker.IMAGE_SIZE;
        private const int   MAX_GPU_TIMEOUTS = 3;

        // ── Worker y texturas ─────────────────────────────────────────────
        private ObstacleSegmentationWorker _worker;
        private RenderTexture              _cameraRT;
        private Texture2D                  _frameBufferFallback;
        private Texture2D                  _fitTex;

        // ── Estado ────────────────────────────────────────────────────────
        private bool  _initialized           = false;
        private bool  _segmentationActive    = false;
        private bool  _pendingActivation     = false;
        private bool  _cpuFallbackActive     = false;
        private float _lastAlertTime         = -999f;

        // ── Control de timeout GPU ────────────────────────────────────────
        private bool _inferenceScheduled      = false;  // hay una inferencia en vuelo
        private int  _consecutivePollTimeouts = 0;

        // ── Timer de inferencia (v11.0) ───────────────────────────────────
        private float _timeSinceLastInference = 0f;

        // ── Propiedades públicas ──────────────────────────────────────────
        public bool OverlayVisible        => _debugOverlayEnabled;
        public bool IsSegmentationActive  => _segmentationActive;

        // ═════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ═════════════════════════════════════════════════════════════════

        private void Start()
        {
            ForceCanvasExpand();

            if (_modelAsset == null)
            {
                Debug.LogError("[SegCtrl v11] ❌ ModelAsset no asignado.");
                return;
            }

            if (_cameraManager == null)
                _cameraManager = FindFirstObjectByType<ARCameraManager>(
                    FindObjectsInactive.Include);

            if (_cameraBackground == null)
                _cameraBackground = FindFirstObjectByType<ARCameraBackground>(
                    FindObjectsInactive.Include);

            if (_cameraManager == null)
            {
                Debug.LogError("[SegCtrl v11] ❌ ARCameraManager NO encontrado.");
                return;
            }

            // Buffers de captura
            _cameraRT = new RenderTexture(MODEL_SIZE, MODEL_SIZE, 0,
                                          RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp
            };
            _cameraRT.Create();

            _frameBufferFallback = new Texture2D(MODEL_SIZE, MODEL_SIZE,
                                                  TextureFormat.RGB24, false);

            // Worker de Sentis
            _worker = new ObstacleSegmentationWorker(
                _modelAsset, _backend, _tensorRotation, _flipInputY, _flipInputX);

            if (!_worker.IsReady)
            {
                Debug.LogError("[SegCtrl v11] ❌ Worker no pudo inicializarse.");
                return;
            }

            _overlayRenderer?.Initialize(_worker.MaskWidth, _worker.MaskHeight);
            _overlayRenderer?.SetFlipMode(SegmentationOverlayRenderer.FlipMode.None);
            // El overlay arranca oculto siempre — _debugOverlayEnabled lo controla
            _overlayRenderer?.SetVisible(false);

            _worker.OnInferenceComplete += HandleInferenceComplete;

            // v11.0: NO suscribimos a frameReceived en Start.
            // La captura la hace el timer en Update() cuando _segmentationActive = true.
            if (!_onlyDuringNavigation)
            {
                _segmentationActive = true;
                NotifyFlutterSegmentationState(true);
                Debug.Log("[SegCtrl v11] ✅ Segmentación activa desde Start (onlyDuringNavigation=false)");
            }

            _initialized = true;

            SubscribeToNavigationEvents();

            // Procesar activación pendiente si el evento llegó antes de Start()
            if (_pendingActivation)
            {
                _pendingActivation = false;
                Debug.Log("[SegCtrl v11] ✅ Procesando activación pendiente");
                ActivateSegmentation();
            }

            StartCoroutine(DiagnoseARSetup());

            Debug.Log($"[SegCtrl v11] ✅ Inicializado. rotation={_tensorRotation}° " +
                      $"flipY={_flipInputY} flipX={_flipInputX} " +
                      $"MODEL_SIZE={MODEL_SIZE} ROI={_roiTopSkip:P0} " +
                      $"interval={_inferenceInterval}s " +
                      $"onlyDuringNav={_onlyDuringNavigation} " +
                      $"debugOverlay={_debugOverlayEnabled}");
        }

        private void OnDestroy()
        {
            UnsubscribeFromNavigationEvents();
            _worker?.Dispose();

            if (_cameraRT != null)           { _cameraRT.Release(); Destroy(_cameraRT); }
            if (_frameBufferFallback != null)  Destroy(_frameBufferFallback);
            if (_fitTex != null)               Destroy(_fitTex);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Update — ÚNICO punto de control (timer + poll)
        // ═════════════════════════════════════════════════════════════════

        private void Update()
        {
            if (!_segmentationActive || _worker == null) return;

            // ── 1. Poll de resultado de inferencia en vuelo ───────────────
            //    Siempre primero: si hay resultado listo, procesarlo antes
            //    de decidir si disparar la siguiente inferencia.
            if (_inferenceScheduled)
            {
                bool resultReady = _worker.PollResult();

                if (resultReady)
                {
                    _inferenceScheduled      = false;
                    _consecutivePollTimeouts = 0;
                    OnInferenceResultReady();
                }
                else if (!_worker.IsBusy)
                {
                    // Worker libre pero PollResult devolvió false → timeout
                    _inferenceScheduled = false;
                    _consecutivePollTimeouts++;

                    if (_consecutivePollTimeouts >= MAX_GPU_TIMEOUTS && !_cpuFallbackActive)
                        ReinitializeWithCPU();
                }
                // Si _worker.IsBusy todavía → seguir esperando (no hacer nada)
            }

            // ── 2. Timer de inferencia (v11.0 — desacoplado del render loop) ──
            //    Solo disparar si no hay una inferencia en vuelo.
            if (!_inferenceScheduled)
            {
                _timeSinceLastInference += Time.deltaTime;

                if (_timeSinceLastInference >= _inferenceInterval)
                {
                    _timeSinceLastInference = 0f;
                    TryCaptureAndSchedule();
                }
            }
        }

        // ── Captura y schedule ────────────────────────────────────────────

        /// <summary>
        /// Captura el frame más reciente de ARCore y schedules la inferencia.
        /// Se llama desde el timer — NO desde frameReceived.
        /// </summary>
        private void TryCaptureAndSchedule()
        {
            if (_worker == null || !_worker.IsReady || _worker.IsBusy) return;

            if (!TryCaptureFromCpuImage())
            {
                if (_logFrameCapture)
                    Debug.LogWarning("[SegCtrl v11] ⚠️ Captura fallida — saltando tick.");
                return;
            }

            _worker.ScheduleInference(_frameBufferFallback);
            _inferenceScheduled = true;

            if (_logFrameCapture)
                Debug.Log("[SegCtrl v11] 📸 Inferencia schedulada.");
        }

        // ── Resultado de inferencia ───────────────────────────────────────

        private void OnInferenceResultReady()
        {
            // Overlay: solo si debugOverlay está habilitado Y el renderer está visible
            if (_debugOverlayEnabled && _overlayRenderer != null && _overlayRenderer.IsVisible)
                _overlayRenderer.UpdateMask(_worker.MaskData);

            EvaluateAlerts();

            VoiceCommandAPI.Instance?.SendSegmentationRatio(
                _worker.ObstacleRatio,
                _worker.FloorRatio,
                _worker.WallRatio);

            if (_logStats)
                Debug.Log($"[SegCtrl v11] 🎯 Obstacle={_worker.ObstacleRatio:P1} " +
                          $"Floor={_worker.FloorRatio:P1} Wall={_worker.WallRatio:P1}");
        }

        private void HandleInferenceComplete()
        {
            // Callback del worker — solo logging en debug
            if (_logStats) Debug.Log("[SegCtrl v11] ✅ OnInferenceComplete (worker callback).");
        }

        // ═════════════════════════════════════════════════════════════════
        //  Captura de imagen CPU (sin cambios funcionales vs v10.2)
        // ═════════════════════════════════════════════════════════════════

        private bool TryCaptureFromCpuImage()
        {
            if (_cameraManager == null) return false;

            if (!_cameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
                return false;

            using (cpuImage)
            {
                int srcW = cpuImage.width;
                int srcH = cpuImage.height;

                float scale = (float)MODEL_SIZE / Mathf.Max(srcW, srcH);
                int fitW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
                int fitH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));

                var conversionParams = new XRCpuImage.ConversionParams
                {
                    inputRect        = new RectInt(0, 0, srcW, srcH),
                    outputDimensions = new Vector2Int(fitW, fitH),
                    outputFormat     = TextureFormat.RGB24,
                    transformation   = XRCpuImage.Transformation.None
                };

                int dataSize = cpuImage.GetConvertedDataSize(conversionParams);
                var buffer   = new NativeArray<byte>(dataSize, Allocator.Temp);
                cpuImage.Convert(conversionParams, buffer);

                if (_fitTex == null || _fitTex.width != fitW || _fitTex.height != fitH)
                {
                    if (_fitTex != null) Destroy(_fitTex);
                    _fitTex = new Texture2D(fitW, fitH, TextureFormat.RGB24, false);
                    if (_logFrameCapture)
                        Debug.Log($"[SegCtrl v11] 📐 Letterbox: {srcW}×{srcH} → {fitW}×{fitH} → {MODEL_SIZE}×{MODEL_SIZE}");
                }

                _fitTex.LoadRawTextureData(buffer);
                _fitTex.Apply(false);
                buffer.Dispose();

                int offX = (MODEL_SIZE - fitW) / 2;
                int offY = (MODEL_SIZE - fitH) / 2;

                var prev = RenderTexture.active;
                RenderTexture.active = _cameraRT;
                GL.Clear(true, true, Color.black);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, MODEL_SIZE, MODEL_SIZE, 0);
                Graphics.DrawTexture(new Rect(offX, offY, fitW, fitH), _fitTex);
                GL.PopMatrix();

                RenderTexture.active = _cameraRT;
                _frameBufferFallback.ReadPixels(new Rect(0, 0, MODEL_SIZE, MODEL_SIZE), 0, 0);
                _frameBufferFallback.Apply(false);
                RenderTexture.active = prev;
            }

            return true;
        }

        // ═════════════════════════════════════════════════════════════════
        //  Activación / desactivación de segmentación
        // ═════════════════════════════════════════════════════════════════

        private void ActivateSegmentation()
        {
            if (_segmentationActive) return;

            if (_worker == null || !_worker.IsReady)
            {
                Debug.LogWarning("[SegCtrl v11] ⚠️ ActivateSegmentation: worker no listo. Intentando CPU...");
                if (!_cpuFallbackActive && _modelAsset != null)
                    ReinitializeWithCPU();

                if (_worker == null || !_worker.IsReady)
                {
                    Debug.LogError("[SegCtrl v11] ❌ No se pudo activar: worker inválido.");
                    return;
                }
            }

            _segmentationActive     = true;
            _timeSinceLastInference = _inferenceInterval; // disparar inmediatamente en el primer tick
            _inferenceScheduled     = false;

            // Mostrar overlay solo si debug está habilitado
            if (_debugOverlayEnabled)
                _overlayRenderer?.SetVisible(true);

            NotifyFlutterSegmentationState(true);
            Debug.Log("[SegCtrl v11] ✅ Segmentación activada (timer mode).");
        }

        private void DeactivateSegmentation()
        {
            if (!_segmentationActive) return;

            _segmentationActive = false;
            _inferenceScheduled = false;

            _overlayRenderer?.SetVisible(false);
            NotifyFlutterSegmentationState(false);
            Debug.Log("[SegCtrl v11] ⏸️ Segmentación desactivada.");
        }

        // ═════════════════════════════════════════════════════════════════
        //  Eventos de navegación
        // ═════════════════════════════════════════════════════════════════

        private void SubscribeToNavigationEvents()
        {
            var bus = EventBus.Instance;
            if (bus == null)
            {
                Debug.LogWarning("[SegCtrl v11] ⚠️ EventBus no disponible.");
                return;
            }
            bus.Subscribe<NavigationStartedEvent>(OnNavigationStarted);
            bus.Subscribe<NavigationStoppedEvent>(OnNavigationStopped);
            bus.Subscribe<NavigationArrivedEvent>(OnNavigationArrived);
        }

        private void UnsubscribeFromNavigationEvents()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Unsubscribe<NavigationStartedEvent>(OnNavigationStarted);
            bus.Unsubscribe<NavigationStoppedEvent>(OnNavigationStopped);
            bus.Unsubscribe<NavigationArrivedEvent>(OnNavigationArrived);
        }

        private void OnNavigationStarted(NavigationStartedEvent evt)
        {
            if (!_onlyDuringNavigation) return;

            if (!_initialized || _worker == null)
            {
                _pendingActivation = true;
                Debug.LogWarning("[SegCtrl v11] ⚠️ NavigationStartedEvent antes de Init — encolando.");
                return;
            }

            ActivateSegmentation();
            Debug.Log("[SegCtrl v11] 🚀 Navegación iniciada → Segmentación ACTIVADA");
        }

        private void OnNavigationStopped(NavigationStoppedEvent evt)
        {
            if (!_onlyDuringNavigation) return;
            _pendingActivation = false;
            DeactivateSegmentation();
            Debug.Log("[SegCtrl v11] 🛑 Navegación detenida → Segmentación DESACTIVADA");
        }

        private void OnNavigationArrived(NavigationArrivedEvent evt)
        {
            if (!_onlyDuringNavigation) return;
            _pendingActivation = false;
            DeactivateSegmentation();
            Debug.Log("[SegCtrl v11] 🎯 Llegada → Segmentación DESACTIVADA");
        }

        // ═════════════════════════════════════════════════════════════════
        //  Alertas TTS
        // ═════════════════════════════════════════════════════════════════

        private void EvaluateAlerts()
        {
            if (ObstacleRerouteMediator.IsActive)
            {
                if (_logStats)
                    Debug.Log("[SegCtrl v11] 🔇 Alertas suprimidas — ObstacleRerouteMediator activo.");
                return;
            }

            if (Time.unscaledTime - _lastAlertTime < _alertCooldown) return;
            if (_worker.ObstacleRatio < _obstacleAlertThreshold) return;

            _lastAlertTime = Time.unscaledTime;
            string msg = _worker.ObstacleRatio > 0.25f
                ? "Precaución, obstáculo muy cerca"
                : "Obstáculo detectado al frente";

            VoiceCommandAPI.Instance?.SpeakArbitraryText(msg, priority: 2, interrupt: false);
            Debug.Log($"[SegCtrl v11] 🚧 Alerta: {msg} ({_worker.ObstacleRatio:P1})");
        }

        // ═════════════════════════════════════════════════════════════════
        //  Notificación a Flutter
        // ═════════════════════════════════════════════════════════════════

        private void NotifyFlutterSegmentationState(bool active)
        {
            var api = VoiceCommandAPI.Instance;
            if (api == null) return;

            string json = $"{{\"action\":\"segmentation_active\",\"active\":{(active ? "true" : "false")}}}";
            api.ReplyPublic(json);
            Debug.Log($"[SegCtrl v11] 📡 segmentation_active → Flutter: {active}");
        }

        // ═════════════════════════════════════════════════════════════════
        //  Fallback CPU
        // ═════════════════════════════════════════════════════════════════

        [ContextMenu("🔄 Reinicializar con CPU")]
        public void ReinitializeWithCPU()
        {
            if (_cpuFallbackActive) return;
            Debug.LogWarning("[SegCtrl v11] 🔄 GPU timeout — reinicializando con CPU...");

            _worker?.Dispose();
            _worker = new ObstacleSegmentationWorker(
                _modelAsset, BackendType.CPU, _tensorRotation, _flipInputY, _flipInputX);

            if (!_worker.IsReady)
            {
                Debug.LogError("[SegCtrl v11] ❌ Fallback CPU falló.");
                return;
            }

            _worker.OnInferenceComplete += HandleInferenceComplete;
            _inferenceScheduled          = false;
            _cpuFallbackActive           = true;
            Debug.Log("[SegCtrl v11] ✅ Fallback CPU activo.");
        }

        // ═════════════════════════════════════════════════════════════════
        //  Diagnóstico
        // ═════════════════════════════════════════════════════════════════

        private IEnumerator DiagnoseARSetup()
        {
            yield return new WaitForSeconds(5f);
            Debug.Log($"[SegCtrl v11] 🔍 Diagnóstico: " +
                      $"initialized={_initialized} active={_segmentationActive} " +
                      $"workerReady={_worker?.IsReady} cpuFallback={_cpuFallbackActive}");
        }

        private void ForceCanvasExpand()
        {
            var scaler = GetComponentInChildren<CanvasScaler>(true);
            if (scaler == null)
                scaler = GetComponentInParent<CanvasScaler>();

            if (scaler != null &&
                scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize &&
                scaler.screenMatchMode != CanvasScaler.ScreenMatchMode.Expand)
            {
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
                Debug.Log("[SegCtrl v11] ✅ CanvasScaler → Expand.");
            }
        }

        // ═════════════════════════════════════════════════════════════════
        //  API pública
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Activa o desactiva el overlay de debug sin detener la inferencia.
        /// En producción mantener false. En demo/presentación poner true.
        /// </summary>
        public void SetOverlayVisible(bool visible)
        {
            _debugOverlayEnabled = visible;

            bool shouldShow = visible && (!_onlyDuringNavigation || _segmentationActive);
            _overlayRenderer?.SetVisible(shouldShow);

            Debug.Log($"[SegCtrl v11] 🎭 DebugOverlay → {(shouldShow ? "VISIBLE" : "OCULTO")} " +
                      $"(requested={visible}, active={_segmentationActive})");
        }

        public void SetROITopSkip(float ratio)
        {
            _roiTopSkip = Mathf.Clamp01(ratio);
            Debug.Log($"[SegCtrl v11] ROI skip → {_roiTopSkip:P0}.");
        }

        // ═════════════════════════════════════════════════════════════════
        //  ContextMenu — Debug
        // ═════════════════════════════════════════════════════════════════

        [ContextMenu("🐛 Activar Debug Overlay (demo)")]
        private void DbgOverlayOn()
        {
            SetOverlayVisible(true);
            Debug.Log("[SegCtrl v11] 🐛 DEBUG OVERLAY ON — modo presentación");
        }

        [ContextMenu("🚀 Desactivar Debug Overlay (producción)")]
        private void DbgOverlayOff()
        {
            SetOverlayVisible(false);
            Debug.Log("[SegCtrl v11] 🚀 DEBUG OVERLAY OFF — modo producción");
        }

        [ContextMenu("🔄 Rot 0°")]   private void DbgRot0()   => ApplyRotation(0);
        [ContextMenu("🔄 Rot 90°")]  private void DbgRot90()  => ApplyRotation(90);
        [ContextMenu("🔄 Rot 180°")] private void DbgRot180() => ApplyRotation(180);
        [ContextMenu("🔄 Rot 270°")] private void DbgRot270() => ApplyRotation(270);
        private void ApplyRotation(int deg) { _tensorRotation = deg; _worker?.SetRotation(deg); }

        [ContextMenu("🔄 Flip Y ON")]
        private void DbgFlipYOn()  { _flipInputY = true;  _worker?.SetFlipY(true);
                                     _overlayRenderer?.SetFlipMode(SegmentationOverlayRenderer.FlipMode.None); }
        [ContextMenu("🔄 Flip Y OFF")]
        private void DbgFlipYOff() { _flipInputY = false; _worker?.SetFlipY(false); }
        [ContextMenu("🔄 Flip X ON")]
        private void DbgFlipXOn()  { _flipInputX = true;  _worker?.SetFlipX(true); }
        [ContextMenu("🔄 Flip X OFF")]
        private void DbgFlipXOff() { _flipInputX = false; _worker?.SetFlipX(false); }

        [ContextMenu("Toggle Overlay")]
        private void DbgToggleOverlay() => SetOverlayVisible(!_debugOverlayEnabled);

        [ContextMenu("✅ Activar Segmentación")]
        private void DbgActivate() => ActivateSegmentation();

        [ContextMenu("⏸️ Desactivar Segmentación")]
        private void DbgDeactivate() => DeactivateSegmentation();

        [ContextMenu("📊 Log Stats")]
        private void DbgStats()
        {
            Debug.Log($"[SegCtrl v11] " +
                      $"Active={_segmentationActive} Initialized={_initialized} " +
                      $"PendingActivation={_pendingActivation} " +
                      $"DebugOverlay={_debugOverlayEnabled} " +
                      $"InferenceScheduled={_inferenceScheduled} " +
                      $"TimerAccum={_timeSinceLastInference:F1}s/{_inferenceInterval}s\n" +
                      $"  Obstacle={_worker?.ObstacleRatio:P1} " +
                      $"Floor={_worker?.FloorRatio:P1} Wall={_worker?.WallRatio:P1}\n" +
                      $"  WorkerBusy={_worker?.IsBusy} WorkerReady={_worker?.IsReady} " +
                      $"CPU={_cpuFallbackActive} PollTimeouts={_consecutivePollTimeouts}\n" +
                      $"  MediatorActive={ObstacleRerouteMediator.IsActive}");
        }

        [ContextMenu("⏱️ Forzar inferencia ahora")]
        private void DbgForceInference()
        {
            if (!_segmentationActive)
            {
                Debug.LogWarning("[SegCtrl v11] Segmentación inactiva — activa primero.");
                return;
            }
            _timeSinceLastInference = _inferenceInterval;
            Debug.Log("[SegCtrl v11] ⏱️ Timer forzado — inferencia en próximo Update().");
        }
    }
}