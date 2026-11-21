# Datenschutz-Konzept: Community-Absturzdatenbank

**Ziel:** Spieler teilen Absturz-Befunde, damit GameDoctor Ursachen über viele Nutzer hinweg sicherer erkennt.

**Zwei Stufen:**

| | Stufe 1 – anonym | Stufe 2 – mit Einwilligung |
|---|---|---|
| Inhalt | Absturz-Fingerabdrücke, einzelne Mod-Meldungen | zusätzlich die vollständige Mod-Liste |
| Personenbezug | keiner – kein Rückschluss auf einzelne Nutzer | ja (pseudonym): die Liste selbst ist ein Wiedererkennungsmerkmal |
| Rechtsgrundlage | keine nötig (anonyme Daten) | Einwilligung, Art. 6 Abs. 1 lit. a, Art. 7 DSGVO |
| Standard | aus, Opt-in | aus, separates Opt-in mit Vorschau der Liste |
| Mehrwert | Absturzstelle ↔ Mod-Zusammenhänge, Verbreitung von Mods | Konflikte zwischen **Kombinationen** von Mods |
| Status | **umgesetzt (Client)** | Konzept |

> Dieses Dokument beschreibt die technische Umsetzung nach bestem Wissen. Es ist keine Rechtsberatung – vor dem
> öffentlichen Start sollten Datenschutzerklärung und Stufe 2 fachkundig geprüft werden.

---

## Warum keine Mod-Listen in Stufe 1?

Namen und Pfade zu entfernen ist der einfache Teil. Gefährlich sind **Quasi-Identifikatoren**: Merkmale, die einzeln
harmlos sind, zusammen aber eine Person wiedererkennbar machen.

Eine vollständige Mod-Liste ist das stärkste Beispiel. Mit hunderten Einträgen aus zehntausenden möglichen Mods ist
sie praktisch einzigartig – und Spieler veröffentlichen ihre Listen selbst (Support-Fragen auf Discord, Reddit, Nexus,
Vortex-Collections). Ein Abgleich würde den Nutzer und alle seine Abstürze zuordenbar machen. Nach Erwägungsgrund 26
DSGVO wären die Daten damit **nicht anonym**. Deshalb gibt es Mod-Listen nur in Stufe 2, mit Einwilligung.

---

## Stufe 1 – anonym (umgesetzt)

### Grundsätze

1. **So grob, dass viele Spieler denselben Wert haben** – Klassen statt Messwerte.
2. **Keine Nutzer-ID.** Kein Wert verbindet zwei Meldungen desselben Spielers.
3. **Jede Meldung steht für sich** – eigener Schlüssel, zufällige Reihenfolge.
4. **Nur öffentliche Mod-Kennungen** (Nexus-Mod-ID). Private, selbstgebaute oder manuell installierte Mods – deren
   Namen persönliche Angaben enthalten könnten – werden nie übertragen.
5. **Im Zweifel nichts senden:** Ein Datenschutz-Wächter verwirft jede auffällige Meldung.

### Meldungsart A: Absturz-Fingerabdruck

| Feld | Beispiel | Warum unbedenklich |
|---|---|---|
| Spiel-Build, Patch | `3.0.5813403`, `2.5` | identisch für alle Spieler dieser Version |
| Absturzstelle | `Cyberpunk2077.exe+0x1ADE00` | Stelle im Programmcode, für alle gleich |
| Fehlerart | `AccessViolation/Read/Heap` | nur die Klasse, **nie** die Speicheradresse |
| Module auf dem Stack | `Boneware.dll` (+ Version, + Nexus-ID falls bekannt) | nur die 0–3 beteiligten Mods |
| Laufzeit | `10-60m` | 4 grobe Klassen |
| Ladephase, VRAM fast voll, Out-of-Memory | `true`/`false` | Ja/Nein |
| Ort in der Spielwelt | `-3,1` (500-m-Raster) | Ort im **Spiel**, nicht des Spielers |
| Frameworks | RED4ext `1.31.1`, CET `1.38.1` | weit verbreitete Versionen |
| Grafikkarte | `NVIDIA`, `RTX 40` | Hersteller + Serie, **nie** das Modell |
| RAM / Mod-Anzahl | `16` / `300+` | Klassen |
| Schlüssel | `7abe24f8…` | siehe „Doppelzähl-Schutz“ |

