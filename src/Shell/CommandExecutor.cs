using System.Diagnostics;
using System.IO;
using System.Text;

namespace TerminalCustom.Shell;

internal sealed class CommandExecutor
{
    private readonly ShellContext _context;
    private readonly BuiltInCommandRegistry _builtIns;
    private readonly PathResolver _pathResolver;

    public CommandExecutor(ShellContext context, BuiltInCommandRegistry builtIns, PathResolver pathResolver)
    {
        _context = context;
        _builtIns = builtIns;
        _pathResolver = pathResolver;
    }

    public bool TryResolveBuiltIn(string name, out string canonical) => _builtIns.TryResolve(name, out canonical);
    public ResolvedExecutable? ResolveExecutable(string name) => _pathResolver.Resolve(name);

    public async Task<CommandExecutionResult> ExecuteBuiltInAsync(
        string canonical, CommandStage stage, string? pipelineInput = null, CancellationToken token = default)
    {
        string input = pipelineInput ?? await ReadInputRedirectAsync(stage, token);
        string[] args = stage.Arguments.Skip(1).Select(_context.Environment.Expand).ToArray();
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
                "where" => Locate(args),
                "set" => SetEnvironment(args),
                "history" => ShowHistory(),
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
                result = await ExecuteExternalCapturedAsync(executable, stage, redirectedInput, token);
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

            string output = await processes[^1].StandardOutput.ReadToEndAsync(token);
            await Task.WhenAll(transfers);
            await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(token)));
            string error = string.Concat(await Task.WhenAll(errors));
            int exitCode = processes[^1].ExitCode;
            CommandExecutionResult result = new(exitCode, output, error);
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
        ResolvedExecutable executable, CommandStage stage, string input, CancellationToken token)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executable, stage, redirect: true) };
        try
        {
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(token);
            if (input.Length > 0) await process.StandardInput.WriteAsync(input.AsMemory(), token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            return new CommandExecutionResult(process.ExitCode, await outputTask, await errorTask);
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
        _context.Environment.ApplyTo(startInfo);
        if (executable.RequiresCmd)
        {
            string script = WindowsCommandLine.BuildForCmd(executable.Path,
                stage.Arguments.Skip(1).Select(_context.Environment.Expand));
            startInfo.Arguments = $"/d /s /c \"{script}\"";
        }
        else
        {
            foreach (string argument in stage.Arguments.Skip(1))
                startInfo.ArgumentList.Add(_context.Environment.Expand(argument));
        }
        return startInfo;
    }

    private CommandExecutionResult ChangeDirectory(string[] args)
    {
        string target = args.Length == 0 ? (_context.Environment.Get("USERPROFILE") ?? _context.CurrentDirectory) : args[0];
        _context.ChangeDirectory(target);
        return new CommandExecutionResult();
    }

    private CommandExecutionResult ListDirectory(string[] args)
    {
        string target = args.FirstOrDefault(argument => !argument.StartsWith('-')) ?? _context.CurrentDirectory;
        string path = _context.ResolvePath(target);
        if (File.Exists(path)) return Success(FormatFile(new FileInfo(path)));
        if (!Directory.Exists(path)) return Failure($"Caminho não encontrado: {path}");
        var output = new StringBuilder();
        foreach (string directory in Directory.EnumerateDirectories(path).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            output.AppendLine($"<DIR>          {Path.GetFileName(directory)}");
        foreach (string file in Directory.EnumerateFiles(path).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            output.AppendLine(FormatFile(new FileInfo(file)));
        return new CommandExecutionResult(Output: output.ToString().TrimEnd());
    }

    private static string FormatFile(FileInfo file) => $"{file.Length,14:N0} {file.Name}";

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
            foreach (string file in ExpandFiles(target)) File.Delete(file);
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

    private CommandExecutionResult ShowHistory() => Success(string.Join(Environment.NewLine,
        _context.History.Entries.Select((command, index) => $"{index + 1,4}  {command}")));

    private static CommandExecutionResult ShowHelp() => Success("""
        Built-ins:
          cd pwd dir/ls cls/clear echo mkdir/md rmdir/rd del/rm
          copy/cp move/mv type/cat touch where/which set/env
          history help exit ai ai-key ai-status ai-prompt

        Operadores: |  >  >>  <
        PowerShell, CMD, Python, SSH e outros programas podem ser iniciados pelo nome.
        """);

    private IEnumerable<string> ExpandFiles(string target)
    {
        string path = _context.ResolvePath(target);
        string? directory = Path.GetDirectoryName(path);
        string pattern = Path.GetFileName(path);
        if (!pattern.Contains('*') && !pattern.Contains('?')) return [path];
        return Directory.EnumerateFiles(directory ?? _context.CurrentDirectory, pattern);
    }

    private async Task<string> ReadInputRedirectAsync(CommandStage stage, CancellationToken token)
    {
        Redirection? input = stage.Redirections.LastOrDefault(item => item.Kind == RedirectionKind.Input);
        return input is null ? string.Empty : await File.ReadAllTextAsync(_context.ResolvePath(input.Path), token);
    }

    private async Task<CommandExecutionResult> ApplyOutputRedirectAsync(
        CommandStage stage, CommandExecutionResult result, CancellationToken token)
    {
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
