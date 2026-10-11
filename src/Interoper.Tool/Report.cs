using Interoper.Generator;

namespace Interoper.Tool;

/// <summary>
/// The run's report (design §5), <c>&lt;out&gt;/interoper-report.txt</c>: a header (the tool's version, the mode, the
/// namespace, the packages), one line per diagnostic sorted by code then subject, and the count per code.
/// </summary>
internal static class Report
{
    public const string FileName = "interoper-report.txt";

    public static string Text(string toolVersion, Api api, string ns, IEnumerable<InstalledPackage> packages, IReadOnlyList<Diagnostic> diagnostics)
    {
        var lines = new List<string>
        {
            $"Interoper {toolVersion}",
            $"api: {api.ToString().ToLowerInvariant()}",
            $"namespace: {ns}",
            $"packages: {string.Join(", ", packages)}",
            "",
        };
        var sorted = diagnostics
            .OrderBy(d => d.Code, StringComparer.Ordinal)
            .ThenBy(d => d.Subject, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList();
        if (sorted.Count == 0)
        {
            lines.Add("No diagnostics.");
        }
        else
        {
            lines.AddRange(sorted.Select(d => d.ToString()));
            lines.Add("");
            lines.AddRange(sorted.GroupBy(d => d.Code).Select(g => $"{g.Key}: {g.Count()}"));
            lines.Add($"total: {sorted.Count}");
        }
        return string.Join('\n', lines) + "\n";
    }
}