### Meldungsart B: „Bei Absturzstelle X war Mod Y installiert“

```json
{"Schema":1,"GameBuild":"3.0.5813403","Signature":"Cyberpunk2077.exe+0xB0C78A","NexusModId":7203,"ModVersion":"2.0.3","DedupKey":"5fe54a46…"}
```

Pro Absturzstelle eine Meldung je installierter öffentlicher Mod. Damit werden auch Mods erkennbar, die nie auf dem
Stack auftauchen (z. B. Archive-Mods wie Garagen oder Wohnungs-Deko), ohne dass je eine Liste übertragen wird.

### Meldungsart C: „Mod Y ist installiert“ (Verbreitung)

```json
{"Schema":1,"Patch":"2.5","NexusModId":33141,"ModVersion":"1.0.1","DedupKey":"32dc49a0…"}
```

Liefert den Vergleichswert: Wie verbreitet ist eine Mod überhaupt? Ohne ihn wirken beliebte Mods automatisch
verdächtig. Erst der Vergleich „bei Absturzstelle X haben 40 von 45 Spielern Mod Y, im Durchschnitt nur 8 %“ ergibt
einen echten Verdacht.

### Was nie übertragen wird

Benutzername, PC-Name, Pfade, E-Mail, IP-Adressen, Steam-/Nexus-Konten, Uhrzeiten oder Datumsangaben, Crash-Dumps
(Speicherabbilder!), Logs im Wortlaut, Spielstände, Screenshots, exakte Positionen, Mod-Listen, Namen privater Mods,
Vortex-Staging-Pfade, Installationszeiten, Vortex-Instanz-ID.

### Doppelzähl-Schutz ohne Nutzer-ID

Wer denselben Spielstand 11× lädt und 11× abstürzt, soll die Statistik nicht 11× beeinflussen. Statt einer
Nutzer-ID erhält **jede Meldung ihren eigenen Schlüssel**:

```
Schlüssel = HMAC-SHA256(lokales Geheimnis, Zweck | Inhalt)   (gekürzt auf 96 Bit)

Fingerabdruck:   Build | Absturzstelle
Paar-Meldung:    "pair"   | Build | Absturzstelle | Nexus-ID
Verbreitung:     "census" | Nexus-ID
```

- Das Geheimnis wird lokal zufällig erzeugt, per Windows-DPAPI geschützt und **verlässt den PC nie**.
- Dieselbe Meldung desselben Spielers → derselbe Schlüssel → zählt einmal.
- Verschiedene Meldungen desselben Spielers → völlig verschiedene Schlüssel → **nicht verknüpfbar**.
  (Geprüft mit echten Daten: 5.511 Meldungen, 5.511 verschiedene Schlüssel.)
- „Neustart als Unbekannter“: Geheimnis neu würfeln (`FingerprintStore.ResetSecret`).

### Technische Absicherung im Client

1. **Vergröberung im Code** (`FingerprintBuilder`) – exakte Werte gelangen gar nicht erst in eine Meldung.
2. **Nur öffentliche Mod-IDs** (`ModInventory`) aus Vortex' `vortex.deployment.json`; Mods ohne Nexus-ID fallen heraus.
3. **Datenschutz-Wächter** (`PrivacyGuard`) prüft jede einzelne Meldung auf Pfade, E-Mail- und IP-Adressen, GUIDs,
   Datums-/Uhrzeitangaben, Steam-IDs sowie Benutzername, PC-Name, Domäne und Profilordner dieses PCs. Treffer → Meldung
   wird verworfen.
4. **Zufällige Reihenfolge** (`CommunityPackage`) – nichts verrät, welche Meldungen zusammen entstanden sind.
5. **Vorschau:** `gamedoctor fingerprint [--out datei.json]` zeigt exakt, was gesendet würde.
6. **Opt-in:** ausgeschaltet, bis der Nutzer es aktiv einschaltet; jederzeit widerrufbar.

### Verbleibendes Restrisiko (ehrlich benannt)

