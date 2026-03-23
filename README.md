# COMPAS AR, módulo IndoorNavAR

Proyecto de grado de Ingeniería de Sistemas, Universidad de San Buenaventura Cali.

Este repositorio contiene el módulo Unity de navegación en interiores asistida por realidad aumentada de COMPAS. Su propósito es resolver la capa espacial de la solución: sesión AR, waypoints, NavMesh, cálculo de rutas, guía contextual y una interfaz móvil adaptada para uso accesible dentro de la experiencia AR.

La aplicación host en Flutter queda a cargo de la conversación, accesibilidad, reconocimiento de voz, activación por palabra clave o mecanismo equivalente, texto a voz y coordinación general de la experiencia del usuario.

Autores: Juan Jose Sanchez, Carlos Eduardo Rangel.

## Tabla de contenidos

1. [Propósito del repositorio](#1-propósito-del-repositorio)
2. [Resumen ejecutivo del sistema](#2-resumen-ejecutivo-del-sistema)
3. [Estado real del proyecto](#3-estado-real-del-proyecto)
4. [Arquitectura actual](#4-arquitectura-actual)
5. [Voz, accesibilidad y responsabilidades entre Flutter y Unity](#5-voz-accesibilidad-y-responsabilidades-entre-flutter-y-unity)
6. [Contrato de integración implementado hoy](#6-contrato-de-integración-implementado-hoy)
7. [UI móvil y consideraciones de accesibilidad](#7-ui-móvil-y-consideraciones-de-accesibilidad)
8. [Modelo de datos y conceptos clave](#8-modelo-de-datos-y-conceptos-clave)
9. [Algoritmo de cálculo de rutas](#9-algoritmo-de-cálculo-de-rutas)
10. [Uso de realidad aumentada en el módulo](#10-uso-de-realidad-aumentada-en-el-módulo)
11. [Estructura del repositorio](#11-estructura-del-repositorio)
12. [Instalación, ejecución y validación](#12-instalación-ejecución-y-validación)
13. [Limitaciones técnicas actuales](#13-limitaciones-técnicas-actuales)
14. [Requisitos de hardware y software](#14-requisitos-de-hardware-y-software)
15. [Guía para investigación y marco teórico](#15-guía-para-investigación-y-marco-teórico)
16. [Hoja de ruta sugerida](#16-hoja-de-ruta-sugerida)

## 1. Propósito del repositorio

Este README está pensado para tres usos prácticos.

- Servir como documentación técnica actualizada del módulo Unity IndoorNavAR.
- Dejar claro qué partes viven en Unity y cuáles viven en Flutter o en la capa móvil anfitriona.
- Ofrecer contexto consistente para redacción académica, mantenimiento del proyecto e interpretación por herramientas de IA.

## 2. Resumen ejecutivo del sistema

COMPAS aborda la orientación en espacios cerrados para personas con discapacidad visual, donde GPS no ofrece precisión suficiente. La solución se divide en dos capas complementarias.

### Flutter / aplicación host

Flutter gestiona la experiencia accesible de alto nivel:

- flujo conversacional;
- reconocimiento de voz;
- activación por palabra clave o mecanismo equivalente de escucha;
- texto a voz;
- estados de interfaz centrados en accesibilidad;
- comunicación semántica con Unity.

### Unity / este repositorio

Unity gestiona la capa espacial y operativa:

- sesión AR y alineación con el entorno físico;
- detección de planos y raycast;
- waypoints y persistencia;
- construcción y serialización de NavMesh;
- cálculo y seguimiento de rutas indoor;
- generación de instrucciones de guía;
- UI móvil in-scene para navegación, gestión de destinos y control de sesión.

En términos funcionales, Flutter interpreta la intención y maneja la accesibilidad global; Unity resuelve la navegación indoor y publica estado operativo para que Flutter decida qué comunicar al usuario.

## 3. Estado real del proyecto

### 3.1 Implementado actualmente en este repositorio

- Gestión de sesión AR con detección de planos y raycast.
- Alineación del origen AR con el entorno capturado.
- Gestión de waypoints: creación, edición, marcado de favoritos, limpieza y carga en lote.
- Cálculo y seguimiento de rutas sobre NavMesh con optimización de trayectoria y control anti atasco.
- Generación y serialización de NavMesh multinivel.
- Persistencia de sesión.
- Puente de integración Flutter ↔ Unity por JSON.
- Guía de navegación basada en eventos e instrucciones contextuales.
- Canal Unity → Flutter para solicitudes de TTS y canal Flutter → Unity para confirmar el estado real del TTS.
- UI móvil responsive en Unity con paneles, listas, favoritos, rutas guiadas, menú lateral, toasts y controles táctiles.

### 3.2 Cambios relevantes respecto a versiones previas de la documentación

- Este repositorio ya no debe documentarse como dependiente de Porcupine ni de Picovoice.
- La activación por voz o wake word ya no se describe aquí como una responsabilidad interna de Unity.
- Flutter es el dueño del engine TTS; Unity no debe asumirse como sintetizador principal.
- La documentación anterior del bridge estaba desactualizada en nombres de acciones y en el flujo de voz.
- La UI del módulo evolucionó: ahora incluye una capa móvil más completa, responsive y orientada a uso accesible durante navegación AR.

### 3.3 Elementos que siguen sujetos a evolución

- Versionado formal del protocolo JSON.
- Estandarización completa del canal de eventos enriquecidos Unity → Flutter.
- Implementación final del proveedor de comandos de voz embebido en la UI de Unity, si se decide mantenerlo dentro del módulo.
- Detección dinámica de obstáculos y recálculo adaptativo con mayor robustez de producción.

## 4. Arquitectura actual

La arquitectura usa separación por dominios para reducir acoplamiento.

### 4.1 Núcleo de dominio

- `Core/Managers`: coordinación de navegación, waypoints, persistencia y carga de modelos.
- `Core/Data`: entidades y metadatos, por ejemplo `WaypointData`.
- `Core/Events`: eventos internos para desacoplar navegación, UI, voz y persistencia.

### 4.2 Capa AR

- `Scripts/AR`: inicialización de AR, validación de capacidades y gestión de planos.
- `AR/`: utilidades de interfaz móvil, acoplamiento con cámara AR y lógica de experiencia in-scene.

### 4.3 Capa de navegación

- `Scripts/Navigation`: pathfinding sobre NavMesh, optimización, agentes, soporte multinivel y serialización.
- `Scripts/Navigation/Voice`: guía de navegación basada en eventos e instrucciones hablables.

### 4.4 Capa de integración

- `Scripts/Integration`: recepción de comandos desde Flutter y publicación de respuestas hacia la app host.

## 5. Voz, accesibilidad y responsabilidades entre Flutter y Unity

Esta separación es importante para no volver a documentar el proyecto con supuestos viejos.

### 5.1 Qué hace Flutter hoy

Flutter o la capa móvil anfitriona se encarga de:

- reconocimiento de voz;
- wake word o mecanismo equivalente de activación;
- texto a voz;
- coordinación del flujo conversacional;
- decisiones finales de accesibilidad y feedback auditivo al usuario.

### 5.2 Qué hace Unity hoy

Unity se encarga de:

- producir eventos semánticos de navegación;
- generar mensajes de guía listos para TTS;
- enviar solicitudes de habla a Flutter;
- recibir confirmación real de cuándo Flutter empieza o termina de hablar;
- adaptar la UI in-scene al contexto de navegación AR.

### 5.3 Aclaración importante sobre Porcupine / Picovoice

Si se usa este repositorio como base documental, debe asumirse que el módulo Unity actual no depende de Porcupine ni de Picovoice como pieza central del flujo de activación por voz. La activación por palabra clave debe describirse como externa al módulo o gestionada por la app host según la estrategia vigente del producto.

### 5.4 Propiedad del TTS

El flujo correcto hoy es este:

1. Unity genera una instrucción o mensaje útil para navegación.
2. Unity envía una solicitud `tts_request` a Flutter.
3. Flutter decide cómo reproducirla con su engine TTS.
4. Flutter devuelve `tts_status` para informar el estado real del habla.
5. Unity ajusta su lógica para no saturar ni solapar indicaciones.

## 6. Contrato de integración implementado hoy

El punto de entrada para comandos desde Flutter es `FlutterBridge` / `OnFlutterCommand`.

### 6.1 Acciones aceptadas actualmente

- `navigate_to`
- `stop_navigation`
- `nav_status`
- `list_waypoints`
- `create_waypoint`
- `remove_waypoint`
- `clear_waypoints`
- `save_session`
- `load_session`
- `tts_status`

### 6.2 Flujo resumido de integración

1. Flutter interpreta intención, voz o acción de accesibilidad.
2. Flutter serializa un JSON con `action` y parámetros.
3. Unity procesa la orden en `FlutterUnityBridge` y la delega a `VoiceCommandAPI`.
4. Los sistemas internos ejecutan navegación, persistencia o consulta de estado.
5. Unity responde a Flutter con mensajes JSON cuando corresponde.
6. Para la guía hablada, Unity emite `tts_request` y Flutter confirma con `tts_status`.

### 6.3 Nota sobre compatibilidad documental

Cualquier documentación anterior que mencione acciones como `navigate_to_waypoint` o un contrato distinto debe considerarse obsoleta frente al código actual del bridge.

## 7. UI móvil y consideraciones de accesibilidad

El módulo ya no es solo una escena AR con lógica de navegación. También incluye una UI móvil más completa para operar la sesión en contexto.

### 7.1 Capacidades actuales de la UI

- barra superior de estado;
- FABs para acciones rápidas;
- bottom sheet con pestañas;
- listado de waypoints;
- favoritos;
- rutas guiadas por múltiples paradas;
- panel de navegación activo con distancia, ETA y acciones de control;
- panel modal para editar waypoints;
- menú lateral para sesión y utilidades;
- toasts para feedback inmediato;
- adaptación a safe areas y cambios de aspecto de pantalla.

### 7.2 Criterios de accesibilidad y usabilidad reflejados en la implementación

- layout responsive con recálculo según relación de aspecto;
- respeto de safe areas para notch, barras del sistema y bordes inferiores;
- contraste alto entre fondo, superficies y acciones primarias;
- reducción de solapamiento entre paneles y controles flotantes;
- feedback redundante por estado visual, eventos y guía hablada vía Flutter;
- soporte para navegación táctil simple durante la experiencia AR.

### 7.3 Sobre el botón o capa de voz dentro de la UI Unity

La UI ya contempla un punto de integración para comandos de voz, pero el proveedor concreto de voz en Unity sigue siendo una interfaz de integración y no debe documentarse como pipeline definitivo del producto. En el estado actual, la capa host en Flutter sigue siendo la referencia principal para accesibilidad por voz.

## 8. Modelo de datos y conceptos clave

### Waypoint

Punto de interés navegable con identificador, nombre, pose tridimensional y metadatos útiles para navegación o UI.

### NavMesh

Representación navegable del entorno. Permite pathfinding sobre superficies válidas y conexión entre niveles.

### Ruta guiada

Secuencia de destinos o waypoints que el usuario puede recorrer como una navegación compuesta por múltiples paradas.

### Sesión

Conjunto persistible de waypoints, configuración asociada y datos necesarios para restaurar una navegación previa.

### Instrucción de guía

Mensaje semántico generado por Unity a partir del estado de la ruta, por ejemplo giros, avance, desvío, llegada o transición de nivel.

## 9. Algoritmo de cálculo de rutas

El módulo trabaja sobre NavMesh de Unity, que internamente se apoya en la librería Recast/Detour. El proceso opera en dos fases.

### Fase 1: construcción de espacio navegable

La geometría del edificio se voxeliza y se analiza para identificar regiones transitables. El resultado es una malla de polígonos convexos conectados que representa el espacio donde el agente puede desplazarse, respetando parámetros de radio, altura y pendiente.

### Fase 2: búsqueda y refinamiento de camino

Sobre la malla generada, Detour aplica búsqueda tipo A* para hallar la secuencia de polígonos entre origen y destino. Luego se refinan esquinas y segmentos para producir una trayectoria más estable y más útil en navegación asistida.

### Pipeline funcional del módulo

1. Solicitud de ruta entre posición actual y destino.
2. Validación de factibilidad del trayecto.
3. Optimización para reducir puntos redundantes.
4. Seguimiento progresivo por waypoints intermedios.
5. Monitoreo de atasco, progreso y necesidad de recálculo.
6. Generación de instrucciones contextuales para guía al usuario.

## 10. Uso de realidad aumentada en el módulo

AR Foundation detecta superficies y ofrece raycast para ubicar contenido virtual en el espacio físico. El módulo usa esta base para alinear escena y navegación con el entorno real capturado por cámara.

La estabilidad final depende de:

- calidad del tracking visual inercial;
- iluminación y textura del entorno;
- calidad de alineación inicial;
- deriva acumulada de la sesión;
- capacidad del dispositivo.

## 11. Estructura del repositorio

- `Assets/IndoorNavAR/`: núcleo del módulo.
- `Assets/IndoorNavAR/Scripts/AR/`: gestión de sesión y capacidades AR.
- `Assets/IndoorNavAR/AR/`: UI móvil, experiencia in-scene y acoplamientos de interacción.
- `Assets/IndoorNavAR/Scripts/Core/`: managers, datos y eventos.
- `Assets/IndoorNavAR/Scripts/Navigation/`: cálculo, optimización y seguimiento de rutas.
- `Assets/IndoorNavAR/Scripts/Navigation/Voice/`: guía hablada y eventos de instrucción.
- `Assets/IndoorNavAR/Scripts/Integration/`: bridge con Flutter y API de comandos/respuestas.
- `Assets/Scenes/`: escenas Unity, incluida `Navegacion.unity`.
- `Packages/`: dependencias Unity.
- `ProjectSettings/`: configuración del proyecto y XR.

## 12. Instalación, ejecución y validación

### 12.1 Requisitos previos

- Unity 6000.2.14f1.
- Android SDK operativo para despliegue móvil.
- Dispositivo Android compatible con ARCore para pruebas reales.

### 12.2 Ejecución local

1. Abrir el proyecto en Unity.
2. Verificar dependencias en `Packages/manifest.json`.
3. Abrir `Assets/Scenes/Navegacion.unity`.
4. Ejecutar en editor para validar lógica y flujos base.
5. Compilar a Android para validar AR, tracking, UI y navegación en condiciones reales.

### 12.3 Validación mínima recomendada

- Crear o cargar waypoints.
- Listar waypoints desde Flutter o desde la UI del módulo.
- Lanzar `navigate_to` y verificar inicio de navegación.
- Confirmar progreso, llegada y cancelación.
- Probar guardado y carga de sesión.
- Verificar que Unity envía solicitudes `tts_request` y que Flutter responde con `tts_status`.
- Revisar comportamiento de la UI en diferentes tamaños de pantalla y safe areas.

## 13. Limitaciones técnicas actuales

- Dependencia fuerte de tracking AR; baja textura o mala iluminación degradan precisión.
- No existe posicionamiento absoluto indoor tipo GPS dentro del módulo.
- La precisión final depende de calidad del modelo espacial y del NavMesh.
- El contrato JSON aún no está versionado formalmente.
- La estrategia final de wake word y reconocimiento de voz depende de la app host.
- Rendimiento sensible al hardware del dispositivo y a la complejidad del entorno.

## 14. Requisitos de hardware y software

### Hardware

- Android compatible con ARCore.
- Cámara trasera funcional.
- Sensores inerciales estables.
- Recomendado dispositivo de gama media alta para sesiones continuas.

### Software

- Unity `6000.2.14f1`.
- Paquetes definidos en `Packages/manifest.json`.
- Aplicación host Flutter o capa Android/iOS equivalente para integración, voz y TTS.

## 15. Guía para investigación y marco teórico

Para un marco teórico sólido, las líneas más alineadas con este módulo son:

- navegación asistiva en interiores para personas con discapacidad visual;
- limitaciones del posicionamiento indoor sin infraestructura dedicada;
- realidad aumentada móvil, tracking visual inercial y deriva espacial;
- modelado de espacios navegables con grafos y NavMesh;
- planificación de rutas y criterios de robustez en asistencia peatonal;
- interacción multimodal accesible, voz, feedback auditivo y carga cognitiva;
- integración de motores 3D en apps móviles híbridas con Unity as a Library.

Este README describe el estado real del módulo para evitar sesgos entre lo implementado, lo prototipado y lo proyectado.

## 16. Hoja de ruta sugerida

- Versionar el contrato JSON con un catálogo formal de acciones y respuestas.
- Consolidar el canal Unity → Flutter para eventos semánticos de navegación.
- Definir la arquitectura final del wake word y documentarla fuera de supuestos históricos.
- Añadir pruebas automatizadas de integración Flutter Unity en escenarios críticos.
- Incorporar detección de obstáculos y recálculo adaptativo con evaluación reproducible.
- Publicar una guía de despliegue e instrumentación para pruebas de investigación.
