# Compas AR, módulo IndoorNavAR

## Introducción

Este repositorio contiene el módulo de navegación en interiores asistido por realidad aumentada del proyecto de grado **Compas**, desarrollado en el programa de Ingeniería de Sistemas. El objetivo del módulo es resolver un problema concreto, orientar a una persona dentro de un edificio cuando no dispone de referencias visuales fiables, o cuando requiere apoyo adicional para desplazarse con seguridad.

El módulo está implementado en Unity y se integra en una aplicación móvil principal desarrollada en Flutter. Su responsabilidad es modelar el entorno interior, calcular rutas navegables y renderizar guías visuales en tiempo real sobre el espacio físico detectado por AR. A nivel de accesibilidad, también publica mensajes de estado que la app Flutter convierte en voz mediante TTS, mientras Flutter captura comandos por voz con VTT y los reenvía a Unity como instrucciones de navegación.

En términos prácticos, este componente no reemplaza la lógica de interacción accesible de Flutter. La complementa. Unity se ocupa de la capa espacial y de navegación AR, Flutter gobierna la experiencia de usuario, la entrada por voz y la salida hablada.

## Arquitectura del módulo

La arquitectura sigue un esquema por capas y responsabilidades. Unity opera como motor de ejecución AR y cálculo de rutas, y Flutter opera como host de la aplicación móvil.

En Unity, la lógica principal se divide en gestión de sesión AR, administración de waypoints, planificación de rutas sobre NavMesh, renderizado de guía y persistencia. La comunicación interna entre subsistemas usa un bus de eventos para reducir acoplamiento y evitar dependencias rígidas entre managers y controladores. Este patrón permite escalar el módulo, por ejemplo para añadir detección de obstáculos o reglas contextuales de asistencia.

La integración con Flutter se realiza mediante un puente de comandos. Flutter envía instrucciones hacia Unity en formato JSON. Unity interpreta acciones como navegación a destino, creación de waypoints, guardado y restauración de sesión. El puente actual está implementado en `FlutterUnityBridge`, diseñado para funcionar con el flujo Flutter, canal nativo Android, UnitySendMessage.

### Flujo general entre componentes

1. Flutter recibe un comando de usuario, táctil o por voz.
2. Flutter procesa intención y envía comando al módulo Unity.
3. Unity valida la instrucción, consulta waypoints y solicita ruta.
4. El sistema de navegación calcula trayectoria y activa seguimiento.
5. Unity renderiza indicadores AR y publica estados de avance.
6. Flutter transforma esos estados en mensajes auditivos para el usuario.

## Tecnologías utilizadas

### Unity

Unity es el entorno de ejecución del módulo AR. Aquí se implementan escena, objetos de navegación, agentes, controladores y servicios de persistencia. También se gestionan ciclo de vida, renderizado en tiempo real y despliegue móvil Android.

### AR Foundation

AR Foundation es la capa de abstracción de AR en Unity. Permite trabajar con detección de planos, raycast espacial y anclaje sin reescribir lógica para cada proveedor nativo. En este proyecto se usa para iniciar sesión AR, detectar superficies útiles y posicionar contenido virtual alineado con el entorno físico.

### ARCore y ARKit

ARCore se usa como backend principal en Android para tracking y plane detection. ARKit es el backend equivalente en iOS, aplicable si el despliegue se extiende a ecosistema Apple. La base del módulo es compatible por diseño con ambos proveedores por medio de AR Foundation, aunque el objetivo operativo actual está centrado en Android.

### Flutter

Flutter es la aplicación contenedora y el punto de interacción accesible con la persona usuaria. Desde Flutter se manejan la interfaz principal, el reconocimiento de voz, la síntesis de voz y la orquestación de comandos hacia Unity. Esta separación es útil porque evita acoplar funciones de accesibilidad a la escena 3D.

### C#

C# es el lenguaje de implementación del módulo Unity. Se utiliza para modelar entidades de navegación, controladores de ruta, servicios de AR, integración con Flutter y persistencia de sesiones.

### Librerías y paquetes relevantes

El proyecto utiliza paquetes del ecosistema Unity para XR, navegación y entrada. Entre los más relevantes están ARCore XR Plugin, AI Navigation, Input System y XR Interaction Toolkit. Además, se usan componentes de TextMeshPro para UI textual y recursos visuales de soporte.

## Funcionamiento del sistema de navegación

El entorno interior se representa en una escena Unity donde se combinan modelos 3D del edificio con superficies navegables generadas mediante NavMesh. El resultado operativo no es solo geometría visual, es una malla de navegación sobre la cual un agente puede calcular trayectorias válidas, incluso en escenarios multinivel.

Los puntos de interés se definen como waypoints con metadatos, identificador, nombre, tipo, posición y configuración visual. Estos waypoints pueden crearse durante ejecución y permanecer persistidos para sesiones posteriores.

El cálculo de rutas parte de la posición actual del agente y del waypoint destino. El sistema solicita una ruta base y luego la optimiza para suavizar el recorrido y evitar comportamientos poco naturales, por ejemplo acercamiento excesivo a bordes o cambios bruscos de dirección. Durante el seguimiento existe control anti bloqueo, con recálculo en caso de estancamiento y validaciones para evitar falsos positivos de llegada.

## Algoritmo de cálculo de rutas

El enfoque general corresponde a planificación sobre grafo navegable derivado de NavMesh. Internamente, Unity resuelve el pathfinding sobre polígonos conectados y entrega una secuencia de esquinas o puntos de control.

Sobre esa ruta base, el módulo aplica una fase de optimización que filtra waypoints, proyecta puntos con mayor holgura y ajusta el seguimiento para un desplazamiento más estable. En otras palabras, no se limita a pedir una ruta y dibujarla, también la refina para que sea usable en navegación asistida real.

