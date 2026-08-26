using System.IO;

namespace TerminalCustom.Shell;

internal sealed record ResolvedExecutable(string Path, bool RequiresCmd);

internal sealed class PathResolver(ShellContext context)
{
    public ResolvedExecutable? Resolve(string command)
    {
        command = context.Environment.Expand(command);
        IEnumerable<string> directories;
        string fileName;

        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            string fullPath = Path.GetFullPath(command, context.CurrentDirectory);
            directories = [Path.GetDirectoryName(fullPath) ?? context.CurrentDirectory];
            fileName = Path.GetFileName(fullPath);
        }
        else
        {
            string path = context.Environment.Get("PATH") ?? string.Empty;
            directories = new[] { context.CurrentDirectory }
                .Concat(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            fileName = command;
        }

        foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string candidate in CandidateNames(fileName))
            {
                string fullPath;
                try { fullPath = Path.GetFullPath(Path.Combine(directory.Trim('"'), candidate)); }
                catch (Exception) when (directory.Length > 0) { continue; }
                if (!File.Exists(fullPath)) continue;
                string extension = Path.GetExtension(fullPath);
                return new ResolvedExecutable(fullPath,
                    extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".bat", StringComparison.OrdinalIgnoreCase));
            }
        }
        return null;
    }

    public IEnumerable<string> DiscoverCommandNames()
    {
        string path = context.Environment.Get("PATH") ?? string.Empty;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(directory.Trim('"'))) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory.Trim('"')); }
            catch { continue; }
            foreach (string file in files)
            {
                string extension = Path.GetExtension(file);
                if (Extensions().Contains(extension, StringComparer.OrdinalIgnoreCase))
                    yield return Path.GetFileNameWithoutExtension(file);
            }
        }
    }

    private IEnumerable<string> CandidateNames(string command)
    {
        if (Path.HasExtension(command))
            return Extensions().Contains(Path.GetExtension(command), StringComparer.OrdinalIgnoreCase)
                ? [command]
                : [];
        return new[] { command }.Concat(Extensions().Select(extension => command + extension));
    }

    private string[] Extensions()
    {
        string pathExt = context.Environment.Get("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        return pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .ToArray();
    }
}
