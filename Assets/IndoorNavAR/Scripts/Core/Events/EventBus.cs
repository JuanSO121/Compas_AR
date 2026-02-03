using System;
using System.Collections.Generic;
using UnityEngine;

namespace IndoorNavAR.Core.Events
{
    /// <summary>
    /// Sistema centralizado de eventos para comunicación desacoplada entre componentes.
    /// ✅ FIXED: DontDestroyOnLoad solo en builds, evita warnings en Editor
    /// </summary>
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
                        
                        // ✅ FIXED: Solo usar DontDestroyOnLoad en builds
                        #if !UNITY_EDITOR
                        DontDestroyOnLoad(go);
                        #endif
                    }
                }
                return _instance;
            }
        }

        private readonly Dictionary<Type, Delegate> _eventDelegates = new Dictionary<Type, Delegate>();
        private readonly List<Delegate> _delegatesToRemove = new List<Delegate>();

        #region Lifecycle

        private void Awake()
        {
            // ✅ NUEVO: Prevenir duplicados en Editor
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning("[EventBus] Instancia duplicada detectada. Destruyendo...");
                Destroy(gameObject);
                return;
            }
            
            _instance = this;
        }

        private void OnDestroy()
        {
            // ✅ FIXED: Limpiar solo si somos la instancia activa
            if (_instance == this)
            {
                ClearAll();
                _instance = null;
            }
        }

        #endregion

        #region Subscription Management

        /// <summary>
        /// Suscribe un handler a un evento específico.
        /// </summary>
        public void Subscribe<T>(Action<T> handler) where T : struct
        {
            Type eventType = typeof(T);
            
            if (_eventDelegates.TryGetValue(eventType, out Delegate existingDelegate))
            {
                _eventDelegates[eventType] = Delegate.Combine(existingDelegate, handler);
            }
            else
            {
                _eventDelegates[eventType] = handler;
            }
        }

        /// <summary>
        /// Desuscribe un handler de un evento específico.
        /// </summary>
        public void Unsubscribe<T>(Action<T> handler) where T : struct
        {
            Type eventType = typeof(T);
            
            if (_eventDelegates.TryGetValue(eventType, out Delegate existingDelegate))
            {
                Delegate newDelegate = Delegate.Remove(existingDelegate, handler);
                
                if (newDelegate == null)
                {
                    _eventDelegates.Remove(eventType);
                }
                else
                {
                    _eventDelegates[eventType] = newDelegate;
                }
            }
        }

        /// <summary>
        /// Publica un evento a todos los suscriptores.
        /// </summary>
        public void Publish<T>(T eventData) where T : struct
        {
            Type eventType = typeof(T);
            
            if (_eventDelegates.TryGetValue(eventType, out Delegate existingDelegate))
            {
                Action<T> callback = existingDelegate as Action<T>;
                
                try
                {
                    callback?.Invoke(eventData);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[EventBus] Error al ejecutar evento {eventType.Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        /// <summary>
        /// Limpia todas las suscripciones.
        /// </summary>
        public void ClearAll()
        {
            _eventDelegates.Clear();
            _delegatesToRemove.Clear();
        }

        #endregion
    }

    #region Event Definitions

    // ========== AR Events ==========
    
    public struct PlaneDetectedEvent
    {
        public UnityEngine.XR.ARFoundation.ARPlane Plane;
        public Vector3 Center;
        public float Area;
    }

    public struct PlaneUpdatedEvent
    {
        public UnityEngine.XR.ARFoundation.ARPlane Plane;
        public Vector3 NewCenter;
        public float NewArea;
    }

    public struct PlaneRemovedEvent
    {
        public UnityEngine.XR.ARFoundation.ARPlane Plane;
    }

    // ========== Waypoint Events ==========
    
    public struct WaypointPlacedEvent
    {
        public string WaypointId;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    public struct WaypointConfiguredEvent
    {
        public string WaypointId;
        public string WaypointName;
        public WaypointType Type;
        public Color Color;
    }

    public struct WaypointRemovedEvent
    {
        public string WaypointId;
    }

    // ========== Model Events ==========
    
    public struct ModelLoadedEvent
    {
        public GameObject ModelInstance;
        public string ModelName;
        public Vector3 Position;
    }

    public struct ModelLoadFailedEvent
    {
        public string ModelName;
        public string ErrorMessage;
    }

    // ========== NavMesh Events ==========
    
    public struct NavMeshGeneratedEvent
    {
        public int SurfaceCount;
        public float TotalArea;
        public bool Success;
    }

    public struct NavMeshGenerationFailedEvent
    {
        public string ErrorMessage;
    }

    // ========== Navigation Events ==========
    
    public struct NavigationStartedEvent
    {
        public string DestinationWaypointId;
        public Vector3 StartPosition;
        public Vector3 DestinationPosition;
        public float EstimatedDistance;
    }

    public struct NavigationCompletedEvent
    {
        public string DestinationWaypointId;
        public float TotalDistance;
        public float TotalTime;
    }

    public struct NavigationCancelledEvent
    {
        public string Reason;
    }

    public struct NavigationProgressEvent
    {
        public float DistanceRemaining;
        public float ProgressPercent;
        public Vector3 CurrentPosition;
    }

    // ========== UI Events ==========
    
    public struct AppModeChangedEvent
    {
        public AppMode PreviousMode;
        public AppMode NewMode;
    }

    public struct ShowMessageEvent
    {
        public string Message;
        public MessageType Type;
        public float Duration;
    }

    #endregion

    #region Enums

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

    #endregion
}