La instrucción de navegación se genera como una combinación de estado continuo y eventos discretos. Estado continuo, distancia restante y progreso. Eventos discretos, inicio de ruta, waypoint alcanzado, recálculo, llegada o fallo. Estos eventos alimentan mensajes que Flutter puede verbalizar.

## Uso de realidad aumentada

AR Foundation habilita la detección de planos y el raycast sobre el mundo físico capturado por cámara. Con esa información se fijan anclajes espaciales, lo que permite ubicar elementos virtuales sin deriva excesiva cuando el usuario se desplaza.

Los elementos de guía se posicionan en coordenadas del mundo AR y se actualizan conforme avanza la navegación. Dependiendo de la configuración de escena, la guía puede mostrarse como marcadores de ruta, puntos de referencia, indicadores de dirección y objetos de destino.

La precisión depende del tracking del dispositivo, de la calidad del mapeo inicial y de las condiciones del entorno, iluminación, textura de superficies y presencia de oclusiones.

## Lógica de funcionamiento del módulo

El ciclo operativo del módulo sigue esta secuencia:

1. Inicialización de la sesión AR y validación de dependencias.
2. Detección de superficies y alineación inicial del contenido.
3. Carga de entorno, modelos y configuración de navegación.
4. Disponibilización de waypoints y selección de destino.
5. Cálculo de ruta en NavMesh y optimización de trayectoria.
6. Ejecución del seguimiento con control de progreso y recálculo cuando aplica.
7. Renderizado de guía AR y emisión de eventos de estado.
8. Finalización por llegada, cancelación o fallo de ruta.

## Integración con Flutter

La integración se plantea con Unity as a Library en Android. Flutter conserva el control de la aplicación y Unity se carga como módulo especializado para navegación AR.

El intercambio de comandos sigue un esquema de mensajería JSON. Flutter envía acciones y parámetros, Unity procesa y ejecuta. Para integración de accesibilidad, Unity publica estados que Flutter consume para text to speech. En sentido inverso, Flutter convierte voz a texto e invoca acciones de navegación sobre Unity.

Acciones de puente actualmente contempladas:

- `navigate_to_waypoint`
- `add_waypoint`
- `clear_waypoints`
- `save_session`
- `load_session`

Este diseño es suficiente para un MVP funcional. Si el proyecto crece, conviene versionar explícitamente el contrato de mensajes para evitar rupturas entre app Flutter y módulo Unity.

## Estructura del proyecto

La organización del repositorio prioriza separación entre activos de Unity, configuración del editor, documentación e integración:

- `Assets/IndoorNavAR/`, núcleo del módulo, scripts de AR, navegación, managers, integración, materiales y recursos.
- `Assets/Scenes/`, escenas de trabajo, incluida la escena de navegación principal.
- `Assets/StreamingAssets/` y `Assets/IndoorNavAR/StreamingAssets/`, recursos cargables en ejecución, por ejemplo modelos del entorno.
- `Packages/`, manifiesto y lock de paquetes Unity.
- `ProjectSettings/`, configuración del proyecto Unity, XR, build y parámetros del editor.
- `docs/`, documentación técnica complementaria, incluida guía de integración Flutter Unity.

## Consideraciones técnicas

### Limitaciones

La solución depende de la calidad de tracking AR en tiempo real. Ambientes con pocas texturas, iluminación deficiente o alto tránsito pueden degradar el anclaje espacial. También hay limitaciones inherentes a GPS indoor inexistente, por eso la calidad del modelo del edificio y del NavMesh es crítica.

Otro punto débil es la dependencia de un contrato de mensajes ad hoc con Flutter. Funciona, pero sin versionado formal puede romperse si cualquiera de los dos módulos cambia nombres de acciones o estructura JSON.

### Requisitos de hardware

Se requiere dispositivo móvil compatible con ARCore, cámara funcional, sensores inerciales estables y capacidad gráfica suficiente para renderizar escena 3D en tiempo real. En pruebas de campo se recomienda gama media alta en adelante para mantener fluidez y estabilidad de tracking.

### Dependencias de software

- Unity 6, versión del proyecto `6000.2.14f1`.
- Android SDK compatible con configuración del módulo host.
- Paquetes XR y navegación definidos en `Packages/manifest.json`.
- Aplicación Flutter con canal nativo Android para invocar Unity.

## Ejecución y validación básica

Para ejecutar el módulo de forma local:

1. Abrir el repositorio en Unity 6.
2. Verificar resolución de paquetes.
3. Abrir la escena `Assets/Scenes/Navegacion.unity`.
4. Ejecutar pruebas iniciales en editor para validar flujo interno.
5. Compilar a dispositivo Android para pruebas AR reales.

Para validar integración con Flutter, se recomienda una prueba de extremo a extremo simple, enviar comando de navegación desde Flutter y confirmar en Unity el inicio de ruta, luego verificar locución TTS de estado en Flutter.

## Recomendaciones de mejora

Si el módulo se va a usar en producción académica o piloto institucional, hay tres mejoras que valen la pena. Primero, incorporar detección dinámica de obstáculos para recálculo contextual. Segundo, formalizar contrato de mensajería con esquema versionado y pruebas automáticas. Tercero, añadir telemetría de navegación para medir tiempos, errores de ruta y puntos de abandono.

## Autores y contexto académico

Desarrollo realizado en el marco de un proyecto de grado en Ingeniería de Sistemas, Universidad de San Buenaventura Cali.

Autores:

- Juan Jose Sanchez
- Carlos Eduardo Rangel
