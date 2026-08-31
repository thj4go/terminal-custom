using System.Text;

namespace TerminalCustom.Shell;

internal sealed class AliasManager
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> All => _aliases;

    public void Set(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Nome de alias inválido.");
        _aliases[name.Trim()] = value;
    }

    public bool Remove(string name) => _aliases.Remove(name);

    public string? Get(string name) => _aliases.TryGetValue(name, out string? value) ? value : null;

    public string Expand(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return commandLine;
        int space = commandLine.IndexOf(' ');
        string name = space >= 0 ? commandLine[..space] : commandLine;
        string? expansion = Get(name);
        if (expansion is null) return commandLine;
        string args = space >= 0 ? commandLine[space..] : string.Empty;
        return expansion + args;
    }

    public string ListFormatted()
    {
        if (_aliases.Count == 0) return "Nenhum alias definido.";
        var sb = new StringBuilder();
        foreach (var pair in _aliases.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"{pair.Key}='{pair.Value}'");
        return sb.ToString().TrimEnd();
    }
}
