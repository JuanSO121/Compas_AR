    using System;
    using System.Collections.Generic;
    using UnityEngine;

    namespace IndoorNavAR.Core.Events
    {
        /// <summary>
        /// Sistema centralizado de eventos para comunicación desacoplada entre componentes.
        /// Implementa patrón Observer/Pub-Sub con gestión automática de suscripciones.
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
                            DontDestroyOnLoad(go);
                        }
                    }
                    return _instance;
                }
            }

            private readonly Dictionary<Type, Delegate> _eventDelegates = new Dictionary<Type, Delegate>();
            private readonly List<Delegate> _delegatesToRemove = new List<Delegate>();

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

            private void OnDestroy()
            {
                ClearAll();
            }
        }

        #region Event Definitions

        // ========== AR Events ==========
        
        /// <summary>
        /// Se dispara cuando se detecta un nuevo plano AR horizontal.
        /// </summary>
        public struct PlaneDetectedEvent
        {
            public UnityEngine.XR.ARFoundation.ARPlane Plane;
            public Vector3 Center;
            public float Area;
        }

        /// <summary>
        /// Se dispara cuando se actualiza un plano AR existente.
        /// </summary>
        public struct PlaneUpdatedEvent
        {
            public UnityEngine.XR.ARFoundation.ARPlane Plane;
            public Vector3 NewCenter;
            public float NewArea;
        }

        /// <summary>
        /// Se dispara cuando se remueve un plano AR.
        /// </summary>
        public struct PlaneRemovedEvent
        {
            public UnityEngine.XR.ARFoundation.ARPlane Plane;
        }

        // ========== Waypoint Events ==========
        
        /// <summary>
        /// Se dispara cuando se coloca un nuevo waypoint.
        /// </summary>
        public struct WaypointPlacedEvent
        {
            public string WaypointId;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        /// <summary>
        /// Se dispara cuando se configura un waypoint con nombre/tipo.
        /// </summary>
        public struct WaypointConfiguredEvent
        {
            public string WaypointId;
            public string WaypointName;
            public WaypointType Type;
            public Color Color;
        }

        /// <summary>
        /// Se dispara cuando se elimina un waypoint.
        /// </summary>
        public struct WaypointRemovedEvent
        {
            public string WaypointId;
        }

        // ========== Model Events ==========
        
        /// <summary>
        /// Se dispara cuando se carga un modelo 3D exitosamente.
        /// </summary>
        public struct ModelLoadedEvent
        {
            public GameObject ModelInstance;
            public string ModelName;
            public Vector3 Position;
        }

        /// <summary>
        /// Se dispara cuando falla la carga de un modelo.
        /// </summary>
        public struct ModelLoadFailedEvent
        {
            public string ModelName;
            public string ErrorMessage;
        }

        // ========== NavMesh Events ==========
        
        /// <summary>
        /// Se dispara cuando se genera o actualiza el NavMesh.
        /// </summary>
        public struct NavMeshGeneratedEvent
        {
            public int SurfaceCount;
            public float TotalArea;
            public bool Success;
        }

        /// <summary>
        /// Se dispara cuando falla la generación del NavMesh.
        /// </summary>
        public struct NavMeshGenerationFailedEvent
        {
            public string ErrorMessage;
        }

        // ========== Navigation Events ==========
        
        /// <summary>
        /// Se dispara cuando comienza la navegación hacia un destino.
        /// </summary>
        public struct NavigationStartedEvent
        {
            public string DestinationWaypointId;
            public Vector3 StartPosition;
            public Vector3 DestinationPosition;
            public float EstimatedDistance;
        }

        /// <summary>
        /// Se dispara cuando se completa la navegación.
        /// </summary>
        public struct NavigationCompletedEvent
        {
            public string DestinationWaypointId;
            public float TotalDistance;
            public float TotalTime;
        }

        /// <summary>
        /// Se dispara cuando se cancela la navegación.
        /// </summary>
        public struct NavigationCancelledEvent
        {
            public string Reason;
        }

        /// <summary>
        /// Se dispara cuando se actualiza el progreso de navegación.
        /// </summary>
        public struct NavigationProgressEvent
        {
            public float DistanceRemaining;
            public float ProgressPercent;
            public Vector3 CurrentPosition;
        }

        // ========== UI Events ==========
        
        /// <summary>
        /// Se dispara cuando cambia el modo de la aplicación.
        /// </summary>
        public struct AppModeChangedEvent
        {
            public AppMode PreviousMode;
            public AppMode NewMode;
        }

        /// <summary>
        /// Se dispara para mostrar mensajes al usuario.
        /// </summary>
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