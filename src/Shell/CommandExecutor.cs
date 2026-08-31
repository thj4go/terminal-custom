using System.Diagnostics;
using System.IO;
using System.Text;

namespace TerminalCustom.Shell;

internal sealed class CommandExecutor
{
    private readonly ShellContext _context;
    private readonly BuiltInCommandRegistry _builtIns;
    private readonly PathResolver _pathResolver;
    private readonly AliasManager _aliases;

    public Action<string>? OutputSink { get; set; }
    public Action<string>? ErrorSink { get; set; }

    public CommandExecutor(ShellContext context, BuiltInCommandRegistry builtIns, PathResolver pathResolver, AliasManager aliases)
    {
        _context = context;
        _builtIns = builtIns;
        _pathResolver = pathResolver;
        _aliases = aliases;
    }

    public bool TryResolveBuiltIn(string name, out string canonical) => _builtIns.TryResolve(name, out canonical);
    public ResolvedExecutable? ResolveExecutable(string name) => _pathResolver.Resolve(name);
    public IEnumerable<string> GetBuiltInNames() => _builtIns.CanonicalNames;
    public IEnumerable<string> GetExecutableNames() => _pathResolver.DiscoverCommandNames();

    public List<string> GetFilePathCompletions(string prefix)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(prefix))
        {
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(_context.CurrentDirectory))
                {
                    string name = Path.GetFileName(entry);
                    if (Directory.Exists(entry)) name += "\\";
                    results.Add(name);
                }
            }
            catch { }
            return results;
        }

        string expanded = _context.ExpandTilde(_context.Environment.Expand(prefix.Trim('"')));
        bool hasDir = expanded.Contains(Path.DirectorySeparatorChar) || expanded.Contains(Path.AltDirectorySeparatorChar);
        string dir;
        string pattern;
        if (hasDir)
        {
            string path = _context.ResolvePath(expanded);
            dir = Path.GetDirectoryName(path) ?? _context.CurrentDirectory;
            pattern = Path.GetFileName(path);
            if (!Directory.Exists(dir))
            {
                dir = _context.CurrentDirectory;
                pattern = Path.GetFileName(expanded);
            }
        }
        else
        {
            dir = _context.CurrentDirectory;
            pattern = expanded;
        }

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir, pattern + "*"))
            {
                string name = Path.GetFileName(entry);
                if (Directory.Exists(entry)) name += "\\";
                results.Add(name);
            }
        }
        catch { }
        return results;
    }

    public async Task<CommandExecutionResult> ExecuteBuiltInAsync(
        string canonical, CommandStage stage, string? pipelineInput = null, CancellationToken token = default)
    {
        string input = pipelineInput ?? await ReadInputRedirectAsync(stage, token);
        string[] args = stage.Arguments.Skip(1)
            .Select(_context.Environment.Expand)
            .Select(_context.ExpandTilde)
            .ToArray();
        if (canonical is not "set") args = ExpandGlobs(args);
        CommandExecutionResult result;
        try
        {
            result = canonical switch
            {
                "cd" => ChangeDirectory(args),
                "pwd" => Success(_context.CurrentDirectory),
                "dir" => ListDirectory(args),
                "clear" => new CommandExecutionResult(ClearRequested: true),
                "echo" => Success(string.Join(' ', args)),
                "mkdir" => MakeDirectories(args),
                "rmdir" => RemoveDirectories(args),
                "del" => DeleteFiles(args),
                "copy" => Copy(args),
                "move" => Move(args),
                "type" => await ReadFilesAsync(args, input, token),
                "touch" => Touch(args),
                "take" => Take(args),
                "open" => Open(args),
                "up" => Up(args),
                "where" => Locate(args),
                "set" => SetEnvironment(args),
                "history" => ShowHistory(),
                "find" => await FindTextAsync(args, input, token),
                "head" => await HeadOrTailAsync(args, input, takeLast: false, token),
                "tail" => await HeadOrTailAsync(args, input, takeLast: true, token),
                "wc" => await WordCountAsync(args, input, token),
                "sort" => await SortTextAsync(args, input, token),
                "uniq" => await UniqueTextAsync(args, input, token),
                "whoami" => Success($"{Environment.UserDomainName}\\{Environment.UserName}"),
                "hostname" => Success(Environment.MachineName),
                "date" => Success(DateTime.Now.ToString("yyyy-MM-dd")),
                "time" => Success(DateTime.Now.ToString("HH:mm:ss")),
                "sleep" => await SleepAsync(args, token),
                "help" => ShowHelp(),
                "exit" => new CommandExecutionResult(ExitRequested: true),
                _ => new CommandExecutionResult(1, Error: $"Built-in desconhecido: {canonical}")
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            result = new CommandExecutionResult(1, Error: ex.Message);
        }

        return await ApplyOutputRedirectAsync(stage, result, token);
    }

    public async Task<CommandExecutionResult> ExecutePipelineAsync(ParsedCommandLine commandLine, CancellationToken token)
    {
        bool allExternal = commandLine.Stages.All(stage =>
            !_builtIns.TryResolve(stage.Name, out _) && _pathResolver.Resolve(stage.Name) is not null);
        return allExternal
            ? await ExecuteExternalPipelineAsync(commandLine, token)
            : await ExecuteMaterializedPipelineAsync(commandLine, token);
    }

    public string? SuggestCommand(string unknown)
    {
        IEnumerable<string> candidates = _builtIns.Names.Concat(_pathResolver.DiscoverCommandNames())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return candidates
            .Select(candidate => (Name: candidate, Distance: EditDistance(unknown, candidate)))
            .Where(item => item.Distance <= Math.Max(1, Math.Min(2, unknown.Length / 3)))
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Name.Length)
            .Select(item => item.Name)
            .FirstOrDefault();
    }

    private async Task<CommandExecutionResult> ExecuteMaterializedPipelineAsync(ParsedCommandLine commandLine, CancellationToken token)
    {
        string? input = null;
        string errors = string.Empty;
        int exitCode = 0;
        foreach (CommandStage stage in commandLine.Stages)
        {
            CommandExecutionResult result;
            if (_builtIns.TryResolve(stage.Name, out string canonical))
                result = await ExecuteBuiltInAsync(canonical, stage, input, token);
            else
            {
                ResolvedExecutable? executable = _pathResolver.Resolve(stage.Name);
                if (executable is null) return new CommandExecutionResult(1, Error: $"Comando não encontrado: {stage.Name}");
                string redirectedInput = input ?? await ReadInputRedirectAsync(stage, token);
                bool last = ReferenceEquals(stage, commandLine.Stages[^1]);
                result = await ExecuteExternalCapturedAsync(executable, stage, redirectedInput, token, last);
                result = await ApplyOutputRedirectAsync(stage, result, token);
            }
            input = result.Output;
            errors += result.Error;
            exitCode = result.ExitCode;
            if (result.ExitRequested || result.ClearRequested || exitCode != 0)
                return result with { Error = errors };
        }
        return new CommandExecutionResult(exitCode, input ?? string.Empty, errors);
    }

    private async Task<CommandExecutionResult> ExecuteExternalPipelineAsync(ParsedCommandLine commandLine, CancellationToken token)
    {
        var processes = new List<Process>();
        var errors = new List<Task<string>>();
        try
        {
            foreach (CommandStage stage in commandLine.Stages)
            {
                ResolvedExecutable executable = _pathResolver.Resolve(stage.Name)!;
                Process process = new() { StartInfo = CreateStartInfo(executable, stage, redirect: true) };
                if (!process.Start()) throw new InvalidOperationException($"Não foi possível iniciar {stage.Name}.");
                processes.Add(process);
                errors.Add(process.StandardError.ReadToEndAsync(token));
            }

            var transfers = new List<Task>();
            string inputRedirect = await ReadInputRedirectAsync(commandLine.Stages[0], token);
            if (inputRedirect.Length > 0)
            {
                await processes[0].StandardInput.WriteAsync(inputRedirect.AsMemory(), token);
            }
            processes[0].StandardInput.Close();

            for (int index = 0; index < processes.Count - 1; index++)
            {
                Process source = processes[index];
                Process destination = processes[index + 1];
                transfers.Add(Task.Run(async () =>
                {
                    await source.StandardOutput.BaseStream.CopyToAsync(destination.StandardInput.BaseStream, token);
                    destination.StandardInput.Close();
                }, token));
            }

            bool stream = CanStream(commandLine.Stages[^1]);
            string output = await ReadStreamAsync(processes[^1].StandardOutput, stream ? OutputSink : null, token);
            await Task.WhenAll(transfers);
            await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(token)));
            string error = string.Concat(await Task.WhenAll(errors));
            int exitCode = processes[^1].ExitCode;
            CommandExecutionResult result = new(exitCode, output, error, Streamed: stream);
            return await ApplyOutputRedirectAsync(commandLine.Stages[^1], result, token);
        }
        catch (OperationCanceledException)
        {
            foreach (Process process in processes)
                try { if (!process.HasExited) process.Kill(true); } catch { }
            return new CommandExecutionResult(130, Error: "Operação cancelada.");
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }

    private async Task<CommandExecutionResult> ExecuteExternalCapturedAsync(
        ResolvedExecutable executable, CommandStage stage, string input, CancellationToken token, bool stream = true)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executable, stage, redirect: true) };
        try
        {
            process.Start();
            bool merge = HasMerge(stage);
            bool streamOut = stream && CanStream(stage);
            bool streamErr = streamOut && (merge || !HasErrorRedirect(stage));
            Task<string> outputTask = ReadStreamAsync(process.StandardOutput, streamOut ? OutputSink : null, token);
            Task<string> errorTask = ReadStreamAsync(process.StandardError,
                streamErr ? (merge ? OutputSink : ErrorSink) : null, token);
            if (input.Length > 0) await process.StandardInput.WriteAsync(input.AsMemory(), token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            string output = await outputTask;
            string error = await errorTask;
            if (merge) { output += error; error = string.Empty; }
            return new CommandExecutionResult(process.ExitCode, output, error, Streamed: streamOut);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            return new CommandExecutionResult(130, Error: "Operação cancelada.");
        }
    }

    public ProcessStartInfo CreateStartInfo(ResolvedExecutable executable, CommandStage stage, bool redirect)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable.RequiresCmd ? (_context.Environment.Get("COMSPEC") ?? "cmd.exe") : executable.Path,
            WorkingDirectory = _context.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = redirect,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            CreateNoWindow = redirect
        };
        if (redirect)
        {
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            startInfo.StandardInputEncoding = Encoding.UTF8;
        }
        _context.Environment.ApplyTo(startInfo);
        string[] args = ExpandGlobs(stage.Arguments.Skip(1)
            .Select(_context.Environment.Expand)
            .Select(_context.ExpandTilde)
            .ToArray());
        if (executable.RequiresCmd)
        {
            string script = WindowsCommandLine.BuildForCmd(executable.Path, args);
            startInfo.Arguments = $"/d /s /c \"{script}\"";
        }
        else
        {
            foreach (string argument in args)
                startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private CommandExecutionResult ChangeDirectory(string[] args)
    {
        string target = args.Length == 0
            ? (_context.Environment.Get("USERPROFILE") ?? _context.CurrentDirectory)
            : ResolveJoinedPath(args, directoriesOnly: true);
        _context.ChangeDirectory(target);
        return new CommandExecutionResult();
    }

    private CommandExecutionResult ListDirectory(string[] args)
    {
        string[] paths = args.Where(argument => !argument.StartsWith('-') && !argument.StartsWith('/')).ToArray();
        string target = paths.Length == 0 ? _context.CurrentDirectory : ResolveJoinedPath(paths, directoriesOnly: false);
        string path = _context.ResolvePath(target);
        if (File.Exists(path)) return Success(FormatFile(new FileInfo(path)));
        if (!Directory.Exists(path)) return Failure($"Caminho não encontrado: {path}");
        var output = new StringBuilder();
        foreach (string directory in Directory.EnumerateDirectories(path).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            output.AppendLine(FormatListEntry("<DIR>", Path.GetFileName(directory)));
        foreach (string file in Directory.EnumerateFiles(path).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            output.AppendLine(FormatFile(new FileInfo(file)));
        return new CommandExecutionResult(Output: output.ToString().TrimEnd());
    }

    private string FormatFile(FileInfo file) => FormatListEntry($"{file.Length,14:N0}", file.Name);

    private string FormatListEntry(string label, string name)
    {
        int columns = int.TryParse(_context.Environment.Get("COLUMNS"), out int parsed) ? parsed : 100;
        string prefix = label == "<DIR>" ? label.PadRight(14) + " " : label + " ";
        int available = Math.Max(8, columns - prefix.Length);
        string visibleName = name.Length <= available
            ? name
            : name[..Math.Max(1, available - 1)] + "…";
        return prefix + visibleName;
    }

    private CommandExecutionResult MakeDirectories(string[] args)
    {
        if (args.Length == 0) return Failure("Uso: mkdir <diretório>");
        foreach (string argument in args) Directory.CreateDirectory(_context.ResolvePath(argument));
        return new CommandExecutionResult();
    }

    private CommandExecutionResult RemoveDirectories(string[] args)
    {
        bool recursive = args.Any(argument => argument.Equals("-r", StringComparison.OrdinalIgnoreCase) ||
                                              argument.Equals("/s", StringComparison.OrdinalIgnoreCase));
        string[] targets = args.Where(argument => !argument.StartsWith('-') && !argument.StartsWith('/')).ToArray();
        if (targets.Length == 0) return Failure("Uso: rmdir [-r] <diretório>");
        foreach (string target in targets) Directory.Delete(_context.ResolvePath(target), recursive);
        return new CommandExecutionResult();
    }

    private CommandExecutionResult DeleteFiles(string[] args)
    {
        string[] targets = args.Where(argument => !argument.StartsWith('-') && !argument.StartsWith('/')).ToArray();
        if (targets.Length == 0) return Failure("Uso: del <arquivo>");
        foreach (string target in targets)
            foreach (string file in ExpandGlob(target)) File.Delete(file);
        return new CommandExecutionResult();
    }

    private CommandExecutionResult Copy(string[] args)
    {
        if (args.Length != 2) return Failure("Uso: copy <origem> <destino>");
        string source = _context.ResolvePath(args[0]);
        string destination = _context.ResolvePath(args[1]);
        if (Directory.Exists(destination)) destination = Path.Combine(destination, Path.GetFileName(source));
        File.Copy(source, destination, true);
        return Success("1 arquivo copiado.");
    }

    private CommandExecutionResult Move(string[] args)
    {
        if (args.Length != 2) return Failure("Uso: move <origem> <destino>");
        string source = _context.ResolvePath(args[0]);
        string destination = _context.ResolvePath(args[1]);
        if (File.Exists(source))
        {
            if (Directory.Exists(destination)) destination = Path.Combine(destination, Path.GetFileName(source));
            File.Move(source, destination, true);
        }
        else Directory.Move(source, destination);
        return new CommandExecutionResult();
    }

    private async Task<CommandExecutionResult> ReadFilesAsync(string[] args, string input, CancellationToken token)
    {
        if (args.Length == 0) return Success(input);
        var output = new StringBuilder();
        foreach (string argument in args)
            output.Append(await File.ReadAllTextAsync(_context.ResolvePath(argument), token));
        return Success(output.ToString());
    }

    private CommandExecutionResult Touch(string[] args)
    {
        if (args.Length == 0) return Failure("Uso: touch <arquivo>");
        foreach (string argument in args)
        {
            string path = _context.ResolvePath(argument);
            using (File.Open(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) { }
            File.SetLastWriteTime(path, DateTime.Now);
        }
        return new CommandExecutionResult();
    }

    private CommandExecutionResult Locate(string[] args)
    {
        if (args.Length == 0) return Failure("Uso: where <comando>");
        var output = new StringBuilder();
        foreach (string argument in args)
        {
            ResolvedExecutable? executable = _pathResolver.Resolve(argument);
            if (executable is null) return Failure($"Comando não encontrado: {argument}");
            output.AppendLine(executable.Path);
        }
        return Success(output.ToString().TrimEnd());
    }

    private CommandExecutionResult SetEnvironment(string[] args)
    {
        if (args.Length == 0)
            return Success(string.Join(Environment.NewLine, _context.Environment.Snapshot()
                .Where(pair => pair.Key is not "LASTOUTPUT")
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}")));
        string expression = string.Join(' ', args);
        int equals = expression.IndexOf('=');
        if (equals > 0)
        {
            _context.Environment.Set(expression[..equals], expression[(equals + 1)..]);
            return new CommandExecutionResult();
        }
        IEnumerable<KeyValuePair<string, string>> matches = _context.Environment.Snapshot()
            .Where(pair => pair.Key.StartsWith(expression, StringComparison.OrdinalIgnoreCase));
        return Success(string.Join(Environment.NewLine, matches.Select(pair => $"{pair.Key}={pair.Value}")));
    }

    private async Task<CommandExecutionResult> FindTextAsync(string[] args, string input, CancellationToken token)
    {
        bool invert = args.Any(value => value.Equals("-v", StringComparison.OrdinalIgnoreCase));
        bool numbered = args.Any(value => value.Equals("-n", StringComparison.OrdinalIgnoreCase));
        string[] values = args.Where(value => !value.Equals("-v", StringComparison.OrdinalIgnoreCase) &&
                                               !value.Equals("-n", StringComparison.OrdinalIgnoreCase) &&
                                               !value.Equals("-i", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (values.Length == 0) return Failure("Uso: find [-n] [-v] <texto> [arquivo...]");
        string pattern = values[0];
        string source = await ReadTextSourcesAsync(values.Skip(1).ToArray(), input, token);
        string[] lines = SplitLines(source);
        var matches = new List<string>();
        for (int index = 0; index < lines.Length; index++)
        {
            bool contains = lines[index].Contains(pattern, StringComparison.OrdinalIgnoreCase);
            if (contains == invert) continue;
            matches.Add(numbered ? $"{index + 1}:{lines[index]}" : lines[index]);
        }
        return new CommandExecutionResult(matches.Count > 0 ? 0 : 1, string.Join(Environment.NewLine, matches));
    }

    private async Task<CommandExecutionResult> HeadOrTailAsync(
        string[] args, string input, bool takeLast, CancellationToken token)
    {
        int count = 10;
        var files = new List<string>();
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "-n" && index + 1 < args.Length && int.TryParse(args[++index], out int parsed))
                count = parsed;
            else if (int.TryParse(args[index], out int direct)) count = direct;
            else files.Add(args[index]);
        }
        if (count < 0) return Failure("A quantidade de linhas não pode ser negativa.");
        string[] lines = SplitLines(await ReadTextSourcesAsync(files.ToArray(), input, token));
        IEnumerable<string> selected = takeLast ? lines.TakeLast(count) : lines.Take(count);
        return Success(string.Join(Environment.NewLine, selected));
    }

    private async Task<CommandExecutionResult> WordCountAsync(string[] args, string input, CancellationToken token)
    {
        string text = await ReadTextSourcesAsync(args, input, token);
        int lines = text.Length == 0 ? 0 : SplitLines(text).Length;
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return Success($"{lines,8} {words,8} {text.Length,8}");
    }

    private async Task<CommandExecutionResult> SortTextAsync(string[] args, string input, CancellationToken token)
    {
        bool reverse = args.Any(value => value.Equals("-r", StringComparison.OrdinalIgnoreCase));
        bool unique = args.Any(value => value.Equals("-u", StringComparison.OrdinalIgnoreCase));
        string[] files = args.Where(value => !value.Equals("-r", StringComparison.OrdinalIgnoreCase) &&
                                             !value.Equals("-u", StringComparison.OrdinalIgnoreCase)).ToArray();
        IEnumerable<string> lines = SplitLines(await ReadTextSourcesAsync(files, input, token))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        if (unique) lines = lines.Distinct(StringComparer.OrdinalIgnoreCase);
        if (reverse) lines = lines.Reverse();
        return Success(string.Join(Environment.NewLine, lines));
    }

    private async Task<CommandExecutionResult> UniqueTextAsync(string[] args, string input, CancellationToken token)
    {
        string[] lines = SplitLines(await ReadTextSourcesAsync(args, input, token));
        var result = new List<string>();
        string? previous = null;
        foreach (string line in lines)
        {
            if (previous is null || !line.Equals(previous, StringComparison.OrdinalIgnoreCase)) result.Add(line);
            previous = line;
        }
        return Success(string.Join(Environment.NewLine, result));
    }

    private static async Task<CommandExecutionResult> SleepAsync(string[] args, CancellationToken token)
    {
        if (args.Length != 1 || !double.TryParse(args[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seconds) || seconds < 0 || seconds > 86400)
            return Failure("Uso: sleep <segundos entre 0 e 86400>");
        await Task.Delay(TimeSpan.FromSeconds(seconds), token);
        return new CommandExecutionResult();
    }

    private async Task<string> ReadTextSourcesAsync(string[] files, string input, CancellationToken token)
    {
        if (files.Length == 0) return input;
        var output = new StringBuilder();
        foreach (string file in files)
        {
            if (output.Length > 0) output.AppendLine();
            output.Append(await File.ReadAllTextAsync(_context.ResolvePath(file), token));
        }
        return output.ToString();
    }

    private static string[] SplitLines(string text) => text
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Split('\n', StringSplitOptions.None)
        .SkipLast(text.EndsWith('\n') || text.EndsWith('\r') ? 1 : 0)
        .ToArray();

    private CommandExecutionResult ShowHistory() => Success(string.Join(Environment.NewLine,
        _context.History.Entries.Select((command, index) => $"{index + 1,4}  {command}")));

    private static CommandExecutionResult ShowHelp() => Success("""
        Built-ins:
          cd pwd dir/ls cls/clear echo mkdir/md rmdir/rd del/rm
          copy/cp move/mv type/cat touch where/which set/env
          history help exit ai ai-key ai-status ai-prompt
          alias unalias jobs fg kill source
          take open/start up copyout retry
          find/grep head tail wc sort uniq sleep
          whoami hostname date time

        Operadores: |  >  >>  <  2>  2>&1  &>  &&  ||  ;  &
        Expansões: ~  $?  $_  %VAR%  $env:VAR  $1..$9  $@  !!  !$  $()
        Scripts .tsh: execução direta, source, if/else/end e repeat/end
        cd -  (volta)   pasta sem comando  (entra sozinho)
        """);

    private string ResolveJoinedPath(string[] args, bool directoriesOnly)
    {
        if (args.Length == 1) return args[0];
        string joined = string.Join(' ', args);
        try
        {
            string resolved = _context.ResolvePath(joined);
            if (directoriesOnly ? Directory.Exists(resolved) : Directory.Exists(resolved) || File.Exists(resolved))
                return joined;
        }
        catch { }
        return args[0];
    }

    private CommandExecutionResult Take(string[] args)
    {
        if (args.Length == 0) return Failure("Uso: take <diretório>");
        string target = string.Join(' ', args);
        Directory.CreateDirectory(_context.ResolvePath(target));
        _context.ChangeDirectory(target);
        return Success(_context.CurrentDirectory);
    }

    private CommandExecutionResult Open(string[] args)
    {
        string target = args.Length == 0 ? _context.CurrentDirectory : ResolveJoinedPath(args, directoriesOnly: false);
        string path = _context.ResolvePath(target);
        if (!File.Exists(path) && !Directory.Exists(path)) return Failure($"Caminho não encontrado: {path}");
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        return new CommandExecutionResult();
    }

    private CommandExecutionResult Up(string[] args)
    {
        int levels = 1;
        if (args.Length > 0 && (!int.TryParse(args[0], out levels) || levels < 1))
            return Failure("Uso: up [n]");
        string path = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("..", levels));
        _context.ChangeDirectory(path);
        return Success(_context.CurrentDirectory);
    }

    private static bool HasGlob(string value) => value.Contains('*') || value.Contains('?');

    private string[] ExpandGlobs(string[] args)
    {
        var result = new List<string>();
        foreach (string arg in args)
        {
            if (!HasGlob(arg)) { result.Add(arg); continue; }
            string[] matches = ExpandGlob(arg).ToArray();
            string resolved = _context.ResolvePath(arg);
            if (matches.Length == 1 && string.Equals(matches[0], resolved, StringComparison.OrdinalIgnoreCase))
                result.Add(arg);
            else result.AddRange(matches);
        }
        return [.. result];
    }

    private IEnumerable<string> ExpandGlob(string target)
    {
        string path = _context.ResolvePath(target);
        string? directory = Path.GetDirectoryName(path);
        string pattern = Path.GetFileName(path);
        if (!HasGlob(pattern)) return [path];
        try
        {
            string[] files = Directory.GetFileSystemEntries(directory ?? _context.CurrentDirectory, pattern);
            return files.Length == 0 ? [path] : files;
        }
        catch { return [path]; }
    }

    private static bool HasMerge(CommandStage stage) =>
        stage.Redirections.Any(item => item.Kind == RedirectionKind.MergeError);

    private static bool HasErrorRedirect(CommandStage stage) =>
        stage.Redirections.Any(item => item.Kind is RedirectionKind.ErrorOutput or RedirectionKind.ErrorAppend);

    private bool CanStream(CommandStage stage) =>
        OutputSink is not null &&
        !stage.Redirections.Any(item => item.Kind is RedirectionKind.Output or RedirectionKind.Append);

    private static async Task<string> ReadStreamAsync(StreamReader reader, Action<string>? sink, CancellationToken token)
    {
        var output = new StringBuilder();
        char[] buffer = new char[4096];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
            if (read <= 0) break;
            string chunk = new(buffer, 0, read);
            output.Append(chunk);
            sink?.Invoke(chunk);
        }
        return output.ToString();
    }

    private async Task<string> ReadInputRedirectAsync(CommandStage stage, CancellationToken token)
    {
        Redirection? heredoc = stage.Redirections.LastOrDefault(item => item.Kind == RedirectionKind.Heredoc);
        if (heredoc is not null) return heredoc.Path;

        Redirection? input = stage.Redirections.LastOrDefault(item => item.Kind == RedirectionKind.Input);
        return input is null ? string.Empty : await File.ReadAllTextAsync(_context.ResolvePath(input.Path), token);
    }

    private async Task<CommandExecutionResult> ApplyOutputRedirectAsync(
        CommandStage stage, CommandExecutionResult result, CancellationToken token)
    {
        if (HasMerge(stage))
            result = result with { Output = result.Output + result.Error, Error = string.Empty };

        Redirection? error = stage.Redirections.LastOrDefault(item => item.Kind is RedirectionKind.ErrorOutput or RedirectionKind.ErrorAppend);
        if (error is not null)
        {
            string errorPath = _context.ResolvePath(error.Path);
            if (error.Kind == RedirectionKind.ErrorAppend)
                await File.AppendAllTextAsync(errorPath, EnsureLineEnding(result.Error), token);
            else await File.WriteAllTextAsync(errorPath, EnsureLineEnding(result.Error), token);
            result = result with { Error = string.Empty };
        }

        Redirection? output = stage.Redirections.LastOrDefault(item => item.Kind is RedirectionKind.Output or RedirectionKind.Append);
        if (output is null) return result;
        string path = _context.ResolvePath(output.Path);
        if (output.Kind == RedirectionKind.Append)
            await File.AppendAllTextAsync(path, EnsureLineEnding(result.Output), token);
        else await File.WriteAllTextAsync(path, EnsureLineEnding(result.Output), token);
        return result with { Output = string.Empty };
    }

    private static string EnsureLineEnding(string value) => value.EndsWith('\n') ? value : value + Environment.NewLine;
    private static CommandExecutionResult Success(string value) => new(Output: value);
    private static CommandExecutionResult Failure(string value) => new(1, Error: value);

    private static int EditDistance(string left, string right)
    {
        left = left.ToLowerInvariant(); right = right.ToLowerInvariant();
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
