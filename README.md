# COMPAS — Módulo de Navegación en Interiores Asistida por Realidad Aumentada

**Proyecto de Grado — Ingeniería de Sistemas**  
Universidad de San Buenaventura Cali  
Autores: Juan Jose Sanchez · Carlos Eduardo Rangel

---

## Tabla de Contenidos

1. [Introducción](#1-introducción)
2. [Arquitectura del módulo](#2-arquitectura-del-módulo)
3. [Tecnologías utilizadas](#3-tecnologías-utilizadas)
4. [Funcionamiento del sistema de navegación](#4-funcionamiento-del-sistema-de-navegación)
5. [Algoritmo de cálculo de rutas](#5-algoritmo-de-cálculo-de-rutas)
6. [Uso de Realidad Aumentada](#6-uso-de-realidad-aumentada)
7. [Lógica de funcionamiento del módulo](#7-lógica-de-funcionamiento-del-módulo)
8. [Integración con Flutter](#8-integración-con-flutter)
9. [Estructura del proyecto](#9-estructura-del-proyecto)
10. [Consideraciones técnicas](#10-consideraciones-técnicas)

---

## 1. Introducción

### 1.1 Propósito del módulo

Este repositorio contiene el módulo de navegación en interiores del sistema **COMPAS**, desarrollado como proyecto de grado en el programa de Ingeniería de Sistemas de la Universidad de San Buenaventura Cali. El módulo tiene como objetivo resolver un problema concreto de accesibilidad: orientar a una persona dentro de un edificio cuando no dispone de referencias visuales fiables o cuando requiere apoyo adicional para desplazarse con seguridad en espacios cerrados.

El módulo no constituye una solución independiente. Opera como componente especializado dentro de una aplicación móvil principal desarrollada en Flutter, a la cual provee capacidades de percepción espacial, cálculo de rutas navegables y renderizado de guías visuales en tiempo real sobre el entorno físico detectado por la cámara del dispositivo.

### 1.2 Descripción general del sistema

COMPAS implementa un sistema de navegación en interiores basado en Realidad Aumentada (AR). A través de la cámara del dispositivo móvil, el sistema detecta superficies físicas del entorno, construye sobre ellas una malla de navegación virtual y calcula trayectorias que guían al usuario hasta su destino mediante indicadores visuales superpuestos al mundo real.

La solución aborda la ausencia de GPS en entornos cerrados mediante el uso de tracking inercial y visual provisto por ARCore, combinado con un modelo tridimensional del edificio precargado. El resultado operativo es una guía paso a paso que informa al usuario sobre dirección, distancia y puntos de referencia, tanto de forma visual en la pantalla como mediante síntesis de voz gestionada desde la capa Flutter de la aplicación.

---

## 2. Arquitectura del módulo

### 2.1 Visión general

La arquitectura sigue un esquema de separación de responsabilidades entre dos capas de ejecución: Unity opera como motor de navegación AR y Flutter actúa como contenedor de la aplicación móvil y punto de interacción con el usuario.

```
┌─────────────────────────────────────────────────────────┐
│                  Aplicación Flutter                      │
│                                                         │
│  ┌─────────────────┐      ┌──────────────────────────┐  │
│  │  Interfaz de    │      │   Servicios de voz        │  │
│  │  usuario        │      │   STT · TTS · Wake Word   │  │
│  └────────┬────────┘      └─────────────┬────────────┘  │
│           │                             │               │
│           └──────────┬──────────────────┘               │
│                      │  JSON sobre canal nativo Android  │
│           ┌──────────▼──────────────────┐               │
│           │     UnityBridgeService      │               │
│           │    FlutterUnityBridge.cs    │               │
│           └──────────┬──────────────────┘               │
└──────────────────────┼──────────────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────────────┐
│                   Módulo Unity                           │
│                                                         │
│  ARSessionManager · ModelLoadManager · NavigationManager │
│  WaypointManager · PersistenceManager                   │
│  NavigationVoiceGuide · NavigationPathController        │
│  MultiLevelNavMeshGenerator · NavMeshSerializer         │
│  AROriginAligner · ARWorldOriginStabilizer              │
│  UserPositionBridge · NavMeshOriginCompensator          │
└─────────────────────────────────────────────────────────┘
```

### 2.2 Responsabilidades por capa

**Flutter** gestiona la experiencia de usuario: interfaz visual, reconocimiento de voz (STT), síntesis de voz (TTS), activación por palabra clave (*wake word*) y orquestación de comandos de navegación. Esta separación garantiza que las funciones de accesibilidad no estén acopladas a la escena tridimensional.

**Unity** gestiona la capa espacial: sesión AR, detección de superficies, carga del modelo del edificio, cálculo de rutas sobre NavMesh, seguimiento del agente de navegación, renderizado de guías visuales y persistencia del estado de la sesión.

### 2.3 Flujo general de funcionamiento

1. Flutter recibe un comando del usuario, ya sea táctil o por voz.
2. Flutter procesa la intención y envía el comando en formato JSON al módulo Unity.
3. Unity valida la instrucción, consulta los puntos de interés disponibles y solicita una ruta.
4. El sistema de navegación calcula la trayectoria sobre la malla NavMesh y activa el seguimiento.
5. Unity renderiza los indicadores AR y publica eventos de avance hacia Flutter.
6. Flutter transforma esos eventos en mensajes auditivos mediante TTS para el usuario.

### 2.4 Bus de eventos interno

La comunicación entre los subsistemas de Unity se realiza a través de un bus de eventos centralizado (`EventBus`). Este patrón desacopla los managers y controladores, evitando dependencias directas entre componentes. Los eventos principales gestionados por el bus incluyen: `ModelLoadedEvent`, `NavigationStartedEvent`, `NavigationCompletedEvent`, `NavigationCancelledEvent`, `GuideAnnouncementEvent` y `NavMeshGeneratedEvent`.

---

## 3. Tecnologías utilizadas

### 3.1 Unity

Unity es el entorno de desarrollo y ejecución del módulo AR. Provee el motor de renderizado en tiempo real, el sistema de físicas, el sistema de agentes de navegación (NavMesh), la gestión del ciclo de vida de la aplicación móvil y las herramientas de compilación para Android. La versión utilizada en este proyecto es **Unity 6000.2.14f1**.

### 3.2 AR Foundation

AR Foundation es la capa de abstracción de Realidad Aumentada de Unity. Permite trabajar con detección de planos, raycast espacial, gestión de anclajes (`ARAnchor`) y tracking de la posición del dispositivo sin necesidad de escribir código específico para cada proveedor nativo. En este módulo, AR Foundation es responsable de iniciar la sesión AR, detectar las superficies horizontales del entorno (suelos) y posicionar el contenido virtual alineado con el espacio físico.

Los componentes centrales de AR Foundation utilizados en el proyecto son: `ARSession`, `ARPlaneManager`, `ARAnchorManager` y `ARRaycastManager`, todos instanciados sobre el GameObject `XR Origin (Mobile AR)`.

### 3.3 ARCore

ARCore es el proveedor nativo de AR para la plataforma Android, utilizado como backend por AR Foundation. Proporciona el tracking visual-inercial (VIO) del dispositivo, que combina datos de la cámara con los sensores IMU (giroscopio y acelerómetro) para estimar la posición y orientación del usuario en tiempo real. ARCore también clasifica los planos detectados (suelo, techo, pared) mediante la propiedad `PlaneClassifications` de AR Foundation 6.x.

### 3.4 Flutter

Flutter es el framework de desarrollo de la aplicación móvil principal. Actúa como contenedor de la aplicación y provee la interfaz de usuario accesible, el reconocimiento de voz a texto (STT), la síntesis de voz (TTS) y la gestión de comandos hacia Unity. La integración de Unity dentro de Flutter se realiza mediante el paquete **flutter_unity_widget**, que carga el módulo Unity como una vista nativa de Android dentro de la jerarquía de widgets de Flutter.

### 3.5 C\#

C# es el lenguaje de implementación del módulo Unity. Se utiliza para modelar todas las entidades del sistema: managers de sesión AR, controladores de navegación, servicios de persistencia, el puente de comunicación con Flutter y la guía de voz. El módulo hace uso de patrones de diseño como Singleton, Observer (vía EventBus) y Service Layer para mantener la coherencia arquitectónica.

### 3.6 Paquetes y librerías adicionales

| Paquete | Versión | Propósito |
|---|---|---|
| AR Foundation | 6.2.x | Abstracción AR multiplataforma |
| ARCore XR Plugin | 6.2.x | Backend nativo Android para tracking |
| AI Navigation | Unity 6 | NavMesh, agentes y NavMeshLink para conexión multinivel |
| XR Interaction Toolkit | Unity 6 | Herramientas de interacción XR |
| Unity Input System | Unity 6 | Gestión de entradas del dispositivo |
| TextMeshPro | Unity 6 | Renderizado de texto en UI y espacio 3D |
| flutter_unity_widget | Última estable | Embebido de Unity dentro de Flutter |

---

## 4. Funcionamiento del sistema de navegación

### 4.1 Representación del entorno interior

El entorno interior se representa en una escena Unity que combina un modelo tridimensional del edificio con una malla de navegación generada en tiempo de ejecución. El modelo del edificio es un archivo 3D (formato GLTF o equivalente) cargado dinámicamente desde `StreamingAssets`, que define la geometría de paredes, pisos y escaleras.

Sobre la geometría del modelo, el componente `MultiLevelNavMeshGenerator` analiza la nube de vértices, detecta clústeres de altura que corresponden a diferentes pisos y construye superficies navegables planas para cada nivel. Estas superficies son bakeadas con el sistema **AI Navigation** de Unity para generar un NavMesh multinivel, que incluye rampas y escaleras como segmentos conectores entre pisos mediante `NavMeshLink`.

El NavMesh resultante puede serializarse a disco mediante `NavMeshSerializer`, lo que permite su restauración en sesiones posteriores sin necesidad de repetir el proceso de análisis y baking.

### 4.2 Puntos de interés (waypoints)

Los puntos de interés se denominan *waypoints* y son gestionados por el componente `WaypointManager`. Cada waypoint contiene: identificador único, nombre, tipo, posición en el espacio 3D y configuración visual. Los waypoints pueden crearse durante la ejecución (cuando el usuario guarda una ubicación de interés) y se persisten en disco junto con el estado de la sesión mediante `PersistenceManager`.

El componente `NavigationStartPointManager` gestiona un subconjunto especial de puntos denominados *start points*, que definen la posición de referencia del usuario al inicio de cada piso. Estos puntos son fundamentales para la alineación del modelo virtual con el espacio físico real.

### 4.3 Alineación del mundo AR

El componente `AROriginAligner` es responsable de sincronizar el origen del sistema de coordenadas de Unity con el espacio físico detectado por ARCore. Cuando se detecta un plano de suelo, el sistema posiciona el `XR Origin` de modo que el modelo del edificio quede alineado con la posición real del usuario. Durante la navegación activa, `AROriginAligner` sincroniza continuamente la posición del agente virtual con la posición de la cámara XR del dispositivo.

---

## 5. Algoritmo de cálculo de rutas

### 5.1 Grafo de navegación (NavMesh)

El cálculo de rutas se basa en el sistema NavMesh de Unity, que representa el espacio navegable del edificio como un grafo de polígonos convexos. Internamente, Unity aplica el algoritmo **A\*** sobre esta malla de polígonos para encontrar el camino óptimo entre la posición del agente y el destino seleccionado. El NavMesh garantiza que todas las rutas calculadas eviten obstáculos estáticos (paredes, muebles) y respeten los parámetros del agente (radio, altura y pendiente máxima).

### 5.2 Optimización de la trayectoria

La ruta devuelta por el NavMesh es una secuencia de esquinas o puntos de control (*corners*). Sobre esta ruta base, `NavigationPathController` aplica una fase de optimización que filtra puntos redundantes, suaviza el recorrido y proyecta los puntos de control sobre la superficie con mayor holgura lateral. El resultado es una ruta refinada (`OptimizedPath`) más adecuada para la navegación peatonal asistida.

Adicionalmente, `NavigationVoiceGuide` aplica una subdivisión de segmentos largos para garantizar una densidad mínima de puntos de evaluación a lo largo del trayecto, lo que permite detectar con mayor precisión el momento en que el usuario debe girar.

### 5.3 Generación de instrucciones de navegación

Las instrucciones de navegación se generan a partir del análisis geométrico de la ruta optimizada. Para cada punto de inflexión de la trayectoria, el sistema calcula el ángulo de giro relativo a la orientación actual del usuario mediante el método `SignedAngleXZ`. El ángulo resultante se clasifica en una de las siguientes categorías:

- **GoStraight**: deflexión menor a 20°
- **SlightLeft / SlightRight**: deflexión entre 20° y 50°
- **TurnLeft / TurnRight**: deflexión entre 50° y 140°
- **UTurn**: deflexión mayor a 140°

Las instrucciones se expresan en lenguaje natural con referencias de posición de reloj (*"a las 3", "a las 9"*), lo que facilita la comprensión sin depender de referencias cardinales (norte, sur, este, oeste). Eventos especiales como la proximidad a escaleras, la detección de que el usuario se ha detenido o desviado, y la confirmación de llegada al destino, se gestionan como eventos discretos con su propia lógica de umbral y temporización.

### 5.4 Control de seguimiento y recálculo

Durante la navegación activa, el sistema evalúa la posición del usuario en cada intervalo de 100ms. Si la desviación lateral respecto a la ruta supera un umbral configurable, se solicita un recálculo del camino. El recálculo también se activa automáticamente ante eventos de recuperación del tracking AR (VIO recovery), garantizando que la ruta siempre parta de la posición real del usuario.

---

## 6. Uso de Realidad Aumentada

### 6.1 Detección del entorno

AR Foundation, a través del backend ARCore, analiza continuamente el flujo de imágenes de la cámara del dispositivo junto con los datos del IMU para construir una representación del entorno físico. El componente `ARPlaneManager` detecta y clasifica superficies planas en el espacio. En este proyecto se utiliza la clasificación `PlaneClassifications.Floor` de AR Foundation 6.x para identificar exclusivamente los planos de suelo y evitar que techos u otras superficies horizontales sean tomados como referencia de posicionamiento.

### 6.2 Anclaje espacial

El posicionamiento del contenido virtual se realiza mediante `ARAnchorManager`. Un anclaje AR es un punto de referencia persistente en el espacio físico al que se adjunta el modelo del edificio. El componente `ARWorldOriginStabilizer` monitorea la deriva del anclaje a lo largo del tiempo y recaptura la referencia cuando la sesión AR se recupera tras una pérdida de tracking, garantizando que el modelo no derive respecto a la posición real del usuario.

### 6.3 Tracking visual-inercial (VIO)

El sistema de tracking de ARCore puede perder la referencia espacial ante condiciones adversas (movimiento excesivo, iluminación deficiente, superficies sin textura). Cuando esto ocurre, el componente `AROriginAligner` congela la posición del agente de navegación y espera a que ARCore complete la relocalización. Una vez recuperado el tracking, el sistema realinea el origen del mundo virtual con el espacio físico real antes de reanudar la guía de navegación.

La causa de la pérdida de tracking (`NotTrackingReason`) se propaga hacia Flutter, donde se muestra al usuario un mensaje contextual (por ejemplo: *"Movimiento muy rápido — mueve el dispositivo más despacio"*).

### 6.4 Visualización de la guía de navegación

Los elementos de guía se posicionan como objetos virtuales en las coordenadas del mundo AR: marcadores de waypoint, indicadores de dirección y puntos de control de la trayectoria activa. Estos elementos se actualizan en tiempo real conforme el usuario avanza por el recorrido. El componente `ARGuideController` administra el ciclo de vida de estos elementos visuales y los adapta según el modo de operación del sistema (Full AR o modo sin AR activo).

---

## 7. Lógica de funcionamiento del módulo

El ciclo operativo completo del módulo sigue la siguiente secuencia:

### 7.1 Inicialización

Al cargar la escena, `NavigationManager` coordina la inicialización del sistema. En primer lugar se valida la disponibilidad de ARCore mediante `ARCapabilityDetector`. Si existe una sesión guardada en disco (modelo + NavMesh), el sistema procede a una restauración rápida. En caso contrario, inicia el flujo completo de detección de planos y carga del modelo.

### 7.2 Carga del entorno y alineación

`ModelLoadManager` carga el modelo del edificio sobre el plano de suelo detectado más grande disponible. Una vez posicionado, `AROriginAligner` alinea el `XR Origin` de Unity con el punto de inicio del nivel 0, de modo que el espacio virtual y el físico queden referenciados entre sí.

`MultiLevelNavMeshGenerator` analiza la geometría del modelo cargado, detecta los pisos y genera el NavMesh multinivel. El resultado se serializa en disco para su reutilización en sesiones futuras.

### 7.3 Disponibilización de waypoints y selección de destino

`WaypointManager` expone los puntos de interés disponibles. Cuando Flutter solicita la lista de waypoints, Unity responde con un array JSON que incluye nombre y posición de cada punto. El usuario selecciona el destino mediante voz o interfaz táctil en Flutter, que envía el comando `navigate_to_waypoint` al módulo Unity.

### 7.4 Cálculo de ruta y preparación del agente

Al recibir el comando de navegación, `NavigationManager` sincroniza la posición del agente virtual con la posición actual de la cámara XR (`ForceSnapAgentToCamera`), activa el modo Full AR en `NavigationPathController` (que impide el movimiento autónomo del agente) y solicita el cálculo de ruta al sistema NavMesh mediante `NavigationAgent.NavigateToWaypoint`.

### 7.5 Guía de navegación activa

`NavigationVoiceGuide` recibe la ruta optimizada, construye la secuencia de instrucciones y comienza a evaluarlas en cada ciclo de actualización. Las instrucciones de giro, alertas de escaleras y confirmaciones de llegada se publican como `GuideAnnouncementEvent` en el bus de eventos. `VoiceCommandAPI` recibe estos eventos y los reenvía hacia Flutter como mensajes JSON con la acción `guide_announcement`.

Flutter procesa estos mensajes a través de `VoiceNavigationService`, que los encola con sistema de prioridades y los reproduce mediante el servicio TTS del dispositivo.

### 7.6 Finalización

La navegación concluye cuando el usuario llega al destino (distancia menor al umbral `arrivalTriggerDist`), cuando el usuario emite el comando de parada, o cuando ocurre un error de ruta irrecuperable. En cualquiera de estos casos, `NavigationManager` publica `NavigationCompletedEvent` o `NavigationCancelledEvent` y el sistema regresa al estado de espera.

---

## 8. Integración con Flutter

### 8.1 Modelo de integración

La integración entre Flutter y Unity se implementa con el patrón **Unity as a Library** en Android. Flutter conserva el control de la aplicación como proceso principal, y el módulo Unity se carga como una vista nativa Android (`UnityWidget`) dentro de la jerarquía de widgets de Flutter mediante el paquete `flutter_unity_widget`.

### 8.2 Canal de comunicación

El intercambio de mensajes entre ambas capas se realiza mediante el mecanismo `UnitySendMessage` del canal nativo Android, con un contrato de mensajería basado en JSON.

**Flutter → Unity** (comandos): Flutter envía objetos JSON al GameObject `FlutterBridge` de la escena Unity. El componente `FlutterUnityBridge.cs` recibe y deserializa estos mensajes, delegando su ejecución a `VoiceCommandAPI`.

**Unity → Flutter** (respuestas y eventos): Unity publica respuestas al canal Flutter mediante `UnityBridgeService.OnUnityResponse`. Flutter deserializa las respuestas y las distribuye a los servicios correspondientes.

### 8.3 Acciones del contrato de mensajería

| Acción | Dirección | Descripción |
|---|---|---|
| `navigate_to_waypoint` | Flutter → Unity | Inicia navegación hacia un destino |
| `add_waypoint` | Flutter → Unity | Crea un nuevo waypoint en la posición actual |
| `clear_waypoints` | Flutter → Unity | Elimina todos los waypoints |
| `save_session` | Flutter → Unity | Persiste el estado de la sesión en disco |
| `load_session` | Flutter → Unity | Restaura el estado de una sesión guardada |
| `list_waypoints` | Flutter → Unity | Solicita la lista de waypoints disponibles |
| `guide_announcement` | Unity → Flutter | Publica una instrucción de navegación para TTS |
| `navigation_arrived` | Unity → Flutter | Notifica la llegada al destino |
| `tracking_state` | Unity → Flutter | Informa el estado del tracking AR y su causa |

### 8.4 Consideraciones de sincronización

Dado que Unity y Flutter operan en hilos distintos, todos los mensajes del puente se procesan en el hilo principal de Unity mediante callbacks del ciclo de vida del `UnityWidget`. El servicio `VoiceNavigationService` en Flutter implementa una cola de prioridad para garantizar que las instrucciones de mayor urgencia (giros, escaleras, obstáculos) interrumpan a las de menor prioridad (recordatorios de progreso), evitando la saturación del TTS.

---

## 9. Estructura del proyecto

```
IndoorNavAR/
│
├── Assets/
│   └── IndoorNavAR/
│       ├── Scripts/
│       │   ├── AR/                     # Sesión AR, alineación de origen, detección de planos
│       │   │   ├── ARSessionManager.cs
│       │   │   ├── AROriginAligner.cs
│       │   │   ├── ARWorldOriginStabilizer.cs
│       │   │   └── ARCapabilityDetector.cs
│       │   │
│       │   ├── Core/
│       │   │   ├── Managers/           # NavigationManager, WaypointManager, ModelLoadManager
│       │   │   ├── Controllers/        # PlacementController, ARGuideController
│       │   │   ├── Data/               # WaypointData, modelos de datos
│       │   │   └── Events/             # EventBus, definiciones de eventos
│       │   │
│       │   ├── Navigation/
│       │   │   ├── NavigationAgent.cs
│       │   │   ├── NavigationPathController.cs
│       │   │   ├── UserPositionBridge.cs
│       │   │   ├── NavMeshOriginCompensator.cs
│       │   │   ├── NavMeshSerializer.cs
│       │   │   ├── MultiLevelNavMeshGenerator.cs
│       │   │   ├── NavMeshSurfaceService.cs
│       │   │   └── Voice/
│       │   │       └── NavigationVoiceGuide.cs
│       │   │
│       │   ├── Integration/            # Puente Flutter ↔ Unity
│       │   │   ├── FlutterUnityBridge.cs
│       │   │   └── VoiceCommandAPI.cs
│       │   │
│       │   └── Persistence/
│       │       └── PersistenceManager.cs
│       │
│       ├── Materials/                  # Materiales y shaders de guía visual
│       ├── Prefabs/                    # Prefabs de waypoints, guías y agentes
│       └── StreamingAssets/           # Modelos 3D del edificio (cargados en runtime)
│
├── Assets/Scenes/
│   └── Navegacion.unity               # Escena principal del módulo
│
├── Packages/
│   ├── manifest.json                  # Dependencias del proyecto Unity
│   └── packages-lock.json
│
├── ProjectSettings/                   # Configuración del proyecto Unity y XR
│
└── docs/                              # Documentación técnica complementaria
    └── flutter-unity-integration.md
```

---

## 10. Consideraciones técnicas

### 10.1 Limitaciones

**Dependencia del tracking AR.** La precisión del sistema está directamente condicionada por la calidad del tracking visual-inercial de ARCore. Ambientes con iluminación deficiente, superficies sin textura visual (paredes blancas lisas, suelos uniformes) o alto tráfico de personas pueden degradar la estabilidad del anclaje espacial y producir instrucciones de navegación inexactas.

**Ausencia de GPS en interiores.** El sistema no dispone de referencia de posición absoluta. La calidad del modelo tridimensional del edificio y la precisión del NavMesh derivado son factores críticos. Un modelo con geometría incorrecta o sin cobertura NavMesh en determinadas áreas generará rutas inválidas o parciales.

**Contrato de mensajería sin versionado formal.** La comunicación entre Flutter y Unity se realiza mediante un contrato de mensajes JSON ad hoc. Cualquier modificación en los nombres de acciones o en la estructura de los payloads requiere actualización coordinada en ambas capas. Se recomienda incorporar un campo de versión de protocolo para sesiones de producción.

**Limitaciones de hardware de gama baja.** El renderizado de la escena AR en tiempo real, combinado con el cálculo de NavMesh y la síntesis de voz, puede degradar el rendimiento en dispositivos de gama baja, afectando la estabilidad del tracking y la latencia de las instrucciones.

### 10.2 Requisitos de hardware

- Dispositivo móvil Android compatible con **ARCore** (se requiere soporte ARCore certificado por Google).
- Cámara trasera con autofoco funcional.
- Sensores inerciales operativos (giroscopio y acelerómetro).
- Capacidad gráfica suficiente para renderizar escena 3D en tiempo real. Se recomienda gama media-alta para mantener estabilidad de tracking a 30fps o superior.
- Mínimo 3 GB de RAM disponible para la aplicación en ejecución.

### 10.3 Dependencias de software

| Dependencia | Versión | Notas |
|---|---|---|
| Unity | 6000.2.14f1 | Editor y runtime del módulo |
| AR Foundation | 6.2.x | Paquete del registry de Unity |
| ARCore XR Plugin | 6.2.x | Backend Android; se instala vía Package Manager |
| AI Navigation | Unity 6 | Requiere habilitación explícita en Package Manager |
| XR Interaction Toolkit | Unity 6 | Configuración de XR en Project Settings |
| Android SDK | API 33+ | Compatible con configuración del módulo host |
| flutter_unity_widget | Última estable | Paquete pub.dev para integración Flutter |
| Android NDK | r23c o superior | Requerido para compilación Unity as a Library |

### 10.4 Ejecución y validación básica

Para ejecutar el módulo de forma local en el editor de Unity:

1. Abrir el repositorio en Unity **6000.2.14f1**.
2. Verificar la resolución de paquetes desde *Window → Package Manager*.
3. Abrir la escena principal en `Assets/Scenes/Navegacion.unity`.
4. Ejecutar pruebas funcionales en el editor para validar el flujo interno de navegación y persistencia.
5. Compilar para dispositivo Android mediante *File → Build Settings → Android → Build and Run*.

Para validar la integración con Flutter, se recomienda una prueba de extremo a extremo: enviar el comando `navigate_to_waypoint` desde la interfaz Flutter y confirmar en Unity el inicio de la ruta; posteriormente, verificar la locución TTS del estado de avance en el dispositivo.

---

*Módulo desarrollado como proyecto de grado en Ingeniería de Sistemas e Ingenieria Mulimedia— Universidad de San Buenaventura Cali.*