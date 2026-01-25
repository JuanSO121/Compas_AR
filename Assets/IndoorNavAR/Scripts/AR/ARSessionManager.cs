using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.AR
{
    [RequireComponent(typeof(ARPlaneManager))]
    [RequireComponent(typeof(ARRaycastManager))]
    public class ARSessionManager : MonoBehaviour
    {
        [Header("Referencias AR")]
        [SerializeField] private ARPlaneManager _planeManager;
        [SerializeField] private ARRaycastManager _raycastManager;
        [SerializeField] private ARAnchorManager _anchorManager;

        [Header("Configuración")]
        [SerializeField] private bool _detectVerticalPlanes = false;
        [SerializeField] private bool _showPlaneVisualization = true;
        [SerializeField] private float _minimumPlaneArea = 0.5f;

        private readonly Dictionary<TrackableId, ARPlane> _detectedPlanes = new Dictionary<TrackableId, ARPlane>();
        private readonly List<ARRaycastHit> _raycastHits = new List<ARRaycastHit>();

        public bool IsSessionReady { get; private set; }
        public int DetectedPlaneCount => _detectedPlanes.Count;
        public IReadOnlyDictionary<TrackableId, ARPlane> DetectedPlanes => _detectedPlanes;

        private void Awake()
        {
            ValidateDependencies();
        }

        private void OnEnable()
        {
            if (_planeManager != null)
            {
                _planeManager.trackablesChanged.AddListener(OnTrackablesChanged);
            }
        }

        private void OnDisable()
        {
            if (_planeManager != null)
            {
                _planeManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
            }
        }

        private void Start()
        {
            InitializeARSession();
        }

        private void ValidateDependencies()
        {
            if (_planeManager == null)
                _planeManager = GetComponent<ARPlaneManager>();

            if (_raycastManager == null)
                _raycastManager = GetComponent<ARRaycastManager>();

            if (_anchorManager == null)
                _anchorManager = GetComponent<ARAnchorManager>();

            if (_planeManager == null)
            {
                Debug.LogError("[ARSessionManager] ARPlaneManager no encontrado. Deshabilitando script.");
                enabled = false;
                return;
            }

            if (_raycastManager == null)
            {
                Debug.LogError("[ARSessionManager] ARRaycastManager no encontrado. Deshabilitando script.");
                enabled = false;
                return;
            }

            Debug.Log("[ARSessionManager] Dependencias validadas correctamente.");
        }

        private void InitializeARSession()
        {
            try
            {
                ConfigurePlaneDetection();
                IsSessionReady = true;

                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "Sesión AR inicializada. Busca superficies horizontales.",
                    Type = MessageType.Info,
                    Duration = 3f
                });

                Debug.Log("[ARSessionManager] Sesión AR inicializada correctamente.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ARSessionManager] Error inicializando sesión AR: {ex.Message}");
                IsSessionReady = false;
            }
        }

        private void ConfigurePlaneDetection()
        {
            PlaneDetectionMode detectionMode = _detectVerticalPlanes
                ? PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical
                : PlaneDetectionMode.Horizontal;

            _planeManager.requestedDetectionMode = detectionMode;
            Debug.Log($"[ARSessionManager] Detección de planos configurada: {detectionMode}");
        }

        private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
        {
            foreach (ARPlane plane in args.added)
            {
                ProcessAddedPlane(plane);
            }

            foreach (ARPlane plane in args.updated)
            {
                ProcessUpdatedPlane(plane);
            }
            foreach (var kvp in args.removed)
            {
                ARPlane plane = kvp.Value;   // Extract the ARPlane
                ProcessRemovedPlane(plane);
            }
        }

        private void ProcessAddedPlane(ARPlane plane)
        {
            if (plane.alignment != PlaneAlignment.HorizontalUp && plane.alignment != PlaneAlignment.HorizontalDown)
            {
                return;
            }

            if (plane.size.x * plane.size.y < _minimumPlaneArea)
            {
                return;
            }

            _detectedPlanes[plane.trackableId] = plane;
            ConfigurePlaneVisualization(plane);

            EventBus.Instance.Publish(new PlaneDetectedEvent
            {
                Plane = plane,
                Center = plane.center,
                Area = plane.size.x * plane.size.y
            });

            Debug.Log($"[ARSessionManager] Plano detectado: {plane.trackableId}, Área: {plane.size.x * plane.size.y:F2}m²");
        }

        private void ProcessUpdatedPlane(ARPlane plane)
        {
            if (!_detectedPlanes.ContainsKey(plane.trackableId))
                return;

            _detectedPlanes[plane.trackableId] = plane;

            EventBus.Instance.Publish(new PlaneUpdatedEvent
            {
                Plane = plane,
                NewCenter = plane.center,
                NewArea = plane.size.x * plane.size.y
            });
        }

        private void ProcessRemovedPlane(ARPlane plane)
        {
            if (_detectedPlanes.Remove(plane.trackableId))
            {
                EventBus.Instance.Publish(new PlaneRemovedEvent
                {
                    Plane = plane
                });

                Debug.Log($"[ARSessionManager] Plano removido: {plane.trackableId}");
            }
        }

        private void ConfigurePlaneVisualization(ARPlane plane)
        {
            if (plane.TryGetComponent<MeshRenderer>(out var meshRenderer))
            {
                meshRenderer.enabled = _showPlaneVisualization;
            }

            if (_showPlaneVisualization && plane.TryGetComponent<MeshRenderer>(out var renderer))
            {
                Material mat = renderer.material;
                if (mat != null)
                {
                    Color color = mat.color;
                    color.a = 0.3f;
                    mat.color = color;
                }
            }
        }

        public bool Raycast(Vector2 screenPosition, out ARRaycastHit hit, TrackableType trackableTypes = TrackableType.PlaneWithinPolygon)
        {
            hit = default;

            if (_raycastManager == null)
                return false;

            _raycastHits.Clear();

            if (_raycastManager.Raycast(screenPosition, _raycastHits, trackableTypes))
            {
                hit = _raycastHits[0];
                return true;
            }

            return false;
        }

        public bool Raycast(Ray ray, out ARRaycastHit hit, float maxDistance = 10f)
        {
            hit = default;

            if (_raycastManager == null)
                return false;

            _raycastHits.Clear();

            if (_raycastManager.Raycast(ray, _raycastHits, TrackableType.PlaneWithinPolygon))
            {
                foreach (var raycastHit in _raycastHits)
                {
                    if (raycastHit.distance <= maxDistance)
                    {
                        hit = raycastHit;
                        return true;
                    }
                }
            }

            return false;
        }

        public ARAnchor CreateAnchor(Pose pose)
        {
            if (_anchorManager == null)
            {
                Debug.LogWarning("[ARSessionManager] ARAnchorManager no disponible.");
                return null;
            }

            try
            {
                ARPlane closestPlane = FindClosestPlane(pose.position);

                if (closestPlane == null)
                {
                    Debug.LogWarning("[ARSessionManager] No se encontró plano AR cercano para anclar.");
                    return null;
                }

                ARAnchor anchor = _anchorManager.AttachAnchor(closestPlane, pose);
                
                if (anchor != null)
                {
                    Debug.Log($"[ARSessionManager] Ancla creada: {anchor.trackableId} en plano {closestPlane.trackableId}");
                }
                else
                {
                    Debug.LogWarning("[ARSessionManager] AttachAnchor devolvió null.");
                }

                return anchor;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ARSessionManager] Error creando ancla: {ex.Message}");
                return null;
            }
        }

        private ARPlane FindClosestPlane(Vector3 position)
        {
            ARPlane closestPlane = null;
            float minDistance = float.MaxValue;

            foreach (KeyValuePair<TrackableId, ARPlane> kvp in _detectedPlanes)
            {
                ARPlane plane = kvp.Value;
                
                if (plane == null)
                    continue;

                float distance = Vector3.Distance(position, plane.center);
                
                if (distance < minDistance)
                {
                    minDistance = distance;
                    closestPlane = plane;
                }
            }

            return closestPlane;
        }

        public void RemoveAnchor(ARAnchor anchor)
        {
            if (anchor == null || _anchorManager == null)
                return;

            try
            {
                if (_anchorManager.TryRemoveAnchor(anchor))
                {
                    Debug.Log($"[ARSessionManager] Ancla removida: {anchor.trackableId}");
                }
                else
                {
                    Debug.LogWarning($"[ARSessionManager] No se pudo remover ancla: {anchor.trackableId}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ARSessionManager] Error removiendo ancla: {ex.Message}");
            }
        }

        public void TogglePlaneVisualization(bool show)
        {
            _showPlaneVisualization = show;

            foreach (KeyValuePair<TrackableId, ARPlane> kvp in _detectedPlanes)
            {
                ARPlane plane = kvp.Value;
                if (plane != null && plane.TryGetComponent<MeshRenderer>(out var renderer))
                {
                    renderer.enabled = show;
                }
            }

            Debug.Log($"[ARSessionManager] Visualización de planos: {show}");
        }

        public void ClearAllPlanes()
        {
            foreach (KeyValuePair<TrackableId, ARPlane> kvp in _detectedPlanes)
            {
                ARPlane plane = kvp.Value;
                if (plane != null)
                {
                    Destroy(plane.gameObject);
                }
            }

            _detectedPlanes.Clear();
            Debug.Log("[ARSessionManager] Todos los planos limpiados.");
        }

        public ARPlane GetLargestPlane()
        {
            ARPlane largestPlane = null;
            float maxArea = 0f;

            foreach (KeyValuePair<TrackableId, ARPlane> kvp in _detectedPlanes)
            {
                ARPlane plane = kvp.Value;
                
                if (plane == null)
                    continue;

                float area = plane.size.x * plane.size.y;
                if (area > maxArea)
                {
                    maxArea = area;
                    largestPlane = plane;
                }
            }

            return largestPlane;
        }
    }
}