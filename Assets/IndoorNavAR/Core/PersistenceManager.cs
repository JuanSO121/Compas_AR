// File: PersistenceManager.cs
// ✅ v14 — REFACTOR_ANCHOR: Eliminar ARWorldOriginStabilizer.
//          Esperar ARSession.state directamente en lugar de FlutterUnityBridge.IsSceneReady.
//          ARAnchorManager nativo gestiona la estabilidad del modelo desde ModelLoadManager.
//
// ════════════════════════════════════════════════════════════════════════════
// CAMBIOS v13+FIX_v11 → v14
// ════════════════════════════════════════════════════════════════════════════
//
//  PROBLEMA RAÍZ (confirmado con documentación oficial AR Foundation 6.1):
//  ─────────────────────────────────────────────────────────────────────────
//  ARWorldOriginStabilizer era un sistema casero que detectaba cuándo XROrigin
//  se movía y reposicionaba el modelo manualmente. Eso es exactamente lo que
//  ARAnchorManager + ARAnchor ya hacen nativamente en C++ nativo, por frame,
//  con mucha mayor precisión. El sistema casero introducía una ventana de tiempo
//  donde el modelo ya se había movido pero la corrección aún no se aplicaba.
//
//  Adicionalmente, esperar FlutterUnityBridge.IsSceneReady para iniciar la
//  carga AR era incorrecto: si Flutter tarda, la restauración puede ocurrir
//  mientras el VIO todavía hace sus correcciones grandes de startup.
//  El estado correcto de espera es ARSession.state == SessionTracking.
//
//  CAMBIOS v14:
//  ─────────────────────────────────────────────────────────────────────────
//  1. Start(): reemplazar espera FlutterUnityBridge.IsSceneReady →
//     esperar ARSession.state == SessionTracking (con timeout).
//  2. LoadSession(): eliminar BeginSessionRestore() / EndSessionRestore().
//  3. ReparentWaypointsAfterAlignment(): eliminar todo lo de ARWorldOriginStabilizer.
//  4. Start() else branch: eliminar ARWorldOriginStabilizer.Instance?.EndSessionRestore().
//  5. LoadSessionData() comentarios de v11 actualizados.
//
//  TODO LO DEMÁS ES IDÉNTICO A v13+FIX_v11.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.XR.ARFoundation;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Navigation;
using IndoorNavAR.Integration;

namespace IndoorNavAR.Core
{
    public class PersistenceManager : MonoBehaviour
    {
        [Header("⚙️ Configuración")]
        [SerializeField] private string _saveFileName   = "navigation_session.json";
        [SerializeField] private bool   _usePlayerPrefs = false;

        [Header("💾 Auto-Guardado")]
        [SerializeField] private bool  _autoSaveEnabled  = false;
        [SerializeField] private float _autoSaveInterval = 60f;

        [Header("📦 Referencias")]
        [SerializeField] private WaypointManager            _waypointManager;
        [SerializeField] private ModelLoadManager           _modelLoadManager;
        [SerializeField] private MultiLevelNavMeshGenerator _navMeshGenerator;

        [Header("─── FIX_CPU — Timing de carga ────────────────────────────")]
        [SerializeField] private int _stairRecreateDelayFrames  = 10;
        [SerializeField] private int _stairRecreateInterFrameMs = 0;
        [SerializeField] private int _postNavMeshDelayMs        = 500;
        [SerializeField] private int _postStairsDelayMs         = 300;

        [Header("─── v14 — Espera de tracking ────────────────────────────")]
        [Tooltip("Tiempo máximo (s) esperando ARSession.SessionTracking antes de intentar cargar sesión.")]
        [SerializeField] private float _trackingWaitTimeout = 15f;

        [Header("🐛 Debug")]
        [SerializeField] private bool _logOperations = true;

        private string _saveFilePath;
        private string SaveFilePath => _saveFilePath;
        private float  _timeSinceLastAutoSave;

        private bool _streamingAssetsCopied = false;
        private bool _firstFrameReady       = false;

        private bool _autoLoadAttempted = false;
        private bool _autoLoadCompleted = false;
        private bool _autoLoadResult    = false;

        private bool _alignmentCompleted = false;
        private List<WaypointSaveData> _pendingWaypointData = null;

        private Vector3 _savedModelPosition = Vector3.zero;

        public bool IsSessionLoadCompleted => _autoLoadAttempted && _autoLoadCompleted;
        public bool AutoLoadResult         => _autoLoadResult;
        public bool IsFullyReady           => _streamingAssetsCopied && _firstFrameReady;

        private bool _isLoading = false;
        private bool _isSaving  = false;

        private System.Threading.Tasks.TaskCompletionSource<bool> _loadingTcs = null;

