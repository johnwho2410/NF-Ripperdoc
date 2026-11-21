using System.Text.RegularExpressions;

namespace GameDoctor.Core.Community;

/// <summary>
/// Letzte Verteidigungslinie: prüft einen fertig serialisierten Fingerabdruck auf alles, was auf eine Person
/// oder einen PC hindeuten könnte. Findet sie etwas, wird der Fingerabdruck verworfen – lieber kein Beitrag als ein unsicherer.
/// </summary>
public static class PrivacyGuard
{
    private static readonly (string Name, Regex Pattern)[] Rules =
    [
        ("Dateipfad", new Regex(@"[A-Za-z]:[\\/]|\\\\[^\\]+\\|/home/|/Users/", RegexOptions.Compiled)),
        ("E-Mail-Adresse", new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled)),
        ("IP-Adresse", new Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b(?!\.\d)", RegexOptions.Compiled)),
        ("GUID/ID", new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled)),
        ("Datum/Uhrzeit", new Regex(@"\b20\d{2}-\d{2}-\d{2}|\b\d{1,2}\.\d{1,2}\.20\d{2}|\b\d{2}:\d{2}(:\d{2})?\b", RegexOptions.Compiled)),
        ("Steam-ID", new Regex(@"\b7656119\d{10}\b", RegexOptions.Compiled)),
    ];

    public sealed record Violation(string Rule, string Match);

    /// <param name="allowed">Bekannte, unbedenkliche Werte (z. B. Versionsnummern wie 1.2.3.4, die sonst wie eine IP aussähen).</param>
    public static IReadOnlyList<Violation> Check(string json, IEnumerable<string>? allowed = null)
    {
        var allow = new HashSet<string>(allowed ?? [], StringComparer.Ordinal);
        var found = new List<Violation>();
        foreach (var (name, pattern) in Rules)
            foreach (Match m in pattern.Matches(json))
                if (!allow.Contains(m.Value)) found.Add(new Violation(name, m.Value));

        // Persönliche Bezeichner dieses PCs dürfen nirgends auftauchen
        foreach (var (label, value) in PersonalValues())
            if (value.Length >= 3 && json.Contains(value, StringComparison.OrdinalIgnoreCase))
                found.Add(new Violation(label, value));
        return found;
    }

    private static IEnumerable<(string, string)> PersonalValues()
    {
        yield return ("Benutzername", Environment.UserName);
        yield return ("PC-Name", Environment.MachineName);
        yield return ("Domäne", Environment.UserDomainName);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) yield return ("Benutzerordner", Path.GetFileName(profile));
    }
}
