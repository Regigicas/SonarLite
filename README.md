# Sonar Lite

Sustituto ligero de la app SteelSeries GG para controlar **Sonar**. Reutiliza el driver,
el APO y el backend oficial (`SteelSeriesSonar.exe`), pero sin GGEZ / GG / Engine / Prism:
la app hace de "GG" para Sonar y ofrece su propia interfaz en la bandeja del sistema.

| | GG oficial | Sonar Lite |
|---|---|---|
| Procesos | 5 | 2 (SonarLite + SteelSeriesSonar) |
| RAM (working set) | ~900 MB | ~120 MB en reposo |

## Uso

```powershell
scripts\publish.ps1        # compila y publica en %LOCALAPPDATA%\Programs\SonarLite
```

- Al arrancar cierra GG si está abierto y lanza Sonar contra el GG embebido.
- Icono de bandeja: clic = abrir; menú = reiniciar Sonar, **Iniciar con Windows (en lugar de GG)**,
  **Volver a SteelSeries GG** (p. ej. para actualizar firmware) y Salir.
- `SonarLite.exe --minimized` arranca solo en la bandeja; `SonarLite.exe --exit` cierra la
  instancia en marcha limpiamente.
- Logs: `%LOCALAPPDATA%\SonarLite\logs\` (`sonarlite.log` y `sonar.log`).

Funciones: volumen y mute de master y de los 5 canales, ChatMix, dispositivo físico de cada
canal, preset de EQ por canal y enrutado de aplicaciones a canales.

## Limitaciones conocidas

- **Rueda física de ChatMix** del casco: llega vía SteelSeries Engine, que no se ejecuta.
- La edición detallada de EQ se hace desde GG (aquí solo se eligen presets).
- Solo aparecen en "Aplicaciones" las apps con una sesión de audio abierta en Windows.

## Estructura

- `src/SonarLite.Core` — `GgHost` (GG mínimo), `SonarProcess` (lanza/vigila Sonar), `SonarClient` (API REST).
- `src/SonarLite` — app WPF de bandeja.
- `src/FakeGG` + `scripts/*-headless.ps1` — modo sin interfaz, para pruebas.
- `docs/sonar-api.md` — arquitectura de Sonar y mapa de su API.
- `_decompiled/` — referencia local del código de SteelSeries (ignorado por git, no redistribuir).
