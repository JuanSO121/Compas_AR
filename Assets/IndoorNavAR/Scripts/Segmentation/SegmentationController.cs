// File: SegmentationController.cs
// ✅ v12.1 — FIX overlay debug: activa segmentación temporalmente para debug visual
//
// ============================================================================
//  CAMBIOS v12.0 → v12.1
// ============================================================================
//
//  FIX OVERLAY C — SetOverlayVisible(true) con _onlyDuringNavigation=true
//  y sin navegación activa activaba el RawImage pero la textura permanecía
//  vacía/negra porque _segmentationActive=false impedía que Update() llamara
//  TryCaptureAndSchedule() y por tanto UpdateMask() nunca recibía datos.
//
//  SOLUCIÓN:
//    • Nuevo flag privado _segmentationActivatedForDebug (bool).
//    • Cuando SetOverlayVisible(true) se llama sin navegación activa,
//      se activa la segmentación internamente (igual que ActivateSegmentation()
//      pero marcando _segmentationActivatedForDebug=true).
//    • Cuando SetOverlayVisible(false) se llama y _segmentationActivatedForDebug
//      es true, se desactiva la segmentación para no consumir recursos en
//      producción tras cerrar el debug overlay.
//    • Si la navegación se inicia mientras el debug overlay está activo,
//      _segmentationActivatedForDebug se limpia (la nav gestiona el ciclo
//      de vida desde ese punto).
//
//  TODOS LOS CAMBIOS DE v12.0 SE CONSERVAN ÍNTEGRAMENTE.

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

        [Tooltip("Muestra la máscara de segmentación sobre la cámara.\n" +
                 "true  = overlay visible (debug / demo).\n" +
                 "false = overlay oculto (producción).")]
        [SerializeField] private bool _debugOverlayEnabled = false;

        [Header("ROI — Región de Interés")]
        [Tooltip("Fracción desde arriba que se OMITE al capturar el frame.")]
        [SerializeField, Range(0f, 0.7f)]
        private float _roiTopSkip = 0.4f;

        [Header("─── Timer de inferencia ──────────────────────────────────────")]
        [Tooltip("Intervalo entre inferencias (s). Mínimo 3s en device. Default: 3s")]
        [SerializeField, Range(0.5f, 10f)]
        private float _inferenceInterval = 3f;

        [Header("─── Alertas TTS ──────────────────────────────────────────────")]
        [SerializeField] private float _obstacleAlertThreshold = 0.20f;
        [SerializeField] private float _obstacleHighThreshold  = 0.40f;
        [SerializeField] private float _alertCooldownNormal    = 7f;
        [SerializeField] private float _alertCooldownHigh      = 12f;
        [SerializeField] private int   _obstacleConfirmCount   = 2;

        [Header("─── Posicionamiento de obstáculos ──────────────────────────────")]
        [Tooltip("Distancia frontal (m) al colocar obstáculo cuando se detecta en zona central.")]
        [SerializeField] private float _obstacleForwardDistance = 1.8f;
        [Tooltip("Offset lateral (m) para obstáculos en zona izquierda/derecha.")]
        [SerializeField] private float _obstacleLateralOffset   = 0.8f;
        [Tooltip("Fracción de píxeles de obstáculo en una columna para considerarla 'bloqueada'.")]
        [SerializeField, Range(0.05f, 0.5f)]
        private float _columnBlockThreshold = 0.15f;
        [Tooltip("Prefab de NavMeshObstacleAgent para instanciar cuando se detecta obstáculo.")]
        [SerializeField] private NavMeshObstacleAgent _obstacleAgentPrefab;

        [Header("Control de activación")]
        [Tooltip("Si está marcado, la segmentación SOLO se activa durante navegación.")]
        [SerializeField] private bool _onlyDuringNavigation = true;

        [Header("Debug")]
        [SerializeField] private bool _logStats        = true;
        [SerializeField] private bool _logFrameCapture = true;

        // ── Constantes ────────────────────────────────────────────────────
        private const int   MODEL_SIZE                    = ObstacleSegmentationWorker.IMAGE_SIZE;
        private const int   MAX_GPU_TIMEOUTS              = 3;
        private const float MIN_INFERENCE_INTERVAL_DEVICE = 3.0f;

        // Regiones de imagen para análisis de posición de obstáculo
        private const float REGION_LEFT_LIMIT   = 0.33f;
        private const float REGION_RIGHT_LIMIT  = 0.67f;
        private const float REGION_NEAR_LIMIT   = 0.6f;  // fila Y normalizada (de abajo)

        // ── Worker y texturas ─────────────────────────────────────────────
        private ObstacleSegmentationWorker _worker;
        private RenderTexture              _cameraRT;
        private Texture2D                  _frameBufferFallback;
        private Texture2D                  _fitTex;

        // ── Estado ────────────────────────────────────────────────────────
        private bool  _initialized        = false;
        private bool  _segmentationActive = false;
        private bool  _pendingActivation  = false;
        private bool  _cpuFallbackActive  = false;

        // ✅ FIX OVERLAY C: flag para saber si la segmentación fue activada
        // solo para mostrar el debug overlay (sin navegación real activa).
        // Al desactivar el overlay se usa para limpiar correctamente.
        private bool _segmentationActivatedForDebug = false;

        // ── Control de timeout GPU ────────────────────────────────────────
        private bool _inferenceScheduled      = false;
        private int  _consecutivePollTimeouts = 0;

        // ── Timer de inferencia ───────────────────────────────────────────
        private float _timeSinceLastInference = 0f;

        // ── Estado de alertas ─────────────────────────────────────────────
        private float _lastAlertTime           = -999f;
        private bool  _obstacleAlertActive     = false;
        private int   _obstacleConsecutiveCount = 0;

        // ── Obstacle placement ────────────────────────────────────────────
        private NavMeshObstacleAgent _activeObstacleAgent = null;
        private float _lastObstaclePlaceTime = -999f;
        private const float OBSTACLE_PLACE_COOLDOWN = 5f;

        // ── Propiedades públicas ──────────────────────────────────────────
        public bool OverlayVisible       => _debugOverlayEnabled;
        public bool IsSegmentationActive => _segmentationActive;

        // ═════════════════════════════════════════════════════════════════
        //  Awake — FIX_VIO: garantías antes de Start()
        // ═════════════════════════════════════════════════════════════════

        private void Awake()
        {
#if !UNITY_EDITOR
            if (!_onlyDuringNavigation)
            {
                _onlyDuringNavigation = true;
                Debug.Log("[SegCtrl v12.1] 📱 [FIX_VIO] _onlyDuringNavigation forzado a true en device.");
            }
            if (_inferenceInterval < MIN_INFERENCE_INTERVAL_DEVICE)
            {
                Debug.LogWarning($"[SegCtrl v12.1] ⚠️ [FIX_VIO] _inferenceInterval corregido a {MIN_INFERENCE_INTERVAL_DEVICE}s.");
                _inferenceInterval = MIN_INFERENCE_INTERVAL_DEVICE;
            }
#endif
        }

        // ═════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ═════════════════════════════════════════════════════════════════

        private void Start()
        {
            ForceCanvasExpand();

            if (_modelAsset == null)
            {
                Debug.LogError("[SegCtrl v12.1] ❌ ModelAsset no asignado.");
                return;
            }

            if (_cameraManager == null)
                _cameraManager = FindFirstObjectByType<ARCameraManager>(FindObjectsInactive.Include);
            if (_cameraBackground == null)
                _cameraBackground = FindFirstObjectByType<ARCameraBackground>(FindObjectsInactive.Include);

            if (_cameraManager == null)
            {
                Debug.LogError("[SegCtrl v12.1] ❌ ARCameraManager NO encontrado.");
                return;
            }

            _cameraRT = new RenderTexture(MODEL_SIZE, MODEL_SIZE, 0, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp
            };
            _cameraRT.Create();

            _frameBufferFallback = new Texture2D(MODEL_SIZE, MODEL_SIZE, TextureFormat.RGB24, false);

            _worker = new ObstacleSegmentationWorker(
                _modelAsset, _backend, _tensorRotation, _flipInputY, _flipInputX);

            if (!_worker.IsReady)
            {
                Debug.LogError("[SegCtrl v12.1] ❌ Worker no pudo inicializarse.");
                return;
            }

            _overlayRenderer?.Initialize(_worker.MaskWidth, _worker.MaskHeight);
            _overlayRenderer?.SetFlipMode(SegmentationOverlayRenderer.FlipMode.None);
            // Comenzar explícitamente oculto
            _overlayRenderer?.SetVisible(false);

            _worker.OnInferenceComplete += HandleInferenceComplete;

            if (!_onlyDuringNavigation)
            {
                _segmentationActive = true;
                NotifyFlutterSegmentationState(true);
                if (_debugOverlayEnabled)
                    _overlayRenderer?.SetVisible(true);
                Debug.Log("[SegCtrl v12.1] ✅ Segmentación activa desde Start.");
            }

            _initialized = true;
            SubscribeToNavigationEvents();

            if (_pendingActivation)
            {
                _pendingActivation = false;
                ActivateSegmentation();
            }

            StartCoroutine(DiagnoseARSetup());

            Debug.Log($"[SegCtrl v12.1] ✅ Inicializado. rotation={_tensorRotation}° " +
                      $"flipY={_flipInputY} flipX={_flipInputX} " +
                      $"interval={_inferenceInterval}s onlyDuringNav={_onlyDuringNavigation}");
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
        //  Update — timer + poll
        // ═════════════════════════════════════════════════════════════════

        private void Update()
        {
            if (!_segmentationActive || _worker == null) return;

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
                    _inferenceScheduled = false;
                    _consecutivePollTimeouts++;
                    if (_consecutivePollTimeouts >= MAX_GPU_TIMEOUTS && !_cpuFallbackActive)
                        ReinitializeWithCPU();
                }
            }

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

        private void TryCaptureAndSchedule()
        {
            if (_worker == null || !_worker.IsReady || _worker.IsBusy) return;

#if !UNITY_EDITOR
            if (UnityEngine.XR.ARFoundation.ARSession.state !=
                UnityEngine.XR.ARFoundation.ARSessionState.SessionTracking)
            {
                if (_logFrameCapture)
                    Debug.Log($"[SegCtrl v12.1] ⏭️ [FIX_VIO] Saltando — ARSession={UnityEngine.XR.ARFoundation.ARSession.state}");
                return;
            }
#endif

            if (!TryCaptureFromCpuImage())
            {
                if (_logFrameCapture)
                    Debug.LogWarning("[SegCtrl v12.1] ⚠️ Captura fallida.");
                return;
            }

            _worker.ScheduleInference(_frameBufferFallback);
            _inferenceScheduled = true;

            if (_logFrameCapture)
                Debug.Log("[SegCtrl v12.1] 📸 Inferencia schedulada.");
        }

        // ── Resultado de inferencia ───────────────────────────────────────

        private void OnInferenceResultReady()
        {
            _overlayRenderer?.UpdateMask(_worker.MaskData);

            UpdateObstacleAlertState();
            EvaluateAlerts();
            EvaluateObstaclePosition();

            VoiceCommandAPI.Instance?.SendSegmentationRatio(
                _worker.ObstacleRatio,
                _worker.FloorRatio,
                _worker.WallRatio);

            if (_logStats)
                Debug.Log($"[SegCtrl v12.1] 🎯 Obstacle={_worker.ObstacleRatio:P1} " +
                          $"Floor={_worker.FloorRatio:P1} Wall={_worker.WallRatio:P1}");
        }

        private void HandleInferenceComplete()
        {
            if (_logStats) Debug.Log("[SegCtrl v12.1] ✅ InferenceComplete.");
        }

        // ═════════════════════════════════════════════════════════════════
        //  Análisis de posición de obstáculo en imagen
        // ═════════════════════════════════════════════════════════════════

        private enum ObstacleRegion { None, Left, Center, Right }

        private ObstacleRegion AnalyzeObstaclePosition(out float normalizedX, out float normalizedY)
        {
            normalizedX = 0.5f;
            normalizedY = 0.5f;

            int[] mask = _worker.MaskData;
            if (mask == null || mask.Length == 0) return ObstacleRegion.None;

            int w = _worker.MaskWidth;
            int h = _worker.MaskHeight;

            int leftCount   = 0;
            int centerCount = 0;
            int rightCount  = 0;
            int total       = w * h;

            long sumX = 0, sumY = 0;
            int  obsCnt = 0;

            for (int row = 0; row < h; row++)
            {
                for (int col = 0; col < w; col++)
                {
                    int idx = row * w + col;
                    if (mask[idx] != ObstacleSegmentationWorker.CLASS_OBSTACLE) continue;

                    float colNorm = (float)col / w;

                    if (colNorm < REGION_LEFT_LIMIT)        leftCount++;
                    else if (colNorm < REGION_RIGHT_LIMIT)  centerCount++;
                    else                                     rightCount++;

                    sumX += col;
                    sumY += row;
                    obsCnt++;
                }
            }

            if (obsCnt == 0) return ObstacleRegion.None;

            normalizedX = (float)sumX / (obsCnt * w);
            normalizedY = (float)sumY / (obsCnt * h);

            float leftRatio   = (float)leftCount   / total;
            float centerRatio = (float)centerCount / total;
            float rightRatio  = (float)rightCount  / total;

            float maxRatio = Mathf.Max(leftRatio, centerRatio, rightRatio);
            if (maxRatio < _columnBlockThreshold) return ObstacleRegion.None;

            if (centerRatio >= leftRatio && centerRatio >= rightRatio)
                return ObstacleRegion.Center;
            if (leftRatio >= rightRatio)
                return ObstacleRegion.Left;
            return ObstacleRegion.Right;
        }

        private void EvaluateObstaclePosition()
        {
            if (_worker.ObstacleRatio < _obstacleAlertThreshold) return;
            if (_obstacleConsecutiveCount < _obstacleConfirmCount) return;
            if (Time.time - _lastObstaclePlaceTime < OBSTACLE_PLACE_COOLDOWN) return;
            if (ObstacleRerouteMediator.IsActive) return;

            ObstacleRegion region = AnalyzeObstaclePosition(
                out float normX, out float normY);

            if (region == ObstacleRegion.None) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 forward = cam.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) return;
            forward.Normalize();

            Vector3 right = cam.transform.right;
            right.y = 0f;
            right.Normalize();

            float lateralSign = 0f;
            switch (region)
            {
                case ObstacleRegion.Left:   lateralSign = -1f; break;
                case ObstacleRegion.Right:  lateralSign =  1f; break;
                case ObstacleRegion.Center: lateralSign =  0f; break;
            }

            float distanceFactor = Mathf.Lerp(1.2f, _obstacleForwardDistance, 1f - normY);
            Vector3 obstacleWorldPos = cam.transform.position
                + forward * distanceFactor
                + right   * lateralSign * _obstacleLateralOffset;

            if (!UnityEngine.AI.NavMesh.SamplePosition(
                    obstacleWorldPos, out UnityEngine.AI.NavMeshHit hit, 2f,
                    UnityEngine.AI.NavMesh.AllAreas))
            {
                Debug.LogWarning($"[SegCtrl v12.1] ⚠️ No se encontró NavMesh para obstáculo en {obstacleWorldPos:F2}");
                return;
            }

            _lastObstaclePlaceTime = Time.time;

            if (_obstacleAgentPrefab != null)
            {
                if (_activeObstacleAgent == null)
                    _activeObstacleAgent = Instantiate(_obstacleAgentPrefab);

                _activeObstacleAgent.PlaceAt(hit.position);
            }

            EventBus.Instance?.Publish(new RouteDeviatedEvent
            {
                UserPosition      = cam.transform.position,
                DeviationDistance = distanceFactor,
                Destination       = GetCurrentDestination(),
            });

            string regionName = region.ToString().ToLower();
            string ttsMsg = region == ObstacleRegion.Center
                ? "Obstáculo al frente, buscando ruta alternativa"
                : $"Obstáculo a la {(region == ObstacleRegion.Left ? "izquierda" : "derecha")}, rodeando";

            VoiceCommandAPI.Instance?.SpeakArbitraryText(ttsMsg, priority: 2, interrupt: false);

            Debug.Log($"[SegCtrl v12.1] 🚧 Obstáculo colocado: región={regionName} " +
                      $"normX={normX:F2} normY={normY:F2} " +
                      $"worldPos={hit.position:F2} dist={distanceFactor:F2}m");
        }

        private Vector3 GetCurrentDestination()
        {
            var agent = FindFirstObjectByType<NavigationAgent>(FindObjectsInactive.Include);
            return agent != null ? agent.LastDestination : Vector3.zero;
        }

        // ═════════════════════════════════════════════════════════════════
        //  Alertas
        // ═════════════════════════════════════════════════════════════════

        private void UpdateObstacleAlertState()
        {
            if (_worker.ObstacleRatio >= _obstacleAlertThreshold)
            {
                _obstacleConsecutiveCount++;
            }
            else
            {
                if (_obstacleConsecutiveCount > 0 && _logStats)
                    Debug.Log($"[SegCtrl v12.1] ✅ Obstáculo despejado.");
                _obstacleConsecutiveCount = 0;
                _obstacleAlertActive      = false;

                if (_activeObstacleAgent != null && _activeObstacleAgent.gameObject.activeSelf)
                    _activeObstacleAgent.Remove();
            }
        }

        private void EvaluateAlerts()
        {
            if (ObstacleRerouteMediator.IsActive) return;
            if (_obstacleAlertActive) return;
            if (_obstacleConsecutiveCount < _obstacleConfirmCount) return;
            if (_worker.ObstacleRatio < _obstacleAlertThreshold) return;

            bool  isHigh   = _worker.ObstacleRatio >= _obstacleHighThreshold;
            float cooldown = isHigh ? _alertCooldownHigh : _alertCooldownNormal;
            float elapsed  = Time.realtimeSinceStartup - _lastAlertTime;

            if (elapsed < cooldown) return;

            _lastAlertTime       = Time.realtimeSinceStartup;
            _obstacleAlertActive = true;

            string msg = isHigh
                ? "Precaución, obstáculo muy cerca"
                : "Obstáculo detectado al frente";

            VoiceCommandAPI.Instance?.SpeakArbitraryText(msg, priority: 2, interrupt: false);
            Debug.Log($"[SegCtrl v12.1] 🚧 Alerta: '{msg}' ratio={_worker.ObstacleRatio:P1}");
        }

        // ═════════════════════════════════════════════════════════════════
        //  Captura CPU
        // ═════════════════════════════════════════════════════════════════

        private bool TryCaptureFromCpuImage()
        {
            if (_cameraManager == null) return false;
            if (!_cameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage)) return false;

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
        //  Activación / desactivación
        // ═════════════════════════════════════════════════════════════════

        private void ActivateSegmentation()
        {
            if (_segmentationActive) return;

            if (_worker == null || !_worker.IsReady)
            {
                Debug.LogWarning("[SegCtrl v12.1] ⚠️ Worker no listo. Intentando CPU...");
                if (!_cpuFallbackActive && _modelAsset != null) ReinitializeWithCPU();
                if (_worker == null || !_worker.IsReady) { Debug.LogError("[SegCtrl v12.1] ❌ Worker inválido."); return; }
            }

            _segmentationActive          = true;
            _timeSinceLastInference      = _inferenceInterval;
            _inferenceScheduled          = false;
            _obstacleConsecutiveCount    = 0;
            _obstacleAlertActive         = false;

            // Si la navegación real inicia, limpiar el flag de debug
            // para que DeactivateSegmentation() se comporte normal al terminar la nav.
            _segmentationActivatedForDebug = false;

            if (_debugOverlayEnabled)
                _overlayRenderer?.SetVisible(true);

            NotifyFlutterSegmentationState(true);
            Debug.Log("[SegCtrl v12.1] ✅ Segmentación activada.");
        }

        private void DeactivateSegmentation()
        {
            if (!_segmentationActive) return;

            _segmentationActive            = false;
            _inferenceScheduled            = false;
            _obstacleConsecutiveCount      = 0;
            _obstacleAlertActive           = false;
            _segmentationActivatedForDebug = false;

            _overlayRenderer?.SetVisible(false);
            NotifyFlutterSegmentationState(false);
            Debug.Log("[SegCtrl v12.1] ⏸️ Segmentación desactivada.");
        }

        // ═════════════════════════════════════════════════════════════════
        //  Eventos de navegación
        // ═════════════════════════════════════════════════════════════════

        private void SubscribeToNavigationEvents()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
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
            if (!_initialized || _worker == null) { _pendingActivation = true; return; }
            ActivateSegmentation();
            Debug.Log("[SegCtrl v12.1] 🚀 Navegación iniciada → Segmentación ACTIVADA");
        }

        private void OnNavigationStopped(NavigationStoppedEvent evt)
        {
            if (!_onlyDuringNavigation) return;
            _pendingActivation = false;
            DeactivateSegmentation();
        }

        private void OnNavigationArrived(NavigationArrivedEvent evt)
        {
            if (!_onlyDuringNavigation) return;
            _pendingActivation = false;
            DeactivateSegmentation();
        }

        // ═════════════════════════════════════════════════════════════════
        //  Notificación Flutter
        // ═════════════════════════════════════════════════════════════════

        private void NotifyFlutterSegmentationState(bool active)
        {
            var api = VoiceCommandAPI.Instance;
            if (api == null) return;
            string json = $"{{\"action\":\"segmentation_active\",\"active\":{(active ? "true" : "false")}}}";
            api.ReplyPublic(json);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Fallback CPU
        // ═════════════════════════════════════════════════════════════════

        [ContextMenu("🔄 Reinicializar con CPU")]
        public void ReinitializeWithCPU()
        {
            if (_cpuFallbackActive) return;
            Debug.LogWarning("[SegCtrl v12.1] 🔄 GPU timeout → CPU...");
            _worker?.Dispose();
            _worker = new ObstacleSegmentationWorker(
                _modelAsset, BackendType.CPU, _tensorRotation, _flipInputY, _flipInputX);
            if (!_worker.IsReady) { Debug.LogError("[SegCtrl v12.1] ❌ Fallback CPU falló."); return; }
            _worker.OnInferenceComplete += HandleInferenceComplete;
            _inferenceScheduled  = false;
            _cpuFallbackActive   = true;
            Debug.Log("[SegCtrl v12.1] ✅ Fallback CPU activo.");
        }

        // ═════════════════════════════════════════════════════════════════
        //  Diagnóstico
        // ═════════════════════════════════════════════════════════════════

        private IEnumerator DiagnoseARSetup()
        {
            yield return new WaitForSeconds(5f);
            Debug.Log($"[SegCtrl v12.1] 🔍 Diagnóstico: " +
                      $"initialized={_initialized} active={_segmentationActive} " +
                      $"workerReady={_worker?.IsReady} onlyDuringNav={_onlyDuringNavigation}");
        }

        private void ForceCanvasExpand()
        {
            var scaler = GetComponentInChildren<CanvasScaler>(true)
                      ?? GetComponentInParent<CanvasScaler>();
            if (scaler != null &&
                scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize &&
                scaler.screenMatchMode != CanvasScaler.ScreenMatchMode.Expand)
            {
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        //  API pública
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Activa o desactiva el overlay de segmentación.
        ///
        /// FIX OVERLAY A (v12.0): visible=true activa el overlay independientemente
        /// del estado de navegación.
        ///
        /// FIX OVERLAY C (v12.1): cuando _onlyDuringNavigation=true y no hay
        /// navegación activa, activa también la segmentación para que la textura
        /// reciba datos reales (sin este fix el overlay aparecía negro/vacío).
        /// Marca _segmentationActivatedForDebug=true para que al desactivar el
        /// overlay la segmentación se detenga automáticamente.
        /// </summary>
        public void SetOverlayVisible(bool visible)
        {
            _debugOverlayEnabled = visible;

            if (visible)
            {
                if (_segmentationActive)
                {
                    // La segmentación ya corre (por navegación u otro motivo).
                    // Solo activar el overlay visual.
                    _overlayRenderer?.SetVisible(true);
                    Debug.Log("[SegCtrl v12.1] 🎭 Overlay activado (segmentación ya activa).");
                }
                else if (!_onlyDuringNavigation)
                {
                    // Modo libre: activar segmentación normal.
                    ActivateSegmentation();
                    _overlayRenderer?.SetVisible(true);
                }
                else
                {
                    // ✅ FIX OVERLAY C: _onlyDuringNavigation=true pero queremos debug visual.
                    // Activar segmentación temporalmente para que UpdateMask() reciba datos.
                    // Sin esto el overlay se ve negro porque la textura nunca se actualiza.
                    _segmentationActivatedForDebug = true;
                    _segmentationActive            = true;
                    _timeSinceLastInference        = _inferenceInterval; // inferir en el próximo tick
                    _inferenceScheduled            = false;
                    _obstacleConsecutiveCount      = 0;
                    _obstacleAlertActive           = false;
                    _overlayRenderer?.SetVisible(true);
                    Debug.Log("[SegCtrl v12.1] 🎭 Overlay debug — segmentación activada temporalmente " +
                              "(sin navegación activa). _segmentationActivatedForDebug=true");
                }
            }
            else
            {
                // ✅ FIX OVERLAY C: si la segmentación fue activada solo para debug,
                // desactivarla al ocultar el overlay para no consumir recursos.
                if (_segmentationActivatedForDebug)
                {
                    _segmentationActivatedForDebug = false;
                    DeactivateSegmentation(); // esto también llama SetVisible(false)
                    Debug.Log("[SegCtrl v12.1] 🎭 Overlay debug desactivado — segmentación detenida.");
                }
                else
                {
                    // La segmentación fue activada por navegación real: no tocarla,
                    // solo ocultar el overlay visual.
                    bool shouldShow = _segmentationActive && _debugOverlayEnabled;
                    _overlayRenderer?.SetVisible(shouldShow);
                }
            }

            Debug.Log($"[SegCtrl v12.1] 🎭 DebugOverlay → {visible} " +
                      $"(segActive={_segmentationActive} forDebug={_segmentationActivatedForDebug})");
        }

        public void SetROITopSkip(float ratio)
        {
            _roiTopSkip = Mathf.Clamp01(ratio);
        }

        // ═════════════════════════════════════════════════════════════════
        //  ContextMenu
        // ═════════════════════════════════════════════════════════════════

        [ContextMenu("🐛 Activar Debug Overlay")]
        private void DbgOverlayOn()  => SetOverlayVisible(true);

        [ContextMenu("🚀 Desactivar Debug Overlay")]
        private void DbgOverlayOff() => SetOverlayVisible(false);

        [ContextMenu("Toggle Overlay")]
        private void DbgToggleOverlay() => SetOverlayVisible(!_debugOverlayEnabled);

        [ContextMenu("✅ Activar Segmentación")]
        private void DbgActivate() => ActivateSegmentation();

        [ContextMenu("⏸️ Desactivar Segmentación")]
        private void DbgDeactivate() => DeactivateSegmentation();

        [ContextMenu("🔄 Rot 90°")]  private void DbgRot90()  => ApplyRotation(90);
        [ContextMenu("🔄 Rot 0°")]   private void DbgRot0()   => ApplyRotation(0);
        [ContextMenu("🔄 Rot 180°")] private void DbgRot180() => ApplyRotation(180);
        [ContextMenu("🔄 Rot 270°")] private void DbgRot270() => ApplyRotation(270);
        private void ApplyRotation(int deg) { _tensorRotation = deg; _worker?.SetRotation(deg); }

        [ContextMenu("🔄 Flip Y ON")]  private void DbgFlipYOn()  { _flipInputY = true;  _worker?.SetFlipY(true);  }
        [ContextMenu("🔄 Flip Y OFF")] private void DbgFlipYOff() { _flipInputY = false; _worker?.SetFlipY(false); }
        [ContextMenu("🔄 Flip X ON")]  private void DbgFlipXOn()  { _flipInputX = true;  _worker?.SetFlipX(true);  }
        [ContextMenu("🔄 Flip X OFF")] private void DbgFlipXOff() { _flipInputX = false; _worker?.SetFlipX(false); }

        [ContextMenu("🔄 Resetear alertas")]
        private void DbgResetAlerts()
        {
            _obstacleConsecutiveCount = 0;
            _obstacleAlertActive      = false;
            _lastAlertTime            = -999f;
        }

        [ContextMenu("📊 Log Stats")]
        private void DbgStats()
        {
            Debug.Log($"[SegCtrl v12.1] Active={_segmentationActive} " +
                      $"ForDebug={_segmentationActivatedForDebug} " +
                      $"Obstacle={_worker?.ObstacleRatio:P1} " +
                      $"Floor={_worker?.FloorRatio:P1} Wall={_worker?.WallRatio:P1}\n" +
                      $"  overlayVisible={_overlayRenderer?.IsVisible} " +
                      $"debugEnabled={_debugOverlayEnabled}");
        }

        [ContextMenu("⏱️ Forzar inferencia ahora")]
        private void DbgForceInference()
        {
            if (!_segmentationActive) { Debug.LogWarning("[SegCtrl v12.1] Activa primero."); return; }
            _timeSinceLastInference = _inferenceInterval;
        }

        [ContextMenu("🔍 Analizar posición de obstáculo")]
        private void DbgAnalyzeObstacle()
        {
            var region = AnalyzeObstaclePosition(out float nx, out float ny);
            Debug.Log($"[SegCtrl v12.1] Región obstáculo: {region} normX={nx:F2} normY={ny:F2}");
        }
    }
}