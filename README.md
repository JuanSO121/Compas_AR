# COMPAS AR (Unity)

> Módulo Unity de navegación en interiores asistida por realidad aumentada para el sistema COMPAS. Gestiona la sesión AR, el modelo tridimensional del entorno, el cálculo de rutas sobre NavMesh multinivel, la guía de voz contextual, la segmentación semántica en tiempo real y la persistencia de sesión.

**Repositorio:** https://github.com/JuanSO121/Compas_AR  
**Autores:** Juan José Sánchez Ocampo · Carlos Eduardo Rangel  
**Institución:** Universidad de San Buenaventura Cali — Ingeniería de Sistemas e Ingeniería Multimedia, 2026

---

## Tabla de contenido

- [Resumen ejecutivo](#resumen-ejecutivo)
- [Arquitectura del módulo](#arquitectura-del-módulo)
- [Tecnologías utilizadas](#tecnologías-utilizadas)
- [Sistema de navegación](#sistema-de-navegación)
- [Guía de voz](#guía-de-voz)
- [Segmentación semántica](#segmentación-semántica)
- [Persistencia de sesión](#persistencia-de-sesión)
- [Integración con Flutter](#integración-con-flutter)
- [Estructura del proyecto](#estructura-del-proyecto)
- [Instalación y ejecución](#instalación-y-ejecución)
- [Limitaciones técnicas](#limitaciones-técnicas)
- [Repositorios relacionados](#repositorios-relacionados)

---

## Resumen ejecutivo

COMPAS AR es el módulo de navegación espacial del sistema COMPAS. Opera como componente especializado embebido dentro de la aplicación Flutter mediante el patrón Unity as a Library. Provee capacidades de percepción espacial (ARCore + AR Foundation), cálculo de rutas navegables sobre NavMesh multinivel con algoritmo A*, guía de voz contextual con instrucciones en lenguaje natural, detección de obstáculos en tiempo real mediante segmentación semántica (DeepLabV3+ con MobileNetV2 vía Unity Sentis) y persistencia de sesión entre usos.

El módulo fue desarrollado y validado sobre la biblioteca universitaria de la Universidad de San Buenaventura Cali como entorno de prueba.

### Funcionalidades implementadas

- Gestión de sesión AR con detección de planos y anclaje mediante `ARAnchorManager` nativo.
- Carga y alineación de modelo tridimensional del edificio (`.obj`) con el entorno físico real.
- Generación de NavMesh multinivel sobre la geometría del modelo con soporte para escaleras mediante `NavMeshLink`.
- Cálculo de rutas con algoritmo A* sobre NavMesh y optimización de trayectoria.
- Guía de voz contextual con instrucciones de giro en formato reloj, alertas de escaleras, detección de parada y desviación.
- Segmentación semántica en tiempo real (clases: fondo, piso, obstáculo, pared) con alerta auditiva ante obstáculos.
- Persistencia de sesión: NavMesh, waypoints y configuración en disco entre sesiones.
- Puente bidireccional Flutter ↔ Unity con cola de comandos por prioridad y stability check de ARSession.
- Gestión de waypoints por comandos de voz: crear, listar, eliminar y limpiar puntos de interés.

---

## Arquitectura del módulo

```
┌─────────────────────────────────────────────────────┐
│                 Aplicación Flutter                   │
│    UnityBridgeService ↔ FlutterUnityBridge.cs       │
└──────────────────────┬──────────────────────────────┘
                       │ JSON (canal nativo Android)
┌──────────────────────▼──────────────────────────────┐
│                  Módulo Unity                        │
│                                                     │
│  FlutterUnityBridge → VoiceCommandAPI               │
│         ↓                      ↓                   │
│  NavigationManager       WaypointManager            │
│         ↓                      ↓                   │
│  NavigationAgent         PersistenceManager         │
│         ↓                                          │
│  NavigationPathController → NavigationVoiceGuide    │
│         ↓                                          │
│  MultiLevelNavMeshGenerator                        │
│         ↓                                          │
│  AROriginAligner → ARCore (VIO)                    │
│         ↓                                          │
│  ObstacleSegmentationWorker (Unity Sentis)         │
└─────────────────────────────────────────────────────┘
```

### Bus de eventos interno

La comunicación entre subsistemas usa un `EventBus` centralizado que desacopla los managers y controladores. Eventos principales: `NavigationStartedEvent`, `NavigationCompletedEvent`, `NavigationCancelledEvent`, `GuideAnnouncementEvent`, `FloorTransitionEvent`, `ObstacleDetectedEvent`, `TTSRequestEvent`.

---

## Tecnologías utilizadas

| Tecnología | Versión | Rol |
|-----------|---------|-----|
| Unity | 6000.2.14f1 | Motor de ejecución AR y navegación |
| AR Foundation | 6.2.x | Abstracción AR: planos, raycast, anclajes |
| ARCore XR Plugin | 6.2.x | Backend nativo Android para VIO |
| AI Navigation | Unity 6 | NavMesh, agentes y NavMeshLink multinivel |
| Unity Sentis | 2.1.x | Inferencia local del modelo de segmentación |
| flutter_unity_widget | Última estable | Embebido Unity dentro de Flutter |
| C# | — | Lenguaje de implementación del módulo |

---

## Sistema de navegación

### Representación del entorno

El entorno se representa combinando un modelo tridimensional del edificio (`.obj`, escaneado con Meta Quest 3) con una NavMesh generada sobre su geometría. `MultiLevelNavMeshGenerator` detecta clústeres de altura por piso y genera superficies navegables conectadas mediante `NavMeshLink` para escaleras.

La NavMesh se serializa en disco (`navmesh_unified.bin` + `navmesh_header.json`) para restauración en sesiones posteriores sin repetir el proceso de baking.

### Cálculo de rutas

El sistema usa el algoritmo A* de Unity sobre la NavMesh poligonal. La función de costo es:

```
f(n) = g(n) + h(n)
```

Donde `g(n)` es el costo acumulado desde el origen y `h(n)` es la distancia euclidiana al destino. Sobre la ruta base, `NavigationPathController` aplica optimización que filtra puntos redundantes y suaviza el recorrido.

### Alineación VIO

`AROriginAligner` sincroniza el origen de Unity con el espacio físico mediante odometría visual-inercial de ARCore. Implementa filtro de flickers (pérdidas de tracking menores a 500ms por CPU starvation se ignoran) y stability check antes de iniciar navegación.

---

## Guía de voz

`NavigationVoiceGuide` genera instrucciones en lenguaje natural a partir del análisis geométrico de la ruta optimizada. Las instrucciones se clasifican por ángulo de giro relativo:

| Tipo | Rango angular |
|------|--------------|
| GoStraight | < 20° |
| SlightLeft / SlightRight | 20° – 50° |
| TurnLeft / TurnRight | 50° – 140° |
| UTurn | > 140° |

Las direcciones se expresan en formato reloj ("a las 3", "a las 9") para facilitar la comprensión sin referencias cardinales. El sistema detecta parada del usuario, desviación de ruta, proximidad a escaleras y llegada al destino como eventos discretos con umbrales configurables.

Las instrucciones se publican como `TTSRequestEvent` con sistema de prioridades (0–3). Las de prioridad 3 (obstáculos, giros urgentes) interrumpen las de menor prioridad. El timeout de `_ttsBusy` es de 8 segundos como fallback ante confirmaciones perdidas de Flutter.

---

## Segmentación semántica

El modelo DeepLabV3+ con backbone MobileNetV2 se ejecuta localmente en el dispositivo mediante Unity Sentis. Clasifica cada pixel de los frames capturados en cuatro clases:

| Clase | Índice |
|-------|--------|
| Fondo | 0 |
| Piso | 1 |
| Obstáculo | 2 |
| Pared | 3 |

| Métrica | Valor |
|---------|-------|
| Dataset de entrenamiento | 698 imágenes |
| MeanIoU (validación) | 0.8096 |
| DiceScore (validación) | 0.8903 |
| Formato de despliegue | ONNX → ModelAsset Unity Sentis |
| Backend de inferencia | GPU (fallback CPU) |

Cuando se detectan píxeles de clase obstáculo con confianza suficiente, el sistema genera una alerta auditiva inmediata y puede recalcular la ruta mediante `ObstacleRerouteMediator`.

---

## Persistencia de sesión

`PersistenceManager` gestiona la persistencia de tres componentes:

- **NavMesh:** serializado en binario con `NavMeshSerializer` (v7.1), preservando rampas y geometría multinivel sin duplicados.
- **Waypoints:** serializados en espacio local del modelo (`hasLocalSpace=true`) para mantener posición relativa independientemente de correcciones VIO posteriores.
- **Sesión JSON:** metadatos del modelo, timestamp y flags de estado.

La restauración sigue el orden: cargar NavMesh → restaurar modelo → esperar alineación VIO (`AROriginAligner.InitialAlignDone`) → re-anclar waypoints en espacio local → notificar a Flutter vía `FlutterUnityBridge.NotifySceneReady()`.

---

## Integración con Flutter

### Estados del bridge

El bridge (`FlutterUnityBridge`) transita por tres estados:

```
Initializing → SessionLoading → Ready
```

Los comandos de navegación se encolan durante la inicialización y se procesan al alcanzar `Ready`. El comando `navigate_to` incluye stability check que espera hasta 5 segundos a que ARSession alcance `SessionTracking` estable.

### Contrato de mensajería (Flutter → Unity)

| Acción | Descripción |
|--------|-------------|
| `ping_scene` | Consulta estado del bridge |
| `navigate_to` | Inicia navegación con stability check de ARSession |
| `stop_navigation` | Detiene navegación activa |
| `list_waypoints` | Lista waypoints disponibles |
| `create_waypoint` | Crea waypoint en posición actual del agente |
| `remove_waypoint` | Elimina waypoint por nombre |
| `clear_waypoints` | Elimina todos los waypoints |
| `save_session` | Persiste sesión en disco |
| `load_session` | Restaura sesión guardada |
| `tts_status` | Notifica estado TTS (done/cancel) |
| `repeat_instruction` | Repite última instrucción de navegación |
| `segmentation_ratio` | Consulta ratios de segmentación actuales |
| `toggle_seg_mask` | Activa/desactiva overlay de segmentación |

### Respuestas principales (Unity → Flutter)

| Acción | Descripción |
|--------|-------------|
| `scene_ready` | Bridge listo para recibir comandos |
| `session_loaded` | Sesión restaurada con conteo de waypoints |
| `guide_announcement` | Instrucción de navegación para TTS |
| `tracking_state` | Estado del tracking AR y causa |
| `navigation_completed` | Usuario llegó al destino |

---

## Estructura del proyecto

```
Assets/IndoorNavAR/
├── Scripts/
│   ├── AR/
│   │   ├── ARSessionManager.cs
│   │   ├── AROriginAligner.cs
│   │   └── ARCapabilityDetector.cs
│   ├── Core/
│   │   ├── Managers/
│   │   │   ├── NavigationManager.cs
│   │   │   ├── WaypointManager.cs
│   │   │   └── ModelLoadManager.cs
│   │   ├── Data/
│   │   │   └── WaypointData.cs
│   │   └── Events/
│   │       └── EventBus.cs
│   ├── Navigation/
│   │   ├── NavigationAgent.cs
│   │   ├── NavigationPathController.cs
│   │   ├── MultiLevelNavMeshGenerator.cs
│   │   ├── NavMeshSerializer.cs
│   │   ├── ObstacleRerouteMediator.cs
│   │   └── Voice/
│   │       └── NavigationVoiceGuide.cs
│   ├── Integration/
│   │   ├── FlutterUnityBridge.cs
│   │   └── VoiceCommandAPI.cs
│   ├── Segmentation/
│   │   └── ObstacleSegmentationWorker.cs
│   └── Core/
│       └── PersistenceManager.cs
├── Prefabs/
├── StreamingAssets/
└── Scenes/
    └── Navegacion.unity
```

---

## Instalación y ejecución

```bash
# 1. Abrir proyecto en Unity 6000.2.14f1
# 2. Verificar paquetes en Packages/manifest.json
# 3. Abrir Assets/Scenes/Navegacion.unity
# 4. Para pruebas lógicas: ejecutar en editor
# 5. Para pruebas AR: compilar a Android
#    File → Build Settings → Android → Build and Run
```

### Requisitos de hardware

| Requisito | Especificación |
|-----------|---------------|
| SO | Android compatible con ARCore |
| Cámara | Trasera con autofoco |
| Sensores | Giroscopio y acelerómetro funcionales |
| RAM | Mínimo 3 GB disponibles |
| Gama | Media-alta recomendada (tracking estable a 30fps) |

### Requisitos de software

| Software | Versión |
|----------|---------|
| Unity | 6000.2.14f1 |
| Android SDK | API 26+ |
| Android NDK | r23c o superior |

---

## Limitaciones técnicas

- La precisión del sistema depende directamente de la calidad del tracking de ARCore. Ambientes con iluminación deficiente o superficies sin textura degradan la estabilidad del anclaje espacial.
- No existe posicionamiento absoluto indoor. La calidad del modelo tridimensional y del NavMesh son factores críticos para la corrección de las rutas generadas.
- El modelo de segmentación semántica alcanza un meanIoU de 0.8096 sobre el conjunto de validación. Se requiere un dataset más robusto para uso en condiciones reales de seguridad con usuarios.
- El rendimiento puede degradarse en dispositivos de gama baja, afectando la latencia de instrucciones y la estabilidad del tracking.

---

## Repositorios relacionados

| Módulo | Repositorio |
|--------|------------|
| Aplicación móvil (Flutter) | https://github.com/JuanSO121/compas-client-mobile |
| Backend (API REST) | https://github.com/JuanSO121/compas-api |