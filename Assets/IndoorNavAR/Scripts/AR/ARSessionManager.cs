// File: ARSessionManager.cs
// ✅ v2.0 — AR Foundation 6.x: PlaneClassifications.Floor + anchor lifecycle fix.
// ✅ v2.1 — Eliminar [RequireComponent] — los managers AR deben vivir en
//           XR Origin (Mobile AR), no en el GameObject de este script.
//
// ============================================================================
//  CAMBIOS v2.0 → v2.1
// ============================================================================
//
//  BUG DE ESCENA — Componentes AR duplicados:
//
//    PROBLEMA:
//      [RequireComponent(typeof(ARPlaneManager))]
//      [RequireComponent(typeof(ARRaycastManager))]
//      forzaba a Unity a añadir esos componentes al GameObject donde vive
//      ARSessionManager ([Core Managers]/ARSessionManager), que es distinto
//      de XR Origin (Mobile AR) donde AR Foundation ya los tiene.
//
//      Resultado: dos ARPlaneManager y dos ARRaycastManager activos en escena.
//        - Planos duplicados en _detectedPlanes
//        - Raycasts con resultados inconsistentes
//        - Warnings "multiple managers of the same type" en consola
//
//    FIX v2.1:
//      - Eliminados [RequireComponent].
//      - ValidateDependencies() busca los managers en toda la escena con
//        FindFirstObjectByType<T>() si los campos del Inspector están vacíos.
//      - Los campos del Inspector deben apuntar a los componentes de
//        XR Origin (Mobile AR) — arrastrar desde el Inspector de Unity.
//
//  SETUP CORRECTO EN ESCENA:
//    XR Origin (Mobile AR)
//      ├── AR Plane Manager     ← aquí viven los managers
//      ├── AR Raycast Manager
//      └── AR Anchor Manager
//
//    [Core Managers]
//      └── ARSessionManager (Script)   ← solo el script, sin managers propios
//            Plane Manager  → drag XR Origin (Mobile AR) [AR Plane Manager]
//            Raycast Manager→ drag XR Origin (Mobile AR) [AR Raycast Manager]
//            Anchor Manager → drag XR Origin (Mobile AR) [AR Anchor Manager]
//
// ============================================================================
//  CAMBIOS v1.0 → v2.0 (conservados íntegramente)
// ============================================================================
//
//  FIX 1 — ProcessAddedPlane(): PlaneClassifications.Floor (AF 6.x)
//  FIX 2 — FindClosestPlane(): mismo filtro de Floor para CreateAnchor()
//  FIX 3 — trackablesChanged: ya usa la API correcta de AF 6.x

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.AR
{
    // ✅ v2.1: [RequireComponent] eliminados.
    // Los managers AR deben estar en XR Origin (Mobile AR), no aquí.
    public class ARSessionManager : MonoBehaviour
    {
        [Header("Referencias AR")]
        [SerializeField] private ARPlaneManager   _planeManager;
        [SerializeField] private ARRaycastManager _raycastManager;
        [SerializeField] private ARAnchorManager  _anchorManager;

        [Header("Configuración")]
        [SerializeField] private bool  _detectVerticalPlanes    = false;
        [SerializeField] private bool  _showPlaneVisualization  = true;
        [SerializeField] private float _minimumPlaneArea        = 0.5f;

        private readonly Dictionary<TrackableId, ARPlane> _detectedPlanes
            = new Dictionary<TrackableId, ARPlane>();
        private readonly List<ARRaycastHit> _raycastHits = new List<ARRaycastHit>();

        public bool IsSessionReady    { get; private set; }
        public int  DetectedPlaneCount => _detectedPlanes.Count;
        public IReadOnlyDictionary<TrackableId, ARPlane> DetectedPlanes => _detectedPlanes;

        // ─── Lifecycle ────────────────────────────────────────────────────

        private void Awake()    => ValidateDependencies();
        private void Start()    => InitializeARSession();

        private void OnEnable()
        {
            if (_planeManager != null)
                _planeManager.trackablesChanged.AddListener(OnTrackablesChanged);
        }

        private void OnDisable()
        {
            if (_planeManager != null)
                _planeManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
        }

        // ─── Inicialización ───────────────────────────────────────────────

        private void ValidateDependencies()
        {
            // ✅ v2.1: Ya NO usar GetComponent<T>() — esos componentes viven en
            // XR Origin (Mobile AR), no en este GameObject.
            // Buscar en escena como fallback si el Inspector no tiene referencias.
            if (_planeManager == null)
                _planeManager = FindFirstObjectByType<ARPlaneManager>();

            if (_raycastManager == null)
                _raycastManager = FindFirstObjectByType<ARRaycastManager>();

            if (_anchorManager == null)
                _anchorManager = FindFirstObjectByType<ARAnchorManager>();

            if (_planeManager == null)
            {
                Debug.LogError("[ARSessionManager] ARPlaneManager no encontrado. " +
                               "Asegúrate de que XR Origin (Mobile AR) tiene AR Plane Manager " +
                               "y arrastra la referencia al Inspector de ARSessionManager.");
                enabled = false; return;
            }
            if (_raycastManager == null)
            {
                Debug.LogError("[ARSessionManager] ARRaycastManager no encontrado. " +
                               "Asegúrate de que XR Origin (Mobile AR) tiene AR Raycast Manager " +
                               "y arrastra la referencia al Inspector de ARSessionManager.");
                enabled = false; return;
            }

            Debug.Log("[ARSessionManager] ✅ v2.1 Dependencias validadas. " +
                      $"PlaneManager en '{_planeManager.gameObject.name}' | " +
                      $"RaycastManager en '{_raycastManager.gameObject.name}'");
        }

        private void InitializeARSession()
        {
            try
            {
                ConfigurePlaneDetection();
                IsSessionReady = true;

                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message  = "Sesión AR inicializada. Busca superficies horizontales.",
                    Type     = MessageType.Info,
                    Duration = 3f
                });

                Debug.Log("[ARSessionManager] Sesión AR inicializada correctamente.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ARSessionManager] Error inicializando: {ex.Message}");
                IsSessionReady = false;
            }
        }

        private void ConfigurePlaneDetection()
        {
            PlaneDetectionMode mode = _detectVerticalPlanes
                ? PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical
                : PlaneDetectionMode.Horizontal;

            _planeManager.requestedDetectionMode = mode;
            Debug.Log($"[ARSessionManager] Detección de planos: {mode}");
        }

        // ─── Plane tracking ───────────────────────────────────────────────

        private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
        {
            foreach (var plane in args.added)   ProcessAddedPlane(plane);
            foreach (var plane in args.updated) ProcessUpdatedPlane(plane);
            foreach (var kvp   in args.removed) ProcessRemovedPlane(kvp.Value);
        }

        /// <summary>
        /// ✅ v2.0 FIX 1: Filtrar planos usando PlaneClassifications.Floor (AF 6.x).
        ///
        /// LÓGICA:
        ///   Caso A — ARCore clasificó el plano como Floor: aceptar siempre.
        ///            Son suelos confirmados por el sistema de clasificación de ARCore.
        ///
        ///   Caso B — ARCore no ha clasificado el plano aún (None) y es HorizontalUp:
        ///            Aceptar provisionalmente (los primeros frames los planos no tienen
        ///            clasificación). Serán promovidos/degradados cuando ARCore clasifique.
        ///
        ///   Caso C — HorizontalDown (techos) o clasificado como Ceiling/Table/etc:
        ///            Rechazar. En v1.0 se aceptaban HorizontalDown como suelos,
        ///            lo que causaba que el modelo se anclara al techo.
        ///
        ///   Caso D — Planos verticales (paredes): rechazar siempre.
        /// </summary>
        private void ProcessAddedPlane(ARPlane plane)
        {
            // ✅ v2.0 FIX 1: Clasificación AF 6.x — preferir Floor explícito
            bool isFloor      = plane.classifications.HasFlag(PlaneClassifications.Floor);
            bool isUnclassifiedHorizontalUp =
                plane.classifications == PlaneClassifications.None &&
                plane.alignment       == PlaneAlignment.HorizontalUp;

            if (!isFloor && !isUnclassifiedHorizontalUp)
            {
                // Loguear para diagnóstico — útil para detectar si el techo
                // se estaba colando en v1.0
                if (plane.alignment == PlaneAlignment.HorizontalDown ||
                    plane.classifications.HasFlag(PlaneClassifications.Ceiling))
                {
                    Debug.Log($"[ARSessionManager] 🚫 Plano techo ignorado: " +
                              $"alignment={plane.alignment} class={plane.classifications}");
                }
                return;
            }

            if (plane.size.x * plane.size.y < _minimumPlaneArea)
                return;

            _detectedPlanes[plane.trackableId] = plane;
            ConfigurePlaneVisualization(plane);

            EventBus.Instance.Publish(new PlaneDetectedEvent
            {
                Plane  = plane,
                Center = plane.center,
                Area   = plane.size.x * plane.size.y
            });

            string classLabel = isFloor ? "Floor✓" : "HorizUp(sin clasificar)";
            Debug.Log($"[ARSessionManager] ✅ Plano [{classLabel}] detectado: " +
                      $"{plane.trackableId} | Área: {plane.size.x * plane.size.y:F2}m²");
        }

        private void ProcessUpdatedPlane(ARPlane plane)
        {
            if (!_detectedPlanes.ContainsKey(plane.trackableId)) return;

            _detectedPlanes[plane.trackableId] = plane;

            EventBus.Instance.Publish(new PlaneUpdatedEvent
            {
                Plane     = plane,
                NewCenter = plane.center,
                NewArea   = plane.size.x * plane.size.y
            });
        }

        private void ProcessRemovedPlane(ARPlane plane)
        {
            if (_detectedPlanes.Remove(plane.trackableId))
            {
                EventBus.Instance.Publish(new PlaneRemovedEvent { Plane = plane });
                Debug.Log($"[ARSessionManager] Plano removido: {plane.trackableId}");
            }
        }

        private void ConfigurePlaneVisualization(ARPlane plane)
        {
            if (plane.TryGetComponent<MeshRenderer>(out var meshRenderer))
                meshRenderer.enabled = _showPlaneVisualization;

            if (_showPlaneVisualization && plane.TryGetComponent<MeshRenderer>(out var renderer))
            {
                Material mat = renderer.material;
                if (mat != null)
                {
                    Color c = mat.color;
                    c.a       = 0.3f;
                    mat.color = c;
                }
            }
        }

        // ─── Raycast ──────────────────────────────────────────────────────

        public bool Raycast(
            Vector2      screenPosition,
            out ARRaycastHit hit,
            TrackableType trackableTypes = TrackableType.PlaneWithinPolygon)
        {
            hit = default;
            if (_raycastManager == null) return false;

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
            if (_raycastManager == null) return false;

            _raycastHits.Clear();
            if (_raycastManager.Raycast(ray, _raycastHits, TrackableType.PlaneWithinPolygon))
            {
                foreach (var h in _raycastHits)
                {
                    if (h.distance <= maxDistance) { hit = h; return true; }
                }
            }
            return false;
        }

        // ─── Anchors ──────────────────────────────────────────────────────

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
                    Debug.LogWarning("[ARSessionManager] No se encontró plano suelo cercano.");
                    return null;
                }

                ARAnchor anchor = _anchorManager.AttachAnchor(closestPlane, pose);

                if (anchor == null)
                {
                    Debug.LogWarning("[ARSessionManager] AttachAnchor devolvió null.");
                    return null;
                }

                // ✅ v2.0 FIX 2: AF 6.0 — ARAnchor se desactiva tras primer fallo.
                if (!anchor.enabled)
                {
                    Debug.LogWarning("[ARSessionManager] ⚠️ ARAnchor creado pero desactivado " +
                                     "(AF 6.0: fallo silencioso). Devolviendo null.");
                    return null;
                }

                Debug.Log($"[ARSessionManager] ⚓ Ancla creada: {anchor.trackableId} " +
                          $"en plano {closestPlane.trackableId}");
                return anchor;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ARSessionManager] Error creando ancla: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ✅ v2.0 FIX 2: Busca el plano suelo más cercano.
        /// _detectedPlanes ya solo contiene suelos gracias a ProcessAddedPlane v2.0,
        /// pero aplicamos el mismo filtro de Floor como defensa en profundidad.
        /// </summary>
        private ARPlane FindClosestPlane(Vector3 position)
        {
            ARPlane closestFloor   = null;
            ARPlane closestFallback = null;
            float   minDistFloor   = float.MaxValue;
            float   minDistFallback = float.MaxValue;

            foreach (var kvp in _detectedPlanes)
            {
                var plane = kvp.Value;
                if (plane == null) continue;

                float dist = Vector3.Distance(position, plane.center);

                if (plane.classifications.HasFlag(PlaneClassifications.Floor))
                {
                    if (dist < minDistFloor) { minDistFloor = dist; closestFloor = plane; }
                }
                else if (plane.alignment == PlaneAlignment.HorizontalUp)
                {
                    if (dist < minDistFallback) { minDistFallback = dist; closestFallback = plane; }
                }
            }

            return closestFloor ?? closestFallback;
        }

        public void RemoveAnchor(ARAnchor anchor)
        {
            if (anchor == null || _anchorManager == null) return;
            try
            {
                if (_anchorManager.TryRemoveAnchor(anchor))
                    Debug.Log($"[ARSessionManager] Ancla removida: {anchor.trackableId}");
                else
                    Debug.LogWarning($"[ARSessionManager] No se pudo remover: {anchor.trackableId}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ARSessionManager] Error removiendo ancla: {ex.Message}");
            }
        }

        // ─── Utilities ────────────────────────────────────────────────────

        public void TogglePlaneVisualization(bool show)
        {
            _showPlaneVisualization = show;
            foreach (var kvp in _detectedPlanes)
            {
                if (kvp.Value != null &&
                    kvp.Value.TryGetComponent<MeshRenderer>(out var r))
                    r.enabled = show;
            }
            Debug.Log($"[ARSessionManager] Visualización de planos: {show}");
        }

        public void ClearAllPlanes()
        {
            foreach (var kvp in _detectedPlanes)
                if (kvp.Value != null) Destroy(kvp.Value.gameObject);

            _detectedPlanes.Clear();
            Debug.Log("[ARSessionManager] Todos los planos limpiados.");
        }

        public ARPlane GetLargestPlane()
        {
            ARPlane largestPlane = null;
            float   maxArea      = 0f;

            foreach (var kvp in _detectedPlanes)
            {
                var plane = kvp.Value;
                if (plane == null) continue;
                float area = plane.size.x * plane.size.y;
                if (area > maxArea) { maxArea = area; largestPlane = plane; }
            }

            return largestPlane;
        }
    }
}