        private List<NavMeshDataInstance> _loadedInstances      = new List<NavMeshDataInstance>();
        private bool                      _navMeshInstanceActive = false;
        private bool                      _navMeshWasBaked       = false;

        // ─── Lifecycle ────────────────────────────────────────────────────

        private async void Awake()
        {
            FindDependencies();
            await CopyStreamingAssetsToPersistent();
            _streamingAssetsCopied = true;
            Log("✅ StreamingAssets copiados — SaveSession/LoadSession desbloqueados.");
        }

        private async void Start()
        {
            while (!_streamingAssetsCopied) await Task.Yield();
            while (!_firstFrameReady)       await Task.Yield();

            _autoLoadAttempted = true;

            // ✅ v14: Esperar tracking estable directamente.
            // La carga AR no depende de Flutter — depende del VIO de ARCore.
            // Flutter puede no estar listo aún, pero eso está bien.
            Log($"⏳ [v14] Esperando ARSession.SessionTracking (timeout={_trackingWaitTimeout}s)...");

            float waited = 0f;
            while (ARSession.state != ARSessionState.SessionTracking && waited < _trackingWaitTimeout)
            {
                await Task.Delay(100);
                waited += 0.1f;
            }

            Log($"[v14] AR state al cargar: {ARSession.state} (esperado {waited:F1}s)");

            if (HasSavedSession())
            {
                Log("🚀 [v14] Cargando sesión...");
                _autoLoadResult = await LoadSession();
                Log($"🚀 [v14] Carga completada: éxito={_autoLoadResult} " +
                    $"waypoints={_waypointManager?.WaypointCount ?? 0}");
            }
            else
            {
                Log("ℹ️ [v14] No hay sesión guardada.");
                _autoLoadResult     = false;
                _alignmentCompleted = true;
            }

            _autoLoadCompleted = true;

            if (!_autoLoadResult)
            {
                Log("✅ [v14] Sin sesión — Flutter notificado desde Start().");
                NotifySessionLoadedToFlutter();
                EventBus.Instance?.Publish(new Events.ARSessionReadyEvent());
            }
            else
            {
                Log("⏳ [v14] Sesión cargada — esperando ReparentWaypointsAfterAlignment() " +
                    "de NavigationManager para notificar Flutter...");
            }
        }

        // ─── ✅ API para NavigationManager ────────────────────────────────

        /// <summary>
        /// ✅ v14 — Re-crear waypoints post-VIO en local space nativo.
        ///
        /// El modelo ya está parentado a un ARAnchor real (gestionado por ARAnchorManager).
        /// ARFoundation mantiene la estabilidad automáticamente — no necesitamos
        /// ARWorldOriginStabilizer ni recapturar ningún anchor manualmente.
        ///
        /// FLUJO v14:
        ///   1. LoadSession() → RestoreModelTransform() → modelo parentado a ARAnchor.
        ///   2. ARFoundation gestiona drift automáticamente desde ese momento.
        ///   3. NavigationManager llama ReparentWaypointsAfterAlignment() (aquí):
        ///      - WaypointManager v8 instancia waypoints en local space.
        ///   4. Resultado: modelo, escaleras y waypoints estables. Sin drift.
        /// </summary>
        public async Task ReparentWaypointsAfterAlignment()
        {
            if (_alignmentCompleted)
            {
                Log("[v14] ReparentWaypointsAfterAlignment ya completado — ignorando.");
                return;
            }

            Log("[v14] ▶️ ReparentWaypointsAfterAlignment — modelo alineado al VIO.");

            if (_waypointManager != null && _pendingWaypointData != null
                && _pendingWaypointData.Count > 0)
            {
                await Task.Yield();
                await Task.Yield();
                await Task.Delay(100);

                Transform modelRoot = _modelLoadManager?.CurrentModel?.transform?.parent
                                   ?? _modelLoadManager?.CurrentModel?.transform;

                if (modelRoot != null)
                {
                    float modelDelta = Vector3.Distance(modelRoot.position, _savedModelPosition);
                    Log($"[v14] Modelo: guardado={_savedModelPosition:F3} | " +
                        $"actual={modelRoot.position:F3} | delta={modelDelta:F3}m");
                }

                Log($"[v14] Re-anclando y re-creando {_pendingWaypointData.Count} waypoints " +
                    $"bajo '{modelRoot?.name ?? "auto"}' (post-VIO)...");

                _waypointManager.ForceReparentToModel(modelRoot);
                _waypointManager.LoadWaypoints(_pendingWaypointData);

                Log($"[v14] ✅ Waypoints re-creados: {_waypointManager.WaypointCount}");
            }
            else
            {
                Log("[v14] Sin waypoints pendientes para re-crear.");
            }

            _alignmentCompleted  = true;
            _pendingWaypointData = null;

            // ✅ v14: Sin ARWorldOriginStabilizer — ARAnchorManager gestiona drift nativo.
            Log("[v14] ✅ ReparentWaypointsAfterAlignment completado. " +
                "ARAnchorManager gestiona estabilidad automáticamente.");

            NotifySessionLoadedToFlutter();
            EventBus.Instance?.Publish(new Events.ARSessionReadyEvent());

            Log("✅ [v14] Flutter notificado.");
        }

