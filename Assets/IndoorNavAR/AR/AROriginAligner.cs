// File: AROriginAligner.cs
// ✅ v6 — FullAR: agente ACTIVO e INVISIBLE, sincronizado con cámara XR.
//         El agente NO se mueve por su cuenta en FullAR.
//
// ============================================================================
//  PROBLEMA CORREGIDO (v5 → v6)
// ============================================================================
//
//  En v5 el agente se DESACTIVABA en FullAR con DisableAgentForFullAR().
//  Esto causaba una cadena de fallos:
//
//    1. NavigationStartPoint.TeleportAgentWhenReady() es una corrutina en
//       NavigationStartPoint (MonoBehaviour). Al desactivar el agente, la
//       corrutina NUNCA se ejecuta → el agente nunca llega al StartPoint.
//
//    2. NavigationPathController necesita el NavMeshAgent activo para
//       calcular la ruta. Con el agente desactivado, NavigateTo() falla
//       silenciosamente → CurrentPath queda null.
//
//    3. NavigationVoiceGuide.WaitForPath() hace timeout porque CurrentPath
//       es null → _isGuiding queda false → sin instrucciones de voz.
//
//  SOLUCIÓN v6:
//    • El agente permanece ACTIVO en FullAR para que todos los MonoBehaviour
//      (NavigationAgent, NavMeshAgent, corrutinas) sigan ejecutándose.
//    • Se hace INVISIBLE desactivando sus Renderers (el usuario no lo ve).
//    • Cada frame, AROriginAligner mueve el agente al NavMesh más cercano
//      a la cámara XR mediante SyncAgentToCameraFullAR().
//    • El NavMeshAgent propio del agente se mantiene DETENIDO en FullAR
//      (isStopped = true) para que el agente NO camine solo hacia el destino.
//      La lógica de movimiento autónomo es solo para NoAR.
//    • NavigationPathController puede calcular rutas desde la posición real
//      del usuario porque el agente está en esa posición.
//    • NavigationVoiceGuide.EvalPos usa AgentPosition (= posición real del
//      usuario en FullAR) para los triggers de instrucciones de ruta.
//
// ============================================================================
//  ARQUITECTURA POR MODO (actualizada)
// ============================================================================
//
//  ── MODO FullAR ──────────────────────────────────────────────────────────
//    ARCore trackea la posición física del usuario (cámara XR).
//    El NavigationAgent está ACTIVO pero INVISIBLE.
//    Su NavMeshAgent está DETENIDO (isStopped=true) → no camina solo.
//    AROriginAligner.SyncAgentToCameraFullAR() mueve el agente al NavMesh
//    más cercano a la cámara cada frame.
//    NavigationPathController calcula la ruta desde esa posición.
//    NavigationVoiceGuide genera instrucciones de voz basadas en la posición
//    real del usuario (agente = usuario en FullAR).
//    ARGuideController detecta FullAR y desactiva su lógica de movimiento.
//
//  ── MODO NoAR ────────────────────────────────────────────────────────────
//    Sin ARCore. El agente ES el usuario virtual, está ACTIVO y VISIBLE.
//    La cámara sigue al agente (FollowAgent).
//    ARGuideController gestiona el movimiento del agente hacia el destino.
//    Las instrucciones de voz se basan en la posición del agente.

using System.Collections;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.XR.ARFoundation;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Navigation;
using Unity.XR.CoreUtils;

namespace IndoorNavAR.AR
{
    public class AROriginAligner : MonoBehaviour
    {
        [Header("─── Referencias ────────────────────────────────────────────")]
        [SerializeField] private XROrigin        _xrOrigin;
        [SerializeField] private NavigationAgent _navigationAgent;

        [Header("─── Configuración ─────────────────────────────────────────")]
        [Tooltip("Nivel del modelo al que buscar el StartPoint (normalmente 0).")]
        [SerializeField] private int _targetLevel = 0;

        [Tooltip("Altura adicional sobre el StartPoint para la alineación inicial del XR Origin (FullAR).")]
        [SerializeField] private float _eyeHeightOffset = 1.6f;

