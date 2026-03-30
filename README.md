# COMPAS Mobile (Flutter) — Asistente de navegación por voz accesible con IA híbrida

> Aplicación móvil Flutter para asistencia de navegación y reconocimiento de entorno, diseñada con enfoque de accesibilidad para personas con discapacidad visual. Integra procesamiento de voz, inferencia local (offline), inferencia en la nube (Groq) y puente con Unity para navegación AR.  

Cliente multiplataforma centrado en Android/iOS para accesibilidad, autenticación por código de acceso y navegación asistida con integración Flutter ↔ Unity.

---

## Tabla de contenido

- [Resumen ejecutivo](#resumen-ejecutivo)
- [Objetivo del sistema](#objetivo-del-sistema)
- [Arquitectura general](#arquitectura-general)
- [Arquitectura técnica](#arquitectura-técnica)
- [Tecnologías y dependencias](#tecnologías-y-dependencias)
- [Estructura del proyecto](#estructura-del-proyecto)
- [Módulos funcionales](#módulos-funcionales)
- [Lógica de negocio y flujos](#lógica-de-negocio-y-flujos)
- [Configuración de entorno](#configuración-de-entorno)
- [Instalación y ejecución](#instalación-y-ejecución)
- [Pruebas y validación](#pruebas-y-validación)
- [Seguridad, privacidad y accesibilidad](#seguridad-privacidad-y-accesibilidad)
- [Integración con backend y Unity](#integración-con-backend-y-unity)
- [Limitaciones actuales y mejoras recomendadas](#limitaciones-actuales-y-mejoras-recomendadas)
- [Uso de este README como referencia de trabajo de grado](#uso-de-este-readme-como-referencia-de-trabajo-de-grado)
- [Créditos y licencia](#créditos-y-licencia)

---

## Resumen ejecutivo

COMPAS Mobile es un cliente Flutter multiplataforma cuyo foco principal es Android/iOS para interacción en tiempo real mediante voz y asistencia de navegación.

### Funcionalidades activas

- Autenticación accesible: registro, login y recuperación por código de 6 dígitos.
- Persistencia de sesión con `flutter_secure_storage`, validación local y refresh automático.
- Pantalla principal AR tras autenticación.
- Coordinación de voz: `NavigationCoordinator`, `speech_to_text` y `flutter_tts`.
- Wake word basada en STT (`oye compas` y variantes).
- Modos IA: `auto`, `online` y `offline`, con verificación de conectividad y Groq.
- Integración Unity: cargar sesión, listar balizas, iniciar/detener navegación y recibir eventos de tracking/TTS.
- Reconocimiento de entorno: demo orientado a UX/accesibilidad.

### Funcionalidades heredadas / en transición

- Dependencia de `google_generative_ai` (Gemini) — no usada actualmente.
- Wake word Picovoice/Porcupine — assets presentes, flujo activo usa STT.
- Reconocimiento de entorno: demo, sin inferencia visual real.

---

## Objetivo del sistema

### Objetivo general

Proveer interfaz accesible para navegación asistida, combinando voz, retroalimentación auditiva/visual e integración AR (Unity).

### Objetivos específicos

- Reducir fricción mediante comandos de voz y activación por palabra clave.
- Mantener funcionalidad degradada offline.
- Exponer estados accesibles (semántica, háptica, mensajes claros).
- Sincronizar intents de usuario con acciones de navegación en Unity.

---

## Arquitectura general

- Entrada: `main.dart` carga `.env`, fija orientación y monta `AuthGate`.
- `AuthGate` decide entre `WelcomeScreen` o `ArNavigationScreen` según tokens.

### Capas principales

1. **Presentación (UI Flutter):** pantallas de autenticación, AR y reconocimiento de entorno.
2. **Orquestación de dominio:** `NavigationCoordinator`, `ConversationService`, `AIModeController`, `VoiceNavigationService`.
3. **Servicios de infraestructura:** STT/TTS, wake word, cliente HTTP, almacenamiento de tokens, bridge Unity.
4. **Integraciones externas:** Backend REST, API Groq, Porcupine, motor Unity.

### Patrones identificados

- Singleton services para voz/IA/Unity.
- Coordinator pattern para centralizar eventos de voz.
- State-driven UI con `StatefulWidget`, `ValueNotifier`, callbacks y streams.
- Fallback progresivo: online → offline/manual según disponibilidad.

---

## Arquitectura técnica

- `main.dart` carga `.env`, fuerza orientación vertical y monta `AuthGate`.
- `AuthGate` determina flujo según tokens locales y refresh token.
- Capas: UI → Coordinator → Servicios → Integraciones externas.

---

## Tecnologías y dependencias

### Flutter / Base

- Flutter `>=3.27.0`, Dart `>=3.8.0 <4.0.0`
- `provider`, `logger`, `flutter_dotenv`

### IA y voz

- `tflite_flutter`, `speech_to_text`, `flutter_tts`, `porcupine_flutter`, `google_generative_ai`

### Audio

- `record`, `audioplayers`, `audio_session`

### Cámara y sensores

- `camera`, `proximity` (simulado)

### Networking y datos

- `http`, `dio`, `connectivity_plus`
- `flutter_secure_storage`, `shared_preferences`, `path_provider`

### Integración AR

- `flutter_unity_widget` (branch experimental Unity 6)

### Permisos

- `permission_handler`

---

## Estructura del proyecto

```text
lib/
  app/
  config/
    api_config.dart
  models/
    api_models.dart
    shared_models.dart
  screens/
    auth/
      welcome_screen.dart
      login_screen_integrated.dart
      register_screen_integrated.dart
      request_new_code_screen.dart
    ar_navigation_screen.dart
    environment_recognition_screen.dart
    voice_navigation_screen.dart
  services/
    api_client.dart
    auth_service.dart
    token_service.dart
    tts_service.dart
    unity_bridge_service.dart
    voice_navigation_service.dart
    user_service.dart
    proximity_service.dart
    AI/
      ai_mode_controller.dart
      conversation_service.dart
      groq_service.dart
      integrated_voice_command_service.dart
      navigation_coordinator.dart
      portable_tokenizer.dart
      robot_fsm.dart
      stt_session_manager.dart
      voice_command_classifier.dart
      wake_word_service.dart
      waypoint_context_service.dart
  utils/
    password_validator.dart
  widgets/
    accessible_camera_button.dart

assets/
  images/
  models/
  wake_words/

test/
  groq_api_test.dart
  test_server_connection.dart