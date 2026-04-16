# Integración de IndoorNavAR (Unity) con Flutter (`flutter_voice_robot`)

Esta guía integra tu proyecto Unity (`Compas_AR`) dentro de tu app Flutter Android con enfoque en accesibilidad y comandos por voz.

## 1) Estrategia recomendada

Usar **Unity as a Library (UaaL)** e incrustar la vista Unity en Android dentro de Flutter.

- Flutter mantiene la UX principal (voz, wake word, TTS, accesibilidad).
- Unity se ejecuta como módulo para AR + navegación interior.
- El intercambio de comandos se hace con `MethodChannel` (Flutter ↔ Kotlin) y `UnitySendMessage` (Kotlin → Unity).

---

## 2) Compatibilidad base con tu app Flutter

Tu app ya usa:

- `minSdk = 26`
- `compileSdk = 36`
- `targetSdk = 36`
- Kotlin + Gradle KTS

Recomendación para evitar conflictos:

- Exportar Unity con la misma versión de SDK (o al menos compatible con 26+).
- Unificar versiones de AndroidX/Kotlin en el módulo app.
- Mantener `multiDexEnabled = true` (ya lo tienes).

---

## 3) Cambios en Unity (este repo)

Se añadió el receptor de comandos:

- `Assets/IndoorNavAR/Scripts/Integration/FlutterUnityBridge.cs`

### Qué hace

Recibe JSON con acciones desde Flutter y ejecuta:

- `navigate_to_waypoint`
- `add_waypoint`
- `clear_waypoints`
- `save_session`
- `load_session`

### Configuración en escena

1. En `Assets/Scenes/Navegacion.unity`, crea un GameObject llamado **`FlutterBridge`**.
2. Añade componente **`FlutterUnityBridge`**.
3. (Opcional) Asigna referencias manuales (`WaypointManager`, `NavigationManager`, `PersistenceManager`). Si no, se autodescubren.

---

## 4) Exportar Unity como módulo Android

En Unity:

1. `File > Build Settings > Android > Switch Platform`.
2. `Player Settings`:
   - Scripting Backend: **IL2CPP**
   - Target Architectures: **ARM64** (y ARMv7 solo si lo necesitas)
   - Min API Level: **26**
3. Build con opción **Export Project**.
4. Obtendrás típicamente módulos: `unityLibrary` y launcher.

En Flutter Android:

1. Copia `unityLibrary/` dentro de `android/` de Flutter.
2. En `android/settings.gradle.kts` agrega:

```kotlin
include(":unityLibrary")
project(":unityLibrary").projectDir = File(rootDir, "unityLibrary")
```

3. En `android/app/build.gradle.kts` agrega dependencia:

```kotlin
dependencies {
    implementation(project(":unityLibrary"))
}
```

---

## 5) Bridge Android (Kotlin) para Flutter ↔ Unity

Crear `android/app/src/main/kotlin/.../MainActivity.kt` (o extender el existente):

```kotlin
package com.example.flutter_voice_robot

import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import com.unity3d.player.UnityPlayer

class MainActivity : FlutterActivity() {
    private val channelName = "compas/unity"

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)

        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, channelName)
            .setMethodCallHandler { call, result ->
                when (call.method) {
                    "sendCommand" -> {
                        val json = call.argument<String>("json") ?: ""
                        UnityPlayer.UnitySendMessage("FlutterBridge", "OnFlutterCommand", json)
                        result.success(true)
                    }
                    else -> result.notImplemented()
                }
            }
    }
}
```

---

## 6) Cliente Flutter para enviar comandos

Crear `lib/services/unity_bridge_service.dart`:

```dart
import 'dart:convert';
import 'package:flutter/services.dart';

class UnityBridgeService {
  static const MethodChannel _channel = MethodChannel('compas/unity');

  Future<void> sendCommand(Map<String, dynamic> cmd) async {
    final jsonCmd = jsonEncode(cmd);
    await _channel.invokeMethod('sendCommand', {'json': jsonCmd});
  }

  Future<void> navigateToWaypoint(String waypointName) async {
    await sendCommand({
      'action': 'navigate_to_waypoint',
      'waypointName': waypointName,
    });
  }

  Future<void> addWaypoint(double x, double y, double z) async {
    await sendCommand({
      'action': 'add_waypoint',
      'x': x,
      'y': y,
      'z': z,
    });
  }

  Future<void> saveSession() async => sendCommand({'action': 'save_session'});
  Future<void> loadSession() async => sendCommand({'action': 'load_session'});
}
```

Ejemplo desde tu `VoiceNavigationScreen`:

```dart
final unity = UnityBridgeService();
await unity.navigateToWaypoint('Entrada Principal');
```

---

## 7) Permisos Android recomendados

En `AndroidManifest.xml` de Flutter y/o unityLibrary verifica:

- `CAMERA`
- `RECORD_AUDIO`
- `INTERNET`
- `ACCESS_NETWORK_STATE`

Y si usas ARCore, asegurarte de metadatos/feature requeridos por Unity en el merge del manifest.

---

## 8) Flujo sugerido de producto (accesible)

1. Usuario da comando de voz en Flutter (`speech_to_text`, `porcupine`).
2. Flutter interpreta intención (local/offline o Gemini).
3. Si es navegación AR, Flutter envía comando JSON a Unity (`MethodChannel`).
4. Unity ejecuta ruta indoor y publica feedback visual/toast.
5. Flutter anuncia estado por TTS para accesibilidad.

---

## 9) Problemas comunes y solución

1. **No carga Unity en Flutter**
   - Revisar `include(":unityLibrary")` y dependencia `implementation(project(":unityLibrary"))`.

2. **`UnitySendMessage` no hace nada**
   - Confirmar GameObject exactamente `FlutterBridge`.
   - Confirmar método público `OnFlutterCommand(string)`.

3. **Conflictos de Gradle / AndroidX**
   - Forzar versiones (como ya haces en `resolutionStrategy`).
   - Ajustar `compileSdk/minSdk/targetSdk` para todos los módulos.

4. **Comandos llegan pero no navega**
   - Verificar que el `WaypointManager` tenga destinos cargados.
   - Verificar nombre exacto del waypoint enviado desde Flutter.

---

## 10) Siguiente paso recomendado

Crear un botón de prueba en Flutter:

- “Test Unity: Ir a Entrada”
- Envía `navigate_to_waypoint` y muestra confirmación TTS.

Con eso validas puente de extremo a extremo antes de conectar todo el pipeline de voz.