        // ─── Notificación a Flutter ───────────────────────────────────────

        private void NotifySessionLoadedToFlutter()
        {
            var api = VoiceCommandAPI.Instance;
            if (api == null)
            {
                Log("⚠️ [v14] VoiceCommandAPI no disponible para enviar session_loaded.");
                return;
            }

            int  wpCount = _waypointManager?.WaypointCount ?? 0;
            bool hasNM   = HasSavedNavMesh;

            string message = _autoLoadResult
                ? (wpCount > 0
                    ? $"Sesión restaurada — {wpCount} baliza(s)"
                    : "Sesión restaurada — sin balizas")
                : "Sin sesión previa guardada";

            string json = $"{{\"action\":\"session_loaded\"," +
                          $"\"ok\":true," +
                          $"\"loaded\":{(_autoLoadResult ? "true" : "false")}," +
                          $"\"waypointCount\":{wpCount}," +
                          $"\"hasNavMesh\":{(hasNM ? "true" : "false")}," +
                          $"\"message\":\"{message}\"}}";

            api.ReplyPublic(json);

            if (_autoLoadResult && wpCount > 0)
            {
                api.MarkWaypointCacheDirty();
                api.ListWaypoints();
            }

            Log($"✅ [v14] session_loaded enviado a Flutter: {json}");
        }

        // ─── Update ───────────────────────────────────────────────────────

        private void Update()
        {
            if (!_firstFrameReady)
            {
                _firstFrameReady = true;
                Log("✅ Primer frame completo — instanciación segura habilitada.");
            }

            if (!_streamingAssetsCopied) return;
            if (!_autoSaveEnabled) return;

            _timeSinceLastAutoSave += Time.deltaTime;
            if (_timeSinceLastAutoSave >= _autoSaveInterval)
            {
                _ = SaveSession();
                _timeSinceLastAutoSave = 0f;
            }
        }

        private void OnDestroy() => RemoveLoadedNavMesh();

        // ─── Inicialización ───────────────────────────────────────────────

        private void FindDependencies()
        {
            _saveFilePath     = Path.Combine(Application.persistentDataPath, _saveFileName);
            _waypointManager  ??= FindFirstObjectByType<WaypointManager>();
            _modelLoadManager ??= FindFirstObjectByType<ModelLoadManager>();
            _navMeshGenerator ??= FindFirstObjectByType<MultiLevelNavMeshGenerator>();

            if (_waypointManager  == null) Debug.LogWarning("[PersistenceManager] ⚠️ WaypointManager no encontrado");
            if (_modelLoadManager == null) Debug.LogWarning("[PersistenceManager] ⚠️ ModelLoadManager no encontrado");
            if (_navMeshGenerator == null) Debug.LogWarning("[PersistenceManager] ⚠️ MultiLevelNavMeshGenerator no encontrado");

            Log($"📂 Ruta: {SaveFilePath}");
        }

        // ─── API pública ──────────────────────────────────────────────────

        public void NotifyNavMeshBaked()
        {
            _navMeshWasBaked = true;
            Log("✅ NavMesh marcado como BAKEADO.");
        }

        // ─── Guardar ──────────────────────────────────────────────────────

