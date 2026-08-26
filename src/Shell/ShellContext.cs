using System.IO;

namespace TerminalCustom.Shell;

internal sealed class ShellContext
{
    public EnvironmentManager Environment { get; }
    public HistoryManager History { get; } = new();
    public string CurrentDirectory { get; private set; }
    public string UserName { get; } = System.Environment.UserName;
    public string ComputerName { get; } = System.Environment.MachineName;

    public ShellContext(string initialDirectory, EnvironmentManager? environment = null)
    {
        Environment = environment ?? new EnvironmentManager();
        CurrentDirectory = Path.GetFullPath(initialDirectory);
        Directory.SetCurrentDirectory(CurrentDirectory);
    }

    public string Prompt => $"{UserName} {CurrentDirectory}> ";

    public void ChangeDirectory(string path)
    {
        string expanded = Environment.Expand(path);
        string resolved = Path.GetFullPath(expanded, CurrentDirectory);
        if (!Directory.Exists(resolved)) throw new DirectoryNotFoundException($"Diretório não encontrado: {resolved}");
        Directory.SetCurrentDirectory(resolved);
        CurrentDirectory = Directory.GetCurrentDirectory();
    }

    public string ResolvePath(string path) => Path.GetFullPath(Environment.Expand(path), CurrentDirectory);
}