        [Tooltip("Frames de espera después de que el modelo se carga antes de inicializar.")]
        [SerializeField] private int _delayFrames = 2;

        [Header("─── Modo NoAR — Seguimiento del agente ─────────────────────")]
        [Tooltip("Offset vertical de la cámara sobre el agente.")]
        [SerializeField] private float _noArCameraHeight = 1.65f;

        [Tooltip("Offset en profundidad hacia atrás del agente (0 = primera persona).")]
        [SerializeField] private float _noArCameraBack = 0.0f;

        [Tooltip("Ángulo de inclinación hacia abajo de la cámara (grados).")]
        [SerializeField] private float _noArPitchAngle = 0.0f;

        [Tooltip("Suavizado del seguimiento (0 = instantáneo).")]
        [SerializeField] private float _noArFollowSmooth = 8f;

        [Tooltip("Si true, la cámara rota para seguir la dirección del agente.")]
        [SerializeField] private bool _noArFollowRotation = true;

        [Header("─── Modo FullAR — Sincronización agente con cámara ─────────")]
        [Tooltip("Radio de búsqueda en el NavMesh al sincronizar el agente con la cámara (m). " +
                 "Aumentar si el modelo tiene suelos gruesos o está muy escalado.")]
        [SerializeField] private float _fullArSnapRadius = 3.0f;

        [Tooltip("Distancia mínima de movimiento de la cámara para re-sincronizar el agente (m). " +
                 "Evita Warp innecesarios cuando la cámara apenas se mueve.")]
        [SerializeField] private float _fullArSyncThreshold = 0.05f;

        [Header("─── Debug ──────────────────────────────────────────────────")]
        [SerializeField] private bool _logAlignment = true;

        // ─── Estado interno ────────────────────────────────────────────────

        private bool _noArMode           = false;
        private bool _followActive       = false;
        private bool _capabilityResolved = false;
        private bool _initialAlignDone   = false;
        private ARCapabilityDetector _capDetector;

        // FullAR: posición de cámara en el último frame de sincronización
        private Vector3 _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);

        // Referencia cacheada al NavMeshAgent del agente
        private NavMeshAgent _agentNavMeshAgent;

        // ─── Propiedades públicas ──────────────────────────────────────────

        /// <summary>True si el dispositivo no tiene ARCore (modo sin AR).</summary>
        public bool IsNoArMode   => _noArMode;
        public bool IsFullARMode => !_noArMode;

        #region Unity Lifecycle

        private void Awake() => FindComponents();

        private void Start() => StartCoroutine(InitializeCapabilityRoutine());

