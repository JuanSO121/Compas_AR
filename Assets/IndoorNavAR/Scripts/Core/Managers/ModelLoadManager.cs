// File: ModelLoadManager.cs
// ✅ v2 — REFACTOR_ANCHOR: Reemplazar ARWorldOriginStabilizer con ARAnchorManager nativo.
//
// ============================================================================
//  CAMBIOS FIX_CPU → v2
// ============================================================================
//
//  PROBLEMA RAÍZ (documentación oficial AR Foundation 6.1):
//    "Without anchors, GameObjects in your scene can move uncontrollably when
//    the AR session's tracking state changes."
//    La solución correcta es parentar el contenido a un ARAnchor gestionado
//    por ARAnchorManager — el sistema lo actualiza automáticamente en cada frame.
//
//    ARWorldOriginStabilizer era un sistema casero que introducía una ventana
//    de tiempo donde el modelo ya se movió pero la corrección aún no se aplicó.
//    ARAnchorManager hace esto en C++ nativo, por frame, sin esa ventana.
//
//  CAMBIOS v2:
//  ─────────────────────────────────────────────────────────────────────────
//  1. SetupStabilizerAsync() y SetupStabilizerWithDelayAsync() → eliminados.
//     Reemplazados por AttachModelToAnchorAsync().
//
//  2. AttachModelToAnchorAsync():
//     - Crea un ARAnchor real en la posición del modelo via TryAddAnchorAsync.
//     - Parentar el modelo al anchor → ARFoundation mantiene todo estable.
//     - Las escaleras y waypoints son hijos del modelo → también se estabilizan.
//
//  3. RestoreModelTransform(), LoadModel():
//     - Llaman AttachModelToAnchorAsync() en lugar de SetupStabilizerAsync().
//
//  4. UnloadCurrentModel():
//     - Elimina el anchor via TryRemoveAnchor.
//     - Sin llamadas a ARWorldOriginStabilizer.
//
//  5. FIX_CPU: DisablePlaneDetectionAfterPlacement() se conserva íntegramente.
//
//  TODO LO DEMÁS ES IDÉNTICO A FIX_CPU.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Core.Managers
{
    public class ModelLoadManager : MonoBehaviour
    {
        [Header("📦 Modelo 3D")]
        [SerializeField] private GameObject _modelPrefab;

        [Header("⚙️ Configuración")]
        [SerializeField] private Transform _modelParent;
        [SerializeField] private float     _defaultScale = 1f;

        [Header("🎯 AR Configuration")]
        [SerializeField] private bool _useARAnchors           = true;
        [SerializeField] private bool _autoLoadOnLargestPlane = false;

        [Header("🔗 Integración de Navegación")]
        [SerializeField] private bool _autoConnectStairs = true;

        [Header("🚫 NavMesh Obstacles")]
        [Tooltip("Si true, los GameObjects con tag 'NavMeshObstacle' se ocultarán al cargar el modelo.")]
        [SerializeField] private bool _hideNavMeshObstacles = true;
        [Tooltip("Si true, destruye los Collider de los obstáculos.")]
        [SerializeField] private bool _destroyObstacleColliders = false;

        [Header("🔄 Restauración de sesión")]
        [Tooltip("Radio XZ (m) en el que se considera que el plano AR detectado " +
                 "corresponde al área donde estaba el modelo guardado.")]
        [SerializeField] private float _planeSearchRadius = 5.0f;

        [Tooltip("Tiempo máximo (s) esperando que ARCore detecte planos horizontales " +
                 "al restaurar la sesión.")]
        [SerializeField] private float _planeWaitTimeout = 8.0f;

        [Tooltip("Intervalo (s) entre checks de planos durante la espera.")]
        [SerializeField] private float _planeCheckInterval = 0.3f;

        [Header("⚓ ARAnchorManager — v2")]
        [SerializeField] private ARAnchorManager _anchorManager;

        [Tooltip("Frames de espera tras posicionar el modelo antes de crear el anchor.")]
        [SerializeField] private int _anchorCreateDelayFrames = 2;

        [Tooltip("Milisegundos de espera adicional antes de crear el anchor en RestoreModelTransform().")]
        [SerializeField] private int _anchorRestoreDelayMs = 300;

        private GameObject _currentModel;
        private ARAnchor   _currentAnchor;
        private bool       _isModelLoaded;

        private static readonly List<Transform> _transformBuffer = new(256);

        #region Properties

        public bool       IsModelLoaded    => _isModelLoaded && _currentModel != null;
        public GameObject CurrentModel     => _currentModel;
        public string     CurrentModelName => _modelPrefab != null ? _modelPrefab.name : "None";

        #endregion

        #region Lifecycle

        private void Awake()
        {
            InitializeModelParent();
            ValidateModelPrefab();

            // Auto-encontrar ARAnchorManager si no está asignado en el Inspector
            if (_anchorManager == null)
                _anchorManager = FindFirstObjectByType<ARAnchorManager>();
        }

        private void Start()
        {
            if (_autoLoadOnLargestPlane && _modelPrefab != null)
                _ = LoadModelOnLargestPlaneAsync();
        }

        #endregion

        #region Initialization

        private void InitializeModelParent()
        {
            if (_modelParent == null)
            {
                _modelParent = new GameObject("[3D_Models_Container]").transform;
                Debug.Log("[ModelLoadManager] ✅ Contenedor de modelos creado");
            }
        }

        private void ValidateModelPrefab()
        {
            if (_modelPrefab == null)
                Debug.LogWarning("[ModelLoadManager] ⚠️ No hay modelo asignado en el Inspector");
            else
                Debug.Log($"[ModelLoadManager] ✅ Modelo configurado: {_modelPrefab.name}");
        }

        #endregion

        #region Model Loading — Restauración de sesión

        public async Task<bool> RestoreModelTransform(
            Vector3    savedPosition,
            Quaternion savedRotation,
            float      scale = 1f)
        {
            try
            {
                Vector3 resolvedPosition = await ResolveARPosition(savedPosition);

                if (_currentModel != null && _currentModel.activeInHierarchy)
                {
                    _currentModel.transform.SetPositionAndRotation(resolvedPosition, savedRotation);
                    _currentModel.transform.localScale = Vector3.one * scale;
                    _isModelLoaded = true;

                    if (_hideNavMeshObstacles)
                        HideNavMeshObstacles(_currentModel);

                    Debug.Log($"[ModelLoadManager] 📍 Modelo reposicionado:\n" +
                              $"  Guardado: {savedPosition:F3}\n" +
                              $"  Resuelto: {resolvedPosition:F3}\n" +
                              $"  Delta Y:  {resolvedPosition.y - savedPosition.y:+0.000;-0.000;0}m");

                    await AttachModelToAnchorAsync(_currentModel, resolvedPosition, savedRotation, isRestore: true);
                    return true;
                }

                DestroyOrphanModelInstances();

                if (_modelPrefab == null)
                {
                    Debug.LogError("[ModelLoadManager] ❌ No hay prefab para restaurar.");
                    return false;
                }

                Debug.Log($"[ModelLoadManager] 📦 Restaurando modelo '{_modelPrefab.name}' " +
                          $"en posición resuelta: {resolvedPosition:F3}");
                await Task.Yield();

                _currentModel = Instantiate(
                    _modelPrefab, resolvedPosition, savedRotation, _modelParent);

                _currentModel.name = $"Model_{_modelPrefab.name}";
                _currentModel.transform.localScale = Vector3.one * scale;
                _currentModel.tag  = "3DModel";
                _isModelLoaded     = true;

                foreach (var col in _currentModel.GetComponentsInChildren<Collider>())
                    col.enabled = false;

                if (_hideNavMeshObstacles)
                    HideNavMeshObstacles(_currentModel);

                await Task.Yield();

                float delta = Vector3.Distance(resolvedPosition, savedPosition);
                Debug.Log($"[ModelLoadManager] ✅ Modelo restaurado.\n" +
                          $"  Pos. guardada:  {savedPosition:F3}\n" +
                          $"  Pos. resuelta:  {resolvedPosition:F3}\n" +
                          $"  Corrección:     {delta:F3}m");

                await AttachModelToAnchorAsync(_currentModel, resolvedPosition, savedRotation, isRestore: true);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModelLoadManager] ❌ RestoreModelTransform: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private async Task<Vector3> ResolveARPosition(
            Vector3 savedPosition,
            bool waitForPlanes = false)
        {
            var arManager = FindFirstObjectByType<AR.ARSessionManager>();

            if (arManager == null)
            {
                Debug.LogWarning("[ModelLoadManager] ⚠️ ARSessionManager no encontrado. " +
                                 "Usando posición guardada.");
                return savedPosition;
            }

#if UNITY_EDITOR
            Debug.Log($"[ModelLoadManager] 🖥️ Editor — usando posición guardada: {savedPosition}");
            await Task.Yield();
            return savedPosition;
#else
            if (ARSession.state == ARSessionState.SessionTracking &&
                arManager.DetectedPlaneCount > 0)
            {
                Debug.Log($"[ModelLoadManager] ✅ ARCore tracking con " +
                          $"{arManager.DetectedPlaneCount} plano(s) — corrección inmediata.");
                ARPlane earlyPlane = FindClosestHorizontalPlane(arManager, savedPosition);
                if (earlyPlane != null)
                    return ApplyPlaneCorrection(earlyPlane, savedPosition);
            }

            if (!waitForPlanes)
            {
                Debug.Log($"[ModelLoadManager] ℹ️ ARCore en {ARSession.state} o sin planos. " +
                          "Usando posición guardada (sin esperar). " +
                          "AROriginAligner corregirá cuando ARCore trackee.");
                await Task.Yield();
                return savedPosition;
            }

            if (ARSession.state != ARSessionState.SessionTracking)
            {
                Debug.LogWarning($"[ModelLoadManager] ⚠️ ARCore en {ARSession.state} " +
                                 "y waitForPlanes=true — imposible esperar sin tracking. " +
                                 "Usando posición guardada.");
                await Task.Yield();
                return savedPosition;
            }

            float elapsed = 0f;
            Debug.Log($"[ModelLoadManager] ⏳ Esperando planos (waitForPlanes=true) " +
                      $"hasta {_planeWaitTimeout}s...");

            while (arManager.DetectedPlaneCount == 0 && elapsed < _planeWaitTimeout)
            {
                await Task.Delay(Mathf.RoundToInt(_planeCheckInterval * 1000));
                elapsed += _planeCheckInterval;
            }

            if (arManager.DetectedPlaneCount == 0)
            {
                Debug.LogWarning($"[ModelLoadManager] ⚠️ Sin planos tras {_planeWaitTimeout}s. " +
                                 $"Usando posición guardada: {savedPosition}");
                return savedPosition;
            }

            ARPlane closestPlane = FindClosestHorizontalPlane(arManager, savedPosition);
            return closestPlane != null
                ? ApplyPlaneCorrection(closestPlane, savedPosition)
                : savedPosition;
#endif
        }

        private Vector3 ApplyPlaneCorrection(ARPlane plane, Vector3 savedPosition)
        {
            float xzDist = Vector2.Distance(
                new Vector2(plane.center.x, plane.center.z),
                new Vector2(savedPosition.x, savedPosition.z));

            if (xzDist <= _planeSearchRadius)
            {
                Debug.Log($"[ModelLoadManager] ✅ Plano AR cercano ({xzDist:F2}m en XZ). " +
                          $"Ancla completa → {plane.center:F3}");
                return plane.center;
            }

            Vector3 resolved = new Vector3(savedPosition.x, plane.center.y, savedPosition.z);
            Debug.LogWarning($"[ModelLoadManager] ⚠️ Plano a {xzDist:F2}m en XZ " +
                             $"(radio: {_planeSearchRadius}m). Solo corrección Y: " +
                             $"Y guardado={savedPosition.y:F3} → Y real={resolved.y:F3}");
            return resolved;
        }

        private static ARPlane FindClosestHorizontalPlane(
            AR.ARSessionManager arManager,
            Vector3             referencePos)
        {
            ARPlane closestFloor   = null;
            ARPlane closestHorizUp = null;
            float   minDistFloor   = float.MaxValue;
            float   minDistHorizUp = float.MaxValue;

            foreach (var kvp in arManager.DetectedPlanes)
            {
                var plane = kvp.Value;
                if (plane == null) continue;

                float dist = Vector3.Distance(referencePos, plane.center);

                if (plane.classifications.HasFlag(PlaneClassifications.Floor))
                {
                    if (dist < minDistFloor) { minDistFloor = dist; closestFloor = plane; }
                }

                if (plane.alignment == PlaneAlignment.HorizontalUp)
                {
                    if (dist < minDistHorizUp) { minDistHorizUp = dist; closestHorizUp = plane; }
                }
            }

            if (closestFloor != null)
            {
                Debug.Log($"[ModelLoadManager] 🏠 Usando plano Floor clasificado: " +
                          $"dist={minDistFloor:F2}m, center={closestFloor.center:F3}");
                return closestFloor;
            }

            if (closestHorizUp != null)
            {
                Debug.LogWarning($"[ModelLoadManager] ⚠️ Sin plano Floor clasificado — " +
                                 $"fallback a HorizontalUp: dist={minDistHorizUp:F2}m, " +
                                 $"center={closestHorizUp.center:F3}");
                return closestHorizUp;
            }

            return null;
        }

        private void DestroyOrphanModelInstances()
        {
            var orphans = GameObject.FindGameObjectsWithTag("3DModel");
            if (orphans.Length == 0) return;

            Debug.LogWarning($"[ModelLoadManager] ⚠️ {orphans.Length} instancia(s) huérfana(s). Destruyendo...");
            foreach (var orphan in orphans)
            {
                if (orphan != _currentModel)
                {
                    Debug.Log($"[ModelLoadManager] 🗑️ Destruyendo: {orphan.name}");
                    Destroy(orphan);
                }
            }
        }

        #endregion

        #region Model Loading — Carga completa (primera vez)

        public async Task<bool> LoadModel(Vector3 position, Quaternion rotation)
        {
            if (_modelPrefab == null)
            {
                Debug.LogError("[ModelLoadManager] ❌ No hay modelo asignado");
                PublishMessage("No hay modelo configurado", MessageType.Error);
                return false;
            }

            try
            {
                Debug.Log($"[ModelLoadManager] 📦 Cargando modelo: {_modelPrefab.name}");
                PublishMessage($"Cargando {_modelPrefab.name}...", MessageType.Info);

                UnloadCurrentModel();
                await Task.Yield();

                _currentModel = Instantiate(_modelPrefab, position, rotation, _modelParent);
                _currentModel.name = $"Model_{_modelPrefab.name}";
                _currentModel.transform.localScale = Vector3.one * _defaultScale;
                _currentModel.tag  = "3DModel";

                OptimizeModel(_currentModel);

                if (_autoConnectStairs)
                    ConnectNavigationSystems();

                _isModelLoaded = true;

                EventBus.Instance?.Publish(new ModelLoadedEvent
                {
                    ModelInstance = _currentModel,
                    ModelName     = _modelPrefab.name,
                    Position      = position
                });

                PublishMessage($"Modelo cargado: {_modelPrefab.name}", MessageType.Success);
                Debug.Log($"[ModelLoadManager] ✅ Modelo cargado en {position}");

                await AttachModelToAnchorAsync(_currentModel, position, rotation, isRestore: false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModelLoadManager] ❌ Error: {ex.Message}");
                EventBus.Instance?.Publish(new ModelLoadFailedEvent
                { ModelName = _modelPrefab?.name ?? "Unknown", ErrorMessage = ex.Message });
                PublishMessage("Error cargando modelo", MessageType.Error);
                return false;
            }
        }

        public async Task<bool> LoadModelOnLargestPlaneAsync()
        {
            if (_modelPrefab == null)
            { Debug.LogError("[ModelLoadManager] ❌ Sin prefab"); return false; }

            var arMgr = FindFirstObjectByType<AR.ARSessionManager>();
            if (arMgr == null)
            { Debug.LogError("[ModelLoadManager] ❌ ARSessionManager no encontrado"); return false; }

            int wait = 10;
            while (arMgr.DetectedPlaneCount == 0 && wait > 0)
            { Debug.Log("[ModelLoadManager] ⏳ Esperando planos..."); await Task.Delay(500); wait--; }

            ARPlane plane = arMgr.GetLargestPlane();
            if (plane == null)
            {
                Debug.LogWarning("[ModelLoadManager] ⚠️ Sin planos — cargando en origen");
                return await LoadModel(Vector3.zero, Quaternion.identity);
            }

            Debug.Log($"[ModelLoadManager] 🎯 Plano encontrado: {plane.size.x * plane.size.y:F2}m²");
            return await LoadModel(plane.center, Quaternion.identity);
        }

        #endregion

        #region ⚓ ARAnchorManager — Anclaje nativo (v2)

        /// <summary>
        /// ✅ v2 — Crea un ARAnchor real y parentar el modelo al anchor.
        ///
        /// ARAnchorManager gestiona el drift automáticamente a partir de aquí.
        /// Las escaleras y waypoints son hijos del modelo → también se estabilizan.
        ///
        /// Documentación AR Foundation 6.1:
        ///   "Parent your content to an ARAnchor managed by ARAnchorManager —
        ///   the system updates it automatically every frame."
        ///
        /// ✅ FIX_CPU: Después de anclar el modelo, llamamos
        /// ARSessionManager.DisablePlaneDetection() para liberar CPU al VIO.
        /// </summary>
        private async Task AttachModelToAnchorAsync(
            GameObject model,
            Vector3    position,
            Quaternion rotation,
            bool       isRestore = false)
        {
            if (model == null) return;

            // Delay antes de crear el anchor
            if (isRestore && _anchorRestoreDelayMs > 0)
                await Task.Delay(_anchorRestoreDelayMs);
            else
                for (int i = 0; i < _anchorCreateDelayFrames; i++)
                    await Task.Yield();

            // Limpiar anchor anterior si existe
            if (_currentAnchor != null)
            {
                if (_anchorManager != null)
                    _anchorManager.TryRemoveAnchor(_currentAnchor);
                _currentAnchor = null;
            }

            if (_anchorManager == null)
            {
                Debug.LogWarning("[ModelLoadManager] ⚠️ ARAnchorManager no encontrado — " +
                                 "modelo sin anchor. Plane detection liberada igualmente.");
                DisablePlaneDetectionAfterPlacement();
                return;
            }

            // Solo crear anchor con tracking estable
            if (ARSession.state != ARSessionState.SessionTracking)
            {
                Debug.LogWarning($"[ModelLoadManager] ⚠️ ARSession en {ARSession.state} — " +
                                 "modelo sin anchor (tracking no estable). " +
                                 "ARFoundation no puede crear anchors sin SessionTracking.");
                DisablePlaneDetectionAfterPlacement();
                return;
            }

            try
            {
                var result = await _anchorManager.TryAddAnchorAsync(new Pose(position, rotation));

                if (result.status.IsSuccess() && result.value != null && result.value.enabled)
                {
                    _currentAnchor = result.value;

                    // ✅ CLAVE: parentar el modelo al anchor.
                    // ARFoundation mueve el ARAnchor cuando el VIO corrige —
                    // todo lo de abajo (escaleras, waypoints) sigue automáticamente.
                    model.transform.SetParent(_currentAnchor.transform, worldPositionStays: true);

                    Debug.Log($"[ModelLoadManager] ⚓ Modelo anclado a ARAnchor nativo.\n" +
                              $"  AnchorId:  {_currentAnchor.trackableId}\n" +
                              $"  ModelPos:  {model.transform.position:F3}\n" +
                              $"  IsRestore: {isRestore}");
                }
                else
                {
                    Debug.LogWarning($"[ModelLoadManager] ⚠️ TryAddAnchorAsync falló: " +
                                     $"status={result.status} | ARSession={ARSession.state}. " +
                                     "Modelo sin anchor — puede haber drift.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModelLoadManager] ❌ AttachModelToAnchorAsync: {ex.Message}");
            }

            // ✅ FIX_CPU: Liberar CPU del plane subsystem ahora que el modelo está posicionado.
            DisablePlaneDetectionAfterPlacement();
        }

        /// <summary>
        /// ✅ FIX_CPU — Deshabilita plane detection liberando CPU para el VIO.
        /// </summary>
        private void DisablePlaneDetectionAfterPlacement()
        {
            var arSessionManager = FindFirstObjectByType<AR.ARSessionManager>();
            if (arSessionManager == null)
            {
                Debug.LogWarning("[ModelLoadManager] ⚠️ [FIX_CPU] ARSessionManager no encontrado " +
                                 "— plane detection no pudo deshabilitarse.");
                return;
            }
            arSessionManager.DisablePlaneDetection();
            Debug.Log("[ModelLoadManager] ✅ [FIX_CPU] Plane detection deshabilitada " +
                      "— CPU liberada para VIO tracker.");
        }

        #endregion

        #region Optimization

        private void OptimizeModel(GameObject model)
        {
            var cols  = model.GetComponentsInChildren<Collider>();
            var rends = model.GetComponentsInChildren<Renderer>();
            foreach (var c in cols)  c.enabled = false;
            foreach (var r in rends) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Debug.Log($"[ModelLoadManager] 🔧 {cols.Length} colliders, {rends.Length} renderers optimizados");

            if (_hideNavMeshObstacles)
                HideNavMeshObstacles(model);
        }

        private void HideNavMeshObstacles(GameObject model)
        {
            if (model == null) return;

            _transformBuffer.Clear();
            model.GetComponentsInChildren<Transform>(includeInactive: true, _transformBuffer);

            int hidden = 0;

            foreach (Transform child in _transformBuffer)
            {
                bool isObstacle = child.CompareTag("NavMeshObstacle")
                               || child.GetComponent<NavMeshObstacle>() != null;

                if (!isObstacle) continue;

                var rend = child.GetComponent<Renderer>();
                if (rend != null) rend.enabled = false;

                var obstacle = child.GetComponent<NavMeshObstacle>();
                if (obstacle != null)
                {
                    obstacle.enabled = false;
                    Debug.Log($"[ModelLoadManager]   🚫 '{child.name}' — NavMeshObstacle desactivado " +
                              "(carving detenido, NavMesh intacto).");
                }

                if (_destroyObstacleColliders)
                {
                    foreach (var col in child.GetComponents<Collider>())
                        Destroy(col);
                }

                hidden++;
            }

            if (hidden > 0)
                Debug.Log($"[ModelLoadManager] 🚫 {hidden} NavMeshObstacle(s) procesados.");
        }

        #endregion

        #region Navigation Integration

        private void ConnectNavigationSystems()
        {
            if (_currentModel == null) return;
            int connected = 0;
            foreach (var sh in FindObjectsByType<Navigation.StairWithLandingHelper>(FindObjectsSortMode.None))
            {
                try { sh.ConnectToModel(_currentModel.transform); connected++; }
                catch (Exception ex) { Debug.LogError($"[ModelLoadManager] ❌ Escalera: {ex.Message}"); }
            }
            if (connected > 0)
                Debug.Log($"[ModelLoadManager] ✅ {connected} escalera(s) conectadas");
        }

        private void DisconnectNavigationSystems()
        {
            foreach (var sh in FindObjectsByType<Navigation.StairWithLandingHelper>(FindObjectsSortMode.None))
            {
                try { sh.Clear(); }
                catch (Exception ex) { Debug.LogError($"[ModelLoadManager] ❌ Clear escalera: {ex.Message}"); }
            }
        }

        #endregion

        #region Model Management

        public void UnloadCurrentModel()
        {
            if (_currentModel == null) return;
            if (_autoConnectStairs) DisconnectNavigationSystems();

            // ✅ v2: Eliminar anchor via ARAnchorManager nativo
            if (_currentAnchor != null)
            {
                if (_anchorManager != null && _currentAnchor.enabled)
                    _anchorManager.TryRemoveAnchor(_currentAnchor);
                else if (_currentAnchor != null && !_currentAnchor.enabled)
                    Debug.Log("[ModelLoadManager] ℹ️ Anchor estaba desactivado — TryRemoveAnchor omitido.");

                _currentAnchor = null;
            }

            Destroy(_currentModel);
            _currentModel  = null;
            _isModelLoaded = false;
            Debug.Log("[ModelLoadManager] 🗑️ Modelo descargado");
            PublishMessage("Modelo descargado", MessageType.Info);

            // Re-habilitar plane detection cuando el modelo se descarga
            var arSessionManager = FindFirstObjectByType<AR.ARSessionManager>();
            arSessionManager?.EnablePlaneDetection();
        }

        public void UpdateModelPosition(Vector3 p)
        {
            if (_currentModel == null) return;
            _currentModel.transform.position = p;
            RefreshStairs();
            _ = AttachModelToAnchorAsync(_currentModel, p, _currentModel.transform.rotation);
        }

        public void UpdateModelRotation(Quaternion r)
        {
            if (_currentModel == null) return;
            _currentModel.transform.rotation = r;
            RefreshStairs();
            _ = AttachModelToAnchorAsync(_currentModel, _currentModel.transform.position, r);
        }

        public void UpdateModelScale(float s)
        { if (_currentModel != null) { _currentModel.transform.localScale = Vector3.one * s; RefreshStairs(); } }

        private void RefreshStairs()
        {
            if (!_autoConnectStairs) return;
            foreach (var sh in FindObjectsByType<Navigation.StairWithLandingHelper>(FindObjectsSortMode.None))
            {
                try { sh.CreateStairSystem(); }
                catch (Exception ex) { Debug.LogError($"[ModelLoadManager] ❌ Refresh escalera: {ex.Message}"); }
            }
        }

        #endregion

        #region Utilities

        private void PublishMessage(string msg, MessageType type) =>
            EventBus.Instance?.Publish(new ShowMessageEvent
            { Message = msg, Type = type, Duration = type == MessageType.Error ? 5f : 3f });

        #endregion

        #region Debug

        [ContextMenu("🔨 Load on Largest Plane")]
        private void DbgLoad()      => _ = LoadModelOnLargestPlaneAsync();

        [ContextMenu("🗑️ Unload")]
        private void DbgUnload()    => UnloadCurrentModel();

        [ContextMenu("🔗 Reconnect Stairs")]
        private void DbgStairs()    => ConnectNavigationSystems();

        [ContextMenu("🚫 Hide Obstacles Now")]
        private void DbgObstacles() => HideNavMeshObstacles(_currentModel);

        [ContextMenu("⚓ Re-anclar modelo ahora")]
        private void DbgReanchor()
        {
            if (_currentModel == null) { Debug.LogWarning("[ModelLoadManager] Sin modelo activo."); return; }
            _ = AttachModelToAnchorAsync(
                _currentModel,
                _currentModel.transform.position,
                _currentModel.transform.rotation);
        }

        [ContextMenu("ℹ️ Info")]
        private void DbgInfo()
        {
            var arMgr = FindFirstObjectByType<AR.ARSessionManager>();

            Debug.Log(
                $"[ModelLoadManager] v2\n" +
                $"  Prefab:           {(_modelPrefab ? _modelPrefab.name : "None")}\n" +
                $"  Loaded:           {_isModelLoaded}\n" +
                $"  Model pos:        {(_currentModel ? _currentModel.transform.position.ToString("F3") : "N/A")}\n" +
                $"  ARAnchor:         {(_currentAnchor != null ? _currentAnchor.trackableId.ToString() : "ninguno")}\n" +
                $"  ARAnchorManager:  {(_anchorManager != null ? "OK" : "no encontrado")}\n" +
                $"  AR planes:        {(arMgr != null ? arMgr.DetectedPlaneCount.ToString() : "N/A")}\n" +
                $"  AR state:         {ARSession.state}");
        }

        #endregion
    }
}