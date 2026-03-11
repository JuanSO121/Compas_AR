# COMPAS AR, módulo IndoorNavAR

Proyecto de grado de Ingeniería de Sistemas, Universidad de San Buenaventura Cali.

Este repositorio contiene el módulo Unity de navegación en interiores asistida por realidad aumentada, pensado para integrarse con una aplicación móvil Flutter orientada a accesibilidad.

Autores: Juan Jose Sanchez, Carlos Eduardo Rangel.

## Tabla de contenidos

1. [Propósito del repositorio](#1-propósito-del-repositorio)
2. [Resumen ejecutivo del sistema](#2-resumen-ejecutivo-del-sistema)
3. [Estado real del proyecto, implementado vs planificado](#3-estado-real-del-proyecto-implementado-vs-planificado)
4. [Arquitectura del módulo](#4-arquitectura-del-módulo)
5. [Flujo operativo de navegación](#5-flujo-operativo-de-navegación)
6. [Tecnologías y su rol](#6-tecnologías-y-su-rol)
7. [Modelo de datos y conceptos clave](#7-modelo-de-datos-y-conceptos-clave)
8. [Algoritmo de cálculo de rutas](#8-algoritmo-de-cálculo-de-rutas)
9. [Uso de realidad aumentada en el módulo](#9-uso-de-realidad-aumentada-en-el-módulo)
10. [Integración con Flutter](#10-integración-con-flutter)
11. [Estructura del repositorio](#11-estructura-del-repositorio)
12. [Instalación, ejecución y validación](#12-instalación-ejecución-y-validación)
13. [Limitaciones técnicas actuales](#13-limitaciones-técnicas-actuales)
14. [Requisitos de hardware y software](#14-requisitos-de-hardware-y-software)
15. [Guía para investigación y marco teórico](#15-guía-para-investigación-y-marco-teórico)
16. [Hoja de ruta sugerida](#16-hoja-de-ruta-sugerida)

## 1. Propósito del repositorio

Este README está diseñado para dos usos prácticos.

Primero, documentación técnica para desarrollo y mantenimiento del módulo Unity IndoorNavAR.

Segundo, contexto completo y consistente para que un modelo de IA pueda comprender el problema, el alcance, la arquitectura, los límites actuales y las líneas de investigación sin ambigüedad.

## 2. Resumen ejecutivo del sistema

COMPAS aborda la orientación en espacios cerrados para personas con discapacidad visual, donde GPS no ofrece precisión útil. La solución divide responsabilidades en dos capas.

Flutter gestiona la interacción accesible, voz a texto, texto a voz, flujo conversacional y experiencia de usuario.

Unity gestiona la capa espacial, sesión AR, detección de planos, carga de entorno, navegación sobre NavMesh, waypoints y persistencia de sesión.

En términos funcionales, Flutter envía comandos y Unity ejecuta navegación indoor en tiempo real con retroalimentación de estado.

## 3. Estado real del proyecto, implementado vs planificado

### 3.1 Implementado en este repositorio

- Gestión de sesión AR con detección de planos y raycast (`ARSessionManager`).
- Gestión de waypoints, creación, búsqueda, edición y limpieza (`WaypointManager`, `WaypointData`).
- Cálculo y seguimiento de rutas sobre NavMesh con optimización de trayectoria y control anti atasco (`NavigationPathController`, `NavigationPathOptimizer`, `NavigationAgent`).
- Generación y serialización de NavMesh multinivel (`MultiLevelNavMeshGenerator`, `NavMeshSerializer`, servicios auxiliares).
- Orquestación de navegación desde manager principal (`NavigationManager`).
- Persistencia de sesión (`PersistenceManager`).
- Puente de comandos Flutter a Unity con acciones JSON definidas (`FlutterUnityBridge`).

### 3.2 Contrato de comandos implementado hoy

Acciones aceptadas por `FlutterUnityBridge`:

- `navigate_to_waypoint`
- `add_waypoint`
- `clear_waypoints`
- `save_session`
- `load_session`

### 3.3 Planificado o sujeto a evolución

- Canal robusto Unity a Flutter para eventos semánticos de navegación estandarizados.
- Versionado formal del protocolo de mensajería JSON.
- Capa explícita de guía de voz contextual en Unity o totalmente delegada a Flutter según estrategia final.
- Detección dinámica de obstáculos y recálculo contextual en línea.

Esta separación es intencional para evitar confundir estado actual con diseño objetivo.

## 4. Arquitectura del módulo

La arquitectura usa separación por dominios para reducir acoplamiento.

### 4.1 Núcleo de dominio

- `Core/Managers`: coordinación de navegación, waypoints, minimapa y carga de modelos.
- `Core/Data`: entidades como waypoint y metadatos asociados.
- `Core/Events`: bus de eventos para comunicación desacoplada.

### 4.2 Capa AR

- `Scripts/AR`: inicialización de AR, capacidades y gestión de planos.
- `AR/`: utilidades de alineación del origen para sincronizar contenido virtual con entorno físico.

### 4.3 Capa de navegación

- `Scripts/Navigation`: pathfinding sobre NavMesh, optimización, coordinación de agentes, soporte multinivel y serialización.

### 4.4 Capa de integración

- `Scripts/Integration`: recepción de comandos desde Flutter y delegación a managers internos.

## 5. Flujo operativo de navegación

1. Se inicia la escena y se validan dependencias AR.
2. El usuario o sistema carga entorno y estado previo.
3. Flutter solicita una acción, por ejemplo navegar a un waypoint.
4. Unity resuelve destino, calcula ruta y activa seguimiento.
5. El controlador actualiza progreso, waypoints alcanzados y llegada.
6. Si hay fallo de ruta o estancamiento, se dispara recálculo o evento de error.
7. El estado puede persistirse para sesiones futuras.

## 6. Tecnologías y su rol

### Unity

Motor de ejecución de lógica AR, navegación y renderizado en tiempo real.

### AR Foundation

Capa de abstracción AR en Unity para planos, raycast y anclajes con backend nativo.

### ARCore

Backend principal en Android para tracking visual inercial y mapeo del entorno.

### ARKit

Backend equivalente en iOS, aplicable si se extiende despliegue fuera de Android.

### Flutter

Aplicación host, interfaz accesible y punto de interacción por voz.

### C#

Lenguaje de implementación del módulo Unity.

### Paquetes Unity relevantes

- `com.unity.xr.arcore`
- `com.unity.ai.navigation`
- `com.unity.inputsystem`
- `com.unity.xr.interaction.toolkit`

## 7. Modelo de datos y conceptos clave

### Waypoint

Punto de interés navegable con identificador, nombre y pose tridimensional.

### NavMesh

Representación navegable del entorno. Permite pathfinding sobre superficies válidas y conexión entre niveles.

### Ruta optimizada

Secuencia procesada a partir de una ruta base para mejorar estabilidad de seguimiento y usabilidad en navegación asistida.

### Sesión

Conjunto persistible de waypoints, configuración de navegación y datos relacionados con malla navegable.

## 8. Algoritmo de cálculo de rutas

El módulo trabaja sobre NavMesh de Unity, que internamente se basa en la librería **Recast/Detour** (Mikko Mononen). El proceso opera en dos fases diferenciadas.

**Fase 1 — Recast (construcción del grafo):** la geometría del modelo del edificio se voxeliza y se analiza para identificar las regiones navegables. El resultado es una malla de polígonos convexos conectados que representa el espacio donde un agente puede desplazarse, respetando parámetros de radio, altura y pendiente máxima.

**Fase 2 — Detour (búsqueda de camino):** sobre la malla de polígonos generada por Recast, Detour ejecuta el algoritmo **A\*** para encontrar la secuencia óptima de polígonos que conecta la posición del agente con el destino. El resultado es un camino al que se aplica el algoritmo de **String Pulling** (algoritmo del canal o *funnel algorithm*) para extraer los puntos de esquina mínimos que definen el trayecto real, eliminando zigzags innecesarios dentro de los polígonos.

Pipeline de navegación en el módulo:

1. Solicitud de ruta entre posición actual y destino.
2. Validación de estado y factibilidad del path.
3. Optimización de trayectoria para reducir puntos redundantes y mejorar holgura lateral.
4. Seguimiento progresivo por waypoints con umbrales de llegada intermedia y llegada final.
5. Mecanismo anti atasco que evita falsos completados y prioriza recálculo.

## 9. Uso de realidad aumentada en el módulo

AR Foundation detecta superficies y ofrece raycast para ubicar contenido virtual en el espacio físico. El módulo usa esta base para alinear escena y navegación con entorno real capturado por cámara.

La estabilidad final depende de calidad de tracking, condiciones de iluminación, textura de superficies y deriva acumulada de la sesión.

## 10. Integración con Flutter

El patrón recomendado en la documentación del proyecto es Unity as a Library en Android, con puente de comandos vía canal nativo y `UnitySendMessage`.

Flujo de integración resumido:

1. Flutter interpreta intención del usuario.
2. Flutter envía JSON al GameObject `FlutterBridge`.
3. `FlutterUnityBridge` procesa la acción y ejecuta lógica interna.
4. Unity publica mensajes de estado por eventos internos y logging.
5. Flutter decide qué comunicar por TTS al usuario final.

Nota importante, este repositorio implementa de forma explícita el canal Flutter a Unity. La salida Unity a Flutter para eventos enriquecidos debe definirse o consolidarse según la arquitectura final del host Flutter.

## 11. Estructura del repositorio

- `Assets/IndoorNavAR/`: núcleo del módulo.
- `Assets/IndoorNavAR/Scripts/AR/`: gestión de sesión y capacidades AR.
- `Assets/IndoorNavAR/Scripts/Core/`: managers, datos y eventos.
- `Assets/IndoorNavAR/Scripts/Navigation/`: cálculo, optimización y seguimiento de rutas.
- `Assets/IndoorNavAR/Scripts/Integration/`: puente con Flutter.
- `Assets/IndoorNavAR/Core/`: persistencia y carga en runtime.
- `Assets/Scenes/`: escenas Unity, incluida `Navegacion.unity`.
- `Packages/`: dependencias Unity.
- `ProjectSettings/`: configuración de proyecto y XR.
- `docs/`: documentación complementaria de integración.

## 12. Instalación, ejecución y validación

### 12.1 Requisitos previos

- Unity 6000.2.14f1.
- Android SDK operativo para despliegue móvil.
- Dispositivo Android compatible con ARCore para pruebas de campo.

### 12.2 Ejecución local

1. Abrir proyecto en Unity.
2. Verificar paquetes en `Packages/manifest.json`.
3. Abrir `Assets/Scenes/Navegacion.unity`.
4. Ejecutar en editor para validación lógica básica.
5. Compilar a Android para validar comportamiento AR real.

### 12.3 Validación mínima recomendada

- Crear o cargar waypoints.
- Lanzar `navigate_to_waypoint` desde integración Flutter.
- Confirmar inicio de navegación y progreso en Unity.
- Guardar sesión, cerrar, recargar sesión y verificar consistencia.

## 13. Limitaciones técnicas actuales

- Dependencia fuerte de tracking AR, ambientes con baja textura o iluminación degradan precisión.
- No existe posicionamiento absoluto indoor tipo GPS, la calidad del modelo y del NavMesh es crítica.
- El contrato JSON actual no está versionado formalmente.
- Rendimiento sensible al hardware del dispositivo.

## 14. Requisitos de hardware y software

### Hardware

- Android compatible con ARCore.
- Cámara trasera funcional.
- Sensores inerciales estables.
- Recomendado dispositivo de gama media alta para pruebas continuas.

### Software

- Unity `6000.2.14f1`.
- Paquetes definidos en `Packages/manifest.json`.
- Integración Flutter Android siguiendo `docs/INTEGRACION_FLUTTER_UNITY.md`.

## 15. Guía para investigación y marco teórico

Para un marco teórico sólido, las líneas que mejor conectan con este módulo son:

- Navegación asistiva en interiores para discapacidad visual.
- Limitaciones del posicionamiento indoor sin infraestructura dedicada.
- Realidad aumentada móvil, tracking visual inercial y deriva espacial.
- Modelado de espacios navegables con grafos y NavMesh.
- Planificación de rutas y criterios de robustez en asistencia peatonal.
- Interacción multimodal accesible, voz, feedback auditivo y carga cognitiva.
- Integración de motores 3D en apps móviles híbridas, Unity as a Library.

Este README describe qué está implementado y qué está proyectado para evitar sesgos en la redacción del estado del arte y en la metodología del trabajo de grado.

## 16. Hoja de ruta sugerida

- Versionar contrato JSON, campo `protocolVersion` y catálogo formal de acciones.
- Definir telemetría de navegación para evaluación experimental reproducible.
- Añadir pruebas automatizadas de integración Flutter Unity en escenarios críticos.
- Incorporar detección de obstáculos y recálculo adaptativo.
- Publicar guía de despliegue de investigación, dataset de recorridos y métricas de evaluación.

---

Si necesitas usar este README como prompt base para IA, úsalo junto con `docs/INTEGRACION_FLUTTER_UNITY.md` para darle contexto de arquitectura y contexto de integración Android Flutter Unity en paralelo.