- **Zeitliche Nähe beim Empfang:** Wer den eingehenden Datenstrom live mitschneidet, könnte vermuten, dass gleichzeitig
  eintreffende Meldungen zusammengehören. Gegenmaßnahmen: Meldungen werden gemischt, in mehreren Paketen mit zufälligen
  Abständen gesendet, der Server speichert keine Empfangszeiten und keine IP-Adressen (siehe Server-Regeln).
- **Sehr seltene Mods:** Eine Mod, die nur eine Handvoll Spieler nutzt, verrät mehr als eine verbreitete. Gegenmaßnahme:
  Auswertungen erst ab 5 verschiedenen Beiträgen (k-Anonymität).

---

## Stufe 2 – Mod-Liste mit Einwilligung (Konzept)

**Mehrwert:** Nur vollständige Listen zeigen Konflikte zwischen **Kombinationen** („mit Mod A *und* Mod B stürzt es ab,
mit jeder allein nicht“).

**Weil die Liste den Nutzer wiedererkennbar machen kann, gilt sie als personenbezogen (pseudonym).** Daraus folgt:

1. **Echte Einwilligung (Art. 7 DSGVO)**
   - standardmäßig aus, aktives Einschalten (kein vorausgewähltes Häkchen)
   - vor dem Einschalten: Vorschau der exakten Liste und ein Klartext-Hinweis, dass man über die Liste theoretisch
     wiedererkannt werden kann
   - freiwillig: GameDoctor funktioniert ohne Stufe 2 genauso
   - Widerruf so leicht wie die Zustimmung – ein Klick
2. **Löschen ohne Nutzerkonto:** GameDoctor erzeugt lokal einen zufälligen **Löschschlüssel**, der Server speichert nur
   dessen Hash. „Meine Daten löschen“ weist den Schlüssel nach → der Server löscht alle zugehörigen Listen. Auch
   Auskunft (Art. 15) läuft darüber. Kein Konto, keine E-Mail.
3. **Strikte Trennung von Stufe 1:** eigener Endpunkt, eigene Speicherung, **keine gemeinsamen Schlüssel**. Sonst
   würde eine Liste nachträglich die anonymen Meldungen desselben Spielers verknüpfbar machen.
4. **Nur öffentliche Mods:** wie in Stufe 1 nur Nexus-IDs + Versionen, keine Namen privater Mods; Datenschutz-Wächter.
5. **Speicherbegrenzung:** automatische Löschung nach 12 Monaten.
6. **Nur Statistiken ausgeben:** einzelne Listen werden niemals angezeigt oder weitergegeben; Kombinations-Auswertungen
   erst ab 5 Beiträgen.
7. Datenschutzerklärung mit Zweck, Speicherdauer, Rechten; Hosting in der EU.

---

## Verbindliche Regeln für den Server (beide Stufen)

Diese Regeln gelten, bevor eine Zeile Server-Code geschrieben wird:

1. **Keine IP-Adressen speichern** – auch nicht in Webserver-, Proxy-, CDN- oder Fehlerprotokollen.
2. **Kein Empfangszeitpunkt genauer als die Kalenderwoche.**
3. **Keine Konten, keine Cookies, kein Tracking.**
4. **k-Anonymität:** Ergebnisse zu einer Absturzstelle, Mod oder Kombination erst ab **5 verschiedenen Schlüsseln**.
5. **Nur Zusammenfassungen ausliefern**, nie einzelne Meldungen oder Listen.
6. **Schema-Prüfung:** Unbekannte Felder oder Werte außerhalb der erlaubten Klassen werden abgelehnt – so kann auch ein
   veränderter Client keine zusätzlichen Daten einschleusen.
7. **Stufe 1 und Stufe 2 getrennt** speichern und verarbeiten.
8. Datenschutzerklärung und Impressum vor dem Start; Hosting in der EU.

---

## Für Spieler ohne Vortex

Ohne `vortex.deployment.json` kennt GameDoctor keine öffentlichen Mod-IDs. Dann werden nur Absturz-Fingerabdrücke
geteilt (Meldungsart A), keine Mod-Meldungen. Eine spätere Erweiterung könnte andere Mod-Manager auslesen.
