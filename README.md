# GameDoctor

Absturz- und Performance-Analyse für Cyberpunk 2077 (später weitere Spiele).

## Stand

**Absturzanalyse (Kommandozeile)** – fertig als erste Version:

- liest alle Crashreports des Spiels (`%LOCALAPPDATA%\REDEngine\ReportQueue`)
- wertet die Minidumps selbst aus: abstürzendes Modul, Fehlerart, Mods auf dem Stack
- erkennt Muster über alle Abstürze (gleiche Absturzstelle, verdächtige Mods)
- liest Mod-Logs: redscript, RED4ext, Cyber Engine Tweaks, ArchiveXL, TweakXL, Codeware
- prüft die Windows-Ereignisanzeige (Treiber-Timeouts, Hardwarefehler, Neustarts)
- prüft das System (RAM, freier Speicher, SSD/HDD)

**Performance-Analyse** – erste Version:

- Frame-Daten über PresentMon (Intel, Open Source; wird nach Zustimmung heruntergeladen, SHA-256-geprüft)
- Sensoren: CPU je Kern, GPU-Last/VRAM/Temperatur/Takt/Drosselung über NVML (NVIDIA), RAM und Auslagerung
- Auswertung: Ø FPS, 1 %/0,1 % Low, Ruckler, CPU- vs. GPU-Limit, VRAM-/RAM-Engpässe, thermische Drosselung
- Empfehlungen auf Basis der echten Spieleinstellungen (`UserSettings.json`)
- Kommandozeile: `gamedoctor perf [--seconds 60]`

**KI-Prompt-Generator:** fasst alle Befunde (System, Abstürze, Muster, Mod-Logs, Mod-Liste, Performance) als
anonymisierten Text für ChatGPT, Claude, Gemini o. ä. zusammen. App: „KI-Prompt“ bzw. „KI-Prompt für diesen Absturz“,
Kommandozeile: `gamedoctor prompt [--out prompt.txt]`.

**Datenquellen:** GameDoctor durchsucht den PC nicht, sondern prüft bekannte Fundorte:

| Quelle | Erkennung |
|---|---|
| Spielordner | festgelegt → laufendes Spiel → Steam (alle Bibliotheken) / GOG / Epic / Xbox-Game-Pass → zuletzt gefunden |
| Mod-Liste | Vortex (`vortex.deployment.json`), Mod Organizer 2 (Instanz, `modlist.txt`, `meta.ini`), REDmod (`mods\`) |
| Mod-Logs | Spielordner und – bei MO2 – aktive Mod-Ordner und `overwrite` |
| Crashreports, Einstellungen | `%LOCALAPPDATA%\REDEngine\ReportQueue`, `%LOCALAPPDATA%\CD Projekt Red\Cyberpunk 2077` |

Im Tab „Mods & System“ zeigt die Übersicht „Datenquellen“, was gefunden wurde und was fehlt; dort lassen sich
Spielordner und MO2-Instanz festlegen oder (auf Klick) die Laufwerke durchsuchen. Kommandozeile:
`gamedoctor sources [--set-game <Ordner>] [--set-mo2 <Instanz>] [--clear] [--search]`.

## Benutzung

Oberfläche (Übersicht, Absturzliste mit Details, Mods & System):

```powershell
dotnet run -c Release --project src\GameDoctor.App
```

Kommandozeile:

```powershell
dotnet run -c Release --project src\GameDoctor.Cli
```

Optionen: `--game <Spielordner>`, `--days 30`, `--details 5`, `--out bericht.md`, `--dump <datei.dmp>` (technische Detailansicht).

## Aufbau

| Ordner | Inhalt |
|---|---|
| `src/GameDoctor.Core/Dump` | eigener Minidump-Leser |
| `src/GameDoctor.Core/Games/Cyberpunk` | Spielordner finden, Crashreports und Mod-Logs lesen |
| `src/GameDoctor.Core/Platform` | Windows-Ereignisanzeige |
| `src/GameDoctor.Core/Analysis` | Modul-Einordnung und Diagnoseregeln |
| `src/GameDoctor.Core/Report` | Markdown-Bericht |
| `src/GameDoctor.Cli` | Kommandozeilenprogramm |
| `src/GameDoctor.App` | WPF-Oberfläche |

## Nächste Schritte

1. Overlay
2. Sensoren für AMD/Intel-GPUs und CPU-Temperatur