        public async Task<bool> SaveSession()
        {
            if (_isSaving)
            {
                Log("⚠️ SaveSession ya en progreso, ignorando llamada duplicada.");
                return false;
            }
            _isSaving = true;

            while (!_streamingAssetsCopied) await Task.Yield();
            while (!_firstFrameReady)       await Task.Yield();

            try
            {
                Log("💾 Guardando sesión...");

                SessionData data = CreateSessionData(navMeshConfirmed: false);
                await WriteSessionJson(data);

                if (_navMeshWasBaked)
                {
                    Log("🔥 NavMesh bakeado → guardando archivos .bin por nivel...");
                    Transform modelTf    = _modelLoadManager?.CurrentModel?.transform;
                    int       levelCount = _navMeshGenerator?.DetectedLevelCount ?? 1;

                    bool navMeshSaved = await NavMeshSerializer.Save(modelTf, levelCount: levelCount);

                    if (navMeshSaved)
                    {
                        data.hasNavMesh = true;
                        await WriteSessionJson(data);
                        LogNavMeshSaveVerification(levelCount);
                        string msg = $"Sesión guardada: {data.waypointCount} baliza(s) + NavMesh ({levelCount} nivel(es))";
                        PublishMessage(msg, MessageType.Success);
                        Log($"✅ {msg}");
                    }
                    else
                    {
                        Debug.LogWarning("[PersistenceManager] ⚠️ NavMesh no guardado — ¿fue generado?");
                        string msg = $"Sesión guardada: {data.waypointCount} baliza(s) (sin NavMesh)";
                        PublishMessage(msg, MessageType.Warning);
                        Log($"⚠️ {msg}");
                    }
                }
                else
                {
                    if (NavMeshSerializer.HasSavedNavMesh && !data.hasNavMesh)
                    {
                        data.hasNavMesh = true;
                        await WriteSessionJson(data);
                        Log("✅ session.json actualizado: hasNavMesh: true");
                    }

                    string msg = $"Sesión guardada: {data.waypointCount} baliza(s)";
                    PublishMessage(msg, MessageType.Success);
                    Log($"✅ {msg}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] ❌ Error guardando: {ex.Message}");
                PublishMessage("Error al guardar sesión", MessageType.Error);
                return false;
            }
            finally
            {
                _isSaving = false;
            }
        }

        private async Task WriteSessionJson(SessionData data)
        {
            string json = JsonUtility.ToJson(data, true);
            if (_usePlayerPrefs)
                await Task.Run(() => { PlayerPrefs.SetString("SessionData", json); PlayerPrefs.Save(); });
            else
                await Task.Run(() => File.WriteAllText(SaveFilePath, json));
        }

        private SessionData CreateSessionData(bool navMeshConfirmed = false)
        {
            var data = new SessionData
            {
                version       = "2.0",
                timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                waypointCount = 0,
                waypoints     = new List<WaypointSaveData>(),
                hasNavMesh    = navMeshConfirmed
            };

            if (_waypointManager != null)
            {
                data.waypoints     = _waypointManager.SerializeWaypoints();
                data.waypointCount = data.waypoints.Count;

                int localCount = data.waypoints.Count(w => w.hasLocalSpace);
                Log($"[v14] Serializando {data.waypointCount} waypoints " +
                    $"({localCount} con localSpace, {data.waypointCount - localCount} legacy).");
            }

            if (_modelLoadManager != null && _modelLoadManager.IsModelLoaded)
            {
                var model = _modelLoadManager.CurrentModel;
                if (model != null)
                {
                    data.hasModel      = true;
                    data.modelName     = _modelLoadManager.CurrentModelName;
                    data.modelPosition = model.transform.position;
                    data.modelRotation = model.transform.rotation;
                    data.modelScale    = model.transform.localScale.x;

                    Log($"[v14] Modelo guardado: pos={data.modelPosition:F3}");
                }
            }

            return data;
        }

        // ─── Cargar ───────────────────────────────────────────────────────

        public async Task<bool> LoadSession()
        {
            if (_isLoading)
            {
                Log("⏳ LoadSession ya en progreso — esperando resultado...");
                return await _loadingTcs.Task;
            }

            _isLoading  = true;
            _loadingTcs = new System.Threading.Tasks.TaskCompletionSource<bool>();

            while (!_streamingAssetsCopied) await Task.Yield();
            while (!_firstFrameReady)       await Task.Yield();

            // ✅ v14: Sin BeginSessionRestore — ARAnchorManager gestiona la estabilidad.

            bool sessionResult = false;
            try
            {
                Log("📂 Cargando sesión...");

                if (!HasSavedSession()) { Log("⚠️ No hay sesión guardada"); return false; }

                string json = _usePlayerPrefs
                    ? await Task.Run(() => PlayerPrefs.GetString("SessionData", ""))
                    : await Task.Run(() => File.ReadAllText(SaveFilePath));

                if (string.IsNullOrEmpty(json)) { Debug.LogWarning("[PersistenceManager] Archivo vacío"); return false; }

                SessionData data = JsonUtility.FromJson<SessionData>(json);
                if (data == null) { Debug.LogError("[PersistenceManager] Error deserializando"); return false; }

                if (data.waypoints != null && data.waypointCount != data.waypoints.Count)
                {
                    Debug.LogWarning($"[PersistenceManager] ⚠️ TRUNCAMIENTO: " +
                                     $"waypointCount={data.waypointCount} vs waypoints.Count={data.waypoints.Count}.");
                    data.waypointCount = data.waypoints.Count;
                }

                bool navMeshActuallyExists = NavMeshSerializer.HasSavedNavMesh;
                if (data.hasNavMesh != navMeshActuallyExists)
                {
                    Debug.LogWarning($"[PersistenceManager] ⚠️ Discrepancia hasNavMesh: " +
                                     $"session={data.hasNavMesh} vs disco={navMeshActuallyExists}. Usando disco.");
                    data.hasNavMesh = navMeshActuallyExists;
                    await WriteSessionJson(data);
                }

                await LoadSessionData(data);

                string resultMsg = $"Sesión cargada: {data.waypointCount} baliza(s)" +
                                   (_navMeshInstanceActive ? " + NavMesh ✓" : " (sin NavMesh)");
                PublishMessage(resultMsg, MessageType.Success);
                Log($"✅ {resultMsg}");
                sessionResult = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] ❌ Error cargando: {ex.Message}\n{ex.StackTrace}");
                PublishMessage("Error al cargar sesión", MessageType.Error);
                sessionResult = false;
                return false;
            }
            finally
            {
                _loadingTcs?.TrySetResult(sessionResult);
                _loadingTcs = null;
                _isLoading  = false;
            }
        }

