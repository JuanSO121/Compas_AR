// File: AROriginAligner.cs
// ✅ FIX v2 — Modo fallback "No AR": si ARCapabilityDetector detecta que el
//             dispositivo no soporta ARCore, el XR Origin se ancla al
//             NavigationAgent y lo sigue cada frame (modo espectador).
//
//  COMPORTAMIENTO:
//    FullAR         → comportamiento original: AR tracking real + alineación al StartPoint
//    ARWithoutPlanes→ igual que FullAR pero sin detección de planos
//    NoAR           → XR Origin se fija al agente. La cámara sigue al personaje
//                     con un offset configurable (altura del ojo, ángulo de vista).
//                     El usuario puede ver el recorrido completo aunque no tenga ARCore.
//
//  INTEGRACIÓN:
//    Requiere ARCapabilityDetector en escena.
//    Si ARCapabilityDetector no está disponible, asume FullAR (comportamiento original).

using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Navigation;
using Unity.XR.CoreUtils;

namespace IndoorNavAR.AR
{
    public class AROriginAligner : MonoBehaviour
    {
        [Header("─── Referencias ────────────────────────────────────────")]
        [SerializeField] private XROrigin         _xrOrigin;
        [SerializeField] private NavigationAgent  _navigationAgent;

        [Header("─── Configuración AR ──────────────────────────────────")]
        [Tooltip("Nivel del modelo al que buscar el StartPoint (normalmente 0).")]
        [SerializeField] private int   _targetLevel       = 0;

        [Tooltip("Altura adicional sobre el StartPoint para colocar la cámara AR.")]
        [SerializeField] private float _eyeHeightOffset   = 1.6f;

        [Tooltip("Si true, se realinea cada vez que se carga un modelo nuevo vía EventBus.")]
        [SerializeField] private bool  _autoAlignOnLoad   = true;

        [Tooltip("Si true, también teleporta el NavigationAgent al StartPoint.")]
        [SerializeField] private bool  _teleportAgent     = true;

        [Tooltip("Frames de espera después de que el modelo se carga antes de alinear.")]
        [SerializeField] private int   _delayFrames       = 2;

        [Header("─── Modo No-AR (fallback) ─────────────────────────────")]
        [Tooltip("Offset vertical de la cámara sobre el agente en modo No-AR.")]
        [SerializeField] private float _noArCameraHeight  = 1.65f;

        [Tooltip("Offset en profundidad (hacia atrás del agente) para vista en 3ª persona.")]
        [SerializeField] private float _noArCameraBack    = 0.0f;

        [Tooltip("Ángulo de inclinación hacia abajo de la cámara en modo No-AR (grados).")]
        [SerializeField] private float _noArPitchAngle    = 0.0f;

        [Tooltip("Suavizado del seguimiento (0 = instantáneo, valores altos = más suave).")]
        [SerializeField] private float _noArFollowSmooth  = 8f;

        [Tooltip("Si true, la cámara rota para seguir la dirección del agente.")]
        [SerializeField] private bool  _noArFollowRotation = true;

        [Header("─── Debug ──────────────────────────────────────────────")]
        [SerializeField] private bool _logAlignment = true;

        // ─── Estado interno ────────────────────────────────────────────────
        private bool               _aligned      = false;
        private bool               _noArMode     = false;   // true cuando ARCapability = NoAR
        private bool               _followActive = false;   // true cuando el loop de seguimiento corre
        private ARCapabilityDetector _capDetector;

        #region Unity Lifecycle

        private void Awake()
        {
            FindComponents();
        }

        private void OnEnable()
        {
            if (_autoAlignOnLoad)
                EventBus.Instance?.Subscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        private void OnDisable()
        {
            EventBus.Instance?.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);
            _followActive = false;
        }

        private void Update()
        {
            if (_followActive && _noArMode)
                FollowAgent();
        }

        #endregion

        #region Component Discovery

        private void FindComponents()
        {
            if (_xrOrigin == null)
                _xrOrigin = FindFirstObjectByType<XROrigin>();

            if (_navigationAgent == null)
                _navigationAgent = FindFirstObjectByType<NavigationAgent>();

            _capDetector = ARCapabilityDetector.Instance
                        ?? FindFirstObjectByType<ARCapabilityDetector>();

            if (_xrOrigin == null)
                Debug.LogWarning("[AROriginAligner] ⚠️ XROrigin no encontrado.");

            if (_capDetector == null)
                Debug.LogWarning("[AROriginAligner] ⚠️ ARCapabilityDetector no encontrado — asumiendo FullAR.");
        }

        #endregion

        #region Event Handlers

        private void OnModelLoaded(ModelLoadedEvent evt)
        {
            Log($"📦 Modelo cargado: {evt.ModelName} → alineando en {_delayFrames} frames...");
            StartCoroutine(AlignWhenReady());
        }

