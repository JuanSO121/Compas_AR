# COMPAS AR, módulo IndoorNavAR

Proyecto de grado de Ingeniería de Sistemas, Universidad de San Buenaventura Cali.

Este repositorio contiene el módulo Unity de navegación en interiores asistida por realidad aumentada de COMPAS. Su propósito es resolver la capa espacial de la solución: sesión AR, waypoints, NavMesh, cálculo de rutas, guía contextual y una interfaz móvil adaptada para uso accesible dentro de la experiencia AR.

La aplicación host en Flutter queda a cargo de la conversación, accesibilidad, texto a voz, coordinación general de la experiencia y cualquier capa adicional de entrada de voz que se integre alrededor del módulo.

Autores: Juan Jose Sanchez, Carlos Eduardo Rangel.

---

## Tabla de contenidos

1. Propósito del repositorio  
2. Resumen ejecutivo del sistema  
3. Estado real del proyecto  
4. Arquitectura actual  
5. Voz, accesibilidad y responsabilidades entre Flutter y Unity  
6. Contrato de integración implementado hoy  
7. UI móvil y consideraciones de accesibilidad  
8. Modelo de datos y conceptos clave  
9. Algoritmo de cálculo de rutas  
10. Uso de realidad aumentada en el módulo  
11. Estructura del repositorio  
12. Instalación, ejecución y validación  
13. Limitaciones técnicas actuales  
14. Requisitos de hardware y software  
15. Guía para investigación y marco teórico  
16. Hoja de ruta sugerida  

---

## 1. Propósito del repositorio

Este README está pensado para tres usos prácticos:

- Servir como documentación técnica actualizada del módulo Unity IndoorNavAR.
- Dejar claro qué partes viven en Unity y cuáles viven en Flutter o en la capa móvil anfitriona.
- Ofrecer contexto consistente para redacción académica, mantenimiento del proyecto e interpretación por herramientas de IA.

---

## 2. Resumen ejecutivo del sistema

COMPAS aborda la orientación en espacios cerrados para personas con discapacidad visual, donde GPS no ofrece precisión suficiente. La solución se divide en dos capas complementarias.

### Flutter / aplicación host

Flutter gestiona la experiencia host de alto nivel:

- flujo conversacional  
- texto a voz  
- estados de interfaz centrados en accesibilidad  
- coordinación de la integración con Unity  
- cualquier capa externa de entrada de voz  

### Unity / este repositorio

Unity gestiona la capa espacial y operativa:

- sesión AR  
- detección de planos  
- waypoints y persistencia  
- NavMesh  
- cálculo de rutas  
- generación de instrucciones  
- UI móvil en escena  

---

## 3. Estado real del proyecto

### 3.1 Implementado

- Gestión de sesión AR  
- Alineación del origen AR  
- Waypoints completos  
- Navegación con NavMesh  
- Persistencia  
- Bridge Flutter ↔ Unity  
- Guía contextual  
- Canal TTS  
- UI móvil completa  

### 3.2 Alcance

- Navegación indoor  
- Integración JSON  
- UI en escena  
- Comunicación con Flutter  

### 3.3 En evolución

- Versionado JSON  
- Eventos enriquecidos  
- Voz embebida  
- Obstáculos dinámicos  

---

## 4. Arquitectura

### Core
- Managers  
- Data  
- Events  

### AR
- Inicialización  
- Planos  
- UI acoplada  

### Navegación
- Pathfinding  
- Optimización  
- Voice  

### Integración
- FlutterBridge  

---

## 5. Voz y accesibilidad

### Flutter
- Manejo de accesibilidad  
- TTS  
- Control de experiencia  

### Unity
- Genera instrucciones  
- Envía `tts_request`  
- Recibe `tts_status`  

---

## 6. Contrato de integración

Acciones disponibles:

- navigate_to  
- stop_navigation  
- nav_status  
- list_waypoints  
- create_waypoint  
- remove_waypoint  
- clear_waypoints  
- save_session  
- load_session  
- tts_status  

---

## 7. UI móvil

Incluye:

- barra superior  
- FABs  
- bottom sheet  
- favoritos  
- rutas  
- panel navegación  
- menú lateral  
- toasts  

### Accesibilidad

- responsive  
- safe areas  
- alto contraste  
- feedback multimodal  

---

## 8. Modelo de datos

### Waypoint
Punto navegable con posición y metadatos.

### NavMesh
Espacio navegable.

### Ruta guiada
Secuencia de destinos.

### Sesión
Estado persistente.

### Instrucción
Mensaje de navegación.

---

## 9. Algoritmo de cálculo de rutas

El módulo calcula rutas utilizando el sistema NavMesh de Unity.

### Paso 1: cálculo de ruta base

Se ajustan el origen y el destino al NavMesh y se calcula la ruta mediante:

Esta función utiliza internamente el sistema de navegación de Unity para encontrar una ruta válida dentro de la malla navegable.

### Paso 2: refinamiento de trayectoria

Una vez obtenida la ruta base, se aplican dos procesos de optimización propios del módulo:

- **Center Pull**: mejora la holgura lateral del recorrido para evitar trayectorias demasiado pegadas a bordes.
- **Funnel conservador**: elimina puntos casi colineales sin comprometer la seguridad ni cruzar bordes del NavMesh.

### Pipeline completo

1. Solicitud de ruta  
2. Ajuste a NavMesh  
3. Cálculo con `NavMesh.CalculatePath`  
4. Refinamiento (Center Pull + Funnel)  
5. Seguimiento  
6. Monitoreo y recálculo  
7. Generación de instrucciones  

---

## 10. Realidad aumentada

Basado en AR Foundation:

- detección de planos  
- raycast  
- alineación  

Factores críticos:

- iluminación  
- tracking  
- dispositivo  

---

## 11. Estructura

- Assets/IndoorNavAR  
- Scripts/AR  
- Scripts/Core  
- Scripts/Navigation  
- Scripts/Navigation/Voice  
- Scripts/Integration  
- Scenes  
- Packages  
- ProjectSettings  

---

## 12. Instalación

### Requisitos

- Unity 6000.2.14f1  
- Android ARCore  

### Ejecución

1. Abrir proyecto  
2. Verificar dependencias  
3. Abrir escena  
4. Ejecutar  
5. Build Android  

---

## 13. Limitaciones

- Dependencia de AR  
- Sin posicionamiento absoluto  
- Precisión depende del entorno  
- JSON no versionado  
- Rendimiento variable  

---

## 14. Requisitos

### Hardware
- Android ARCore  
- Cámara  
- Sensores  

### Software
- Unity  
- Flutter host  

---

## 15. Marco teórico

- navegación indoor  
- AR móvil  
- NavMesh  
- accesibilidad  
- voz  
- integración Unity  

---

## 16. Hoja de ruta

- Versionar JSON  
- Eventos Unity → Flutter  
- Arquitectura de voz  
- Pruebas integración  
- Obstáculos dinámicos  
- Evaluación experimental  