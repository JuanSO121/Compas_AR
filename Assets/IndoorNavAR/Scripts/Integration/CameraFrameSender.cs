// File: CameraFrameSender.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Integration/
// ✅ v1.0 — Envío de frames ARCore a Flutter para segmentación semántica
//
// ============================================================================
//  PROPÓSITO
// ============================================================================
//
//  ARCore/ARKit toma control exclusivo de la cámara trasera a nivel de
//  hardware. Flutter no puede abrir un CameraController independiente
//  mientras Unity está activo — la cámara ya está tomada.
//
//  Solución: Unity ya tiene los frames a través de ARCameraManager.
//  Este componente los submuestrea, comprime a JPEG y los reenvía a
//  Flutter via VoiceCommandAPI.SendFrameToFlutter(), que usa el mismo
//  canal UnitySendMessage del bridge existente.
//
//  Flutter intercepta los mensajes action="frame_data" antes de pasarlos
//  a onResponse, y los entrega al ObstacleDetectionService via stream.
//  ObstacleDetectionService ya tiene la lógica TFLite — solo cambia la
//  fuente de entrada de CameraImage a Uint8List JPEG.
//
// ============================================================================
//  CONFIGURACIÓN RECOMENDADA
// ============================================================================
//
//  _targetFps   = 10   — suficiente para segmentación en tiempo real
//  _jpegQuality = 50   — ~15 KB/frame, buena relación calidad/tamaño
//  _targetSize  = 224  — resolución nativa del modelo TFLite típico
//
//  Para dispositivos lentos: bajar a _targetFps = 5, _jpegQuality = 35.
//  El modelo TFLite es más sensible a resolución que a compresión JPEG.
//
// ============================================================================
//  REQUISITO
// ============================================================================
//
//  AR Foundation → Project Settings → XR Plug-in Management:
//  asegúrate de que "AR Foundation CPU Image" esté habilitado.
//  Con ARCore ya activo en el proyecto, suele estar disponible por defecto.

