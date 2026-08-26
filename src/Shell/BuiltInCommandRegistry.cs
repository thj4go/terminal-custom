namespace TerminalCustom.Shell;

internal sealed class BuiltInCommandRegistry
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cd"] = "cd", ["pwd"] = "pwd",
        ["dir"] = "dir", ["ls"] = "dir",
        ["cls"] = "clear", ["clear"] = "clear",
        ["echo"] = "echo",
        ["mkdir"] = "mkdir", ["md"] = "mkdir",
        ["rmdir"] = "rmdir", ["rd"] = "rmdir",
        ["del"] = "del", ["rm"] = "del",
        ["copy"] = "copy", ["cp"] = "copy",
        ["move"] = "move", ["mv"] = "move",
        ["type"] = "type", ["cat"] = "type",
        ["touch"] = "touch",
        ["where"] = "where", ["which"] = "where",
        ["set"] = "set", ["env"] = "set",
        ["history"] = "history", ["help"] = "help", ["exit"] = "exit",
        ["ai-key"] = "ai-key", ["ai-status"] = "ai-status", ["ai-prompt"] = "ai-prompt", ["ai"] = "ai"
    };

    public IEnumerable<string> Names => _aliases.Keys;
    public bool TryResolve(string name, out string canonical) => _aliases.TryGetValue(name, out canonical!);
}
