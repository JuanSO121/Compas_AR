# Compas AR · IndoorNavAR

Sistema de navegación asistida para interiores, orientado a **personas con discapacidad visual**, desarrollado como proyecto de grado en la **Universidad de San Buenaventura Cali**.

Este repositorio corresponde al módulo de realidad aumentada y navegación en interiores (**IndoorNavAR**), parte del ecosistema **Compas**. Su objetivo es permitir desplazamientos más seguros dentro de edificios mediante:

- detección y alineación del entorno en AR,
- creación y gestión de puntos de interés (waypoints),
- generación de rutas navegables sobre NavMesh multinivel,
- persistencia de sesiones de navegación.

> Próximamente, este módulo se integrará con un componente de reconocimiento de obstáculos para recálculo dinámico de rutas y mejora de seguridad en movilidad.

---

## Autores

- **Juan Jose Sanchez**
- **Carlos Eduardo Rangel**

---

## Contexto del proyecto

El proyecto busca apoyar la movilidad autónoma en interiores aprovechando tecnologías de AR y planificación de rutas. A nivel técnico, IndoorNavAR combina:

- **AR Foundation / ARCore** para reconocimiento de superficies y anclaje espacial.
- **Unity AI Navigation (NavMesh)** para cálculo y seguimiento de rutas.
- Una arquitectura modular en C# para separar responsabilidades de AR, navegación, UI, eventos y persistencia.

---

## Arquitectura funcional (IndoorNavAR)

La carpeta principal del sistema es:

- `Assets/IndoorNavAR`

Y su estructura lógica se organiza así:

### 1) Módulo AR

Se encarga de iniciar la sesión de realidad aumentada, detectar planos y soportar raycasts/anclas:

- `Assets/IndoorNavAR/Scripts/AR/ARSessionManager.cs`
- `Assets/IndoorNavAR/AR/AROriginAligner.cs`

**Qué hace:**

- configura detección de planos horizontales/verticales,
- mantiene inventario de planos detectados,
- permite hacer raycast a superficies,
- crea y elimina anclas AR para fijar contenido virtual al entorno.

### 2) Módulo de navegación

Responsable de generar y seguir rutas sobre NavMesh, incluyendo escenarios multinivel:

- `Assets/IndoorNavAR/Scripts/Navigation/NavigationPathController.cs`
- `Assets/IndoorNavAR/Scripts/Navigation/NavigationPathOptimizer.cs`
- `Assets/IndoorNavAR/Scripts/Navigation/MultiLevelNavMeshGenerator.cs`
- `Assets/IndoorNavAR/Scripts/Navigation/NavMeshAgentCoordinator.cs`
- `Assets/IndoorNavAR/Scripts/Navigation/SecondFloorOpeningGenerator.cs`
- `Assets/IndoorNavAR/Scripts/Navigation/StairWithLandingHelper.cs`

**Qué hace:**

- calcula rutas optimizadas,
- evita falsas llegadas y recálculos agresivos,
- maneja seguimiento por waypoints,
- soporta tránsito entre niveles (escaleras/rampas),
- coordina el agente de navegación y el proceso de bake/carga de malla navegable.

### 3) Módulo de waypoints y datos

Permite crear, editar, remover, serializar y cargar puntos de referencia:

- `Assets/IndoorNavAR/Scripts/Core/Managers/WaypointManager.cs`
- `Assets/IndoorNavAR/Scripts/Core/Data/WaypointData.cs`

**Qué hace:**

- administra catálogo de waypoints,
- facilita búsquedas por tipo, nombre o proximidad,
- serializa datos para guardado de sesión,
- publica eventos para que la UI se mantenga sincronizada.

### 4) Módulo de persistencia

Gestiona guardado/carga de sesión (waypoints + estado de navegación/NavMesh):

- `Assets/IndoorNavAR/Core/PersistenceManager.cs`
- `Assets/IndoorNavAR/Scripts/Navigation/NavMeshSerializer.cs`

**Qué hace:**

- guarda sesión en `navigation_session.json` (ruta persistente de la app),
- conserva/recupera archivos de NavMesh por niveles,
- restaura estado de waypoints y configuración relevante,
- permite auto-guardado configurable.

### 5) Interfaz de usuario móvil

Interacción principal para usuarios en dispositivo Android:

- `Assets/IndoorNavAR/AR/MobileNavigationUI.cs`

**Qué hace:**

- lista y búsqueda de waypoints,
- panel de navegación activa (distancia/ETA/progreso),
- acciones rápidas (agregar, recalcular, cancelar),
- diseño responsivo y comportamiento adaptativo por pantalla.

### 6) Sistema de eventos

Comunicación desacoplada entre módulos:

- `Assets/IndoorNavAR/Scripts/Core/Events/EventBus.cs`

**Qué aporta:**

- reduce acoplamiento entre managers, UI y navegación,
- facilita escalabilidad del proyecto,
- simplifica integración futura con módulo de reconocimiento de obstáculos.

---

## Requisitos técnicos

- **Unity Editor:** `6000.2.14f1` (Unity 6)
- **Plataforma objetivo principal:** Android (ARCore)
- **Paquetes clave:**
  - `com.unity.xr.arcore`
  - `com.unity.ai.navigation`
  - `com.unity.inputsystem`
  - `com.unity.xr.interaction.toolkit`

---

## Escena principal

La escena habilitada en Build Settings es:

- `Assets/Scenes/Navegacion.unity`

---

## Cómo ejecutar el proyecto

1. Abrir el repositorio con Unity `6000.2.14f1`.
2. Verificar que los paquetes se resuelvan correctamente desde `Packages/manifest.json`.
3. Abrir la escena `Assets/Scenes/Navegacion.unity`.
4. Ejecutar en editor para validación básica de flujo.
5. Para pruebas reales de AR, compilar e instalar en dispositivo Android compatible con ARCore.

---

## Estado actual y hoja de ruta

### Estado actual

✅ IndoorNavAR ya incluye:

- base de navegación indoor con AR,
- manejo de waypoints,
- NavMesh multinivel,
- persistencia de sesión,
- UI móvil funcional.

### Próxima integración

🔜 Integración del módulo de reconocimiento de obstáculos para:

- detección de obstáculos en tiempo real,
- recálculo de ruta seguro y contextual,
- mejora de asistencia para movilidad autónoma.

---

## Licencia y uso académico

Este repositorio forma parte de un **proyecto académico de grado**. Si deseas reutilizar componentes o colaborar, se recomienda contactar primero a los autores para acordar lineamientos de uso, citación y continuidad del desarrollo.

---

## Contacto institucional

**Universidad de San Buenaventura Cali**

Proyecto de grado enfocado en tecnología asistiva para navegación interior de personas con discapacidad visual.
