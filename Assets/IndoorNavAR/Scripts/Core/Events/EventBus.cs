// File: EventBus.cs
// ✅ v3.3 — Añade ARSessionReadyEvent para PersistenceManager v12 / SceneReadyNotifier v3.0
//
// ════════════════════════════════════════════════════════════════════════════
// CAMBIOS v3.2 → v3.3
// ════════════════════════════════════════════════════════════════════════════
//
//  ÚNICO CAMBIO: nuevo struct ARSessionReadyEvent
//
//  CONTEXTO:
//    PersistenceManager v12 publica ARSessionReadyEvent cuando la auto-carga
//    de sesión termina (_autoLoadCompleted = true).
//    SceneReadyNotifier v3.0 se suscribe a este evento para reaccionar
//    inmediatamente en lugar de hacer polling de IsSessionLoadCompleted.
//
//    Esto elimina la dependencia de timeout en Flutter para detectar si
//    Unity tiene sesión cargada: el evento llega exactamente cuando la
//    carga termina, sin importar cuánto tiempo tomó.
//
//    Flujo:
//      PersistenceManager.Start() → LoadSession() → ... → ARSessionReadyEvent
//      SceneReadyNotifier.OnARSessionReadyEvent() → NotifyReady() si todo listo
//      FlutterUnityBridge.NotifySceneReady() → scene_ready a Flutter con datos reales
//
//  TODOS LOS COMPORTAMIENTOS DE v3.2 SE CONSERVAN ÍNTEGRAMENTE.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace IndoorNavAR.Core.Events
{
    public class EventBus : MonoBehaviour
    {
        private static EventBus _instance;
        public static EventBus Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<EventBus>();
                    if (_instance == null)
                    {
                        GameObject go = new GameObject("[EventBus]");
                        _instance = go.AddComponent<EventBus>();
#if !UNITY_EDITOR
                        DontDestroyOnLoad(go);
#endif
                    }
                }
                return _instance;
            }
        }

        private readonly Dictionary<Type, Delegate> _eventDelegates = new();
        private readonly List<Delegate> _delegatesToRemove = new();

        #region Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                ClearAll();
                _instance = null;
            }
        }

