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

Notas sobre el enrutado (verificado):
- Sonar usa `IAudioPolicyConfig::SetPersistedDefaultAudioEndpoint` (roles Multimedia y Console),
  la misma preferencia que "Volumen de la aplicación y preferencias de dispositivo" de Windows.
- Windows la guarda **por ruta del ejecutable** (`HKCU\Software\Microsoft\Internet Explorer\
  LowRegistry\Audio\PolicyConfig\PropertyStore`), así que sobrevive a reinicios de la app pero
  se pierde si la ruta cambia (p. ej. Discord en cada actualización).
- Con un proceso que aún no ha inicializado audio, la llamada devuelve 204 pero Windows la
  ignora: el primer momento útil es la apertura de la sesión de audio.
- Si la app ya tiene el stream abierto, puede no moverse hasta reiniciarse.
- `/sock` emite `SONAR_EVENT_AUDIO_SESSION_OPENED_DATA` / `..._CLOSED_DATA` con el mismo
  formato que un elemento de `GET /AudioDeviceRouting`.

### Volumen en dB

Cada canal es el volumen escalar del endpoint virtual (`SetMasterVolumeLevelScalar`, rango
[-96, 0] dB por software). La conversión sigue la curva "audio taper" de Windows, no 20·log10:
0,5 → -10,5 dB; 0,02 → -58,2 dB. Tabla medida en `src/SonarLite.Core/VolumeCurve.cs`.
El master no es un endpoint: reescala proporcionalmente los canales.

### Dispositivo físico de salida por canal
| Método | Ruta |
|---|---|
| GET | `/classicRedirections` |
| PUT | `/classicRedirections/{redirección}/deviceId/{mmDeviceId}` |
| PUT | `/classicRedirections/render/deviceId/{mmDeviceId}` (todos los de salida) |
| GET/PUT | `/streamRedirections`, `/streamRedirections/{id}/deviceId/{deviceId}`, `/{id}/redirections/{rol}/isEnabled/{bool}` |

### Activar / desactivar canales
| Método | Ruta |
|---|---|
| PUT | `/AudioInitialization/{rol}/isEnabled/{bool}` → 200 |

Es lo que usa GG para desactivar canales. Verificado:
- Sonar oculta o muestra el endpoint virtual del canal en Windows
  (`IPolicyConfig::SetEndpointVisibility`); desactivado, desaparece del panel de sonido de
  Windows, de `GET /audioDevices` y de `GET /AudioDeviceRouting`. No hay GET de estado: el
  canal está activo si su VAD aparece en `/audioDevices` (que solo lista endpoints activos).
- Al ver el cambio de estado del endpoint, Sonar guarda `IS_MEDIA_ENABLED` / `IS_AUX_ENABLED`
  (`key_value` de `database.db`) y en cada arranque reaplica ese estado. Solo persiste Media y
  Aux; también lo recoge si el endpoint se desactiva desde el panel de sonido de Windows.
- La API acepta cualquier rol, pero la interfaz de GG solo permite desactivar Media y Aux
  (Juego, Chat y Micrófono son los canales principales y no se persistirían).
- Desactivar es asíncrono (la petición vuelve antes de que desaparezca el endpoint); activar
  espera a que el endpoint esté activo.
- Volúmenes (`/volumeSettings/classic`), presets y `/classicRedirections` siguen incluyendo el
  canal desactivado.

### Presets / EQ
| Método | Ruta |
|---|---|
| GET | `/configs?vad={vad}` (todos los del dispositivo), `/configs/selected` (el seleccionado de cada uno), `/configs/options`, `/presets/{vad}` |
| PUT | `/configs/{id}/select` |
| POST | `/configs` (crear) · `/configs/{id}/isFavorite/{bool}` · `/configs/{id}/setFavoritePosition/{0..8}` |
| PUT | `/configs` (modificar: config completo en el cuerpo) |
| DELETE | `/configs/{id}` |

vad: `game`, `chatRender`, `chatCapture`, `media`, `aux`. Un config (preset) es
`{id, name, virtualAudioDevice, schemaVersion, isPreset, image, isFavorite, favoritePosition,
data, defaultData, …}`. `data` son los ajustes de efectos y `defaultData`, los de restablecer. Los
presets de SteelSeries (`isPreset: true`) vienen de las migraciones SQL de Sonar (unos 345 en
`game`), así que `GET /configs?vad=game` pesa del orden de 1 MB.

Verificado contra el proceso:
- `PUT /configs` reemplaza el config entero. `schemaVersion` debe ser la actual (o `-1`). Si el
  config está seleccionado, Sonar lo aplica al momento. El `updatedAt` de la respuesta es el
  anterior, aunque se guarda bien.
- `POST /configs` ignora el `id` y crea uno nuevo. Pone `isFavorite: false`. Sin `image`, pone
  el icono por defecto del dispositivo (`8.svg` en game). Sin `data` (y con `schemaVersion: -1`),
  usa los valores neutros de Sonar, que también pasan a ser su `defaultData`.
