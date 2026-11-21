# NF-RIPPERDOC (GameDoctor)

**Cyberpunk 2077 mod diagnostics – beta.** A standalone Windows app that reads your game's crash reports, points to suspect mods, checks your mod setup, measures FPS and bottlenecks, and suggests concrete next steps. It never changes your mods or game files.

*Deutsch: siehe [docs/LIESMICH-README.txt](docs/LIESMICH-README.txt).*

## Features

- **Crash analysis:** parses the game's crash dumps with its own parser; shows crash site, error type, time until crash, VRAM, GPU and driver; finds mods on the crashing thread's stack (a hint, not proof); detects patterns across crashes
- **Mod logs:** reads RED4ext, Cyber Engine Tweaks, redscript, ArchiveXL, TweakXL and Codeware logs; checks the Windows event log, RAM, disk space and page file
- **Try a fix:** pick a suspect, make the change yourself (Vortex, Mod Organizer 2 or manually), and GameDoctor watches whether the crash site comes back
- **Performance:** frame data via Intel's open-source [PresentMon](https://github.com/GameTechDev/PresentMon) (downloaded only after you confirm), GPU/CPU sensors, 1% / 0.1% lows, stutters, GPU-/CPU-bound analysis, optional in-game overlay (Ctrl+Shift+O, borderless windowed mode)
- **Mods & system overview:** installed mods with version and Nexus ID, data sources and what is missing; detects Steam, GOG, Epic and Xbox Game Pass installs and Vortex, Mod Organizer 2 and REDmod
- **Optional AI analysis:** an anonymized summary for ChatGPT, Claude, Gemini and others, or analysis inside the app with your own account or a local model – only when you click the button
- English and German interface (follows Windows, switchable), several skins

## Privacy

No telemetry, no account. Analysis runs on your PC. The only network use is the PresentMon download (after confirmation, SHA-256-verified) and the optional AI analysis. Settings and recordings are stored in `%LOCALAPPDATA%\GameDoctor`. See [docs/DATENSCHUTZ-COMMUNITY.md](docs/DATENSCHUTZ-COMMUNITY.md) (German) for the concept of the planned, anonymous community database; nothing of it is sent yet.

## Build from source

Requires Windows 10/11 and the .NET 10 SDK. See [docs/BUILDING.md](docs/BUILDING.md).

```powershell
dotnet build GameDoctor.slnx -c Release
dotnet run --project src/GameDoctor.App -c Release
```

## Status

Early beta, Cyberpunk 2077 only. Unofficial fan-made tool, not affiliated with or endorsed by CD PROJEKT RED. Cyberpunk 2077 is a trademark of CD PROJEKT S.A. PresentMon is by Intel (MIT license).

## License

Copyright (C) 2026 Noisefloor

This program is free software: you can redistribute it and/or modify it under the terms of the [GNU General Public License](LICENSE) as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the license for details.
