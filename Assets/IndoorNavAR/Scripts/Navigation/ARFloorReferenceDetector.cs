using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Sistema de detección de piso real mediante AR Foundation.
    /// PRIORIDAD: LiDAR/Depth > Plane Detection > Fallback estadístico
    /// </summary>
    public class ARFloorReferenceDetector : MonoBehaviour
    {
        [Header("AR Foundation Referencias")]
        [SerializeField] private ARPlaneManager _planeManager;
        [SerializeField] private AROcclusionManager _occlusionManager;
        [SerializeField] private ARCameraManager _cameraManager;
        
        [Header("Configuración de Detección")]
        [Tooltip("Tiempo de escaneo inicial (segundos)")]
        [SerializeField] private float _initialScanDuration = 3f;
        
        [Tooltip("Área mínima de plano para considerar válido (m²)")]
        [SerializeField] private float _minPlaneArea = 0.5f;
        
        [Tooltip("Margen de tolerancia para altura de piso (metros)")]
        [SerializeField] private float _floorHeightTolerance = 0.1f;
        
        [Header("Fallback Depth Sampling")]
        [Tooltip("Número de samples de depth para análisis")]
        [SerializeField] private int _depthSampleCount = 100;
        
        [Tooltip("Región de pantalla para samplear (0-1)")]
        [SerializeField] private Vector2 _depthSampleRegionMin = new Vector2(0.2f, 0.0f);
        [SerializeField] private Vector2 _depthSampleRegionMax = new Vector2(0.8f, 0.3f);
        
        [Header("Estado")]
        [SerializeField] private bool _showDebugInfo = true;
        
        // Estado interno
        private bool _isScanning;
        private float _scanStartTime;
        private float? _detectedFloorHeight;
        private DetectionMethod _usedMethod;
        private List<ARPlane> _floorPlanes = new List<ARPlane>();
        
        // Capacidades del dispositivo
        private bool _hasPlaneDetection;
        private bool _hasDepthDetection;
        private bool _hasPlaneClassification;

        #region Properties

        public bool IsReady => _detectedFloorHeight.HasValue;
        public float? FloorHeight => _detectedFloorHeight;
        public DetectionMethod UsedMethod => _usedMethod;
        public bool IsScanning => _isScanning;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            ValidateDependencies();
            DetectDeviceCapabilities();
        }

        private void OnEnable()
        {
            if (_planeManager != null)
            {
                _planeManager.trackablesChanged.AddListener(OnPlanesChanged);
            }
        }

        private void OnDisable()
        {
            if (_planeManager != null)
            {
                _planeManager.trackablesChanged.RemoveListener(OnPlanesChanged);
            }
        }

        #endregion

        #region Initialization

        private void ValidateDependencies()
        {
            if (_planeManager == null)
                _planeManager = FindFirstObjectByType<ARPlaneManager>();
            
            if (_occlusionManager == null)
                _occlusionManager = FindFirstObjectByType<AROcclusionManager>();
            
            if (_cameraManager == null)
                _cameraManager = FindFirstObjectByType<ARCameraManager>();
            
            if (_planeManager == null)
            {
                Debug.LogWarning("[ARFloorReference] ARPlaneManager no encontrado. Funcionalidad limitada.");
            }
        }

        private void DetectDeviceCapabilities()
        {
            // Detectar plane detection
            _hasPlaneDetection = _planeManager != null;
            
            // Detectar depth (LiDAR o ARCore Depth API)
            _hasDepthDetection = _occlusionManager != null && 
                                 _occlusionManager.descriptor != null &&
                                 _occlusionManager.descriptor.environmentDepthImageSupported == Supported.Supported;
            
            // Detectar plane classification (iOS ARKit 3.0+)
            _hasPlaneClassification = _planeManager != null && 
                                     _planeManager.descriptor != null &&
                                     _planeManager.descriptor.supportsClassification;
            
            LogCapabilities();
        }

        private void LogCapabilities()
        {
            Debug.Log("[ARFloorReference] ===== CAPACIDADES AR =====");
            Debug.Log($"  • Plane Detection: {_hasPlaneDetection}");
            Debug.Log($"  • Depth Detection: {_hasDepthDetection}");
            Debug.Log($"  • Plane Classification: {_hasPlaneClassification}");
            Debug.Log("==========================================");
        }

        #endregion

        #region Public API

        /// <summary>
        /// Inicia detección automática de piso usando todas las técnicas disponibles.
        /// </summary>
        public void StartFloorDetection()
        {
            if (_isScanning)
            {
                Debug.LogWarning("[ARFloorReference] Detección ya en progreso.");
                return;
            }
            
            _isScanning = true;
            _scanStartTime = Time.time;
            _detectedFloorHeight = null;
            _floorPlanes.Clear();
            
            Debug.Log("[ARFloorReference] 🔍 Iniciando detección de piso...");
            
            // Habilitar detección de planos horizontales
            if (_planeManager != null)
            {
                _planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;
            }
            
            Invoke(nameof(FinishScanAndAnalyze), _initialScanDuration);
        }

        /// <summary>
        /// Fuerza finalización del escaneo y análisis inmediato.
        /// </summary>
        public void ForceFinishScan()
        {
            if (!_isScanning) return;
            
            CancelInvoke(nameof(FinishScanAndAnalyze));
            FinishScanAndAnalyze();
        }

        /// <summary>
        /// Obtiene altura de referencia para NavMesh (con tolerancia).
        /// </summary>
        public bool GetFloorHeightRange(out float minHeight, out float maxHeight)
        {
            if (!_detectedFloorHeight.HasValue)
            {
                minHeight = maxHeight = 0f;
                return false;
            }
            
            minHeight = _detectedFloorHeight.Value - _floorHeightTolerance;
            maxHeight = _detectedFloorHeight.Value + _floorHeightTolerance;
            return true;
        }

        #endregion

        #region Detection Methods

        private void FinishScanAndAnalyze()
        {
            _isScanning = false;
            
            Debug.Log("[ARFloorReference] 📊 Analizando datos recolectados...");
            
            // MÉTODO 1: Plane Classification (iOS ARKit 3.0+)
            if (_hasPlaneClassification && TryDetectFloorByClassification(out float classifiedHeight))
            {
                _detectedFloorHeight = classifiedHeight;
                _usedMethod = DetectionMethod.PlaneClassification;
                OnFloorDetected();
                return;
            }
            
            // MÉTODO 2: Lowest Horizontal Plane (más confiable en espacios reales)
            if (_hasPlaneDetection && TryDetectLowestPlane(out float lowestPlaneHeight))
            {
                _detectedFloorHeight = lowestPlaneHeight;
                _usedMethod = DetectionMethod.LowestPlane;
                OnFloorDetected();
                return;
            }
            
            // MÉTODO 3: Depth Sampling (LiDAR/ARCore Depth)
            if (_hasDepthDetection && TryDetectFloorByDepth(out float depthFloorHeight))
            {
                _detectedFloorHeight = depthFloorHeight;
                _usedMethod = DetectionMethod.DepthSampling;
                OnFloorDetected();
                return;
            }
            
            // MÉTODO 4: Fallback - usar cámara AR como referencia
            Debug.LogWarning("[ARFloorReference] ⚠️ No se pudo detectar piso por AR. Usando fallback.");
            _detectedFloorHeight = GetCameraFloorEstimate();
            _usedMethod = DetectionMethod.CameraFallback;
            OnFloorDetected();
        }

        /// <summary>
        /// MÉTODO 1: Detección por clasificación de planos (ARKit 3.0+)
        /// </summary>
        private bool TryDetectFloorByClassification(out float floorHeight)
        {
            floorHeight = 0f;
            
            if (_planeManager == null || !_hasPlaneClassification)
                return false;
            
            // Buscar planos clasificados como "Floor"
            // Convert TrackableCollection to enumerable
            List<ARPlane> floorPlanes = new List<ARPlane>();
            foreach (var plane in _planeManager.trackables)
            {
                if (plane.classification == PlaneClassification.Floor &&
                    plane.size.x * plane.size.y >= _minPlaneArea)
                {
                    floorPlanes.Add(plane);
                }
            }
            
            // Ordenar por área (más grande primero)
            floorPlanes = floorPlanes.OrderByDescending(p => p.size.x * p.size.y).ToList();
            
            if (floorPlanes.Count == 0)
                return false;
            
            // Usar el plano más grande
            ARPlane largestFloor = floorPlanes[0];
            floorHeight = largestFloor.center.y;
            _floorPlanes.Add(largestFloor);
            
            Debug.Log($"[ARFloorReference] ✅ Piso detectado por CLASIFICACIÓN: Y={floorHeight:F3}m (área={largestFloor.size.x * largestFloor.size.y:F2}m²)");
            return true;
        }

        /// <summary>
        /// MÉTODO 2: Detección por plano más bajo (universal)
        /// </summary>
        private bool TryDetectLowestPlane(out float floorHeight)
        {
            floorHeight = 0f;
            
            if (_planeManager == null)
                return false;
            
            // Filtrar planos horizontales válidos
            List<ARPlane> horizontalPlanes = new List<ARPlane>();
            foreach (var plane in _planeManager.trackables)
            {
                if ((plane.alignment == PlaneAlignment.HorizontalUp || 
                     plane.alignment == PlaneAlignment.HorizontalDown) &&
                    plane.size.x * plane.size.y >= _minPlaneArea)
                {
                    horizontalPlanes.Add(plane);
                }
            }
            
            // Ordenar por altura (más bajo primero)
            horizontalPlanes = horizontalPlanes.OrderBy(p => p.center.y).ToList();
            
            if (horizontalPlanes.Count == 0)
                return false;
            
            // Tomar el plano más bajo
            ARPlane lowestPlane = horizontalPlanes[0];
            floorHeight = lowestPlane.center.y;
            _floorPlanes.Add(lowestPlane);
            
            Debug.Log($"[ARFloorReference] ✅ Piso detectado por PLANO MÁS BAJO: Y={floorHeight:F3}m (planos={horizontalPlanes.Count})");
            return true;
        }

        /// <summary>
        /// MÉTODO 3: Detección por depth sampling (LiDAR/ARCore Depth)
        /// </summary>
        private bool TryDetectFloorByDepth(out float floorHeight)
        {
            floorHeight = 0f;
            
            if (_occlusionManager == null || _cameraManager == null)
                return false;
            
            // Obtener textura de depth
            var depthTexture = _occlusionManager.environmentDepthTexture;
            if (depthTexture == null)
                return false;
            
            Camera arCamera = _cameraManager.GetComponent<Camera>();
            if (arCamera == null)
                return false;
            
            // Samplear puntos en región inferior de pantalla
            List<float> depthSamples = new List<float>();
            
            for (int i = 0; i < _depthSampleCount; i++)
            {
                // Generar posición aleatoria en región inferior
                float u = UnityEngine.Random.Range(_depthSampleRegionMin.x, _depthSampleRegionMax.x);
                float v = UnityEngine.Random.Range(_depthSampleRegionMin.y, _depthSampleRegionMax.y);
                
                Vector2 screenPoint = new Vector2(u * Screen.width, v * Screen.height);
                
                // Raycast desde cámara
                Ray ray = arCamera.ScreenPointToRay(screenPoint);
                
                // Obtener depth en ese punto (requiere shader/compute para leer textura)
                // Por simplicidad, usamos la distancia del raycast
                if (Physics.Raycast(ray, out RaycastHit hit, 10f))
                {
                    depthSamples.Add(hit.point.y);
                }
            }
            
            if (depthSamples.Count < 10)
                return false;
            
            // Analizar distribución de alturas (usar percentil bajo)
            depthSamples.Sort();
            int percentileIndex = Mathf.FloorToInt(depthSamples.Count * 0.1f); // 10% percentil
            floorHeight = depthSamples[percentileIndex];
            
            Debug.Log($"[ARFloorReference] ✅ Piso detectado por DEPTH SAMPLING: Y={floorHeight:F3}m (samples={depthSamples.Count})");
            return true;
        }

        /// <summary>
        /// MÉTODO 4: Fallback - estimar piso desde posición de cámara
        /// </summary>
        private float GetCameraFloorEstimate()
        {
            if (_cameraManager != null)
            {
                Camera arCamera = _cameraManager.GetComponent<Camera>();
                if (arCamera != null)
                {
                    // Asumir que la cámara está a ~1.5m del piso
                    float estimatedFloor = arCamera.transform.position.y - 1.5f;
                    Debug.Log($"[ARFloorReference] 📍 Piso estimado por CÁMARA: Y={estimatedFloor:F3}m");
                    return estimatedFloor;
                }
            }
            
            // Último recurso: usar Y=0
            Debug.LogWarning("[ARFloorReference] ⚠️ Usando Y=0 como fallback final");
            return 0f;
        }

        #endregion

        #region Event Callbacks

        private void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
        {
            if (!_isScanning) return;
            
            // Loggear planos nuevos
            foreach (ARPlane plane in args.added)
            {
                if (_showDebugInfo)
                {
                    Debug.Log($"[ARFloorReference] Plano detectado: {plane.trackableId}, " +
                             $"Tipo={plane.alignment}, Área={plane.size.x * plane.size.y:F2}m², " +
                             $"Y={plane.center.y:F3}m");
                }
            }
        }

        private void OnFloorDetected()
        {
            Debug.Log($"[ARFloorReference] ========== PISO DETECTADO ==========");
            Debug.Log($"  ✅ Altura: {_detectedFloorHeight:F3}m");
            Debug.Log($"  📍 Método: {_usedMethod}");
            Debug.Log($"  📐 Tolerancia: ±{_floorHeightTolerance}m");
            Debug.Log($"  🎯 Rango válido: [{_detectedFloorHeight - _floorHeightTolerance:F3}, {_detectedFloorHeight + _floorHeightTolerance:F3}]m");
            Debug.Log("==========================================");
        }

        #endregion

        #region Gizmos

        private void OnDrawGizmos()
        {
            if (!_showDebugInfo || !_detectedFloorHeight.HasValue)
                return;
            
            // Dibujar plano de referencia del piso
            Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
            Vector3 center = new Vector3(0f, _detectedFloorHeight.Value, 0f);
            Vector3 size = new Vector3(10f, 0.01f, 10f);
            Gizmos.DrawCube(center, size);
            
            // Dibujar rango de tolerancia
            Gizmos.color = new Color(0f, 1f, 0f, 0.1f);
            Vector3 minCenter = new Vector3(0f, _detectedFloorHeight.Value - _floorHeightTolerance, 0f);
            Vector3 maxCenter = new Vector3(0f, _detectedFloorHeight.Value + _floorHeightTolerance, 0f);
            Gizmos.DrawCube(minCenter, size);
            Gizmos.DrawCube(maxCenter, size);
            
            // Dibujar planos de piso detectados
            Gizmos.color = Color.green;
            foreach (ARPlane plane in _floorPlanes)
            {
                if (plane != null)
                {
                    Gizmos.DrawWireCube(plane.center, new Vector3(plane.size.x, 0.1f, plane.size.y));
                }
            }
        }

        #endregion

        #region Data Structures

        public enum DetectionMethod
        {
            None,
            PlaneClassification,  // ARKit 3.0+ Floor classification
            LowestPlane,          // Plano horizontal más bajo
            DepthSampling,        // LiDAR/Depth API sampling
            CameraFallback        // Estimación por cámara
        }

        #endregion
    }
}