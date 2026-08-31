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
        if (!_variables.ContainsKey("LASTEXITCODE")) _variables["LASTEXITCODE"] = "0";
        if (!_variables.ContainsKey("PYTHONUTF8")) _variables["PYTHONUTF8"] = "1";
        if (!_variables.ContainsKey("PYTHONIOENCODING")) _variables["PYTHONIOENCODING"] = "utf-8";
    }

    public string? Get(string name) => _variables.TryGetValue(name, out string? value) ? value : null;

    public void Set(string name, string value, bool export = true)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Nome de variável inválido.");
        string key = name.Trim();
        _variables[key] = value;
        if (export && key is not "LASTOUTPUT" and not "LASTTIME")
            Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.Process);
    }

    public void Unset(string name, bool export = true)
    {
        string key = name.Trim();
        _variables.Remove(key);
        if (export) Environment.SetEnvironmentVariable(key, null, EnvironmentVariableTarget.Process);
    }

    public string Expand(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        value = value.Replace("$?", Get("LASTEXITCODE") ?? "0", StringComparison.Ordinal);
        value = value.Replace("$_", Get("LASTOUTPUT") ?? string.Empty, StringComparison.Ordinal);
        value = value.Replace("$@", Get("ARGS") ?? string.Empty, StringComparison.Ordinal);
        value = PositionalArgumentPattern().Replace(value, match =>
            Get("ARG" + match.Groups[1].Value) ?? match.Value);
        value = PowerShellEnvironmentPattern().Replace(value, match =>
        {
            string name = match.Groups["plain"].Success
                ? match.Groups["plain"].Value
                : match.Groups["braced"].Value;
            return Get(name) ?? match.Value;
        });
        return VariablePattern().Replace(value, match => Get(match.Groups[1].Value) ?? match.Value);
    }

    public IReadOnlyDictionary<string, string> Snapshot() => _variables;

    public void ApplyTo(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Clear();
        foreach ((string name, string value) in _variables)
        {
            if (name is "LASTOUTPUT") continue;
            startInfo.Environment[name] = value;
        }
    }

    [GeneratedRegex("%([^%]+)%")]
    private static partial Regex VariablePattern();

    [GeneratedRegex(@"\$(?:env:(?<plain>[A-Za-z_][A-Za-z0-9_]*)|\{env:(?<braced>[^}]+)\})", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellEnvironmentPattern();

    [GeneratedRegex(@"\$([0-9]+)")]
    private static partial Regex PositionalArgumentPattern();
}