        private async Task LoadSessionData(SessionData data)
        {
            await Task.Yield();
            await Task.Yield();
            await Task.Delay(200);

            // ── 0. Guardar posición del modelo para diagnóstico ───────────
            _savedModelPosition = data.modelPosition;
            Log($"[v14] Posición guardada del modelo: {_savedModelPosition:F3}");

            // ── 1. Restaurar modelo ───────────────────────────────────────
            if (data.hasModel && _modelLoadManager != null)
            {
                Log($"📦 Restaurando modelo: {data.modelName}");

                var restoreTask = _modelLoadManager.RestoreModelTransform(
                    data.modelPosition, data.modelRotation, data.modelScale);

#if UNITY_EDITOR
                bool modelOk = await restoreTask;
                if (!modelOk)
                    Debug.LogWarning("[PersistenceManager] ⚠️ RestoreModelTransform retornó false");
                else
                    Log("✅ Modelo restaurado y anclado a ARAnchor.");
#else
                var timeoutTask = Task.Delay(11000);
                var winner      = await Task.WhenAny(restoreTask, timeoutTask);

                if (winner == timeoutTask)
                    Debug.LogError("[PersistenceManager] ❌ TIMEOUT RestoreModelTransform.");
                else
                {
                    bool modelOk = await restoreTask;
                    if (!modelOk)
                        Debug.LogWarning("[PersistenceManager] ⚠️ RestoreModelTransform retornó false");
                    else
                        Log("✅ Modelo restaurado y anclado a ARAnchor.");
                }
#endif

                await Task.Yield();
                await Task.Yield();
                await Task.Yield();
                await Task.Delay(500);
            }

            // ── 2. Cargar NavMesh ─────────────────────────────────────────
            Log("🔧 Llamando LoadNavMeshFromFile...");
            await LoadNavMeshFromFile();

            Log($"🔧 [FIX_CPU_B] Esperando {_postNavMeshDelayMs}ms post-NavMesh...");
            await Task.Delay(_postNavMeshDelayMs);
            Log("🔧 LoadNavMeshFromFile + espera completados.");

            // ── 3. Anclar [Waypoints] bajo el modelo ──────────────────────
            if (_waypointManager != null)
            {
                Transform modelRoot = _modelLoadManager?.CurrentModel?.transform?.parent
                                   ?? _modelLoadManager?.CurrentModel?.transform;

                Log($"📦 ReparentToModel provisional → target='{modelRoot?.name ?? "auto"}'");
                _waypointManager.ReparentToModel(modelRoot);
            }

            // ── 4. Cargar waypoints provisionales ─────────────────────────
            if (_waypointManager != null && data.waypoints != null && data.waypoints.Count > 0)
            {
                var validWaypoints = data.waypoints
                    .Where(w => w != null
                                && !string.IsNullOrEmpty(w.id)
                                && !string.IsNullOrEmpty(w.name)
                                && !float.IsNaN(w.position.x)
                                && !float.IsNaN(w.position.y)
                                && !float.IsNaN(w.position.z))
                    .ToList();

                if (validWaypoints.Count != data.waypoints.Count)
                    Debug.LogWarning($"[PersistenceManager] ⚠️ Filtrados " +
                                     $"{data.waypoints.Count - validWaypoints.Count} waypoints inválidos.");

                int localSpaceCount = validWaypoints.Count(w => w.hasLocalSpace);
                Log($"📍 Cargando {validWaypoints.Count} waypoints válidos (provisional). " +
                    $"[v14] {localSpaceCount} con localSpace, " +
                    $"{validWaypoints.Count - localSpaceCount} legacy.");

                _waypointManager.LoadWaypoints(validWaypoints);
                Log($"✅ Waypoints provisionales: {_waypointManager.WaypointCount}");

                _pendingWaypointData = validWaypoints;
                Log($"[v14] {validWaypoints.Count} waypoints pendientes para re-crear post-VIO.");
            }
            else
            {
                Log($"ℹ️ Sin waypoints que cargar (count={data.waypoints?.Count ?? 0})");
                _alignmentCompleted = false;
            }
        }

