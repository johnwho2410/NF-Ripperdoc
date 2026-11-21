using GameDoctor.Core.Games.Cyberpunk;

namespace GameDoctor.Core.Community;

/// <summary>
/// Schritt-für-Schritt-Anleitungen, wie der Spieler einen Lösungsversuch selbst umsetzt –
/// passend zum erkannten Mod-Manager. GameDoctor selbst ändert nie etwas an den Mods.
/// </summary>
public static class FixInstructions
{
    public static List<string> For(FixAction action, string targetLabel, int? nexusModId, IReadOnlyList<string> targetFiles,
        ModInventory inventory, CyberpunkInstall? install)
    {
        // Wie heißt die Mod im Mod-Manager? (z. B. „Boneware-patch-2.5-ef91fad“ statt „z_Boneware (Boneware.dll)“)
        var package = targetFiles.Select(inventory.PackageNameForFile).OfType<string>().FirstOrDefault() ?? targetLabel;
        var nexus = nexusModId is int id ? $"https://www.nexusmods.com/cyberpunk2077/mods/{id}" : null;

        return action switch
        {
            FixAction.DisableMod => inventory.Manager switch
            {
                "Vortex" =>
                [
                    "Cyberpunk beenden.",
                    "Vortex öffnen und links auf „Mods“ gehen.",
                    $"Oben im Suchfeld nach „{package}“ suchen.",
                    "In der Spalte „Status“ den Schalter auf „Deaktiviert“ stellen.",
                    "Oben auf „Bereitstellen“ (Deploy) klicken – erst dann verschwinden die Dateien aus dem Spielordner.",
                    "Zurück zu GameDoctor und „Erneut prüfen“ klicken. Steht dort „Mod ist deaktiviert“, kannst du ganz normal spielen.",
                ],
                "Mod Organizer 2" =>
                [
                    "Cyberpunk beenden.",
                    "Mod Organizer 2 öffnen.",
                    $"In der Mod-Liste links das Häkchen bei „{package}“ entfernen.",
                    "Das Spiel künftig über Mod Organizer 2 starten („Ausführen“) – nur dann gilt die Änderung.",
                    "Zurück zu GameDoctor und „Erneut prüfen“ klicken.",
                ],
                _ => ManualDisable(targetFiles, install),
            },

            FixAction.UpdateMod =>
            [
                "Cyberpunk beenden.",
                nexus is not null ? $"Die Mod-Seite öffnen: {nexus} – im Reiter „Files“ die neueste Version herunterladen." : $"Die neueste Version von „{package}“ von der Mod-Seite herunterladen.",
                inventory.Manager switch
                {
                    "Vortex" => "Über „Mod Manager Download“ installiert Vortex die neue Version; danach auf „Bereitstellen“ klicken.",
                    "Mod Organizer 2" => "Über „Mod Manager Download“ in Mod Organizer 2 installieren und die neue Version aktivieren.",
                    _ => "Die alten Dateien durch die neuen ersetzen (vorher sichern).",
                },
                "Auf der Mod-Seite prüfen, ob auch Abhängigkeiten (RED4ext, Codeware, ArchiveXL, TweakXL …) ein Update brauchen.",
                "Danach ganz normal spielen – GameDoctor beobachtet, ob die Abstürze an dieser Stelle ausbleiben.",
            ],

            FixAction.ChangeSetting =>
            [
                "Die Änderung im Spiel bzw. in Windows vornehmen (falls noch nicht geschehen).",
                "Danach ganz normal spielen – GameDoctor beobachtet ab jetzt, ob die Abstürze an dieser Stelle ausbleiben.",
            ],

            _ => ["Die Änderung vornehmen und danach ganz normal spielen."],
        };
    }

    private static List<string> ManualDisable(IReadOnlyList<string> targetFiles, CyberpunkInstall? install)
    {
        var steps = new List<string>
        {
            "Cyberpunk beenden.",
            "Einen Sicherungsordner anlegen, z. B. „Cyberpunk Mods deaktiviert“ auf dem Desktop.",
        };
        var located = install is null ? [] : Locate(install, targetFiles).Take(8).ToList();
        if (located.Count > 0)
        {
            steps.Add("Diese Dateien aus dem Spielordner dorthin verschieben (nicht löschen – so kannst du sie später zurücklegen):");
            steps.AddRange(located.Select(f => "   • " + f));
            if (targetFiles.Count > located.Count) steps.Add($"   • … und weitere Dateien dieser Mod ({targetFiles.Count} insgesamt)");
        }
        else
        {
            steps.Add($"Die Dateien der Mod aus dem Spielordner dorthin verschieben: {string.Join(", ", targetFiles.Take(5))}{(targetFiles.Count > 5 ? " …" : "")}");
        }
        steps.Add("Zurück zu GameDoctor und „Erneut prüfen“ klicken.");
        return steps;
    }

    /// <summary>Relative Pfade (ab Spielordner) der gesuchten Dateien.</summary>
    private static IEnumerable<string> Locate(CyberpunkInstall install, IReadOnlyList<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { install.BinDir, install.Red4ExtDir, install.ArchiveModDir, install.RedscriptDir, Path.Combine(install.Root, "r6", "tweaks") })
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList(); } catch { continue; }
            foreach (var f in files)
                if (wanted.Contains(Path.GetFileName(f)))
                    yield return Path.GetRelativePath(install.Root, f);
        }
    }
}