- `DELETE /configs/{id}`: si estaba seleccionado, Sonar selecciona el preset por defecto del
  dispositivo ("Flat" en game). **No protege los presets de SteelSeries**: borrar uno es
  definitivo, porque solo las migraciones los insertan y no se vuelven a crear.
- Favoritos: máximo 9 por dispositivo (`/configs/options` → `isFavoriteLimitReached`). Por
  encima del límite, la petición se ignora sin error.
- La interfaz de GG no se refresca si el preset seleccionado cambia por la API.

En el código no hay eventos `/sock` de altas, bajas ni renombrados (solo
`SONAR_EVENT_FAVORITE_CONFIGS_UPDATED`), así que el cliente debe releer la lista tras cambiarla.

Cómo lo hace GG (y SonarLite igual):
- Edita el preset seleccionado en el sitio, también los de SteelSeries; cada cambio es un `PUT`.
- "Restablecer" copia `defaultData` en `data`.
- "Duplicar" hace un `POST` con el `data` del original y el nombre `"<nombre> *"` (añade `" *"`
  hasta que quede libre), y lo selecciona. La copia de un preset de SteelSeries lleva el icono por
  defecto del dispositivo, y su `defaultData` son los valores neutros, no los del original.
- No deja renombrar ni borrar presets de SteelSeries, ni borrar el último preset propio de un
  dispositivo. Los nombres no se repiten por dispositivo.
- Liga `bassBoostState` / `voiceClarityState` / `trebleBoostState`.`enabled` a
  `parametricEQ.enabled` (al cambiar el EQ o el valor de un refuerzo).

Ajustes de `data` y rangos de la interfaz de GG:

| Dispositivo | Ruta en `data` | Rango |
|---|---|---|
| game, media, aux | `parametricEQ.enabled`, `parametricEQ.filter1..10` `{enabled, frequency, gain, qFactor, type}` | 20–20000 Hz, ±12 dB, Q 0,5–10 |
| | `bassBoostState` / `voiceClarityState` / `trebleBoostState` `{enabled, value}` | ±12 dB |
| | `virtualSurroundState`, `formFactor` (`headphones` / `speakers`), `reverbGainDB`, `virtualSurroundChannels.*` | |
| | `generalGain` | 0–12 dB |
| | `smartVolume` `{enabled, volumeLevel, loudness}` | nivel 0–1 |
| chatRender, chatCapture | `parametricEQ.*` (sin refuerzos) | como arriba |
| | `noiseCancelingState` `{enabled, value}` (ClearCast IA) | 0–1, ver abajo |
| | `noiseReductionState`, `impactNoiseReductionState` `{enabled, value}` | 0–1; GG los desactiva con ClearCast activo |
| | `noiseGateState` `{enabled, value}`, `automaticNoiseGateState.enabled` | −60 a −10 dB; el automático ignora el umbral |
| | `volumeStabilizerState` `{enabled, value}` | 0–1 |
| chatCapture | `acousticEchoCancelingState` | solo con la función desbloqueada |

Ecualizador paramétrico (`parametricEQ`), verificado contra el proceso con un preset de prueba:
- Cada `filterN` es `{enabled, frequency, gain, qFactor, type}`. Un `PUT /configs` con bandas
  modificadas se aplica al momento si el preset está seleccionado, y se guarda tal cual
  (por ejemplo 182,5 Hz o Q 1,87).
- `type` admite los 10 valores de `Filter.FilterType` de Sonar: `byPass`, `lowPass`, `highPass`,
  `bandPassPeakQ`, `bandPassPeak0dB`, `notchFilter`, `allPass`, `peakingEQ`, `lowShelving` y
  `highShelving`. La interfaz de GG solo ofrece seis (`peakingEQ`, `lowShelving`, `highShelving`,
  `lowPass`, `highPass`, `notchFilter`), y son también los únicos que usan los presets de
  SteelSeries (en `game`: unos 3300 `peakingEQ`, 64 `highPass`, 49 `highShelving`, 6
  `lowShelving`, 5 `lowPass` y 1 `notchFilter`).
- Los presets de SteelSeries cubren todo el rango de GG (20–20000 Hz, ±12 dB, Q 0,5–10). Los
  valores neutros de `game` (los de `POST /configs` sin `data`) son 10 campanas con ganancia 0 y
  Q 0,7071, de las que solo están activas las 5 primeras (35, 120, 1000, 6000 y 18000 Hz).
- La ganancia solo influye en campana y estantes. Los refuerzos de graves, voz y agudos no son
  bandas: Sonar los pasa al APO como ajustes propios (`kSet_BassBoostGainDB`, …), así que la
  forma de su filtro no se ve desde la API.

GG muestra la intensidad de ClearCast en porcentaje logarítmico:
`% = 100 · 10^((60·v − 60) / 20)`, de modo que el valor por defecto 0,9 se ve como 50 %.

### Otros
`/mode` (GET, PUT `/{id}`), `/audioDevices`, `/linkAll`, `/fallbackSettings/*`,
`/deviceOut/*`, `/audioSamples/*`, `/features/*`, `/v1/status`, websocket `/sock` (eventos hacia la UI).
