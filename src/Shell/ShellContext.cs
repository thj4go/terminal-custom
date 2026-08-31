using System.IO;

namespace TerminalCustom.Shell;

internal sealed class ShellContext
{
    private string? _previousDirectory;

    public EnvironmentManager Environment { get; }
    public HistoryManager History { get; }
    public string CurrentDirectory { get; private set; }
    public string UserName { get; } = System.Environment.UserName;
    public string ComputerName { get; } = System.Environment.MachineName;

    public ShellContext(string initialDirectory, EnvironmentManager? environment = null, bool persistHistory = false)
    {
        Environment = environment ?? new EnvironmentManager();
        History = persistHistory
            ? new HistoryManager(Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".terminal_history"))
            : new HistoryManager();
        CurrentDirectory = Path.GetFullPath(initialDirectory);
        Directory.SetCurrentDirectory(CurrentDirectory);
    }

    public string Prompt => $"{UserName} {CurrentDirectory}> ";

    public string ExpandTilde(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~') return path;
        string home = Environment.Get("USERPROFILE") ??
                      System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (path.Length == 1) return home;
        if (path[1] is '/' or '\\') return Path.Combine(home, path[2..]);
        return path;
    }

    public void ChangeDirectory(string path)
    {
        if (path == "-")
        {
            if (_previousDirectory is null)
                throw new DirectoryNotFoundException("Não há diretório anterior.");
            path = _previousDirectory;
        }

        string expanded = ExpandTilde(Environment.Expand(path));
        if (expanded is "..." or "....")
            expanded = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("..", expanded.Length - 1));

        string resolved;
        try { resolved = Path.GetFullPath(expanded, CurrentDirectory); }
        catch (Exception ex) { throw new DirectoryNotFoundException(ex.Message); }

        if (!Directory.Exists(resolved))
        {
            string? fuzzy = FuzzyFindDirectory(expanded);
            if (fuzzy is null) throw new DirectoryNotFoundException($"Diretório não encontrado: {resolved}");
            resolved = fuzzy;
        }

        string previous = CurrentDirectory;
        Directory.SetCurrentDirectory(resolved);
        _previousDirectory = previous;
        CurrentDirectory = Directory.GetCurrentDirectory();
    }

    public bool TryEnterDirectory(string path)
    {
        try { ChangeDirectory(path); return true; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public bool TryEnterDirectoryExact(string path)
    {
        try
        {
            string resolved = ResolvePath(path);
            if (!Directory.Exists(resolved)) return false;
            ChangeDirectory(resolved);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    public string ResolvePath(string path) => Path.GetFullPath(ExpandTilde(Environment.Expand(path)), CurrentDirectory);

    private string? FuzzyFindDirectory(string name)
    {
        if (name.IndexOfAny(['\\', '/']) >= 0) return null;
        try
        {
            string[] dirs = Directory.GetDirectories(CurrentDirectory);
            string? exact = dirs.FirstOrDefault(d =>
                Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            string[] contains = dirs.Where(d =>
                Path.GetFileName(d).Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (contains.Length == 1) return contains[0];

            var ranked = dirs
                .Select(d => (Path: d, Distance: EditDistance(name, Path.GetFileName(d))))
                .Where(item => item.Distance <= Math.Max(1, Math.Min(2, name.Length / 3)))
                .OrderBy(item => item.Distance)
                .ThenBy(item => Path.GetFileName(item.Path).Length)
                .ToArray();
            if (ranked.Length == 0) return null;
            if (ranked.Length == 1 || ranked[0].Distance < ranked[1].Distance) return ranked[0].Path;
        }
        catch { }
        return null;
    }

    private static int EditDistance(string left, string right)
    {
        left = left.ToLowerInvariant();
        right = right.ToLowerInvariant();
        int[,] distance = new int[left.Length + 1, right.Length + 1];
        for (int i = 0; i <= left.Length; i++) distance[i, 0] = i;
        for (int j = 0; j <= right.Length; j++) distance[0, j] = j;
        for (int i = 1; i <= left.Length; i++)
            for (int j = 1; j <= right.Length; j++)
                distance[i, j] = Math.Min(Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
        return distance[left.Length, right.Length];
    }
}