        // ─── NavMesh ──────────────────────────────────────────────────────

        public async Task<bool> LoadNavMeshFromFile()
        {
            while (!_streamingAssetsCopied) await Task.Yield();
            while (!_firstFrameReady)       await Task.Yield();

            if (!NavMeshSerializer.HasSavedNavMesh)
            {
                Log("⚠️ No hay NavMesh guardado en disco.");
                return false;
            }

            RemoveLoadedNavMesh();

            Transform modelTf = _modelLoadManager?.CurrentModel?.transform;

            var (success, firstInstance, allInstances) =
                await NavMeshSerializer.LoadMulti(modelTf);

            if (success)
            {
                _loadedInstances       = allInstances;
                _navMeshInstanceActive = true;
                _navMeshWasBaked       = false;
                Log($"📐 NavMesh restaurado: {allInstances.Count} instancia(s).");

                await RecreateStairGeometryAsync();
            }
            else
            {
                Log("❌ Falló la restauración del NavMesh.");
            }

            return success;
        }

        private async Task RecreateStairGeometryAsync()
        {
            var stairHelpers = FindObjectsByType<StairWithLandingHelper>(FindObjectsSortMode.None);

            if (stairHelpers.Length == 0)
            {
                Log("ℹ️ No hay StairWithLandingHelper en escena.");
                NavigationStartPointManager.ConfirmModelPositioned();
                Log("📍 Posición del modelo confirmada a todos los StartPoints.");
                NavigationStartPointManager.NotifyNavMeshReadyAfterSessionRestore();
                return;
            }

            Log($"🪜 [FIX_CPU_A] Recreando geometría de {stairHelpers.Length} escalera(s) " +
                $"— distribuido en frames (delay inicial: {_stairRecreateDelayFrames} frames)...");

            for (int i = 0; i < _stairRecreateDelayFrames; i++)
                await Task.Yield();

            int recreated = 0, failed = 0;

            foreach (var helper in stairHelpers)
            {
                if (helper == null) continue;
                try
                {
                    helper.CreateStairSystem();
                    recreated++;
                    Log($"  ✅ Escalera '{helper.name}' recreada.");

                    await Task.Yield();
                    await Task.Yield();

                    if (_stairRecreateInterFrameMs > 0)
                        await Task.Delay(_stairRecreateInterFrameMs);
                }
                catch (Exception ex)
                {
                    failed++;
                    Debug.LogWarning($"[PersistenceManager] ⚠️ Error escalera '{helper.name}': {ex.Message}");
                }
            }

            Log($"🪜 Escaleras: {recreated} recreadas, {failed} con error.");

            if (recreated > 0)
            {
                Log($"🪜 [FIX_CPU_A] Esperando {_postStairsDelayMs}ms para que colliders se asienten...");
                await Task.Delay(_postStairsDelayMs);
                Log("🪜 Colliders de escalera listos.");
            }

            NavigationStartPointManager.ConfirmModelPositioned();
            Log("📍 Posición del modelo confirmada a todos los StartPoints.");
            NavigationStartPointManager.NotifyNavMeshReadyAfterSessionRestore();
        }

        public void RemoveLoadedNavMesh()
        {
            if (_navMeshInstanceActive)
            {
                int removed = 0;
                foreach (var inst in _loadedInstances)
                {
                    if (inst.valid) { NavMesh.RemoveNavMeshData(inst); removed++; }
                }
                _loadedInstances.Clear();
                _navMeshInstanceActive = false;
                _navMeshWasBaked       = false;
                Log($"🗑️ {removed} instancia(s) NavMesh eliminadas.");
            }
        }

        // ─── Utilidades ───────────────────────────────────────────────────

        public bool HasSavedSession()
        {
            if (_usePlayerPrefs) return PlayerPrefs.HasKey("SessionData");

            if (string.IsNullOrEmpty(_saveFilePath))
            {
                _saveFilePath = Path.Combine(Application.persistentDataPath, _saveFileName);
                Debug.LogWarning($"[PersistenceManager] _saveFilePath reconstruido: {_saveFilePath}");
            }

            bool exists = File.Exists(_saveFilePath);
            Debug.Log($"[PersistenceManager] HasSavedSession → {_saveFilePath} | existe: {exists}");
            return exists;
        }

        public bool HasSavedNavMesh => NavMeshSerializer.HasSavedNavMesh;

