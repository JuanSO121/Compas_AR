// File: SegmentationController.cs
// ✅ v10.2 — Fix: Segmentación no se activaba al iniciar navegación
//
// ============================================================================
//  CAMBIOS v10.1 → v10.2
// ============================================================================
//
//  BUG #1 — CRÍTICO: _initialized bloqueaba PollResult durante navegación
//    En v10.1, Update() tenía el guard:
//      if (!_segmentationActive || !_initialized) return;
//    Si NavigationStartedEvent llegaba antes de que Start() completara
//    (_initialized = false), los frames eran capturados por OnCameraFrameReceived
//    pero PollResult() nunca se ejecutaba → inferencia completada pero resultados
//    nunca procesados. La segmentación parecía "activa" pero no funcionaba.
//
//    FIX: PollResult() ahora se llama si _segmentationActive && _worker != null,
//    sin depender de _initialized. El guard _initialized se mantiene solo para
//    la captura de frames (OnCameraFrameReceived), donde sí es necesario.
//
//  BUG #2 — Doble suscripción a frameReceived en ReinitializeWithCPU()
//    v10.1 desuscribía siempre con -= (correcto), pero si _segmentationActive
//    era true y el worker se reiniciaba, la re-suscripción se hacía sin verificar
//    si ya había una suscripción activa previa del ciclo anterior. En práctica
//    esto podía causar que OnCameraFrameReceived se llamara dos veces por frame.
//
//    FIX: ReinitializeWithCPU() desuscribe antes de re-suscribir
//    independientemente del estado, garantizando exactamente 1 suscripción.
//
//  BUG #3 — ActivateSegmentation() no verificaba _worker.IsReady
//    Si el worker fallaba al inicializar (GPU no disponible), ActivateSegmentation()
//    igualmente suscribía el frameReceived y marcaba _segmentationActive = true,
//    pero PollResult() fallaba silenciosamente cada frame.
//
//    FIX: ActivateSegmentation() verifica _worker?.IsReady antes de activar.
//    Si el worker no está listo, intenta reinicializar con CPU como fallback.
//
//  BUG #4 — Race condition: NavigationStartedEvent antes de Start()
//    Si el EventBus publicaba NavigationStartedEvent durante el primer frame
//    (posible en dispositivos lentos donde AR se inicializa rápido), Start()
//    aún no había completado y _worker era null → NullReferenceException en
//    ActivateSegmentation() → _segmentationActive quedaba false.
//
//    FIX: ActivateSegmentation() hace null-check de _worker y encola la
//    activación para el siguiente frame si Start() aún no completó.
//
//  TODOS LOS CAMBIOS DE v10.1 SE CONSERVAN ÍNTEGRAMENTE.

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
        [SerializeField] private bool _showOverlay = true;

        [Header("ROI — Región de Interés")]
        [Tooltip("Fracción desde arriba que se OMITE al capturar el frame.")]
        [SerializeField, Range(0f, 0.7f)]
        private float _roiTopSkip = 0.4f;

        [Header("Frecuencia de inferencia")]
        [SerializeField] private int _inferenceEveryNFrames = 6;

        [Header("Frecuencia adaptativa")]
        [SerializeField] private float _motionThreshold = 0.015f;
        [SerializeField] private int   _maxSkipFrames   = 18;

        [Header("Alertas TTS")]
        [SerializeField] private float _obstacleAlertThreshold = 0.12f;
        [SerializeField] private float _alertCooldown          = 3.5f;

        [Header("✅ v10.0: Control de activación")]
        [Tooltip("Si está marcado, la segmentación SOLO se activa durante navegación.")]
        [SerializeField] private bool _onlyDuringNavigation = true;

        [Header("Debug")]
        [SerializeField] private bool _logStats        = true;
        [SerializeField] private bool _logFrameCapture = true;

        // ── Privado ────────────────────────────────────────────────────────
        private const int MODEL_SIZE = ObstacleSegmentationWorker.IMAGE_SIZE;

        private ObstacleSegmentationWorker _worker;
        private RenderTexture _cameraRT;
        private Texture2D     _frameBufferFallback;
        private Texture2D     _fitTex;

        private int   _frameCounter;
        private int   _framesSinceLastInference;
        private float _lastAlertTime    = -999f;
        private bool  _initialized;

        private bool _wasWorkerBusy           = false;
        private int  _consecutivePollTimeouts = 0;
        private bool _cpuFallbackActive       = false;
        private const int MAX_GPU_TIMEOUTS    = 3;

        private Vector3 _lastCamPos = Vector3.zero;
        private int     _currentInterval;
        private int     _totalFramesReceived = 0;
        private Camera  _arCamera;

        // ✅ v10.0: Estado de activación de segmentación
        private bool _segmentationActive = false;

        // ✅ v10.2: Flag para encolar activación pendiente si Start() no completó
        private bool _pendingActivation = false;

        public bool OverlayVisible => _showOverlay;
        
        // ✅ v10.0: Propiedad pública para verificar si la segmentación está activa
        public bool IsSegmentationActive => _segmentationActive;

        // ─────────────────────────────────────────────────────────────────

        private void Start()
        {
            ForceCanvasExpand();

            if (_modelAsset == null)
            {
                Debug.LogError("[SegCtrl] ❌ ModelAsset no asignado.");
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
                Debug.LogError("[SegCtrl] ❌ ARCameraManager NO encontrado.");
                return;
            }

            _arCamera = _cameraManager.GetComponent<Camera>();

            _cameraRT = new RenderTexture(MODEL_SIZE, MODEL_SIZE, 0,
                                          RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp
            };
            _cameraRT.Create();

            _frameBufferFallback = new Texture2D(MODEL_SIZE, MODEL_SIZE,
                                                  TextureFormat.RGB24, false);

            _worker = new ObstacleSegmentationWorker(
                _modelAsset, _backend, _tensorRotation, _flipInputY, _flipInputX);

            if (!_worker.IsReady)
            {
                Debug.LogError("[SegCtrl] ❌ Worker no pudo inicializarse.");
                return;
            }

            _overlayRenderer?.Initialize(_worker.MaskWidth, _worker.MaskHeight);
            // ✅ Solo visible si NO depende de navegación o si ya está activo
            bool shouldShow = !_onlyDuringNavigation && _showOverlay;
            _overlayRenderer?.SetVisible(shouldShow);
            _overlayRenderer?.SetFlipMode(SegmentationOverlayRenderer.FlipMode.None);

            _worker.OnInferenceComplete += HandleInferenceComplete;
            
            // ✅ v10.0: Solo suscribirse a frames si NO requiere navegación
            if (!_onlyDuringNavigation)
            {
                _cameraManager.frameReceived += OnCameraFrameReceived;
                _segmentationActive = true;
                // ✅ v10.1: Notificar a Flutter el estado inicial
                NotifyFlutterSegmentationState(true);
            }

            _currentInterval = _inferenceEveryNFrames;
            _initialized     = true;

            // ✅ v10.0: Suscribirse a eventos de navegación
            SubscribeToNavigationEvents();

            // ✅ v10.2: Procesar activación pendiente si NavigationStartedEvent
            //           llegó antes de que Start() completara.
            if (_pendingActivation)
            {
                _pendingActivation = false;
                Debug.Log("[SegCtrl] ✅ Procesando activación pendiente (NavigationStartedEvent llegó antes de Start())");
                ActivateSegmentation();
            }

            StartCoroutine(DiagnoseARFrames());

            Debug.Log($"[SegCtrl] ✅ v10.2 inicializado. rotation={_tensorRotation}° " +
                      $"flipY={_flipInputY} flipX={_flipInputX} " +
                      $"MODEL_SIZE={MODEL_SIZE} ROI={_roiTopSkip:P0} " +
                      $"onlyDuringNav={_onlyDuringNavigation}");
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
                Debug.Log("[SegCtrl] ✅ CanvasScaler → Expand.");
            }
        }

        private void OnDestroy()
        {
            if (_cameraManager != null)
                _cameraManager.frameReceived -= OnCameraFrameReceived;

            // ✅ v10.0: Desuscribirse de eventos de navegación
            UnsubscribeFromNavigationEvents();

            _worker?.Dispose();

            if (_cameraRT != null)            { _cameraRT.Release(); Destroy(_cameraRT); }
            if (_frameBufferFallback != null)    Destroy(_frameBufferFallback);
            if (_fitTex != null)                 Destroy(_fitTex);
        }

        // ✅ v10.0 ──────────────────────────────────────────────────────────
        // Gestión de eventos de navegación
        // ──────────────────────────────────────────────────────────────────

        private void SubscribeToNavigationEvents()
        {
            var bus = EventBus.Instance;
            if (bus == null)
            {
                Debug.LogWarning("[SegCtrl] ⚠️ EventBus no disponible. " +
                                 "Segmentación no se activará automáticamente.");
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
            
            // ✅ v10.2: Si Start() aún no completó (_worker == null o !_initialized),
            //           encolar la activación para ejecutarla cuando esté listo.
            if (!_initialized || _worker == null)
            {
                _pendingActivation = true;
                Debug.LogWarning("[SegCtrl] ⚠️ NavigationStartedEvent recibido antes de inicialización completa. " +
                                 "Activación encolada.");
                return;
            }

            ActivateSegmentation();
            Debug.Log("[SegCtrl] 🚀 Navegación iniciada → Segmentación ACTIVADA");
        }

        private void OnNavigationStopped(NavigationStoppedEvent evt)
        {
            if (!_onlyDuringNavigation) return;
            
            _pendingActivation = false; // ✅ v10.2: cancelar activación pendiente si la nav se detiene antes de inicializar
            DeactivateSegmentation();
            Debug.Log("[SegCtrl] 🛑 Navegación detenida → Segmentación DESACTIVADA");
        }

        private void OnNavigationArrived(NavigationArrivedEvent evt)
        {
            if (!_onlyDuringNavigation) return;
            
            _pendingActivation = false; // ✅ v10.2: cancelar activación pendiente
            DeactivateSegmentation();
            Debug.Log("[SegCtrl] 🎯 Llegada a destino → Segmentación DESACTIVADA");
        }

        private void ActivateSegmentation()
        {
            if (_segmentationActive) return;

            // ✅ v10.2: Verificar que el worker esté listo antes de activar.
            //           Si no lo está, intentar fallback CPU.
            if (_worker == null || !_worker.IsReady)
            {
                Debug.LogWarning("[SegCtrl] ⚠️ ActivateSegmentation: worker no listo. " +
                                 "Intentando fallback CPU...");
                if (!_cpuFallbackActive && _modelAsset != null)
                    ReinitializeWithCPU();
                
                // Si tras el intento sigue sin estar listo, abortar
                if (_worker == null || !_worker.IsReady)
                {
                    Debug.LogError("[SegCtrl] ❌ No se pudo activar segmentación: worker inválido.");
                    return;
                }
            }
            
            _segmentationActive = true;
            
            // ✅ v10.2: Garantizar exactamente 1 suscripción desuscribiendo primero
            if (_cameraManager != null)
            {
                _cameraManager.frameReceived -= OnCameraFrameReceived; // safe: -= no falla si no estaba suscrito
                _cameraManager.frameReceived += OnCameraFrameReceived;
            }
            
            if (_showOverlay)
                _overlayRenderer?.SetVisible(_showOverlay);
            
            // ✅ v10.1: Notificar a Flutter que la segmentación está activa
            NotifyFlutterSegmentationState(true);
            
            Debug.Log("[SegCtrl] ✅ Segmentación activada — consumo de recursos iniciado");
        }

        private void DeactivateSegmentation()
        {
            if (!_segmentationActive) return;
            
            _segmentationActive = false;
            
            if (_cameraManager != null)
                _cameraManager.frameReceived -= OnCameraFrameReceived;
            
            _overlayRenderer?.SetVisible(false);
            
            // ✅ v10.1: Notificar a Flutter que la segmentación está inactiva
            NotifyFlutterSegmentationState(false);
            
            Debug.Log("[SegCtrl] ⏸️ Segmentación desactivada — recursos liberados");
        }

        // ✅ v10.1 ──────────────────────────────────────────────────────────
        // Notificación de estado a Flutter
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Envía a Flutter el estado actual de la segmentación:
        ///   { "action": "segmentation_active", "active": true/false }
        /// </summary>
        private void NotifyFlutterSegmentationState(bool active)
        {
            var api = VoiceCommandAPI.Instance;
            if (api == null) return;

            string json = $"{{\"action\":\"segmentation_active\",\"active\":{(active ? "true" : "false")}}}";
            api.ReplyPublic(json);

            Debug.Log($"[SegCtrl] 📡 segmentation_active → Flutter: {active}");
        }

        // ─────────────────────────────────────────────────────────────────

        private IEnumerator DiagnoseARFrames()
        {
            yield return new WaitForSeconds(5f);
            if (_totalFramesReceived == 0)
                Debug.LogWarning("[SegCtrl] ⚠️ No se recibieron frames AR en 5s.");
            else
                Debug.Log($"[SegCtrl] ✅ {_totalFramesReceived} frames AR en 5s.");
        }

        // ── Frame loop ────────────────────────────────────────────────────

        private void OnCameraFrameReceived(ARCameraFrameEventArgs args)
        {
            // ✅ OPTIMIZACIÓN: Early exit si segmentación no está activa
            if (!_segmentationActive) return;

            _totalFramesReceived++;

            if (_logFrameCapture && _totalFramesReceived <= 5)
                Debug.Log($"[SegCtrl] 📸 Frame AR #{_totalFramesReceived}");

            // ✅ v10.2: Guard _initialized solo para captura de frames
            //           (PollResult se mueve a Update() con su propio guard mejorado)
            if (!_initialized || _worker == null || !_worker.IsReady) return;

            _frameCounter++;
            _framesSinceLastInference++;

            UpdateAdaptiveInterval();

            bool mustInfer   = _framesSinceLastInference >= _maxSkipFrames;
            bool timeToInfer = _frameCounter % _currentInterval == 0;
            if (!timeToInfer && !mustInfer) return;

            if (_worker.IsBusy) return;

            if (TryCaptureFromCpuImage())
            {
                _framesSinceLastInference = 0;
                _wasWorkerBusy = true;
                _worker.ScheduleInference(_frameBufferFallback);
            }
        }

        private bool TryCaptureFromBackground()
        {
            if (_cameraBackground == null) return false;
            Texture camTexture = _cameraBackground.material?.mainTexture;
            if (camTexture == null) return false;
            Graphics.Blit(camTexture, _cameraRT,
                          new Vector2(1f, 1f - _roiTopSkip), new Vector2(0f, 0f));
            return true;
        }

        private bool TryCaptureFromCpuImage()
        {
            if (!_cameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
            {
                if (_logFrameCapture)
                    Debug.LogWarning("[SegCtrl] ⚠️ TryAcquireLatestCpuImage falló.");
                return false;
            }

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
                        Debug.Log($"[SegCtrl] 📐 Letterbox: {srcW}×{srcH} → {fitW}×{fitH} → {MODEL_SIZE}×{MODEL_SIZE}");
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
                RenderTexture.active = prev;

                RenderTexture.active = _cameraRT;
                _frameBufferFallback.ReadPixels(new Rect(0, 0, MODEL_SIZE, MODEL_SIZE), 0, 0);
                _frameBufferFallback.Apply(false);
                RenderTexture.active = prev;
            }

            return true;
        }

        // ── Update ────────────────────────────────────────────────────────

        private void Update()
        {
            // ✅ v10.2 FIX CRÍTICO: PollResult() se ejecuta si _segmentationActive && _worker != null,
            //    independientemente de _initialized. Esto resuelve el caso donde NavigationStartedEvent
            //    llegaba antes de que Start() completara: el worker ya estaba ejecutando inferencias
            //    pero PollResult() nunca se llamaba por el guard !_initialized previo.
            //
            //    Se mantiene la verificación de _worker != null para evitar NullReferenceException
            //    en el caso extremo de race condition durante el primer frame.
            if (!_segmentationActive || _worker == null) return;

            bool resultReady = _worker.PollResult();

            if (_wasWorkerBusy && !_worker.IsBusy && !resultReady)
            {
                _consecutivePollTimeouts++;
                _wasWorkerBusy = false;
                if (_consecutivePollTimeouts >= MAX_GPU_TIMEOUTS && !_cpuFallbackActive)
                    ReinitializeWithCPU();
            }

            if (resultReady)
            {
                _wasWorkerBusy           = false;
                _consecutivePollTimeouts = 0;
                
                // ✅ OPTIMIZACIÓN: Solo actualizar overlay si está visible
                if (_overlayRenderer != null && _overlayRenderer.IsVisible)
                    _overlayRenderer.UpdateMask(_worker.MaskData);
                
                EvaluateAlerts();

                VoiceCommandAPI.Instance?.SendSegmentationRatio(
                    _worker.ObstacleRatio,
                    _worker.FloorRatio,
                    _worker.WallRatio
                );

                if (_logStats)
                    Debug.Log($"[SegCtrl] 🎯 Obstacle={_worker.ObstacleRatio:P1} " +
                              $"Floor={_worker.FloorRatio:P1} Wall={_worker.WallRatio:P1} " +
                              $"interval={_currentInterval}f");
            }
        }

        // ── Frecuencia adaptativa ─────────────────────────────────────────

        private void UpdateAdaptiveInterval()
        {
            if (_arCamera == null) return;
            Vector3 camPos = _arCamera.transform.position;
            float delta = Vector3.Distance(camPos, _lastCamPos);
            _lastCamPos = camPos;
            _currentInterval = delta < _motionThreshold
                ? Mathf.Min(_inferenceEveryNFrames * 2, _maxSkipFrames)
                : _inferenceEveryNFrames;
        }

        private void HandleInferenceComplete()
        {
            if (_logStats) Debug.Log("[SegCtrl] ✅ OnInferenceComplete.");
        }

        // ── Fallback CPU ──────────────────────────────────────────────────

        [ContextMenu("🔄 Reinicializar con CPU")]
        public void ReinitializeWithCPU()
        {
            if (_cpuFallbackActive) return;
            Debug.LogWarning("[SegCtrl] 🔄 GPU timeout — reinicializando con CPU...");

            // ✅ v10.2: Desuscribir siempre antes de disponer el worker
            if (_cameraManager != null)
                _cameraManager.frameReceived -= OnCameraFrameReceived;

            _worker?.Dispose();

            _worker = new ObstacleSegmentationWorker(
                _modelAsset, BackendType.CPU, _tensorRotation, _flipInputY, _flipInputX);

            if (!_worker.IsReady) { Debug.LogError("[SegCtrl] ❌ Fallback CPU falló."); return; }

            _worker.OnInferenceComplete += HandleInferenceComplete;
            
            // ✅ v10.2: Re-suscribir exactamente una vez si segmentación está activa
            if (_segmentationActive && _cameraManager != null)
                _cameraManager.frameReceived += OnCameraFrameReceived;
            
            _cpuFallbackActive = true;
            Debug.Log("[SegCtrl] ✅ Fallback CPU activo.");
        }

        // ── Alertas TTS ───────────────────────────────────────────────────

        private void EvaluateAlerts()
        {
            if (ObstacleRerouteMediator.IsActive)
            {
                if (_logStats)
                    Debug.Log("[SegCtrl] 🔇 EvaluateAlerts suprimido — ObstacleRerouteMediator activo.");
                return;
            }

            if (Time.unscaledTime - _lastAlertTime < _alertCooldown) return;
            if (_worker.ObstacleRatio < _obstacleAlertThreshold) return;

            _lastAlertTime = Time.unscaledTime;
            string msg = _worker.ObstacleRatio > 0.25f
                ? "Precaución, obstáculo muy cerca"
                : "Obstáculo detectado al frente";

            VoiceCommandAPI.Instance?.SpeakArbitraryText(msg, priority: 2, interrupt: false);
            Debug.Log($"[SegCtrl] 🚧 Alerta standalone: {msg} ({_worker.ObstacleRatio:P1})");
        }

        // ── Toggle overlay ────────────────────────────────────────────────

        public void SetOverlayVisible(bool visible)
        {
            _showOverlay = visible;

            bool shouldShow = visible && (!_onlyDuringNavigation || _segmentationActive);

            _overlayRenderer?.SetVisible(shouldShow);

            Debug.Log($"[SegCtrl] 🎭 Overlay → {(shouldShow ? "VISIBLE" : "OCULTO")} " +
                    $"(requested={visible}, active={_segmentationActive})");
        }

        // ── Debug ─────────────────────────────────────────────────────────

        [ContextMenu("🔄 Rot 0°")]   private void DbgRot0()   => ApplyRotation(0);
        [ContextMenu("🔄 Rot 90°")]  private void DbgRot90()  => ApplyRotation(90);
        [ContextMenu("🔄 Rot 180°")] private void DbgRot180() => ApplyRotation(180);
        [ContextMenu("🔄 Rot 270°")] private void DbgRot270() => ApplyRotation(270);
        private void ApplyRotation(int deg) { _tensorRotation = deg; _worker?.SetRotation(deg); }

        [ContextMenu("🔄 Flip Y ON")]
        private void DbgFlipYOn()  { _flipInputY = true;  _worker?.SetFlipY(true);
                                     _overlayRenderer?.SetFlipMode(SegmentationOverlayRenderer.FlipMode.None);
                                     Debug.Log("[SegCtrl] FlipY=ON"); }

        [ContextMenu("🔄 Flip Y OFF")]
        private void DbgFlipYOff() { _flipInputY = false; _worker?.SetFlipY(false);
                                     Debug.Log("[SegCtrl] FlipY=OFF"); }

        [ContextMenu("🔄 Flip X ON")]
        private void DbgFlipXOn()  { _flipInputX = true;  _worker?.SetFlipX(true);
                                     Debug.Log("[SegCtrl] FlipX=ON"); }

        [ContextMenu("🔄 Flip X OFF")]
        private void DbgFlipXOff() { _flipInputX = false; _worker?.SetFlipX(false);
                                     Debug.Log("[SegCtrl] FlipX=OFF"); }

        [ContextMenu("Toggle Overlay")]
        private void DbgToggleOverlay()
        {
            SetOverlayVisible(!_showOverlay);
        }

        [ContextMenu("✅ Activar Segmentación")]
        private void DbgActivate() => ActivateSegmentation();

        [ContextMenu("⏸️ Desactivar Segmentación")]
        private void DbgDeactivate() => DeactivateSegmentation();

        [ContextMenu("Log Stats")]
        private void DbgStats()
        {
            Debug.Log($"[SegCtrl] Active={_segmentationActive} " +
                      $"Initialized={_initialized} PendingActivation={_pendingActivation} " +
                      $"Obstacle={_worker?.ObstacleRatio:P1} Floor={_worker?.FloorRatio:P1} " +
                      $"Wall={_worker?.WallRatio:P1} " +
                      $"Busy={_worker?.IsBusy} Frames={_totalFramesReceived} Rot={_tensorRotation}° " +
                      $"FlipY={_flipInputY} FlipX={_flipInputX} Interval={_currentInterval}f " +
                      $"MODEL_SIZE={MODEL_SIZE} CPU={_cpuFallbackActive} " +
                      $"Overlay={_showOverlay} " +
                      $"MediatorActive={ObstacleRerouteMediator.IsActive}");
        }

        public void SetROITopSkip(float ratio)
        {
            _roiTopSkip = Mathf.Clamp01(ratio);
            Debug.Log($"[SegCtrl] ROI skip → {_roiTopSkip:P0}.");
        }
    }
}