        private void OnEnable()  => EventBus.Instance?.Subscribe<ModelLoadedEvent>(OnModelLoaded);
        private void OnDisable()
        {
            EventBus.Instance?.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);
            _followActive = false;
        }

        private void Update()
        {
            // NoAR: la cámara sigue al agente
            if (_followActive && _noArMode)
            {
                FollowAgent();
                return;
            }

            // FullAR: el agente sigue a la cámara (posición del usuario real)
            if (!_noArMode && _initialAlignDone)
                SyncAgentToCameraFullAR();
        }

        #endregion

        #region Component Discovery

        private void FindComponents()
        {
            if (_xrOrigin == null)
                _xrOrigin = FindFirstObjectByType<XROrigin>();
            if (_navigationAgent == null)
                _navigationAgent = FindFirstObjectByType<NavigationAgent>();

            if (_navigationAgent != null)
                _agentNavMeshAgent = _navigationAgent.GetComponent<NavMeshAgent>();

            _capDetector = ARCapabilityDetector.Instance
                        ?? FindFirstObjectByType<ARCapabilityDetector>();

            if (_xrOrigin == null)
                Debug.LogWarning("[AROriginAligner] ⚠️ XROrigin no encontrado.");
            if (_capDetector == null)
                Debug.LogWarning("[AROriginAligner] ⚠️ ARCapabilityDetector no encontrado — asumiendo FullAR.");
        }

        #endregion

        #region Capability Initialization

        private IEnumerator InitializeCapabilityRoutine()
        {
            yield return null;

            if (_capDetector != null)
                yield return _capDetector.WaitUntilReady();

            ARCapabilityLevel level = _capDetector != null
                ? _capDetector.Current
                : ARCapabilityLevel.FullAR;

            _capabilityResolved = true;
            Log($"📡 [Start] Capacidad AR: {level}");

            if (level == ARCapabilityLevel.NoAR)
            {
                _noArMode = true;
                Log("📵 Modo NoAR — agente ES el usuario virtual (activo y visible).");

                // NoAR: PathController en modo movimiento activo
                if (_navigationAgent != null)
                {
                    var pc = _navigationAgent.GetComponent<NavigationPathController>();
                    pc?.SetFullARMode(false);
                }

                var arSession = FindFirstObjectByType<ARSession>();
                if (arSession != null) { arSession.enabled = false; Log("📵 ARSession desactivada."); }

                var planeManager = FindFirstObjectByType<ARPlaneManager>();
                if (planeManager != null) { planeManager.enabled = false; Log("📵 ARPlaneManager desactivado."); }

                // NoAR: agente activo y VISIBLE
                SetAgentActiveAndVisible(makeVisible: true);

                var modelMgr = FindFirstObjectByType<IndoorNavAR.Core.Managers.ModelLoadManager>();
                if (modelMgr != null && modelMgr.IsModelLoaded)
                    ActivateNoArMode();
            }
            else
            {
                _noArMode     = false;
                _followActive = false;

                Log("📡 Modo FullAR — agente ACTIVO e INVISIBLE, sincronizado con cámara XR.");
                Log("📡 El agente NO caminará solo: isStopped=true hasta que se inicie navegación.");

                // ✅ CRÍTICO: Activar modo FullAR en PathController AHORA.
                // Esto garantiza que FollowPath() nunca mueva el transform,
                // incluso antes de la primera llamada a NavigateToWaypoint().
                if (_navigationAgent != null)
                {
                    var pc = _navigationAgent.GetComponent<NavigationPathController>();
                    if (pc != null)
                    {
                        pc.SetFullARMode(true);
                        Log("✅ PathController.SetFullARMode(true) — movimiento del agente bloqueado.");
                    }
                    else
                    {
                        Debug.LogWarning("[AROriginAligner] ⚠️ NavigationPathController no encontrado " +
                                         "en VirtualAssistant. El agente podría moverse en FullAR.");
                    }
                }

                // FullAR: agente activo pero INVISIBLE y DETENIDO
                // - Activo: para que NavigationStartPoint, NavMeshAgent y
                //   NavigationPathController funcionen correctamente.
                // - Invisible: el usuario no debe ver al NPC.
                // - Detenido: el agente no debe moverse por su cuenta.
                SetAgentActiveAndVisible(makeVisible: false);
                StopAgentMovement();

                if (level == ARCapabilityLevel.ARWithoutPlanes)
                {
                    var pm = FindFirstObjectByType<ARPlaneManager>();
                    if (pm != null) { pm.enabled = false; Log("📡 ARPlaneManager desactivado."); }
                }
            }
        }

        /// <summary>
        /// Activa el GameObject del agente (siempre) y controla la visibilidad
        /// de sus Renderers sin desactivar el GameObject. Así todos los
        /// MonoBehaviour siguen ejecutándose independientemente del modo.
        /// </summary>
        private void SetAgentActiveAndVisible(bool makeVisible)
        {
            if (_navigationAgent == null) return;

            // Garantizar que el GameObject esté activo
            if (!_navigationAgent.gameObject.activeSelf)
            {
                _navigationAgent.gameObject.SetActive(true);
                Log($"✅ Agente activado (estaba desactivado).");
            }

            // Controlar visibilidad mediante Renderers
            foreach (var r in _navigationAgent.GetComponentsInChildren<Renderer>(true))
                r.enabled = makeVisible;

            Log(makeVisible
                ? "👁️ Agente VISIBLE (NoAR)."
                : "🙈 Agente INVISIBLE (FullAR) — Renderers desactivados, MonoBehaviour activos.");
        }

        /// <summary>
        /// En FullAR, detiene el movimiento autónomo del NavMeshAgent.
        /// El agente nunca debe caminar hacia el destino en FullAR;
        /// solo existe para que NavigationPathController calcule rutas.
        /// </summary>
        private void StopAgentMovement()
        {
            if (_agentNavMeshAgent == null) return;

            if (_agentNavMeshAgent.enabled && _agentNavMeshAgent.isOnNavMesh)
            {
                _agentNavMeshAgent.isStopped = true;
                _agentNavMeshAgent.ResetPath();
            }

            Log("🛑 [FullAR] NavMeshAgent detenido — el agente no caminará autónomamente.");
        }

        #endregion

        #region Event Handlers

        private void OnModelLoaded(ModelLoadedEvent evt)
        {
            Log($"📦 Modelo cargado: {evt.ModelName}");
            StartCoroutine(HandleModelReady());
        }

        #endregion

        #region Public API

        /// <summary>Llamar desde NavigationManager al final de InitializeFromSavedSession().</summary>
        public void NotifySessionRestored() => StartCoroutine(HandleModelReady());

        /// <summary>
        /// FullAR: alinea XR Origin al StartPoint una sola vez (si no se hizo ya).
        /// NoAR: activa seguimiento del agente.
        /// </summary>
        public void AlignToStartPoint() => StartCoroutine(HandleModelReady());

        /// <summary>Fuerza nueva alineación inicial. Útil si el modelo fue recargado.</summary>
        public void ForceRealign()
        {
            if (!_noArMode) _initialAlignDone = false;
            StartCoroutine(HandleModelReady());
        }

        #endregion

        #region Core Logic

        private IEnumerator HandleModelReady()
        {
            for (int i = 0; i < _delayFrames; i++)
                yield return null;

            if (_capDetector != null && !_capabilityResolved)
                yield return _capDetector.WaitUntilReady();

            ARCapabilityLevel level = _capDetector != null
                ? _capDetector.Current
                : ARCapabilityLevel.FullAR;

            if (level == ARCapabilityLevel.NoAR)
            {
                _noArMode = true;
                ActivateNoArMode();
            }
            else
            {
                _noArMode     = false;
                _followActive = false;
                AlignXROriginOnce();
            }
        }

        /// <summary>
        /// FullAR: posiciona XR Origin en StartPoint UNA SOLA VEZ al inicio.
        /// El agente permanece activo e invisible, detenido, esperando la primera
        /// sincronización con la cámara XR en el siguiente Update().
        /// </summary>
        private void AlignXROriginOnce()
        {
            if (_xrOrigin == null)
            {
                Debug.LogError("[AROriginAligner] ❌ XROrigin es null.");
                return;
            }

            var startPoint = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            if (startPoint == null)
            {
                Debug.LogWarning($"[AROriginAligner] ⚠️ No hay StartPoint para nivel {_targetLevel}.");
                return;
            }

            startPoint.ConfirmModelPositioned();

            if (!_initialAlignDone)
            {
                Vector3 targetPos = startPoint.transform.position + Vector3.up * _eyeHeightOffset;
                _xrOrigin.MoveCameraToWorldLocation(targetPos);
                _initialAlignDone    = true;
                _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0); // forzar primera sync

                Log($"✅ [FullAR] XR Origin → {targetPos}. ARCore toma control.");
                Log("✅ [FullAR] La sincronización agente↔cámara comenzará en el próximo Update().");
            }
            else
            {
                Log("📡 [FullAR] Alineación ya realizada — XR Origin intocado.");
            }

            // Garantizar estado correcto del agente
            SetAgentActiveAndVisible(makeVisible: false);
            StopAgentMovement();

            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message  = "Navegación lista",
                Type     = MessageType.Success,
                Duration = 3f
            });
        }

        #endregion

        #region FullAR — Sincronización agente con cámara XR

        /// <summary>
        /// En FullAR, mueve el agente al punto del NavMesh más cercano
        /// a la cámara XR. Se llama cada frame desde Update().
        ///
        /// IMPORTANTE: Solo reposiciona el agente (transform + Warp).
        /// NO inicia navegación ni modifica la ruta activa.
        /// El NavMeshAgent mantiene isStopped=true cuando no hay navegación,
        /// y ARGuideController garantiza que el agente no se mueva en FullAR.
        ///
        /// El throttle (_fullArSyncThreshold) evita Warp innecesarios cuando
        /// la cámara apenas se mueve (usuario quieto).
        /// </summary>
        private void SyncAgentToCameraFullAR()
        {
            if (_navigationAgent == null || _xrOrigin?.Camera == null) return;
            if (!_navigationAgent.gameObject.activeSelf)               return;

            Vector3 cameraPos = _xrOrigin.Camera.transform.position;

            // Throttle: solo actualizar si la cámara se movió lo suficiente
            if (Vector3.Distance(cameraPos, _lastSyncedCameraPos) < _fullArSyncThreshold)
                return;

            // Buscar el punto del NavMesh más cercano a la cámara XR
            if (!NavMesh.SamplePosition(cameraPos, out NavMeshHit hit,
                    _fullArSnapRadius, NavMesh.AllAreas))
                return; // No hay NavMesh cerca — esperar a que se cargue

            // Solo mover si la diferencia es significativa
            if (Vector3.Distance(_navigationAgent.transform.position, hit.position) < _fullArSyncThreshold)
                return;

            // Mover el agente a la posición del usuario sobre el NavMesh
            _navigationAgent.transform.position = hit.position;

            // Warp del NavMeshAgent solo si no está navegando activamente.
            // Si está navegando, NavigationPathController gestiona su posición.
            if (_agentNavMeshAgent != null && _agentNavMeshAgent.enabled && _agentNavMeshAgent.isOnNavMesh)
            {
                if (!_navigationAgent.IsNavigating)
                {
                    _agentNavMeshAgent.Warp(hit.position);
                    // Mantener detenido
                    _agentNavMeshAgent.isStopped = true;
                }
                // Si está navegando, la ruta ya fue calculada desde la posición
                // correcta — no interferir con el pathfinding activo.
            }

            _lastSyncedCameraPos = cameraPos;
        }

        #endregion

        #region NoAR Follower Mode

        private void ActivateNoArMode()
        {
            if (_xrOrigin == null)
            {
                Debug.LogError("[AROriginAligner] ❌ XROrigin es null.");
                return;
            }

            // NoAR: agente activo y visible
            SetAgentActiveAndVisible(makeVisible: true);

            var startPoint = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            if (startPoint != null)
            {
                startPoint.ConfirmModelPositioned();
                startPoint.ReteleportAgent();
            }

            if (_navigationAgent != null)
                SnapCameraToAgent(_navigationAgent.transform.position, _navigationAgent.transform.forward);

            _followActive = true;

            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message  = "Modo visualización activo (sin ARCore)",
                Type     = MessageType.Info,
                Duration = 4f
            });

            Log("✅ [NoAR] Cámara siguiendo al agente (usuario virtual).");
        }

        private void FollowAgent()
        {
            if (_navigationAgent == null || _xrOrigin == null) return;

            Transform agentTf  = _navigationAgent.transform;
            Vector3   agentPos = agentTf.position;
            Vector3   agentFwd = agentTf.forward;

            Vector3 desiredCamPos = agentPos
                + Vector3.up * _noArCameraHeight
                - agentFwd   * _noArCameraBack;

            Quaternion desiredCamRot;
            if (_noArFollowRotation && agentFwd != Vector3.zero)
            {
                Vector3 lookDir = _noArCameraBack > 0f
                    ? (agentPos - desiredCamPos).normalized
                    : agentFwd;
                desiredCamRot = Quaternion.LookRotation(lookDir) * Quaternion.Euler(_noArPitchAngle, 0f, 0f);
            }
            else
            {
                desiredCamRot = _xrOrigin.Camera.transform.rotation;
            }

            float t = _noArFollowSmooth > 0f ? Time.deltaTime * _noArFollowSmooth : 1f;

            Vector3    smoothPos = Vector3.Lerp(_xrOrigin.Camera.transform.position, desiredCamPos, t);
            Quaternion smoothRot = Quaternion.Slerp(_xrOrigin.Camera.transform.rotation, desiredCamRot, t);

            _xrOrigin.MoveCameraToWorldLocation(smoothPos);
            if (_noArFollowRotation)
                _xrOrigin.MatchOriginUpCameraForward(Vector3.up, smoothRot * Vector3.forward);
        }

        private void SnapCameraToAgent(Vector3 agentPos, Vector3 agentFwd)
        {
            Vector3 desiredCamPos = agentPos
                + Vector3.up * _noArCameraHeight
                - agentFwd   * _noArCameraBack;

            _xrOrigin.MoveCameraToWorldLocation(desiredCamPos);
            if (_noArFollowRotation && agentFwd != Vector3.zero)
                _xrOrigin.MatchOriginUpCameraForward(Vector3.up, agentFwd);
        }

        #endregion

        #region Utilities & Debug

        private void Log(string msg)
        {
            if (_logAlignment) Debug.Log($"[AROriginAligner] {msg}");
        }

        [ContextMenu("ℹ️ Info de estado")]
        private void DebugInfo()
        {
            var sp    = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            var level = _capDetector?.Current ?? ARCapabilityLevel.FullAR;

            Debug.Log("══════════════════════════════════════════════");
            Debug.Log("  AROriginAligner v6 — Estado");
            Debug.Log("══════════════════════════════════════════════");
            Debug.Log($"  Modo:               {(IsNoArMode ? "NoAR (agente visible)" : "FullAR (agente invisible, activo)")}");
            Debug.Log($"  Capacidad AR:       {level}");
            Debug.Log($"  Seguimiento NoAR:   {_followActive}");
            Debug.Log($"  Alineación inicial: {_initialAlignDone}");
            Debug.Log($"  XR Origin:          {(_xrOrigin != null ? _xrOrigin.gameObject.name : "NULL")}");
            Debug.Log($"  Camera pos:         {(_xrOrigin?.Camera?.transform.position.ToString() ?? "N/A")}");
            Debug.Log($"  StartPoint:         {(sp != null ? $"{sp.gameObject.name} @ {sp.transform.position}" : "No encontrado")}");
            Debug.Log($"  Agente:             {(_navigationAgent != null ? $"{_navigationAgent.gameObject.name} activo={_navigationAgent.gameObject.activeSelf}" : "NULL")}");
            Debug.Log($"  Agente pos:         {(_navigationAgent?.transform.position.ToString() ?? "N/A")}");
            Debug.Log($"  NavMeshAgent stop:  {(_agentNavMeshAgent != null ? _agentNavMeshAgent.isStopped.ToString() : "N/A")}");
            Debug.Log($"  Última sync cámara: {_lastSyncedCameraPos}");
            Debug.Log("══════════════════════════════════════════════");
        }

        [ContextMenu("📵 Forzar modo NoAR (test)")]
        private void DebugForceNoAr()
        {
            _noArMode = true;
            SetAgentActiveAndVisible(makeVisible: true);
            ActivateNoArMode();
        }

        [ContextMenu("📡 Forzar modo FullAR (test)")]
        private void DebugForceFullAr()
        {
            _noArMode            = false;
            _followActive        = false;
            _initialAlignDone    = false;
            _lastSyncedCameraPos = new Vector3(float.PositiveInfinity, 0, 0);
            SetAgentActiveAndVisible(makeVisible: false);
            StopAgentMovement();
            AlignXROriginOnce();
        }

        #endregion
    }
}