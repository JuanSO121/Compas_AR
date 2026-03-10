// File: ModelLoadManager.cs
// ✅ FIX #A — RestoreModelTransform(): ancla el modelo al plano AR real de la
//             sesión ACTUAL en lugar de usar la posición world-space guardada.
// ✅ FIX #B — HideNavMeshObstacles(): usa obstacle.enabled=false en lugar de
//             Destroy(obstacle) para no invalidar el NavMesh cargado desde disco.
// ✅ v8.4   — Integración con ARWorldOriginStabilizer.
// ✅ v8.5   — PlaneClassifications.Floor + check anchor.enabled (AF 6.x).
//
// ============================================================================
//  CAMBIOS v8.4 → v8.5
// ============================================================================
//
//  FIX 1 — FindClosestHorizontalPlane(): PlaneClassifications.Floor (AF 6.x)
//
//    PROBLEMA v8.4:
//      Filtraba por PlaneAlignment.HorizontalUp | HorizontalDown.
//      En interiores los techos también son HorizontalDown. Si ARCore detectaba
//      el techo antes que el suelo, ResolveARPosition() anclaba el modelo al
//      techo → modelo flotando a altura de techo.
//
//    FIX v8.5 — Estrategia en dos pasos:
//      1. Buscar planos con PlaneClassifications.Floor (ARCore AF 6.x los
//         clasifica explícitamente). Son suelos garantizados.
//      2. Fallback a PlaneAlignment.HorizontalUp si no hay ningún Floor
//         clasificado aún (los primeros segundos de la sesión).
//      Los techos (HorizontalDown) quedan excluidos en ambos pasos.
//
//  FIX 2 — CreateARAnchor() + UnloadCurrentModel(): check anchor.enabled
//
//    CAMBIO DE LIFECYCLE en AF 6.0:
//      "ARAnchor disables itself after the first failed attempt instead of
//       retrying every frame."
//      AttachAnchor() puede devolver un ARAnchor no-null pero desactivado
//      (_currentAnchor.enabled == false) si el intento falla. Operar sobre
//      un anchor desactivado produce comportamiento indefinido.
//
//    FIX v8.5:
//      - CreateARAnchor(): loguear advertencia y limpiar si enabled=false.
//      - UnloadCurrentModel(): solo llamar TryRemoveAnchor si enabled=true.
//
// ============================================================================
//  PROBLEMA #A — Posición incorrecta al restaurar sesión
// ============================================================================
//
//  CAUSA RAÍZ: ARCore reinicia su sistema de coordenadas world-space en cada
//  sesión. savedPosition era correcta en la sesión anterior pero ya no
//  corresponde al suelo físico real en la sesión actual.
//
//  SÍNTOMA: El modelo aparece flotando, hundido, o desplazado horizontalmente
//  respecto al edificio real. Todos los waypoints y el NavMesh quedan
//  desalineados con el entorno físico.
//
//  FIX: ResolveARPosition() espera planos AR (máx. _planeWaitTimeout seg.) y
//  busca el plano horizontal más cercano a savedPosition:
//    • Si encuentra plano cercano en XZ (≤ _planeSearchRadius): usa plane.center
//      como posición completa (X, Y y Z del suelo real actual).
//    • Si el plano está lejos en XZ: aplica solo corrección de Y (suelo real)
//      conservando X/Z guardados (el edificio no se movió horizontalmente).
//    • Si no hay planos tras timeout: usa savedPosition con advertencia (NoAR
//      o planos aún no detectados — el usuario verá el fallback).
//
// ============================================================================
//  PROBLEMA #B — Destroy(NavMeshObstacle) durante restauración
// ============================================================================
//
//  CAUSA RAÍZ: NavMeshObstacle en modo Carve modifica el NavMesh en tiempo
//  real. Destruirlo con Destroy() durante la restauración de sesión crea un
//  "hueco" permanente en el NavMesh que fue recargado desde disco, porque
//  el carving se aplica al NavMesh activo incluso si el NavMesh viene de un
//  archivo y no de un bake en vivo.
//
//  SÍNTOMA: Partes del NavMesh recargado aparecen con "huecos" en posiciones
//  que deberían ser navegables. El agente no puede atravesar esas zonas.
//
//  FIX: obstacle.enabled = false — desactiva el carving sin destruir el
//  componente. El NavMesh cargado desde disco queda intacto.
//
// ============================================================================
//  INTEGRACIÓN ARWorldOriginStabilizer (v8.4)
// ============================================================================
//
//  Tras posicionar el modelo (ResolveARPosition) o restaurarlo
//  (RestoreModelTransform), se llama a:
//    1. ARWorldOriginStabilizer.CaptureModelAnchor() — guarda el offset
//       modelo↔cámara en espacio local de la cámara.
//    2. ARWorldOriginStabilizer.EnableStabilization() — activa el monitoreo
//       de drift del XR Origin en LateUpdate.
//
//  Si el XR Origin se desplaza (VIO recovery, relocalizacion), el estabilizador
//  reposiciona el modelo automáticamente para mantener la alineación con el
//  entorno físico real.
//
// ============================================================================
//  TODOS LOS FIXES ORIGINALES (#1 y #2) SE CONSERVAN ÍNTEGRAMENTE.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;   // PlaneAlignment (AR Foundation 4.x)
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
        [Tooltip("Si true, destruye los Collider de los obstáculos. " +
                 "Déjalo false si usas NavMeshObstacle Carve (destruirlos crea huecos en el NavMesh).")]
        [SerializeField] private bool _destroyObstacleColliders = false;

        [Header("🔄 Restauración de sesión")]
        [Tooltip("Radio XZ (m) en el que se considera que el plano AR detectado " +
                 "corresponde al área donde estaba el modelo guardado. " +
                 "Si el plano está más lejos, solo se corrige la Y (altura del suelo).")]
        [SerializeField] private float _planeSearchRadius = 5.0f;

        [Tooltip("Tiempo máximo (s) esperando que ARCore detecte planos horizontales " +
                 "al restaurar la sesión. Si se agota, se usa la posición guardada como fallback.")]
        [SerializeField] private float _planeWaitTimeout = 8.0f;

        [Tooltip("Intervalo (s) entre checks de planos durante la espera.")]
        [SerializeField] private float _planeCheckInterval = 0.3f;

        [Header("🧲 Estabilizador AR (v8.4)")]
        [Tooltip("Frames de espera tras posicionar el modelo antes de capturar el anchor " +
                 "del estabilizador. Da tiempo a que ARCore estabilice la posición de la cámara.")]
        [SerializeField] private int _stabilizerCaptureDelayFrames = 2;

        [Tooltip("Milisegundos de espera adicional antes de capturar el anchor en " +
                 "RestoreModelTransform(). Permite que ResolveARPosition() termine.")]
        [SerializeField] private int _stabilizerRestoreDelayMs = 300;

        private GameObject _currentModel;
        private ARAnchor   _currentAnchor;
        private bool       _isModelLoaded;

        // ✅ PERF: Buffer estático reutilizable — evita alloc en HideNavMeshObstacles.
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

        /// <summary>
        /// ✅ FIX #A: FLUJO LIGERO para restaurar sesión guardada.
        ///
        /// A diferencia de LoadModel(), este método NO publica ModelLoadedEvent
        /// porque el modelo ya fue configurado en una sesión anterior.
        ///
        /// La posición savedPosition viene del world-space de la sesión ANTERIOR,
        /// que no corresponde al suelo real de la sesión actual de ARCore.
        /// ResolveARPosition() busca el plano AR real más cercano y usa su
        /// posición como ancla del modelo en el espacio físico actual.
        ///
        /// ✅ v8.4: Al finalizar, configura ARWorldOriginStabilizer para detectar
        /// y corregir futuros desplazamientos del XR Origin automáticamente.
        /// </summary>
        public async Task<bool> RestoreModelTransform(
            Vector3    savedPosition,
            Quaternion savedRotation,
            float      scale = 1f)
        {
            try
            {
                // ✅ FIX #A: Resolver posición real en AR ANTES de instanciar o mover
                Vector3 resolvedPosition = await ResolveARPosition(savedPosition);

                // Caso 1: Ya hay modelo activo → solo reposicionar al suelo real
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

                    // ✅ v8.4: Recalibrar estabilizador con la nueva posición resuelta
                    await SetupStabilizerAsync(_currentModel);

                    return true;
                }

                // Caso 2: Instancias huérfanas → destruirlas antes de instanciar
                DestroyOrphanModelInstances();

                // Caso 3: Sin modelo → instanciar en posición resuelta
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

                // Deshabilitar colliders del modelo base (no necesarios en runtime AR)
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

                // ✅ v8.4: Configurar estabilizador tras restaurar el modelo
                await SetupStabilizerAsync(_currentModel);

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModelLoadManager] ❌ RestoreModelTransform: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// ✅ FIX #A — Núcleo de la corrección de posición.
        ///
        /// Espera hasta _planeWaitTimeout segundos a que ARCore detecte al menos
        /// un plano horizontal (suelo real). Luego busca el más cercano a savedPosition.
        ///
        /// Lógica de corrección:
        ///   • Plano en radio XZ ≤ _planeSearchRadius  → usar plane.center completo
        ///     (corrección de X, Y y Z — el suelo real puede estar levemente desplazado)
        ///   • Plano más lejos en XZ                   → solo corrección de Y
        ///     (el edificio no se movió horizontalmente, solo varía el origen Y de ARCore)
        ///   • Sin planos tras timeout                  → savedPosition (fallback)
        /// </summary>
        private async Task<Vector3> ResolveARPosition(Vector3 savedPosition)
        {
            var arManager = FindFirstObjectByType<AR.ARSessionManager>();

            // NoAR o ARSessionManager no disponible → fallback inmediato
            if (arManager == null)
            {
                Debug.LogWarning("[ModelLoadManager] ⚠️ ARSessionManager no encontrado. " +
                                 "Usando posición guardada (posible modo NoAR).");
                return savedPosition;
            }

#if UNITY_EDITOR
            Debug.Log($"[ModelLoadManager] 🖥️ Editor — usando posición guardada directamente: {savedPosition}");

            // Evita warning CS1998
            await Task.Yield();

            return savedPosition;
#else

            // Esperar planos con timeout (solo en dispositivo con ARCore real)
            float elapsed = 0f;
            while (arManager.DetectedPlaneCount == 0 && elapsed < _planeWaitTimeout)
            {
                if (elapsed % 2f < _planeCheckInterval)
                    Debug.Log($"[ModelLoadManager] ⏳ Esperando planos AR... " +
                              $"({elapsed:F1}s / {_planeWaitTimeout}s)");

                await Task.Delay(Mathf.RoundToInt(_planeCheckInterval * 1000));
                elapsed += _planeCheckInterval;
            }

            if (arManager.DetectedPlaneCount == 0)
            {
                Debug.LogWarning($"[ModelLoadManager] ⚠️ Sin planos AR tras {_planeWaitTimeout}s. " +
                                 $"Fallback a posición guardada: {savedPosition}");
                return savedPosition;
            }

            // Buscar el plano horizontal más cercano
            ARPlane closestPlane = FindClosestHorizontalPlane(arManager, savedPosition);
            if (closestPlane == null)
            {
                Debug.LogWarning("[ModelLoadManager] ⚠️ Solo planos verticales detectados. " +
                                 $"Fallback a posición guardada: {savedPosition}");
                return savedPosition;
            }

            // Distancia XZ entre el plano y la posición guardada
            float xzDist = Vector2.Distance(
                new Vector2(closestPlane.center.x, closestPlane.center.z),
                new Vector2(savedPosition.x, savedPosition.z));

            Vector3 resolved;

            if (xzDist <= _planeSearchRadius)
            {
                resolved = closestPlane.center;
                Debug.Log($"[ModelLoadManager] ✅ Plano AR cercano ({xzDist:F2}m en XZ). " +
                          $"Ancla completa → {resolved:F3}");
            }
            else
            {
                resolved = new Vector3(savedPosition.x, closestPlane.center.y, savedPosition.z);
                Debug.LogWarning($"[ModelLoadManager] ⚠️ Plano a {xzDist:F2}m en XZ " +
                                 $"(radio: {_planeSearchRadius}m). Solo corrección Y: " +
                                 $"Y guardado={savedPosition.y:F3} → Y real={resolved.y:F3}");
            }

            return resolved;

#endif
        }

        /// <summary>
        /// ✅ v8.5 FIX 1: Encuentra el plano AR de suelo más cercano a una posición.
        ///
        /// ESTRATEGIA EN DOS PASOS (AF 6.x):
        ///
        /// Paso 1 — PlaneClassifications.Floor:
        ///   ARCore en AF 6.x clasifica planos con PlaneClassifications flags.
        ///   Un plano Floor es suelo garantizado por ARCore.
        ///   Excluye automáticamente techos, paredes y superficies no clasificadas.
        ///
        /// Paso 2 — Fallback a PlaneAlignment.HorizontalUp:
        ///   Si no hay planos Floor clasificados aún (los primeros segundos de
        ///   sesión antes de que ARCore complete la clasificación), usar solo
        ///   HorizontalUp (excluye HorizontalDown = techos).
        ///   En v8.4 incluíamos HorizontalDown, lo que causaba que ResolveARPosition()
        ///   pudiera anclar el modelo al techo en interiores de dos alturas.
        /// </summary>
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

                // ✅ v8.5 Paso 1: plano clasificado como Floor por ARCore
                if (plane.classifications.HasFlag(PlaneClassifications.Floor))
                {
                    if (dist < minDistFloor) { minDistFloor = dist; closestFloor = plane; }
                }

                // ✅ v8.5 Paso 2 (fallback): solo HorizontalUp (excluye techos)
                if (plane.alignment == PlaneAlignment.HorizontalUp)
                {
                    if (dist < minDistHorizUp) { minDistHorizUp = dist; closestHorizUp = plane; }
                }
            }

            // Preferir Floor clasificado; si no hay, usar HorizontalUp como fallback
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

        /// <summary>
        /// FLUJO COMPLETO — para primera colocación en AR.
        /// Sí publica ModelLoadedEvent (a diferencia de RestoreModelTransform).
        ///
        /// ✅ v8.4: Configura ARWorldOriginStabilizer al finalizar la carga.
        /// </summary>
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

                if (_useARAnchors)
                    await CreateARAnchor(position, rotation);

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

                // ✅ v8.4: Configurar estabilizador tras primera carga
                await SetupStabilizerAsync(_currentModel);

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

        #region ARWorldOriginStabilizer — Integración (v8.4)

        /// <summary>
        /// ✅ v8.4: Configura ARWorldOriginStabilizer tras posicionar el modelo.
        ///
        /// Flujo:
        ///   1. Espera _stabilizerCaptureDelayFrames frames para que ARCore
        ///      estabilice la posición de la cámara.
        ///   2. Llama CaptureModelAnchor() — guarda offset modelo↔cámara en
        ///      espacio local de la cámara (estable frente a drift del XR Origin).
        ///   3. Llama EnableStabilization() — activa el monitoreo de drift en LateUpdate.
        ///
        /// Si ARWorldOriginStabilizer no está en la escena, el método simplemente
        /// no hace nada (la estabilización es opcional).
        /// </summary>
        private async Task SetupStabilizerAsync(GameObject model)
        {
            if (model == null) return;

            var stabilizer = AR.ARWorldOriginStabilizer.Instance;
            if (stabilizer == null)
            {
                Debug.Log("[ModelLoadManager] ℹ️ ARWorldOriginStabilizer no encontrado en escena — " +
                          "estabilización de origin no activa.");
                return;
            }

            // Esperar frames para que ARCore estabilice la cámara
            for (int i = 0; i < _stabilizerCaptureDelayFrames; i++)
                await Task.Yield();

            stabilizer.SetModelRoot(model.transform);
            stabilizer.CaptureModelAnchor(model.transform);
            stabilizer.EnableStabilization();

            Debug.Log($"[ModelLoadManager] ✅ ARWorldOriginStabilizer configurado.\n" +
                      $"  ModelRoot: {model.name}\n" +
                      $"  ModelPos:  {model.transform.position:F3}");
        }

        /// <summary>
        /// ✅ v8.4: Variante con delay en ms adicional (para RestoreModelTransform
        /// donde ResolveARPosition puede tardar más en estabilizar).
        /// </summary>
        private async Task SetupStabilizerWithDelayAsync(GameObject model)
        {
            if (model == null) return;

            var stabilizer = AR.ARWorldOriginStabilizer.Instance;
            if (stabilizer == null) return;

            // Delay adicional para restauración de sesión
            if (_stabilizerRestoreDelayMs > 0)
                await Task.Delay(_stabilizerRestoreDelayMs);

            stabilizer.SetModelRoot(model.transform);
            stabilizer.CaptureModelAnchor(model.transform);
            stabilizer.EnableStabilization();

            Debug.Log($"[ModelLoadManager] ✅ ARWorldOriginStabilizer recalibrado tras restaurar.\n" +
                      $"  ModelRoot: {model.name}\n" +
                      $"  ModelPos:  {model.transform.position:F3}");
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

        /// <summary>
        /// ✅ FIX #B: Oculta visualmente NavMeshObstacles usando enabled=false
        /// en lugar de Destroy(), para no invalidar el NavMesh cargado desde disco.
        ///
        /// POR QUÉ NO Destroy():
        ///   NavMeshObstacle en modo Carve modifica el NavMesh en tiempo real.
        ///   Si el NavMesh viene de un archivo (PersistenceManager) y se destruye
        ///   un NavMeshObstacle Carve, Unity aplica el "des-carving" al NavMesh
        ///   activo, creando huecos que no estaban en el archivo guardado.
        ///   Resultado: zonas que deberían ser navegables quedan bloqueadas.
        ///
        /// obstacle.enabled = false detiene el carving sin destruir el componente.
        /// El NavMesh recargado desde disco queda intacto.
        /// </summary>
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

                // Ocultar renderer
                var rend = child.GetComponent<Renderer>();
                if (rend != null) rend.enabled = false;

                // ✅ FIX #B: Desactivar, NO destruir
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

        #region AR Anchoring

        private async Task CreateARAnchor(Vector3 position, Quaternion rotation)
        {
            try
            {
                var anchorMgr = FindFirstObjectByType<ARAnchorManager>();
                if (anchorMgr == null)
                { Debug.LogWarning("[ModelLoadManager] ⚠️ ARAnchorManager no encontrado"); return; }

                var arMgr = FindFirstObjectByType<AR.ARSessionManager>();
                if (arMgr == null || arMgr.DetectedPlaneCount == 0)
                { Debug.LogWarning("[ModelLoadManager] ⚠️ Sin planos para anclar"); return; }

                await Task.Yield();

                ARPlane closest = FindClosestHorizontalPlane(arMgr, position);
                if (closest == null) return;

                _currentAnchor = anchorMgr.AttachAnchor(closest, new Pose(position, rotation));

                // ✅ v8.5 FIX 2: En AF 6.0, ARAnchor se desactiva tras el primer
                // intento fallido en lugar de reintentar cada frame. Un anchor
                // no-null pero con enabled=false indica fallo silencioso.
                if (_currentAnchor == null)
                {
                    Debug.LogWarning("[ModelLoadManager] ⚠️ AttachAnchor devolvió null.");
                    return;
                }

                if (!_currentAnchor.enabled)
                {
                    Debug.LogWarning("[ModelLoadManager] ⚠️ ARAnchor creado pero desactivado " +
                                     "(AF 6.0: fallo silencioso en primer intento). " +
                                     "El modelo funcionará sin anchor físico AR.");
                    _currentAnchor = null; // limpiar — no operar sobre anchor muerto
                    return;
                }

                _currentModel.transform.SetParent(_currentAnchor.transform);
                Debug.Log($"[ModelLoadManager] ⚓ Ancla creada: {_currentAnchor.trackableId}");
            }
            catch (Exception ex) { Debug.LogError($"[ModelLoadManager] ❌ Ancla: {ex.Message}"); }
        }

        #endregion

        #region Model Management

        public void UnloadCurrentModel()
        {
            if (_currentModel == null) return;
            if (_autoConnectStairs) DisconnectNavigationSystems();

            // ✅ v8.4: Detener estabilizador antes de destruir el modelo
            AR.ARWorldOriginStabilizer.Instance?.DisableStabilization();

            if (_currentAnchor != null)
            {
                // ✅ v8.5 FIX 2: Solo remover si el anchor está activo (AF 6.0).
                // Un anchor desactivado ya fue marcado como fallido por AR Foundation
                // y no necesita (ni debe) ser removido explícitamente.
                if (_currentAnchor.enabled)
                    FindFirstObjectByType<ARAnchorManager>()?.TryRemoveAnchor(_currentAnchor);
                else
                    Debug.Log("[ModelLoadManager] ℹ️ Anchor estaba desactivado — TryRemoveAnchor omitido.");

                _currentAnchor = null;
            }

            Destroy(_currentModel);
            _currentModel  = null;
            _isModelLoaded = false;
            Debug.Log("[ModelLoadManager] 🗑️ Modelo descargado");
            PublishMessage("Modelo descargado", MessageType.Info);
        }

        public void UpdateModelPosition(Vector3 p)
        {
            if (_currentModel == null) return;
            _currentModel.transform.position = p;
            RefreshStairs();
            // Recalibrar estabilizador tras mover el modelo manualmente
            _ = SetupStabilizerAsync(_currentModel);
        }

        public void UpdateModelRotation(Quaternion r)
        {
            if (_currentModel == null) return;
            _currentModel.transform.rotation = r;
            RefreshStairs();
            _ = SetupStabilizerAsync(_currentModel);
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

        [ContextMenu("🔍 Test ResolveARPosition (0,0,0)")]
        private void DbgResolve()   => _ = ResolveARPosition(Vector3.zero);

        [ContextMenu("🧲 Recapturar anchor estabilizador")]
        private void DbgRecaptureStabilizer()
        {
            if (_currentModel == null) { Debug.LogWarning("[ModelLoadManager] Sin modelo activo."); return; }
            _ = SetupStabilizerAsync(_currentModel);
        }

        [ContextMenu("ℹ️ Info")]
        private void DbgInfo()
        {
            var arMgr      = FindFirstObjectByType<AR.ARSessionManager>();
            var stabilizer = AR.ARWorldOriginStabilizer.Instance;

            Debug.Log(
                $"[ModelLoadManager]\n" +
                $"  Prefab:           {(_modelPrefab ? _modelPrefab.name : "None")}\n" +
                $"  Loaded:           {_isModelLoaded}\n" +
                $"  Model pos:        {(_currentModel ? _currentModel.transform.position.ToString("F3") : "N/A")}\n" +
                $"  Anchor:           {(_currentAnchor != null ? _currentAnchor.trackableId.ToString() : "ninguno")}\n" +
                $"  AR planes:        {(arMgr != null ? arMgr.DetectedPlaneCount.ToString() : "ARSessionManager no encontrado")}\n" +
                $"  [Stabilizer]\n" +
                $"    Instance:       {(stabilizer != null ? "OK" : "no encontrada en escena")}\n" +
                $"    AnchorCaptured: {(stabilizer != null ? stabilizer.AnchorCaptured.ToString() : "N/A")}\n" +
                $"    IsStabilizing:  {(stabilizer != null ? stabilizer.IsStabilizing.ToString() : "N/A")}");
        }

        #endregion
    }
}