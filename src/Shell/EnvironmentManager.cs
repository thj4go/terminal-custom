using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TerminalCustom.Shell;

internal sealed partial class EnvironmentManager
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);

    public EnvironmentManager()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            _variables[(string)entry.Key] = entry.Value?.ToString() ?? string.Empty;
    }

    public string? Get(string name) => _variables.TryGetValue(name, out string? value) ? value : null;

    public void Set(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Nome de variável inválido.");
        _variables[name.Trim()] = value;
        Environment.SetEnvironmentVariable(name.Trim(), value, EnvironmentVariableTarget.Process);
    }

    public string Expand(string value) => VariablePattern().Replace(value, match =>
        Get(match.Groups[1].Value) ?? match.Value);

    public IReadOnlyDictionary<string, string> Snapshot() => _variables;

    public void ApplyTo(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Clear();
        foreach ((string name, string value) in _variables)
            startInfo.Environment[name] = value;
    }

    [GeneratedRegex("%([^%]+)%")]
    private static partial Regex VariablePattern();
}
