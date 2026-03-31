// File: SegmentationController.cs
// ✅ v9 — FlipX independiente + forzar Canvas Scaler a Expand en runtime
//
// ============================================================================
//  CAMBIOS v8 → v9
// ============================================================================
//
//  FIX 1 — _flipInputX añadido
//    Expone el flip horizontal del input como campo serializado y
//    como ContextMenu "Flip X ON/OFF" para calibrar en runtime sin rebuild.
//
//  FIX 2 — ForceCanvasExpand() en Start()
//    El CanvasScaler con Match=0.5 en dispositivos con aspect ratio distinto
//    a 1080×1920 (ej. Pixel 5 = 1080×2340) produce un canvas de 978px.
//    ForceCanvasExpand() cambia el ScreenMatchMode a Expand en runtime,
//    garantizando que el canvas siempre tenga el ancho correcto.
//    Esto es un seguro adicional: lo ideal es cambiarlo también en el Inspector.
//
//  SIN CAMBIOS — IMAGE_SIZE=312, letterbox, fallback CPU, TTS,
//    frecuencia adaptativa, DiagnoseARFrames, AlignToROI eliminado.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Collections;
using Unity.Sentis;
using IndoorNavAR.Integration;

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
        [Tooltip("Fracción desde arriba que se OMITE al capturar el frame. " +
                 "NO afecta el tamaño del overlay (siempre full screen).")]
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

        [Header("Debug")]
        [SerializeField] private bool _logStats        = true;
        [SerializeField] private bool _logFrameCapture = true;

        // ── Privado ────────────────────────────────────────────────────────
        private const int MODEL_SIZE = ObstacleSegmentationWorker.IMAGE_SIZE; // 312

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

        // ─────────────────────────────────────────────────────────────────

        private void Start()
        {
            // ✅ FIX 2: Forzar CanvasScaler a Expand para garantizar ancho correcto
            // en dispositivos con aspect ratio distinto a la referencia 1080×1920.
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

            // ✅ FIX 1: flipX pasado al constructor
            _worker = new ObstacleSegmentationWorker(
                _modelAsset, _backend, _tensorRotation, _flipInputY, _flipInputX);

            if (!_worker.IsReady)
            {
                Debug.LogError("[SegCtrl] ❌ Worker no pudo inicializarse.");
                return;
            }

            _overlayRenderer?.Initialize(_worker.MaskWidth, _worker.MaskHeight);
            _overlayRenderer?.SetVisible(_showOverlay);
            _overlayRenderer?.SetFlipMode(SegmentationOverlayRenderer.FlipMode.None);

            _worker.OnInferenceComplete += HandleInferenceComplete;
            _cameraManager.frameReceived += OnCameraFrameReceived;

            _currentInterval = _inferenceEveryNFrames;
            _initialized     = true;

            StartCoroutine(DiagnoseARFrames());

            Debug.Log($"[SegCtrl] ✅ Inicializado. rotation={_tensorRotation}° " +
                      $"flipY={_flipInputY} flipX={_flipInputX} " +
                      $"MODEL_SIZE={MODEL_SIZE} ROI={_roiTopSkip:P0}");
        }

        // ✅ FIX 2: Fuerza ScreenMatchMode.Expand en el CanvasScaler del overlay.
        // Garantiza que el canvas lógico tenga el ancho de pantalla completo
        // independientemente del aspect ratio del dispositivo.
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
                Debug.Log("[SegCtrl] ✅ CanvasScaler → Expand (forzado en runtime).");
            }
        }

        private void OnDestroy()
        {
            if (_cameraManager != null)
                _cameraManager.frameReceived -= OnCameraFrameReceived;

            _worker?.Dispose();

            if (_cameraRT != null)            { _cameraRT.Release(); Destroy(_cameraRT); }
            if (_frameBufferFallback != null)    Destroy(_frameBufferFallback);
            if (_fitTex != null)                 Destroy(_fitTex);
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
            _totalFramesReceived++;

            if (_logFrameCapture && _totalFramesReceived <= 5)
                Debug.Log($"[SegCtrl] 📸 Frame AR #{_totalFramesReceived}");

            if (!_initialized || !_worker.IsReady) return;

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
            if (!_initialized) return;

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
                _overlayRenderer?.UpdateMask(_worker.MaskData);
                EvaluateAlerts();

                if (_logStats)
                    Debug.Log($"[SegCtrl] 🎯 Obstacle={_worker.ObstacleRatio:P1} " +
                              $"Floor={_worker.FloorRatio:P1} interval={_currentInterval}f");
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

            _cameraManager.frameReceived -= OnCameraFrameReceived;
            _worker?.Dispose();

            _worker = new ObstacleSegmentationWorker(
                _modelAsset, BackendType.CPU, _tensorRotation, _flipInputY, _flipInputX);

            if (!_worker.IsReady) { Debug.LogError("[SegCtrl] ❌ Fallback CPU falló."); return; }

            _worker.OnInferenceComplete += HandleInferenceComplete;
            _cameraManager.frameReceived += OnCameraFrameReceived;
            _cpuFallbackActive = true;
            Debug.Log("[SegCtrl] ✅ Fallback CPU activo.");
        }

        // ── Alertas TTS ───────────────────────────────────────────────────

        private void EvaluateAlerts()
        {
            if (Time.unscaledTime - _lastAlertTime < _alertCooldown) return;
            if (_worker.ObstacleRatio < _obstacleAlertThreshold) return;
            _lastAlertTime = Time.unscaledTime;
            string msg = _worker.ObstacleRatio > 0.25f
                ? "Precaución, obstáculo muy cerca"
                : "Obstáculo detectado al frente";
            VoiceCommandAPI.Instance?.SpeakArbitraryText(msg, priority: 2, interrupt: false);
            Debug.Log($"[SegCtrl] 🚧 Alerta: {msg} ({_worker.ObstacleRatio:P1})");
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
        private void DbgToggleOverlay() { _showOverlay = !_showOverlay; _overlayRenderer?.SetVisible(_showOverlay); }

        [ContextMenu("Log Stats")]
        private void DbgStats()
        {
            Debug.Log($"[SegCtrl] Obstacle={_worker?.ObstacleRatio:P1} Floor={_worker?.FloorRatio:P1} " +
                      $"Busy={_worker?.IsBusy} Frames={_totalFramesReceived} Rot={_tensorRotation}° " +
                      $"FlipY={_flipInputY} FlipX={_flipInputX} Interval={_currentInterval}f " +
                      $"MODEL_SIZE={MODEL_SIZE} CPU={_cpuFallbackActive}");
        }

        public void SetROITopSkip(float ratio)
        {
            _roiTopSkip = Mathf.Clamp01(ratio);
            Debug.Log($"[SegCtrl] ROI skip → {_roiTopSkip:P0} (solo captura, no overlay).");
        }
    }
}