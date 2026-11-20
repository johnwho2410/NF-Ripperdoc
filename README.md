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

1. Performance-Aufzeichnung (PresentMon, CPU/GPU-Limit-Erkennung)
2. Overlay
