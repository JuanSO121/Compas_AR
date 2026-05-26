// File: PersistenceManager.cs
// ✅ v14.4 — FIX_VIO: Serializar carga pesada hasta que VIO esté estable.
//
// ============================================================================
//  CAMBIOS v14.3 → v14.4
// ============================================================================
//
//  PROBLEMA RAÍZ (logs del dispositivo Infinix X6887):
//  ─────────────────────────────────────────────────────────────────────────
//  VioFaultDetector: Insufficient inliers (37/262, 16/228, 5/255)
//  → ARSession cae SessionTracking → SessionInitializing en loop.
//
//  Dos causas en cascada:
//
//  CAUSA 1 — RAM crítica:
//    El proceso consume ~1 GB RAM, superando el umbral de ARCore por 48 KB.
//    Esto detiene el mapping online a los ~3.4s.
//
//  CAUSA 2 — CPU starvation del VIO:
//    ARPerformanceManager registra HeavyLoad +1 [3 ops]: RecreateStairs
//    justo cuando el VIO intenta inicializarse. RecreateStairGeometryAsync
//    + LoadNavMeshFromFile + RestoreModelTransform compiten con el VIO por CPU.
//
//  SOLUCIONES v14.4:
//  ─────────────────────────────────────────────────────────────────────────
//  FIX_VIO — WaitForVIOStableBeforeHeavyWork():
//    LoadNavMeshFromFile() espera a SessionTracking estable (5 checks × 300ms)
//    antes de llamar RecreateStairGeometryAsync(). Timeout de 8s.
//    En Editor → skip inmediato.
//
//  TODOS LOS COMPORTAMIENTOS DE v14.3 SE CONSERVAN ÍNTEGRAMENTE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.XR.ARFoundation;
using IndoorNavAR.AR;
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

        [Header("─── v14.4 FIX_VIO — Espera VIO antes de RecreateStairs ──")]
        [Tooltip("Tiempo máximo (s) esperando SessionTracking estable antes de RecreateStairGeometryAsync().\n" +
                 "Si el timeout se alcanza, se continúa con advertencia.\nDefault: 8s")]
        [SerializeField] private float _vioStableTimeoutSec = 15f;

        [Tooltip("Intervalo de polling (ms) para verificar SessionTracking en WaitForVIOStable.\nDefault: 300ms")]
        [SerializeField] private int _vioPollMs = 300;

        [Tooltip("Checks consecutivos en SessionTracking requeridos para considerar el VIO estable.\nDefault: 5")]
        [SerializeField] private int _vioStableChecks = 5;

        [Tooltip("Delay adicional (ms) tras detectar VIO estable, para que consolide estado interno.\nDefault: 500ms")]
        [SerializeField] private int _vioPostStableDelayMs = 500;

        [Header("🐛 Debug")]
        [SerializeField] private bool _logOperations = true;

        private string _saveFilePath;
        private string SaveFilePath => _saveFilePath;
        private float  _timeSinceLastAutoSave;

        private bool _streamingAssetsCopied = false;
        private bool _firstFrameReady       = false;

        // ✅ v14.3 FIX: estos flags ahora se ponen en true correctamente en LoadSession()
        private bool _autoLoadAttempted = false;
        private bool _autoLoadCompleted = false;
        private bool _autoLoadResult    = false;

        private bool _alignmentCompleted = false;
        private List<WaypointSaveData> _pendingWaypointData = null;

        private bool _sessionWasRestored = false;
        private Vector3 _savedModelPosition = Vector3.zero;

        /// <summary>
        /// ✅ v14.3 FIX: Retorna true cuando LoadSession() ha terminado (con o sin éxito).
        /// </summary>
        public bool IsSessionLoadCompleted => _autoLoadAttempted && _autoLoadCompleted;
        public bool AutoLoadResult         => _autoLoadResult;
        public bool SessionWasRestored     => _sessionWasRestored;
        public bool IsFullyReady => _streamingAssetsCopied && _firstFrameReady;

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
        }

        private void Start()
        {
            Log("[v14.4] PersistenceManager listo — esperando NavigationManager.");

            if (!HasSavedSession())
            {
                _autoLoadAttempted = true;
                _autoLoadCompleted = true;
                _autoLoadResult    = false;
                Log("[v14.4] Sin sesión guardada → IsSessionLoadCompleted=true (no hay nada que cargar).");
            }
        }

        // ─── API para NavigationManager ───────────────────────────────────

        public async Task ReparentWaypointsAfterAlignment()
        {
            if (_alignmentCompleted)
            {
                Log("[v14.4] ReparentWaypointsAfterAlignment ya completado — ignorando.");
                return;
            }

            Log("[v14.4] ▶️ ReparentWaypointsAfterAlignment — modelo alineado al VIO.");

            if (_waypointManager != null && _pendingWaypointData != null
                && _pendingWaypointData.Count > 0)
            {
                await Task.Yield();
                await Task.Yield();
                await Task.Delay(100);

                Transform modelRoot = _modelLoadManager?.CurrentModel?.transform?.parent
                                   ?? _modelLoadManager?.CurrentModel?.transform;

                Log($"[v14.4] Re-anclando {_pendingWaypointData.Count} waypoints " +
                    $"bajo '{modelRoot?.name ?? "auto"}' (post-VIO)...");

                _waypointManager.ForceReparentToModel(modelRoot);
                _waypointManager.LoadWaypoints(_pendingWaypointData);

                Log($"[v14.4] ✅ Waypoints re-creados: {_waypointManager.WaypointCount}");
            }

            _alignmentCompleted  = true;
            _pendingWaypointData = null;

            ARPerformanceManager.Instance?.EndHeavyLoad("ReparentWaypointsAfterAlignment completado");
            Log("[v14.4] ✅ ARPerformanceManager.EndHeavyLoad() — VIO puede recuperar frecuencia.");

            NotifySessionLoadedToFlutter(sessionWasRestored: _sessionWasRestored);
            EventBus.Instance?.Publish(new Events.ARSessionReadyEvent());

            FlutterUnityBridge.NotifySceneReady(
                $"ReparentWaypointsAfterAlignment completado — sessionRestored={_sessionWasRestored}");

            Log($"✅ [v14.4] Flutter notificado con sessionWasRestored={_sessionWasRestored}.");
        }

        // ─── Notificación a Flutter ───────────────────────────────────────

        private void NotifySessionLoadedToFlutter(bool sessionWasRestored)
        {
            var api = VoiceCommandAPI.Instance;
            if (api == null)
            {
                Log("⚠️ VoiceCommandAPI no disponible para enviar session_loaded.");
                return;
            }

            int  wpCount = _waypointManager?.WaypointCount ?? 0;
            bool hasNM   = HasSavedNavMesh;

            string message = sessionWasRestored
                ? (wpCount > 0
                    ? $"Sesión restaurada — {wpCount} baliza(s)"
                    : "Sesión restaurada — sin balizas")
                : "Sin sesión previa guardada";

            string json = $"{{\"action\":\"session_loaded\"," +
                          $"\"ok\":true," +
                          $"\"loaded\":{(sessionWasRestored ? "true" : "false")}," +
                          $"\"waypointCount\":{wpCount}," +
                          $"\"hasNavMesh\":{(hasNM ? "true" : "false")}," +
                          $"\"message\":\"{message}\"}}";

            api.ReplyPublic(json);

            if (sessionWasRestored && wpCount > 0)
            {
                api.MarkWaypointCacheDirty();
                api.ListWaypoints();
            }

            Log($"✅ [v14.4] session_loaded enviado: {json}");
        }

        // ─── Update ───────────────────────────────────────────────────────

        private void Update()
        {
            if (!_firstFrameReady)
            {
                _firstFrameReady = true;
                Log("✅ Primer frame completo.");
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
        }

        // ─── Guardar ──────────────────────────────────────────────────────

        public async Task<bool> SaveSession()
        {
            if (_isSaving) return false;
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
                    Transform modelTf    = _modelLoadManager?.CurrentModel?.transform;
                    int       levelCount = _navMeshGenerator?.DetectedLevelCount ?? 1;
                    bool navMeshSaved = await NavMeshSerializer.Save(modelTf, levelCount: levelCount);

                    if (navMeshSaved)
                    {
                        data.hasNavMesh = true;
                        await WriteSessionJson(data);
                        PublishMessage($"Sesión guardada: {data.waypointCount} baliza(s) + NavMesh", MessageType.Success);
                    }
                    else
                    {
                        PublishMessage($"Sesión guardada: {data.waypointCount} baliza(s) (sin NavMesh)", MessageType.Warning);
                    }
                }
                else
                {
                    if (NavMeshSerializer.HasSavedNavMesh && !data.hasNavMesh)
                    {
                        data.hasNavMesh = true;
                        await WriteSessionJson(data);
                    }
                    PublishMessage($"Sesión guardada: {data.waypointCount} baliza(s)", MessageType.Success);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] ❌ Error guardando: {ex.Message}");
                PublishMessage("Error al guardar sesión", MessageType.Error);
                return false;
            }
            finally { _isSaving = false; }
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
                }
            }

            return data;
        }

        // ─── Cargar ───────────────────────────────────────────────────────

        public async Task<bool> LoadSession()
        {
            if (_isLoading)
            {
                Log("⏳ LoadSession ya en progreso — esperando...");
                return await _loadingTcs.Task;
            }

            _isLoading  = true;
            _loadingTcs = new System.Threading.Tasks.TaskCompletionSource<bool>();

            while (!_streamingAssetsCopied) await Task.Yield();
            while (!_firstFrameReady)       await Task.Yield();

            bool sessionResult = false;
            try
            {
                Log("📂 Cargando sesión...");

                if (!HasSavedSession())
                {
                    Log("⚠️ No hay sesión guardada");
                    _autoLoadAttempted = true;
                    _autoLoadCompleted = true;
                    _autoLoadResult    = false;
                    FlutterUnityBridge.NotifySceneReady("LoadSession: sin sesión guardada");
                    return false;
                }

                string json = _usePlayerPrefs
                    ? await Task.Run(() => PlayerPrefs.GetString("SessionData", ""))
                    : await Task.Run(() => File.ReadAllText(SaveFilePath));

                if (string.IsNullOrEmpty(json)) return false;

                SessionData data = JsonUtility.FromJson<SessionData>(json);
                if (data == null) return false;

                if (data.waypoints != null && data.waypointCount != data.waypoints.Count)
                    data.waypointCount = data.waypoints.Count;

                bool navMeshActuallyExists = NavMeshSerializer.HasSavedNavMesh;
                if (data.hasNavMesh != navMeshActuallyExists)
                {
                    data.hasNavMesh = navMeshActuallyExists;
                    await WriteSessionJson(data);
                }

                _alignmentCompleted = false;
                await LoadSessionData(data);

                _sessionWasRestored = true;
                sessionResult = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] ❌ Error cargando: {ex.Message}\n{ex.StackTrace}");
                _sessionWasRestored = false;
                sessionResult       = false;
                return false;
            }
            finally
            {
                _autoLoadAttempted = true;
                _autoLoadCompleted = true;
                _autoLoadResult    = sessionResult;

                _loadingTcs?.TrySetResult(sessionResult);
                _loadingTcs = null;
                _isLoading  = false;

                Log($"[v14.4] LoadSession finalizado — resultado={sessionResult} " +
                    $"IsSessionLoadCompleted={IsSessionLoadCompleted}");
            }
        }

        private async Task LoadSessionData(SessionData data)
        {
            await Task.Yield();
            await Task.Yield();
            await Task.Delay(200);

            _savedModelPosition = data.modelPosition;

            ARPerformanceManager.Instance?.BeginHeavyLoad("LoadSessionData — RestoreModel inicio");

            if (data.hasModel && _modelLoadManager != null)
            {
                Log($"📦 Restaurando modelo: {data.modelName}");

                var restoreTask = _modelLoadManager.RestoreModelTransform(
                    data.modelPosition, data.modelRotation, data.modelScale);

#if UNITY_EDITOR
                bool modelOk = await restoreTask;
                if (!modelOk) Debug.LogWarning("[PersistenceManager] ⚠️ RestoreModelTransform falló.");
#else
                var timeoutTask = Task.Delay(11000);
                var winner      = await Task.WhenAny(restoreTask, timeoutTask);
                if (winner == timeoutTask)
                    Debug.LogError("[PersistenceManager] ❌ TIMEOUT RestoreModelTransform.");
                else
                {
                    bool modelOk = await restoreTask;
                    if (!modelOk) Debug.LogWarning("[PersistenceManager] ⚠️ RestoreModelTransform falló.");
                }
#endif
                await Task.Yield();
                await Task.Yield();
                await Task.Yield();
                await Task.Delay(500);
            }

            Log("🔧 Llamando LoadNavMeshFromFile...");
            await LoadNavMeshFromFile();

            Log($"🔧 Esperando {_postNavMeshDelayMs}ms post-NavMesh...");
            await Task.Delay(_postNavMeshDelayMs);

            if (_waypointManager != null)
            {
                Transform modelRoot = _modelLoadManager?.CurrentModel?.transform?.parent
                                   ?? _modelLoadManager?.CurrentModel?.transform;
                _waypointManager.ReparentToModel(modelRoot);
            }

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

                // ✅ v14.4 FIX_DUPLICATE
                if (!_alignmentCompleted)
                {
                    _waypointManager.LoadWaypoints(validWaypoints);
                    Log($"[v14.4] {validWaypoints.Count} waypoints provisionales cargados.");
                }
                else
                {
                    Log($"[v14.4] _alignmentCompleted=true — saltando carga provisional " +
                        $"({validWaypoints.Count} waypoints ya en escena).");
                }
                _pendingWaypointData = validWaypoints;
            }
            else
            {
                _alignmentCompleted = false;
            }

            ARPerformanceManager.Instance?.EndHeavyLoad("LoadSessionData completado — esperando alineación VIO");
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

            for (int i = 0; i < 3; i++) await Task.Yield();

            ARPerformanceManager.Instance?.BeginHeavyLoad("LoadNavMeshFromFile — BuildNavMeshData");

            var (success, firstInstance, allInstances) =
                await NavMeshSerializer.LoadMulti(modelTf);

            ARPerformanceManager.Instance?.EndHeavyLoad("LoadNavMeshFromFile completado");

            if (success)
            {
                _loadedInstances       = allInstances;
                _navMeshInstanceActive = true;
                _navMeshWasBaked       = false;

                await Task.Delay(_postNavMeshDelayMs);

                // ✅ v14.4 FIX_VIO: Esperar tracking estable antes de trabajo pesado.
                // RecreateStairGeometryAsync corre en el main thread y compite con el VIO
                // por CPU, causando Insufficient inliers y resets del tracker.
                await WaitForVIOStableBeforeHeavyWork();

                await RecreateStairGeometryAsync();
            }
            else
            {
                Log("❌ Falló la restauración del NavMesh.");
            }

            return success;
        }

        /// <summary>
        /// ✅ v14.4 FIX_VIO — Espera a que ARCore esté en SessionTracking antes de
        /// ejecutar trabajo pesado en el main thread que compite con el VIO.
        ///
        /// El VIO necesita ~3s para inicializar; hacer NavMesh+Stairs durante ese
        /// tiempo provoca CPU starvation y resets del tracker (Insufficient inliers).
        ///
        /// En Editor no hay ARCore real → skip inmediato.
        /// Timeout de _vioStableTimeoutSec como fallback.
        /// </summary>
        private async Task WaitForVIOStableBeforeHeavyWork()
        {
#if UNITY_EDITOR
            Log("[FIX_VIO] Editor — skip de espera VIO.");
            return;
#endif
            float elapsed     = 0f;
            int   stableCount = 0;

            Log($"[FIX_VIO] Esperando SessionTracking estable antes de RecreateStairs " +
                $"(timeout={_vioStableTimeoutSec}s, poll={_vioPollMs}ms, checks={_vioStableChecks})...");

            while (elapsed < _vioStableTimeoutSec)
            {
                await Task.Delay(_vioPollMs);
                elapsed += _vioPollMs / 1000f;

                if (ARSession.state == ARSessionState.SessionTracking)
                {
                    stableCount++;
                    if (stableCount >= _vioStableChecks)
                    {
                        Log($"[FIX_VIO] SessionTracking estable ({stableCount} checks) en {elapsed:F1}s — " +
                            "procediendo con RecreateStairs.");
                        // Delay adicional para que el VIO consolide su estado interno
                        await Task.Delay(_vioPostStableDelayMs);
                        return;
                    }
                }
                else
                {
                    if (stableCount > 0)
                        Log($"[FIX_VIO] Tracking perdido en check {stableCount} — reset.");
                    stableCount = 0;
                }
            }

            Debug.LogWarning($"[PersistenceManager] [FIX_VIO] Timeout {_vioStableTimeoutSec}s esperando " +
                             "SessionTracking. Continuando RecreateStairs — puede causar flickers.");
        }

        private async Task RecreateStairGeometryAsync()
        {
            await Task.Yield();

            var stairs = FindObjectsByType<StairWithLandingHelper>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            foreach (var stair in stairs)
            {
                if (stair == null)
                    continue;

                try
                {
                    if (stair.HasValidGeometry())
                    {
                        Debug.Log(
                            $"[PersistenceManager] ✅ '{stair.name}' ya válida.");
                        continue;
                    }

                    stair.CreateStairSystem();

                    // opcional si hay muchas escaleras
                    await Task.Yield();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[PersistenceManager] ⚠️ Error recreando '{stair.name}': {ex}");
                }
            }

            await Task.Yield();
        }

        public void RemoveLoadedNavMesh()
        {
            if (_navMeshInstanceActive)
            {
                int removed = 0;
                foreach (var inst in _loadedInstances)
                    if (inst.valid) { NavMesh.RemoveNavMeshData(inst); removed++; }
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
                _saveFilePath = Path.Combine(Application.persistentDataPath, _saveFileName);

            return File.Exists(_saveFilePath);
        }

        public bool HasSavedNavMesh => NavMeshSerializer.HasSavedNavMesh;

        public void NotifyNavMeshBaked()
        {
            _navMeshWasBaked = true;
            Log("✅ NavMesh marcado como BAKEADO.");
        }

        public void ClearSavedData()
        {
            try
            {
                if (_usePlayerPrefs) { PlayerPrefs.DeleteKey("SessionData"); PlayerPrefs.Save(); }
                else if (File.Exists(SaveFilePath)) File.Delete(SaveFilePath);
                NavMeshSerializer.DeleteSaved();
                RemoveLoadedNavMesh();
                PublishMessage("Datos eliminados", MessageType.Info);
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
                return $"Guardado: {d.timestamp}\nBalizas: {d.waypointCount}\n" +
                       $"NavMesh: {(d.hasNavMesh ? "✓" : "no")}\n" +
                       $"SessionWasRestored: {_sessionWasRestored}\n" +
                       $"IsSessionLoadCompleted: {IsSessionLoadCompleted}\n" +
                       $"ARPerfMgr: {(ARPerformanceManager.Instance != null ? "OK" : "NULL")}\n" +
                       NavMeshSerializer.GetSavedInfo();
            }
            catch { return "Error leyendo guardado"; }
        }

        public SessionStats GetSessionStats()
        {
            var stats = new SessionStats { hasSession = HasSavedSession(), hasNavMesh = HasSavedNavMesh };
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

        // ─── StreamingAssets ─────────────────────────────────────────────

        private async Task CopyStreamingAssetsToPersistent()
        {
            string[] files = { "navigation_session.json", "navmesh_header.json", "navmesh_unified.bin" };

            foreach (string file in files)
            {
                string destPath = Path.Combine(Application.persistentDataPath, file);
                if (File.Exists(destPath)) continue;

                string srcPath = Path.Combine(Application.streamingAssetsPath, file);

#if UNITY_ANDROID && !UNITY_EDITOR
                using var req = UnityEngine.Networking.UnityWebRequest.Get(srcPath);
                await req.SendWebRequest();
                if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                    await Task.Run(() => File.WriteAllBytes(destPath, req.downloadHandler.data));
#else
                if (File.Exists(srcPath))
                    await Task.Run(() => File.Copy(srcPath, destPath));
#endif
            }
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        private void Log(string msg) { if (_logOperations) Debug.Log($"[PersistenceManager] {msg}"); }

        private void PublishMessage(string msg, MessageType type) =>
            EventBus.Instance?.Publish(new ShowMessageEvent
            { Message = msg, Type = type, Duration = type == MessageType.Error ? 5f : 3f });

        // ─── ContextMenu ──────────────────────────────────────────────────

        [ContextMenu("💾 Save Session")]        private void DbgSave()    => _ = SaveSession();
        [ContextMenu("📂 Load Session")]        private void DbgLoad()    => _ = LoadSession();
        [ContextMenu("🗑️ Clear All Data")]      private void DbgClear()   => ClearSavedData();
        [ContextMenu("ℹ️ Show Info")]            private void DbgInfo()    => Debug.Log(GetLastSaveInfo());
        [ContextMenu("✅ Ver flags")]
        private void DbgFlags() => Debug.Log(
            $"[PersistenceManager] v14.4 flags:\n" +
            $"  streaming={_streamingAssetsCopied}\n" +
            $"  firstFrame={_firstFrameReady}\n" +
            $"  autoLoadAttempted={_autoLoadAttempted}\n" +
            $"  autoLoadCompleted={_autoLoadCompleted}\n" +
            $"  autoLoadResult={_autoLoadResult}\n" +
            $"  IsSessionLoadCompleted={IsSessionLoadCompleted}\n" +
            $"  sessionWasRestored={_sessionWasRestored}\n" +
            $"  alignmentCompleted={_alignmentCompleted}\n" +
            $"  pendingWaypoints={_pendingWaypointData?.Count ?? 0}\n" +
            $"  isLoading={_isLoading}\n" +
            $"  isSaving={_isSaving}\n" +
            $"  ARPerfMgr={ARPerformanceManager.Instance != null}\n" +
            $"  Application.targetFPS={Application.targetFrameRate}\n" +
            $"  ARSession.state={ARSession.state}");
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