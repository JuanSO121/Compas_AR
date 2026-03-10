// File: EventBus.cs
// ✅ v3.1 — Solo se expande GuideAnnouncementType para NavigationVoiceGuide v5.1
//
// ============================================================================
//  CAMBIOS v3 → v3.1
// ============================================================================
//
//  ÚNICO CAMBIO: enum GuideAnnouncementType
//
//    ANTES (v3): 7 valores — todos los VoiceInstructionType no listados
//    caían en default → ResumeGuide dentro de NavigationVoiceGuide.Speak(),
//    por lo que Flutter recibía type="ResumeGuide" para giros, llegada,
//    inicio de navegación, etc. y no podía asignar prioridades correctas.
//
//    AHORA (v3.1): 19 valores — mapeo 1:1 con cada VoiceInstructionType.
//    Los 7 valores originales conservan su posición ordinal (0-6) para no
//    romper código que compare por índice numérico.
//    Los 12 valores nuevos se añaden a partir del índice 7.
//
//  TODO LO DEMÁS ES IDÉNTICO A v3.

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
    /// empezó o terminó de hablar. ARGuideController lo escucha para
    /// pausar/reanudar el NPC durante instrucciones de alta prioridad.
    /// </summary>
    public struct TTSSpeakingEvent
    {
        /// <summary>True cuando el TTS empieza a hablar, false cuando termina.</summary>
        public bool IsSpeaking;

        /// <summary>
        /// Prioridad de la instrucción que se está leyendo (0-4).
        /// ARGuideController solo pausa el NPC para prioridad ≥ 3.
        /// </summary>
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

    public struct NavigationArrivedEvent
    {
        public string  WaypointName;
        public Vector3 Position;
    }

    // ── Guía NPC ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Publicado por ARGuideController para avisos de voz que Flutter debe
    /// leer en voz alta (TTS) al usuario con baja visión.
    /// VoiceCommandAPI se suscribe y lo reenvía a Flutter con
    /// action="guide_announcement".
    /// </summary>
    public struct GuideAnnouncementEvent
    {
        public GuideAnnouncementType AnnouncementType;
        public string Message;
        public int    CurrentFloor;
    }

    /// <summary>
    /// ✅ v3.1 — Enum expandido de 7 a 19 valores.
    ///
    /// Los 7 valores originales conservan su índice ordinal (0-6) para
    /// no romper comparaciones numéricas existentes en el proyecto.
    /// Los 12 valores nuevos (7-18) dan a Flutter la información de tipo
    /// necesaria para asignar prioridades TTS correctas en
    /// VoiceNavigationService._priorityForType().
    /// </summary>
    public enum GuideAnnouncementType
    {
        // ── Originales v3 (NO reordenar) ─────────────────────────────────────
        ApproachingStairs     = 0,   // Escaleras próximas        → urgent
        StartingClimb         = 1,   // Iniciando subida           → urgent
        StartingDescent       = 2,   // Iniciando bajada           → urgent
        FloorReached          = 3,   // Llegó al piso destino      → medium
        WaitingForUser        = 4,   // Usuario detenido           → low
        ResumeGuide           = 5,   // Reanudar guía (genérico)   → medium
        StairsComplete        = 6,   // Escaleras completadas      → medium

        // ── Nuevos v3.1 ───────────────────────────────────────────────────────
        ResumeAfterSeparation = 7,   // Reanudar tras separación   → medium
        StartNavigation       = 8,   // Inicio de navegación       → medium
        Arrived               = 9,   // Llegada al destino         → medium

        TurnLeft              = 10,  // Giro izquierda             → high
        TurnRight             = 11,  // Giro derecha               → high
        SlightLeft            = 12,  // Giro leve izquierda        → high
        SlightRight           = 13,  // Giro leve derecha          → high
        UTurn                 = 14,  // Media vuelta               → high

        GoStraight            = 15,  // Continuar recto            → low
        UserDeviated          = 16,  // Desviado / desorientado    → urgent
        ObstacleWarning       = 17,  // Posible obstáculo          → urgent
        ProgressUpdate        = 18,  // Actualización de progreso  → low
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