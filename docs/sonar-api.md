# Sonar: arquitectura y API local

Obtenido decompilando `SteelSeriesSonar.dll` y `SoundStage.*.dll` (GG 121.0.0 / Sonar 1.104.0)
y verificado contra el proceso en ejecución. El código decompilado vive en `_decompiled/`
(ignorado por git: es código propietario, solo como referencia local).

## Piezas

| Pieza | Papel |
|---|---|
| `SteelSeries-Sonar-VAD.sys` | Driver kernel: endpoints virtuales Gaming / Chat / Media / Aux / Microphone |
| `Sonar.APO.dll` (+ ONNX/DirectML) | DSP dentro de `audiodg` (EQ, surround, ClearCast IA) |
| `SteelSeriesSonar.exe` | Backend .NET 8 + ASP.NET Core: mezcla, routing, presets, API REST |
| GGEZ / GG / Engine / Prism | Lanzador, núcleo GG, hardware SteelSeries, RGB — **prescindibles** |

## Dependencia de Sonar con GG

Sonar recibe todo por variables de entorno (`ApplicationConfigurationService`):

| Variable | Uso |
|---|---|
| `GG_SERVER_ADDRESS` | Base HTTP de GG. Si es `http://` no se valida certificado |
| `GG_WS_ENDPOINT` | Websocket de eventos de GG |
| `GG_CERT_TEXT`, `GG_API_AUTH_TOKEN` | Solo necesarios contra el GG real (https) |
| `APP_DATA_PATH` | `C:\ProgramData\SteelSeries\GG\apps\sonar` (contiene `db\database.db`) |
| `ENVIRONMENT`, `GG_VERSION` | `production`, versión de GG |
| `SOUNDSTAGE_ENCRYPTED_WEB_SERVER_ADDRESS` | Fija el puerto de la API (si no, puerto aleatorio) |

El directorio de trabajo debe ser la carpeta de instalación de Sonar (busca
`driver\apoDriverPackage` y `db-migrations` en rutas relativas).

Llamadas a GG que **cierran Sonar** si fallan: `POST /subApps/register`, `POST /subApps/ready`,
conexión al websocket, y `GET /healthCheck` (cada 5 s; 3 fallos ⇒ se cierra como "Orphaned").
Respuesta esperada de health: `{"healthCheck":{"status":true,"db_read":true}}`.

Opcionales (fallan sin consecuencias): `/user`, `/locale`, `/analytics/track`, `/overlays`,
`/keyboardShortcuts`, `/v1/loadouts*`, `/v1/alerts/*`, Engine (`/subApps` → `engine`).

Eventos websocket relevantes (`{"event": "...", "data": ...}`):
- GG → Sonar: `EVENT_SHUTDOWN` (cierre limpio), `EVENT_DEVICE_CHATMIX` (rueda física de ChatMix,
  la emite Engine), `EVENT_KEYBOARD_SHORTCUT`, `EVENT_MM_DEVICE_METADATA`, `deviceConnected`.
- Sonar → GG: `EVENT_SONAR_STATUS`, `EVENT_SONAR_CHATMIX_DATA`, `EVENT_DEVICE_FETCH_CHATMIX`.

## API REST de Sonar (sin autenticación, solo loopback)

Valores de enums (se aceptan en camelCase):
- rol (`DeviceRole`): `game`, `chatRender`, `chatCapture`, `media`, `aux`, `master` (solo en volumen)
- redirección (`RedirectionId`): `game`, `chat`, `mic`, `media`, `aux`
- modo: `classic`, `stream` · stream id: `streaming`, `monitoring`, `mic` · dataFlow: `render`, `capture`

### Volumen y mute
| Método | Ruta |
|---|---|
| GET | `/volumeSettings/classic` |
| PUT | `/volumeSettings/classic/Master/Volume/{0..1}` · `/Master/Mute/{bool}` |
| PUT | `/volumeSettings/classic/{rol}/Volume/{0..1}` · `/{rol}/Mute/{bool}` |
| GET/PUT | `/volumeSettings/streamer` · `/streamer/{streamId}/{rol}/volume/{v}` · `/isMuted/{bool}` |

### ChatMix
| GET | `/v1/chatMix` → `{balance: -1..1, state}` |
|---|---|
| PUT | `/v1/chatMix?balance={-1..1}` |

### Enrutado de aplicaciones
| Método | Ruta |
|---|---|
| GET | `/AudioDeviceRouting` → sesiones de audio por endpoint (proceso, pid, estado) |
| PUT | `/AudioDeviceRouting/{dataFlow}/{deviceId}/{processId}` (deviceId = endpoint virtual de Sonar) |
| GET | `/AudioDeviceRouting/{dataFlow}/{processId}` → endpoint actual del proceso |

### Dispositivo físico de salida por canal
| Método | Ruta |
|---|---|
| GET | `/classicRedirections` |
| PUT | `/classicRedirections/{redirección}/deviceId/{mmDeviceId}` |
| PUT | `/classicRedirections/render/deviceId/{mmDeviceId}` (todos los de salida) |
| GET/PUT | `/streamRedirections`, `/streamRedirections/{id}/deviceId/{deviceId}`, `/{id}/redirections/{rol}/isEnabled/{bool}` |

### Presets / EQ
| Método | Ruta |
|---|---|
| GET | `/configs`, `/configs/selected`, `/configs/options`, `/presets/{vad}` |
| PUT | `/configs/{id}/select` |
| POST/PUT/DELETE | `/configs` (crear / modificar / borrar) |

### Otros
`/mode` (GET, PUT `/{id}`), `/audioDevices`, `/linkAll`, `/fallbackSettings/*`,
`/deviceOut/*`, `/audioSamples/*`, `/features/*`, `/v1/status`, websocket `/sock` (eventos hacia la UI).
