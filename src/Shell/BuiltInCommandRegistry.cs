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
        ["ai-key"] = "ai-key", ["ai-status"] = "ai-status", ["ai-prompt"] = "ai-prompt", ["ai"] = "ai",
        ["next"] = "next-io", ["next-io"] = "next-io", ["ia"] = "next-io",
        ["alias"] = "alias", ["unalias"] = "unalias",
        ["jobs"] = "jobs", ["fg"] = "fg", ["kill"] = "kill",
        ["source"] = "source",
        ["take"] = "take",
        ["open"] = "open", ["start"] = "open", ["explorer"] = "open",
        ["up"] = "up",
        ["copyout"] = "copyout", ["clipout"] = "copyout",
        ["retry"] = "retry",
        ["find"] = "find", ["grep"] = "find",
        ["head"] = "head", ["tail"] = "tail", ["wc"] = "wc",
        ["sort"] = "sort", ["uniq"] = "uniq",
        ["whoami"] = "whoami", ["hostname"] = "hostname",
        ["date"] = "date", ["time"] = "time", ["sleep"] = "sleep"
    };

    public IEnumerable<string> Names => _aliases.Keys;
    public IEnumerable<string> CanonicalNames => _aliases.Values.Distinct(StringComparer.OrdinalIgnoreCase);
    public bool TryResolve(string name, out string canonical) => _aliases.TryGetValue(name, out canonical!);
}