        public void ClearSavedData()
        {
            try
            {
                if (_usePlayerPrefs) { PlayerPrefs.DeleteKey("SessionData"); PlayerPrefs.Save(); }
                else if (File.Exists(SaveFilePath)) File.Delete(SaveFilePath);

                NavMeshSerializer.DeleteSaved();
                RemoveLoadedNavMesh();
                PublishMessage("Datos eliminados", MessageType.Info);
                Log("🗑️ Todos los datos eliminados.");
            }
            catch (Exception ex) { Debug.LogError($"[PersistenceManager] ❌ Error limpiando: {ex.Message}"); }
        }

        public string GetLastSaveInfo()
        {
            if (!HasSavedSession()) return "Sin guardado";
            try
            {
                string json = _usePlayerPrefs
                    ? PlayerPrefs.GetString("SessionData", "")
                    : File.ReadAllText(SaveFilePath);
                var d = JsonUtility.FromJson<SessionData>(json);
                int localCount = d.waypoints?.Count(w => w.hasLocalSpace) ?? 0;
                return $"Guardado: {d.timestamp}\nBalizas: {d.waypointCount} ({localCount} con localSpace)\n" +
                       $"Modelo: {(d.hasModel ? d.modelName : "Ninguno")}\n" +
                       $"NavMesh: {(d.hasNavMesh ? "✓" : "no")}\n" +
                       $"AutoLoad completado: {_autoLoadCompleted} | resultado: {_autoLoadResult}\n" +
                       $"AlignmentCompleted: {_alignmentCompleted}\n" +
                       $"isLoading: {_isLoading} | isSaving: {_isSaving}\n" +
                       NavMeshSerializer.GetSavedInfo();
            }
            catch { return "Error leyendo guardado"; }
        }

        public SessionStats GetSessionStats()
        {
            var stats = new SessionStats
            {
                hasSession    = HasSavedSession(),
                waypointCount = 0,
                hasModel      = false,
                modelName     = "None",
                timestamp     = "N/A",
                hasNavMesh    = HasSavedNavMesh
            };
            if (!stats.hasSession) return stats;
            try
            {
                string json = _usePlayerPrefs
                    ? PlayerPrefs.GetString("SessionData", "")
                    : File.ReadAllText(SaveFilePath);
                var d = JsonUtility.FromJson<SessionData>(json);
                if (d != null)
                {
                    stats.waypointCount = d.waypointCount;
                    stats.hasModel      = d.hasModel;
                    stats.modelName     = d.modelName ?? "None";
                    stats.timestamp     = d.timestamp;
                    stats.hasNavMesh    = d.hasNavMesh;
                }
            }
            catch (Exception ex) { Debug.LogError($"[PersistenceManager] Error stats: {ex.Message}"); }
            return stats;
        }

        // ─── Diagnóstico ──────────────────────────────────────────────────

        private void LogNavMeshSaveVerification(int expectedLevelCount)
        {
            string info = NavMeshSerializer.GetSavedInfo();
            Log($"📐 Verificación NavMesh guardado:\n{info}");

            if (expectedLevelCount > 1)
            {
                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                if (tri.vertices.Length == 0)
                { Debug.LogWarning("[PersistenceManager] ⚠️ CalculateTriangulation vacía."); return; }

                float minY = float.MaxValue, maxY = float.MinValue;
                foreach (var v in tri.vertices)
                {
                    if (v.y < minY) minY = v.y;
                    if (v.y > maxY) maxY = v.y;
                }

                float yRange = maxY - minY;
                if (yRange < 1.0f)
                    Debug.LogWarning($"[PersistenceManager] ⚠️ NavMesh puede ser incompleto: " +
                                     $"rango Y={yRange:F2}m para {expectedLevelCount} nivel(es).");
                else
                    Log($"✅ NavMesh verificado: Y=[{minY:F2},{maxY:F2}] rango={yRange:F2}m.");
            }
        }

        private async Task CopyStreamingAssetsToPersistent()
        {
            string[] files = { "navigation_session.json", "navmesh_header.json", "navmesh_unified.bin" };

            foreach (string file in files)
            {
                string destPath = Path.Combine(Application.persistentDataPath, file);
                if (File.Exists(destPath)) { Log($"📦 Ya existe, omitiendo: {file}"); continue; }

                string srcPath = Path.Combine(Application.streamingAssetsPath, file);

#if UNITY_ANDROID && !UNITY_EDITOR
                using var req = UnityEngine.Networking.UnityWebRequest.Get(srcPath);
                await req.SendWebRequest();
                if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    await Task.Run(() => File.WriteAllBytes(destPath, req.downloadHandler.data));
                    Log($"📦 Copiado desde StreamingAssets: {file}");
                }
                else
                    Debug.LogWarning($"[PersistenceManager] ⚠️ No se pudo copiar {file}: {req.error}");
#else
                if (File.Exists(srcPath))
                {
                    await Task.Run(() => File.Copy(srcPath, destPath));
                    Log($"📦 Copiado desde StreamingAssets: {file}");
                }
                else
                    Debug.LogWarning($"[PersistenceManager] ⚠️ No encontrado en StreamingAssets: {file}");
#endif
            }
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        private void Log(string msg) { if (_logOperations) Debug.Log($"[PersistenceManager] {msg}"); }

