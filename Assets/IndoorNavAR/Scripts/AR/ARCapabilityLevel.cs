// File: ARCapabilityDetector.cs
// Compatible con Unity 2021–2022 / AR Foundation 4.x
// Versión basada en Coroutine (la correcta para tu proyecto)

using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace IndoorNavAR.AR
{
    public enum ARCapabilityLevel
    {
        FullAR,
        ARWithoutPlanes,
        NoAR
    }

    public class ARCapabilityDetector : MonoBehaviour
    {
        public static ARCapabilityDetector Instance { get; private set; }

        [Header("Referencias")]
        [SerializeField] private ARPlaneManager _planeManager;

        [Header("Debug")]
        [SerializeField] private bool _verbose = true;
        [SerializeField] private int _forceLevel = -1; // -1 = auto

        public ARCapabilityLevel Current { get; private set; } = ARCapabilityLevel.NoAR;
        public bool IsReady { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            StartCoroutine(DetectRoutine());
        }

private IEnumerator DetectRoutine()
{
    if (_forceLevel >= 0 && _forceLevel <= 2)
    {
        Current = (ARCapabilityLevel)_forceLevel;
        IsReady = true;
        yield break;
    }

#if UNITY_EDITOR
    Current = ARCapabilityLevel.FullAR;
    IsReady = true;
    Log($"Capacidad AR detectada → {Current}");
    yield break;
#else

    yield return ARSession.CheckAvailability();

    if (ARSession.state == ARSessionState.Unsupported)
    {
        Log("Dispositivo sin soporte AR.");
        Current = ARCapabilityLevel.NoAR;
        IsReady = true;
        yield break;
    }

    if (ARSession.state == ARSessionState.NeedsInstall)
    {
        Log("AR necesita instalación, solicitando...");
        yield return ARSession.Install();

        // ✅ NUEVO: verificar resultado de la instalación
        if (ARSession.state != ARSessionState.Ready &&
            ARSession.state != ARSessionState.SessionInitializing &&
            ARSession.state != ARSessionState.SessionTracking)
        {
            Log($"Instalación AR rechazada o fallida (state={ARSession.state}) → NoAR");
            Current = ARCapabilityLevel.NoAR;
            IsReady = true;
            yield break;
        }
    }

    // ✅ NUEVO: esperar a que la sesión realmente arranque (timeout 5s)
    float timeout = 5f;
    while (ARSession.state == ARSessionState.SessionInitializing && timeout > 0f)
    {
        timeout -= Time.deltaTime;
        yield return null;
    }

    // ✅ NUEVO: si no está tracked/ready después del timeout → NoAR
    bool sessionActive = ARSession.state == ARSessionState.SessionTracking ||
                         ARSession.state == ARSessionState.Ready;

    if (!sessionActive)
    {
        Log($"Sesión AR no activa tras timeout (state={ARSession.state}) → NoAR");
        Current = ARCapabilityLevel.NoAR;
        IsReady = true;
        yield break;
    }

    _planeManager ??= FindAnyObjectByType<ARPlaneManager>();

    if (_planeManager == null)
    {
        Current = ARCapabilityLevel.ARWithoutPlanes;
        IsReady = true;
        Log($"Capacidad AR detectada → {Current}");
        yield break;
    }

    yield return new WaitForSeconds(1f);

    var descriptor = _planeManager.descriptor;
    bool supportsPlanes =
        descriptor != null &&
        (descriptor.supportsHorizontalPlaneDetection ||
         descriptor.supportsVerticalPlaneDetection);

    Current = supportsPlanes
        ? ARCapabilityLevel.FullAR
        : ARCapabilityLevel.ARWithoutPlanes;

    IsReady = true;
    Log($"Capacidad AR detectada → {Current}");

#endif
}


        public IEnumerator WaitUntilReady()
        {
            while (!IsReady)
                yield return null;
        }

        private void Log(string msg)
        {
            if (_verbose)
                Debug.Log($"[ARCapability] {msg}");
        }
    }
}