using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace IndoorNavAR.Integration
{
    /// <summary>
    /// Captura frames de la cámara AR (ya controlada por ARCore/ARKit)
    /// y los envía a Flutter a 10 fps como JPEG Base64 comprimido.
    ///
    /// Attach este componente al mismo GameObject que VoiceCommandAPI
    /// o a cualquier objeto persistente de la escena AR.
    /// </summary>
    public class CameraFrameSender : MonoBehaviour
    {
        [Header("Dependencias (auto-detectadas si quedan vacías)")]
        [SerializeField] private ARCameraManager _cameraManager;

        [Header("─── Parámetros de envío ─────────────────────────────────")]
        [Tooltip("Frames por segundo enviados a Flutter. " +
                 "10 fps es suficiente para segmentación semántica. " +
                 "Bajar a 5 si hay lag de bridge.")]
        [SerializeField] private int _targetFps = 10;

        [Tooltip("Calidad JPEG (1-100). 50 = ~15 KB/frame. " +
                 "El modelo TFLite tolera bien la compresión — " +
                 "bajar hasta 35 en dispositivos con poca RAM.")]
        [Range(20, 80)]
        [SerializeField] private int _jpegQuality = 50;

        [Tooltip("Dimensión cuadrada del frame enviado (px). " +
                 "224 coincide con la resolución de entrada del modelo TFLite " +
                 "de segmentación — el resize se hace aquí en Unity para " +
                 "minimizar el payload del bridge.")]
        [SerializeField] private int _targetSize = 224;

        [Header("─── Debug ───────────────────────────────────────────────")]
        [SerializeField] private bool _logFrameSent = false;
        [SerializeField] private bool _enabled = true;

        // ─── Estado interno ────────────────────────────────────────────

        private float    _interval;
        private float    _lastSentTime = -999f;
        private Texture2D _resizedTex;          // reutilizada entre frames
        private bool     _initialized;
        private int      _framesSent;           // para debug

        // ─── Propiedades públicas ──────────────────────────────────────

        /// <summary>Activar/desactivar el envío de frames en runtime.</summary>
        public bool IsEnabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public int FramesSent => _framesSent;

        // ─── Lifecycle ─────────────────────────────────────────────────

        private void Awake()
        {
            _cameraManager ??= FindFirstObjectByType<ARCameraManager>();

            if (_cameraManager == null)
            {
                Debug.LogError("[CameraFrameSender] ❌ ARCameraManager no encontrado. " +
                               "Verifica que XR Origin tiene AR Camera Manager.");
                enabled = false;
                return;
            }

            _interval    = 1f / Mathf.Max(1, _targetFps);
            _resizedTex  = new Texture2D(_targetSize, _targetSize,
                                         TextureFormat.RGBA32, mipChain: false);
            _initialized = true;

            Debug.Log($"[CameraFrameSender] ✅ v1.0 — " +
                      $"{_targetFps} fps | JPEG q={_jpegQuality} | {_targetSize}×{_targetSize}px");
        }

        private void OnEnable()
        {
            if (_cameraManager != null)
                _cameraManager.frameReceived += OnARFrameReceived;
        }

        private void OnDisable()
        {
            if (_cameraManager != null)
                _cameraManager.frameReceived -= OnARFrameReceived;
        }

        private void OnDestroy()
        {
            if (_resizedTex != null)
                Destroy(_resizedTex);
        }

        // ─── Captura y envío ───────────────────────────────────────────

        /// <summary>
        /// Llamado por ARCameraManager cada vez que llega un nuevo frame de cámara.
        /// Submuestrea según _targetFps, convierte, comprime y envía a Flutter.
        /// </summary>
        private void OnARFrameReceived(ARCameraFrameEventArgs args)
        {
            if (!_initialized || !_enabled) return;
            if (Time.unscaledTime - _lastSentTime < _interval) return;

            // TryAcquireLatestCpuImage requiere AR Foundation CPU Image habilitado
            if (!_cameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
                return;

            using (cpuImage)
            {
                SendFrame(cpuImage);
            }
        }

        private void SendFrame(XRCpuImage cpuImage)
        {
            try
            {
                // ── 1. Parámetros de conversión ──────────────────────────────
                // MirrorY corrige la orientación que ARCore reporta en coordenadas
                // de cámara (Y invertida respecto a la pantalla).
                var convParams = new XRCpuImage.ConversionParams
                {
                    inputRect        = new RectInt(0, 0, cpuImage.width, cpuImage.height),
                    outputDimensions = new Vector2Int(_targetSize, _targetSize),
                    outputFormat     = TextureFormat.RGBA32,
                    transformation   = XRCpuImage.Transformation.MirrorY,
                };

                // ── 2. Convertir a buffer RGBA32 ─────────────────────────────
                int byteLength = _targetSize * _targetSize * 4;
                var rawBytes   = new NativeArray<byte>(byteLength, Allocator.Temp);

                try
                {
                    cpuImage.Convert(convParams, rawBytes);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CameraFrameSender] Error en Convert: {ex.Message}");
                    rawBytes.Dispose();
                    return;
                }

                // ── 3. Cargar en Texture2D reutilizable ──────────────────────
                _resizedTex.LoadRawTextureData(rawBytes);
                _resizedTex.Apply(updateMipmaps: false);
                rawBytes.Dispose();

                // ── 4. Comprimir a JPEG ──────────────────────────────────────
                byte[] jpeg = _resizedTex.EncodeToJPG(_jpegQuality);
                if (jpeg == null || jpeg.Length == 0)
                {
                    Debug.LogWarning("[CameraFrameSender] EncodeToJPG retornó vacío.");
                    return;
                }

                // ── 5. Serializar y enviar ────────────────────────────────────
                // Formato: { "action": "frame_data", "data": "<base64>", "w": 224, "h": 224 }
                // Flutter intercepta este action en UnityBridgeService antes de
                // pasarlo a onResponse — no contamina el canal de comandos.
                string b64  = Convert.ToBase64String(jpeg);
                string json = $"{{\"action\":\"frame_data\"," +
                              $"\"data\":\"{b64}\"," +
                              $"\"w\":{_targetSize}," +
                              $"\"h\":{_targetSize}}}";

                var api = VoiceCommandAPI.Instance;
                if (api == null)
                {
                    // VoiceCommandAPI aún no disponible — no es error, ocurre al inicio
                    return;
                }

                api.SendFrameToFlutter(json);
                _lastSentTime = Time.unscaledTime;
                _framesSent++;

                if (_logFrameSent)
                    Debug.Log($"[CameraFrameSender] 📸 Frame {_framesSent} enviado — " +
                              $"{jpeg.Length / 1024f:F1} KB");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CameraFrameSender] ❌ Error enviando frame: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ─── Debug ─────────────────────────────────────────────────────

        [ContextMenu("ℹ️ Estado")]
        private void DebugState()
        {
            Debug.Log($"[CameraFrameSender] " +
                      $"enabled={_enabled} | initialized={_initialized} | " +
                      $"fps={_targetFps} | quality={_jpegQuality} | size={_targetSize} | " +
                      $"framesSent={_framesSent} | " +
                      $"cameraManager={(_cameraManager != null ? _cameraManager.gameObject.name : "NULL")}");
        }

        [ContextMenu("🔄 Toggle envío")]
        private void DebugToggle() => _enabled = !_enabled;
    }
}