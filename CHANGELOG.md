# Changelog

## 0.3.3 beta – Clearer recommendations

- Recommendations now **name the mods** they are about (for example "Update “ModX” first") instead of pointing at "the mods listed above".
- **Mod conflicts:** "Try a fix" opened from a conflict suggests the conflicting mods first and explains the conflict. If all mods involved in a crash are no longer installed, the finding is only informational.

## 0.3.2 beta – Name the conflicting mods

- **redscript conflicts** now name the mods involved and the game function they both replace (for example "modA ↔ modB: both replace VehicleComponent.SomeMethod"), including mods managed by Mod Organizer 2.

## 0.3.1 beta – Clearer results

- **Fix attempts:** once you mark an attempt as "helped" or "did not help", the card shows it as closed and explains what happens next, instead of staying on "Test running".
- **ArchiveXL** in the call path is no longer treated as a suspect (many mods use it). A crash directly inside ArchiveXL is only a warning, with advice to look at the mods that use it.
- **New time range "Today"** (since midnight) for the displayed crashes, the report and the AI prompt.
- **Removed mods:** crash modules of mods that are no longer installed are marked "no longer installed", left out of the suspect statistics and the mod search, and the AI prompt tells the AI to ignore them and use old crashes only for cross-checking.

## 0.3.0 beta – Help for players

- **"Your next step"** card on the overview: up to three concrete steps in plain language, most important first.
- **Simple view by default.** Findings are explained without jargon. The switch "Technical details" (status bar) shows addresses, registers, call paths and function names.
- **"Which mod is it?"** – guided search for the culprit mod. Each round suggests which mods to disable (halving the list), tells you how long to play, checks the result and can save the outcome as a fix attempt. You make every change yourself.
- Plain-language texts for the main crash findings.
- English translations for all new texts.

## 0.2.0 beta – Deeper crash analysis

- **Address classification** of the crash site (null access, invalid pointer, unmapped memory, inside game/mod code).
- **Register and object analysis** of the crash dump: pointer registers and class fingerprints of objects involved.
- **Optional function names** for crash sites and call paths, plus assignment to a game subsystem (e.g. senses, weapons, rendering) and an overview "Crashes by subsystem". Names come from the community project cp2077-symbols (MIT), are downloaded only after confirmation (about 59 MB, checksum-verified) and shown only if they match the installed game version.
- Function names and subsystem included in the Markdown report and the AI summary.
- Command line: `symbols` command, `--dump` prints addresses and function names.

## 0.1.0 beta – First public release

- **Redesign "NF-RIPPERDOC"** with new logo, app icon, tabs and cards.
- **German and English interface**, follows the Windows language, switchable at any time.
- Overview: **top problem signatures**; Mods & System: **table of installed mods** with version, Nexus ID and crash markers.
- **Crash analysis:** reads REDengine crash reports and dumps, finds mods on the crashing stack, detects recurring crash sites and mod coupling, checks logs of RED4ext, CET, redscript, ArchiveXL, TweakXL and Codeware, Windows event log, RAM, disk space and page file.
- **Performance analysis:** frame times via PresentMon (download only with consent), 1% / 0.1% lows, stutters, bottleneck detection (GPU / CPU / limiter), CPU, GPU, VRAM, temperature and clock sensors, recordings saved.
- **Try a fix:** clear instructions for Vortex, Mod Organizer 2 and manual installs, immediate check, automatic evaluation of the result.
- **In-game overlay** with live values and warnings (Ctrl+Shift+O, borderless windowed mode).
- **AI analysis** (optional): anonymized summary for copy and paste, or direct analysis with your own account (incl. Claude) or a local model.
- **Skins** "Night City" and "Neutral".
- Robust detection of Steam, GOG, Epic, Xbox Game Pass, Vortex, Mod Organizer 2 and REDmod, with a data-source overview.
- Anonymous crash fingerprint and public Nexus mod IDs for later community comparison.
- GPL-3.0 license, English README and build guide.
