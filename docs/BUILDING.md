# Building NF-RIPPERDOC (GameDoctor) from source

NF-RIPPERDOC is a Windows desktop app written in C# (.NET 10, WPF). The release ZIP is built from this repository with the steps below.

## Requirements

- Windows 10 or 11, 64-bit
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (includes the Windows Desktop workload for WPF)
- Git

All NuGet dependencies are restored automatically:

| Package | Version | Used for |
|---|---|---|
| Anthropic | 13.12.0 | Optional AI analysis with the user's own API key |
| System.Diagnostics.EventLog | 10.0.0 | Reading the Windows event log (driver timeouts, hardware errors) |
| System.Security.Cryptography.ProtectedData | 10.0.0 | Encrypting the user's optional API key with Windows DPAPI |

## Build and run

```powershell
git clone <repository-url>
cd GameDoctor
dotnet build GameDoctor.slnx -c Release
dotnet run --project src/GameDoctor.App -c Release
```

## Build the release package

This is the exact command used for the release ZIP (self-contained, no single-file packing, no compression, no debug symbols):

```powershell
dotnet publish src/GameDoctor.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o artifacts/GameDoctor-0.1.0-win-x64-folder
```

The output folder contains `GameDoctor.exe` plus the .NET runtime files. Copy `docs/LIESMICH-README.txt` next to it and zip the folder.

## Project layout

| Folder | Content |
|---|---|
| `src/GameDoctor.Core` | Analysis engine: crash dump parser, mod/log analysis, performance analysis, localization (`Localization/en/*.json`) |
| `src/GameDoctor.App` | WPF user interface, skins (`Themes/`), icon and logo (`Assets/`) |
| `src/GameDoctor.Cli` | Command-line version of the analysis |
| `docs/` | Documentation |

## What the program does on the system (for reviewers)

- **Reads** the game's crash reports (`%LOCALAPPDATA%\REDEngine\ReportQueue`), game settings, and mod manager files in the game folder (Vortex/MO2/REDmod). The minidump files are parsed by the program's own parser (`Dump/MinidumpReader.cs`); no debugger APIs are used.
- **Reads** the Windows event log and system information (RAM, disk, page file).
- **Performance recording:** reads GPU sensors through NVIDIA's `nvml.dll` (installed with the graphics driver) and per-core CPU load through the Windows system information API. Frame data comes from Intel's open-source PresentMon.
- **PresentMon download:** only after the user confirms in a dialog. The URL is fixed in `Performance/PresentMonTool.cs` (official GitHub release v2.6.0) and the file is verified against a pinned SHA-256 hash before it is used.
- **In-game overlay:** a normal always-on-top window with a global hotkey (`RegisterHotKey`, Ctrl+Shift+O). It does not inject into or read the memory of the game.
- **Writes** only to `%LOCALAPPDATA%\GameDoctor` (settings, recordings, fix attempts). It never modifies game or mod files.
- **Network:** nothing is sent automatically. The only network use is the PresentMon download above and the optional AI analysis, which runs only after the user configures a provider and clicks the button.