        #endregion

        #region Public API

        [ContextMenu("🎯 Alinear al StartPoint ahora")]
        public void AlignToStartPoint()
        {
            StartCoroutine(AlignAfterFrames(1));
        }

        public void ForceRealign()
        {
            _aligned = false;
            AlignToStartPoint();
        }

        #endregion

        #region Core Alignment

        /// <summary>
        /// Espera a que ARCapabilityDetector esté listo, luego decide qué modo usar.
        /// </summary>
        private IEnumerator AlignWhenReady()
        {
            // Esperar frames configurados
            for (int i = 0; i < _delayFrames; i++)
                yield return null;

            // Si hay detector, esperar a que termine la detección
            if (_capDetector != null)
                yield return _capDetector.WaitUntilReady();

            // Decidir modo
            ARCapabilityLevel level = _capDetector != null
                ? _capDetector.Current
                : ARCapabilityLevel.FullAR;

            Log($"📡 Modo AR detectado: {level}");

            if (level == ARCapabilityLevel.NoAR)
            {
                Log("📵 Dispositivo sin ARCore → activando modo seguimiento de agente.");
                _noArMode = true;
                ActivateNoArMode();
            }
            else
            {
                _noArMode = false;
                _followActive = false;
                PerformAlignment();
            }
        }

        private IEnumerator AlignAfterFrames(int frames)
        {
            for (int i = 0; i < frames; i++)
                yield return null;
            PerformAlignment();
        }

