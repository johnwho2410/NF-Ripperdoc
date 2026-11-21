# Datenschutz-Konzept: Community-Absturzdatenbank

**Ziel:** Spieler teilen Absturz-Befunde, damit GameDoctor Ursachen über viele Nutzer hinweg sicherer erkennt.
**Grundsatz:** Aus den geteilten Daten darf sich **kein Rückschluss auf einzelne Nutzer** ziehen lassen – weder direkt
(Namen, Pfade, IDs) noch indirekt über Kombinationen von Merkmalen.

## Die eigentliche Gefahr: Kombinationen

Namen und Pfade zu entfernen ist der einfache Teil. Gefährlich sind **Quasi-Identifikatoren**: Merkmale, die einzeln
harmlos sind, in Kombination aber eine Person wiedererkennbar machen. Das stärkste Beispiel ist eine vollständige
Mod-Liste – mit hunderten Einträgen ist sie praktisch einzigartig und ließe sich z. B. mit Screenshots auf Discord
oder Nexus abgleichen. Genauso: exakte Hardware + exakte Uhrzeiten + exakte Spielposition.

Deshalb gilt für jedes Feld: **So grob, dass viele Spieler denselben Wert haben.**

## Was ein Fingerabdruck enthält (Schema 1)

| Feld | Beispiel | Warum unbedenklich |
|---|---|---|
| Spiel-Build, Patch | `3.0.5813403`, `2.5` | identisch für alle Spieler dieser Version |
| Absturzstelle | `Cyberpunk2077.exe+0x1ADE00` | Stelle im Programmcode, für alle gleich |
| Fehlerart | `AccessViolation/Read/Heap` | nur die Klasse, **nie** die Speicheradresse |
| Module auf dem Stack | `Boneware.dll` (+ Dateiversion, falls vorhanden) | nur die 0–3 beteiligten Mods, **keine Mod-Liste**, nur Dateiname |
| Laufzeit | `10-60m` | 4 grobe Klassen |
| Ladephase, VRAM fast voll, Out-of-Memory | `true`/`false` | Ja/Nein |
| Ort in der Spielwelt | `-3,1` (500-m-Raster) | Ort im **Spiel**, nicht des Spielers |
| Frameworks | RED4ext `1.31.1`, CET `1.38.1` | weit verbreitete Versionen |
| Grafikkarte | `NVIDIA`, `RTX 40` | Hersteller + Serie, **nie** das Modell |
| RAM | `16` | Klassen 8 / 16 / 32 / 64+ |
| Mod-Anzahl | `300+` | Klassen <50 / 50–149 / 150–299 / 300+ |
| Doppelzähl-Schutz | `7abe24f8…` | siehe unten |

**Nicht enthalten – niemals:** Benutzername, PC-Name, Pfade, E-Mail, IP-Adressen, Steam-/Nexus-IDs, Uhrzeiten oder
Datumsangaben, Crash-Dumps (Speicherabbilder!), Logs im Wortlaut, Mod-Listen, Spielstände, Screenshots, exakte Positionen.

## Doppelzähl-Schutz ohne Nutzer-ID

Wer denselben Spielstand 11× lädt und 11× abstürzt, soll die Statistik nicht 11× beeinflussen. Statt einer
Nutzer-ID gibt es:

```
DedupKey = HMAC-SHA256(lokaler Zufallsschlüssel, Build + Absturzstelle)   (gekürzt auf 96 Bit)
```

- Der Schlüssel wird lokal zufällig erzeugt, per Windows-DPAPI geschützt und **verlässt den PC nie**.
- Gleicher Spieler + gleiche Absturzstelle → gleicher Wert → zählt einmal.
- Gleicher Spieler + **andere** Absturzstelle → völlig anderer Wert → verschiedene Abstürze eines Spielers lassen
  sich **nicht** miteinander verknüpfen.
- „Neustart als Unbekannter“: Schlüssel neu würfeln (`FingerprintStore.ResetSecret`), danach ist auch dieser
  Zusammenhang gekappt.

## Technische Absicherung im Client

1. **Vergröberung im Code** (`FingerprintBuilder`) – exakte Werte werden gar nicht erst in den Fingerabdruck übernommen.
2. **Datenschutz-Wächter** (`PrivacyGuard`) prüft jeden fertigen Fingerabdruck auf Pfade, E-Mail- und IP-Adressen,
   GUIDs, Datums-/Uhrzeitangaben, Steam-IDs sowie Benutzername, PC-Name, Domäne und Profilordner dieses PCs.
   Schlägt er an, wird der Fingerabdruck **verworfen** – lieber kein Beitrag als ein unsicherer.
3. **Vorschau:** Bevor etwas geteilt wird, sieht der Nutzer exakt den Text, der gesendet würde (`gamedoctor fingerprint`).
4. **Opt-in:** Teilen ist ausgeschaltet, bis der Nutzer es ausdrücklich einschaltet. Jederzeit widerrufbar.

## Verbindliche Regeln für den späteren Server

Diese Regeln gelten, bevor eine Zeile Server-Code geschrieben wird:

1. **Keine IP-Adressen speichern** – auch nicht in Webserver-, Proxy- oder Fehlerprotokollen.
2. **Kein Empfangszeitpunkt genauer als die Kalenderwoche.**
3. **Keine Konten, keine Cookies, kein Tracking.**
4. **Mindestgröße für Auswertungen (k-Anonymität):** Ergebnisse zu einer Absturzstelle werden erst angezeigt, wenn
   mindestens **5 verschiedene DedupKeys** beigetragen haben. Seltene Kombinationen bleiben unsichtbar.
5. **Nur Zusammenfassungen ausliefern**, nie einzelne Fingerabdrücke.
6. **Schema-Prüfung auf dem Server:** Unbekannte Felder oder Werte außerhalb der erlaubten Klassen werden abgelehnt –
   so kann auch ein veränderter oder fehlerhafter Client keine zusätzlichen Daten einschleusen.
7. Datenschutzerklärung und Impressum vor dem Start; Hosting in der EU.

## Offene Frage: Vergleichswerte („Wie verbreitet ist eine Mod überhaupt?“)

Für eine faire Statistik muss man wissen, wie häufig eine Mod bei *allen* Spielern vorkommt – sonst wirken beliebte
Mods automatisch verdächtig. Eine vollständige Mod-Liste darf dafür aber **nicht** übertragen werden. Mögliche Wege
(noch zu entscheiden, bevor der Server gebaut wird):

- **Einzelzählungen ohne Zusammenhang:** Pro Mod nur „+1, ist installiert“, als getrennte, nicht verknüpfte Beiträge.
- **Rauschen (Randomized Response):** Jeder Eintrag wird mit einer festen Wahrscheinlichkeit zufällig verfälscht; auf
  die Masse gerechnet bleibt die Statistik korrekt, für den Einzelnen ist nichts beweisbar.
- **Nur verbreitete Mods:** Es werden nur Mods gezählt, die auf einer öffentlichen Liste bekannter Mods stehen.