        private void PublishMessage(string msg, MessageType type) =>
            EventBus.Instance?.Publish(new ShowMessageEvent
            { Message = msg, Type = type, Duration = type == MessageType.Error ? 5f : 3f });

        // ─── ContextMenu ──────────────────────────────────────────────────

        [ContextMenu("💾 Save Session")]        private void DbgSave()      => _ = SaveSession();
        [ContextMenu("📂 Load Session")]        private void DbgLoad()      => _ = LoadSession();
        [ContextMenu("🗺️ Load NavMesh Only")]   private void DbgNavMesh()   => _ = LoadNavMeshFromFile();
        [ContextMenu("🗑️ Clear All Data")]      private void DbgClear()     => ClearSavedData();
        [ContextMenu("ℹ️ Show Info")]            private void DbgInfo()      => Debug.Log(GetLastSaveInfo());
        [ContextMenu("📐 NavMesh Info")]         private void DbgNavInfo()   => Debug.Log(NavMeshSerializer.GetSavedInfo());
        [ContextMenu("📊 Instance Count")]      private void DbgInstances() => Debug.Log($"[PersistenceManager] Instancias: {_loadedInstances.Count}");
        [ContextMenu("🪜 Recrear Escaleras")]   private void DbgRecreateStairs() => _ = RecreateStairGeometryAsync();
        [ContextMenu("✅ Ver flags")]
        private void DbgFlags() => Debug.Log(
            $"[PersistenceManager] v14 flags:\n" +
            $"  streaming={_streamingAssetsCopied}\n" +
            $"  firstFrame={_firstFrameReady}\n" +
            $"  autoLoadAttempted={_autoLoadAttempted}\n" +
            $"  autoLoadCompleted={_autoLoadCompleted}\n" +
            $"  autoLoadResult={_autoLoadResult}\n" +
            $"  alignmentCompleted={_alignmentCompleted}\n" +
            $"  pendingWaypoints={_pendingWaypointData?.Count ?? 0}\n" +
            $"  isLoading={_isLoading}\n" +
            $"  isSaving={_isSaving}\n" +
            $"  savedModelPosition={_savedModelPosition:F3}\n" +
            $"  ARSession.state={ARSession.state}\n" +
            $"  [FIX_CPU_A] stairDelayFrames={_stairRecreateDelayFrames}\n" +
            $"  [FIX_CPU_A] postStairsDelayMs={_postStairsDelayMs}\n" +
            $"  [FIX_CPU_B] postNavMeshDelayMs={_postNavMeshDelayMs}");
        [ContextMenu("🔧 Reparar hasNavMesh")]  private void DbgRepairSessionJson() { if (!HasSavedSession()) { Log("No hay session.json"); return; } _ = RepairSessionJson(); }
        [ContextMenu("🔥 Force Baked Flag")]    private void DbgBakedFlag() { NotifyNavMeshBaked(); Log("🔥 _navMeshWasBaked forzado a true"); }

        private async Task RepairSessionJson()
        {
            try
            {
                string json = _usePlayerPrefs
                    ? PlayerPrefs.GetString("SessionData", "")
                    : await Task.Run(() => File.ReadAllText(SaveFilePath));
                var data = JsonUtility.FromJson<SessionData>(json);
                if (data == null) { Log("❌ No se pudo leer el session.json"); return; }
                bool realState = NavMeshSerializer.HasSavedNavMesh;
                data.hasNavMesh = realState;
                await WriteSessionJson(data);
                Log($"✅ session.json reparado: hasNavMesh = {realState}");
            }
            catch (Exception ex) { Debug.LogError($"[PersistenceManager] Error reparando: {ex.Message}"); }
        }
    }

    // ─── Data Structures ──────────────────────────────────────────────────

    [Serializable]
    public class SessionData
    {
        public string version;
        public string timestamp;
        public int    waypointCount;
        public List<WaypointSaveData> waypoints;
        public bool       hasModel;
        public string     modelName;
        public Vector3    modelPosition;
        public Quaternion modelRotation;
        public float      modelScale;
        public bool       hasNavMesh;
    }

    public struct SessionStats
    {
        public bool   hasSession;
        public int    waypointCount;
        public bool   hasModel;
        public string modelName;
        public string timestamp;
        public bool   hasNavMesh;
    }
}