#if UNITY_EDITOR
        [UnityEngine.RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticInstance() => _instance = null;
#endif

        #endregion

        #region Pub/Sub

        public void Subscribe<T>(Action<T> handler) where T : struct
        {
            var type = typeof(T);
            _eventDelegates[type] = _eventDelegates.TryGetValue(type, out var existing)
                ? Delegate.Combine(existing, handler)
                : handler;
        }

        public void Unsubscribe<T>(Action<T> handler) where T : struct
        {
            var type = typeof(T);
            if (!_eventDelegates.TryGetValue(type, out var existing)) return;
            var updated = Delegate.Remove(existing, handler);
            if (updated == null) _eventDelegates.Remove(type);
            else                 _eventDelegates[type] = updated;
        }

        public void Publish<T>(T eventData) where T : struct
        {
            if (!_eventDelegates.TryGetValue(typeof(T), out var existing)) return;
            try   { (existing as Action<T>)?.Invoke(eventData); }
            catch (Exception ex)
            { Debug.LogError($"[EventBus] {typeof(T).Name}: {ex.Message}\n{ex.StackTrace}"); }
        }

        public void ClearAll()
        {
            _eventDelegates.Clear();
            _delegatesToRemove.Clear();
        }

        #endregion
    }

    // =========================================================================
    // EVENTOS
    // =========================================================================

    // ── AR ────────────────────────────────────────────────────────────────────

    public struct PlaneDetectedEvent
    {
        public UnityEngine.XR.ARFoundation.ARPlane Plane;
        public Vector3 Center;
        public float   Area;
    }

    public struct PlaneUpdatedEvent
    {
        public UnityEngine.XR.ARFoundation.ARPlane Plane;
        public Vector3 NewCenter;
        public float   NewArea;
    }

    public struct PlaneRemovedEvent
    {
        public UnityEngine.XR.ARFoundation.ARPlane Plane;
    }

    /// <summary>
    /// ✅ v3.3 NUEVO — Publicado por PersistenceManager v12 cuando la auto-carga
    /// de sesión al inicio termina (_autoLoadCompleted = true).
    ///
    /// SceneReadyNotifier v3.0 se suscribe a este evento para:
    ///   1. Eliminar la dependencia del polling de IsSessionLoadCompleted.
    ///   2. Reaccionar inmediatamente sin importar cuánto tardó LoadSession().
    ///   3. Enviar scene_ready a Flutter con los datos reales de sesión.
    ///
    /// Sin campos: la sola llegada del evento es la señal de que la carga terminó.
    /// Los datos de la sesión se leen directamente de PersistenceManager.AutoLoadResult
    /// y WaypointManager.WaypointCount en BuildReadyDetail().
    /// </summary>

    // ── Waypoints ─────────────────────────────────────────────────────────────

    public struct WaypointPlacedEvent
    {
        public string     WaypointId;
        public Vector3    Position;
        public Quaternion Rotation;
    }

    public struct WaypointConfiguredEvent
    {
        public string       WaypointId;
        public string       WaypointName;
        public WaypointType Type;
        public Color        Color;
    }

    public struct WaypointRemovedEvent
    {
        public string WaypointId;
    }

    public struct WaypointsBatchLoadedEvent
    {
        public int Count;
    }

    /// <summary>
    /// Publicado por VoiceCommandAPI cuando Flutter reporta que el TTS
    /// empezó o terminó de hablar.
    /// </summary>
    public struct TTSSpeakingEvent
    {
        public bool IsSpeaking;
        public int Priority;
    }

    // ── Modelo 3D ─────────────────────────────────────────────────────────────

    public struct ModelLoadedEvent
    {
        public GameObject ModelInstance;
        public string     ModelName;
        public Vector3    Position;
    }

    public struct ModelLoadFailedEvent
    {
        public string ModelName;
        public string ErrorMessage;
    }

    // ── NavMesh ───────────────────────────────────────────────────────────────

    public struct NavMeshGeneratedEvent
    {
        public int   SurfaceCount;
        public float TotalArea;
        public bool  Success;
    }

    public struct NavMeshGenerationFailedEvent
    {
        public string ErrorMessage;
    }

    // ── Navegación ────────────────────────────────────────────────────────────

    public struct NavigationStartedEvent
    {
        public string  DestinationWaypointId;
        public Vector3 StartPosition;
        public Vector3 DestinationPosition;
        public float   EstimatedDistance;
    }

    public struct NavigationCompletedEvent
    {
        public string DestinationWaypointId;
        public float  TotalDistance;
        public float  TotalTime;
    }

    public struct NavigationCancelledEvent
    {
        public string Reason;
    }

    /// <summary>
    /// ✅ v3.2 — Publicado por NavigationManager.StopNavigation() cuando
    /// el usuario detiene la navegación de forma intencional.
    /// </summary>
    public struct NavigationStoppedEvent
    {
        public string DestinationWaypointName;
    }

    public struct NavigationProgressEvent
    {
        public float   DistanceRemaining;
        public float   ProgressPercent;
        public Vector3 CurrentPosition;
    }

    public struct FloorTransitionEvent
    {
        public int     FromLevel;
        public int     ToLevel;
        public Vector3 AgentPosition;
    }

    public struct RouteDeviatedEvent
    {
        public Vector3 UserPosition { get; set; }
        public float DeviationDistance { get; set; }
        public Vector3 Destination { get; set; }
    }

    public struct NavigationArrivedEvent
    {
        public string  WaypointName;
        public Vector3 Position;
    }

    public struct ObstacleDetectedEvent
    {
        public Vector3 ObstaclePosition;
        public float DetectedRatio;
    }

    // ── Guía NPC ─────────────────────────────────────────────────────────────

    public struct GuideAnnouncementEvent
    {
        public GuideAnnouncementType AnnouncementType;
        public string Message;
        public int    CurrentFloor;
    }

    public enum GuideAnnouncementType
    {
        // ── Originales v3 (NO reordenar) ─────────────────────────────────────
        ApproachingStairs     = 0,
        StartingClimb         = 1,
        StartingDescent       = 2,
        FloorReached          = 3,
        WaitingForUser        = 4,
        ResumeGuide           = 5,
        StairsComplete        = 6,

        // ── Nuevos v3.1 ───────────────────────────────────────────────────────
        ResumeAfterSeparation = 7,
        StartNavigation       = 8,
        Arrived               = 9,

        TurnLeft              = 10,
        TurnRight             = 11,
        SlightLeft            = 12,
        SlightRight           = 13,
        UTurn                 = 14,

        GoStraight            = 15,
        UserDeviated          = 16,
        ObstacleWarning       = 17,
        ProgressUpdate        = 18,
    }

    // ── UI / Mensajes ─────────────────────────────────────────────────────────

    public struct AppModeChangedEvent
    {
        public AppMode PreviousMode;
        public AppMode NewMode;
    }

    public struct ShowMessageEvent
    {
        public string      Message;
        public MessageType Type;
        public float       Duration;
    }

    // =========================================================================
    // ENUMS
    // =========================================================================

    public enum WaypointType
    {
        Generic,
        Entrance,
        Exit,
        Kitchen,
        Bathroom,
        Bedroom,
        LivingRoom,
        DiningRoom,
        Office,
        Hallway,
        Stairs,
        Elevator,
        Custom
    }

    public enum AppMode
    {
        Initialization,
        PlaneDetection,
        ModelPlacement,
        WaypointPlacement,
        WaypointConfiguration,
        Navigation,
        Settings
    }

    public enum MessageType
    {
        Info,
        Success,
        Warning,
        Error
    }
}