        private void PerformAlignment()
        {
            if (_xrOrigin == null)
            {
                Debug.LogError("[AROriginAligner] ❌ XROrigin es null.");
                return;
            }

            var startPoint = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            if (startPoint == null)
            {
                Debug.LogWarning($"[AROriginAligner] ⚠️ No hay NavigationStartPoint para nivel {_targetLevel}.");
                return;
            }

            startPoint.ConfirmModelPositioned();

            Vector3 startWorldPos    = startPoint.transform.position;
            Vector3 targetCameraPos  = startWorldPos + Vector3.up * _eyeHeightOffset;
            _xrOrigin.MoveCameraToWorldLocation(targetCameraPos);

            Log($"✅ XR Origin alineado → cámara en {targetCameraPos}");

            if (_teleportAgent && _navigationAgent != null)
            {
                bool ok = _navigationAgent.TeleportTo(startWorldPos);
                if (ok)  Log($"✅ NavigationAgent teleportado a {startWorldPos}");
                else     Debug.LogWarning("[AROriginAligner] ⚠️ TeleportTo falló — ¿NavMesh activo?");
            }

            startPoint.ReteleportAgent();
            _aligned = true;

            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message  = "Posicionado en el inicio del edificio",
                Type     = MessageType.Success,
                Duration = 3f
            });
        }

        #endregion

        #region No-AR Follower Mode

        /// <summary>
        /// Activa el modo fallback: desactiva el AR tracking,
        /// teleporta al StartPoint y empieza a seguir al agente.
        /// </summary>
        private void ActivateNoArMode()
        {
            if (_xrOrigin == null)
            {
                Debug.LogError("[AROriginAligner] ❌ XROrigin es null — no se puede activar modo No-AR.");
                return;
            }

            // Desactivar ARSession para que no intente hacer tracking
            // (ahorra batería y evita que sobreescriba la posición del Origin)
            var arSession = FindFirstObjectByType<ARSession>();
            if (arSession != null)
            {
                arSession.enabled = false;
                Log("📵 ARSession desactivada (modo No-AR).");
            }

            // Desactivar ARPlaneManager para que no intente detectar planos
            var planeManager = FindFirstObjectByType<ARPlaneManager>();
            if (planeManager != null)
            {
                planeManager.enabled = false;
                Log("📵 ARPlaneManager desactivada.");
            }

            // Posicionar XR Origin sobre el StartPoint para el primer frame
            var startPoint = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            if (startPoint != null)
            {
                startPoint.ConfirmModelPositioned();
                startPoint.ReteleportAgent();

                // Posicionar cámara sobre el agente inmediatamente (sin lerp)
                if (_navigationAgent != null)
                {
                    Vector3 agentPos = _navigationAgent.transform.position;
                    SnapCameraToAgent(agentPos, _navigationAgent.transform.forward);
                }
            }

            // Activar el loop de seguimiento en Update()
            _followActive = true;
            _aligned      = true;

            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message  = "Modo visualización activo (sin ARCore)",
                Type     = MessageType.Info,
                Duration = 4f
            });

            Log("✅ Modo No-AR activo — cámara siguiendo al agente.");
        }

        /// <summary>
        /// Llamado cada Update() en modo No-AR.
        /// Mueve el XR Origin para que la cámara siga al agente con suavizado.
        /// </summary>
        private void FollowAgent()
        {
            if (_navigationAgent == null || _xrOrigin == null) return;

            Transform agentTf = _navigationAgent.transform;

            // Calcular la posición deseada de la cámara
            Vector3 agentPos     = agentTf.position;
            Vector3 agentForward = agentTf.forward;
            Vector3 desiredCamPos = agentPos
                + Vector3.up    * _noArCameraHeight
                - agentForward  * _noArCameraBack;

            // Calcular la rotación deseada de la cámara
            Quaternion desiredCamRot;
            if (_noArFollowRotation && agentForward != Vector3.zero)
            {
                Vector3 lookDir = agentForward;
                if (_noArCameraBack > 0)
                    lookDir = (agentPos - desiredCamPos).normalized; // mira hacia el agente
                desiredCamRot = Quaternion.LookRotation(lookDir) *
                                Quaternion.Euler(_noArPitchAngle, 0, 0);
            }
            else
            {
                desiredCamRot = _xrOrigin.Camera.transform.rotation;
            }

            // Mover el XR Origin para que la cámara quede en desiredCamPos/Rot
            // usando la API oficial de AR Foundation (no modificar transform directamente)
            float t = _noArFollowSmooth > 0
                ? Time.deltaTime * _noArFollowSmooth
                : 1f;

            Vector3    currentCamPos = _xrOrigin.Camera.transform.position;
            Quaternion currentCamRot = _xrOrigin.Camera.transform.rotation;

            Vector3    smoothPos = Vector3.Lerp(currentCamPos, desiredCamPos, t);
            Quaternion smoothRot = Quaternion.Slerp(currentCamRot, desiredCamRot, t);

            _xrOrigin.MoveCameraToWorldLocation(smoothPos);

            // Rotar solo el Camera offset, no el Origin completo
            if (_noArFollowRotation)
                _xrOrigin.MatchOriginUpCameraForward(Vector3.up, smoothRot * Vector3.forward);
        }

        /// <summary>
        /// Posicionamiento instantáneo (sin lerp) — para el primer frame.
        /// </summary>
        private void SnapCameraToAgent(Vector3 agentPos, Vector3 agentForward)
        {
            Vector3 desiredCamPos = agentPos
                + Vector3.up   * _noArCameraHeight
                - agentForward * _noArCameraBack;

            _xrOrigin.MoveCameraToWorldLocation(desiredCamPos);

            if (_noArFollowRotation && agentForward != Vector3.zero)
            {
                _xrOrigin.MatchOriginUpCameraForward(Vector3.up, agentForward);
            }
        }

        #endregion

        #region Utilities

        private void Log(string msg)
        {
            if (_logAlignment)
                Debug.Log($"[AROriginAligner] {msg}");
        }

        #endregion

        #region Debug Info

        [ContextMenu("ℹ️ Info de alineación")]
        private void DebugInfo()
        {
            var startPoint = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            ARCapabilityLevel level = _capDetector?.Current ?? ARCapabilityLevel.FullAR;

            Debug.Log("══════════════════════════════════════");
            Debug.Log("  AROriginAligner — Estado");
            Debug.Log("══════════════════════════════════════");
            Debug.Log($"  Modo AR:         {level}");
            Debug.Log($"  Modo No-AR:      {_noArMode}");
            Debug.Log($"  Seguimiento:     {_followActive}");
            Debug.Log($"  XR Origin:       {(_xrOrigin != null ? _xrOrigin.gameObject.name : "NULL")}");
            Debug.Log($"  AR Camera pos:   {(_xrOrigin?.Camera?.transform.position.ToString() ?? "N/A")}");
            Debug.Log($"  StartPoint:      {(startPoint != null ? startPoint.gameObject.name : "No encontrado")}");
            Debug.Log($"  SP posición:     {(startPoint != null ? startPoint.transform.position.ToString() : "N/A")}");
            Debug.Log($"  Agente pos:      {(_navigationAgent != null ? _navigationAgent.transform.position.ToString() : "N/A")}");
            Debug.Log($"  Nivel target:    {_targetLevel}");
            Debug.Log($"  Alineado:        {_aligned}");
            Debug.Log("══════════════════════════════════════");
        }

        [ContextMenu("📵 Forzar modo No-AR (test)")]
        private void DebugForceNoAr()
        {
            _noArMode = true;
            ActivateNoArMode();
        }

        [ContextMenu("📡 Forzar modo FullAR (test)")]
        private void DebugForceFullAr()
        {
            _noArMode     = false;
            _followActive = false;
            AlignToStartPoint();
        }

        #endregion
    }
}