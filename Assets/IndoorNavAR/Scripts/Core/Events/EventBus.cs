// File: EventBus.cs
// ✅ v3 — Sin modificaciones respecto al original.
// GuideAnnouncementEvent y GuideAnnouncementType ya estaban definidos aquí.
// No se toca este archivo.

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

    public enum GuideAnnouncementType
    {
        ApproachingStairs,
        StartingClimb,
        StartingDescent,
        FloorReached,
        WaitingForUser,
        ResumeGuide,
        StairsComplete
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