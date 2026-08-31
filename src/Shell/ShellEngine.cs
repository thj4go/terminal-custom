using System.Diagnostics;
using System.IO;
using System.Text;

namespace TerminalCustom.Shell;

internal sealed class ShellEngine : IDisposable
{
    private readonly CommandParser _parser = new();
    private readonly ShellContext _context;
    private readonly CommandExecutor _executor;
    private readonly AiBridgeServer? _ai;
    private readonly AliasManager _aliases = new();
    private CancellationTokenSource? _commandCancellation;
    private ConPtySession? _interactiveSession;
    private bool _disposed;
    private readonly Dictionary<int, BackgroundJob> _backgroundJobs = new();
    private readonly object _jobsLock = new();
    private int _nextJobId = 1;
    private short _columns = 100;
    private short _rows = 30;
    private string _lastOutput = string.Empty;
    private string? _lastCommand;
    private string? _lastTerminalCommand;
    private string _lastTerminalOutput = string.Empty;
    private int _lastTerminalExitCode;
    private long? _lastTerminalElapsedMilliseconds;
    private static readonly HashSet<string> CapturedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "git", "ssh", "scp", "rsync", "docker", "kubectl",
        "python", "python3", "node", "npm", "npx", "dotnet",
        "cargo", "rustc", "java", "javac", "go", "ruby", "php"
    };

    public event Action<string>? OutputReceived;
    public event Action? ClearRequested;
    public event Action? ExitRequested;
    public event Action? InteractiveEnded;

    public string Prompt => _context.Prompt;
    public HistoryManager History => _context.History;
    public bool IsInteractive => _interactiveSession is not null;
    public bool IsBusy => _commandCancellation is not null;

    public ShellEngine(string initialDirectory, AiBridgeServer? ai, bool persistHistory = true)
    {
        _context = new ShellContext(initialDirectory, persistHistory: persistHistory);
        var builtIns = new BuiltInCommandRegistry();
        var resolver = new PathResolver(_context);
        _executor = new CommandExecutor(_context, builtIns, resolver, _aliases);
        _ai = ai;
    }

    public void Start()
    {
        LoadStartupScript();
        Emit(Prompt);
    }

    private void LoadStartupScript()
    {
        string rcPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".terminalrc");
        if (!File.Exists(rcPath)) return;
        try
        {
            foreach (string line in File.ReadAllLines(rcPath))
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                if (trimmed.StartsWith("alias ", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = trimmed[6..].Trim();
                    int eq = rest.IndexOf('=');
                    if (eq > 0) _aliases.Set(rest[..eq].Trim(), rest[(eq + 1)..].Trim().Trim('"', '\''));
                }
                else if (trimmed.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = trimmed[4..].Trim();
                    int eq = rest.IndexOf('=');
                    if (eq > 0) _context.Environment.Set(rest[..eq].Trim(), rest[(eq + 1)..]);
                }
            }
        }
        catch { }
    }

    public Task SubmitAsync(string commandLine, short columns, short rows) =>
        SubmitCoreAsync(commandLine, columns, rows, emitPrompt: true, addHistory: true);

    private async Task SubmitCoreAsync(string commandLine, short columns, short rows, bool emitPrompt, bool addHistory)
    {
        if (_disposed || IsBusy || IsInteractive) return;
        _columns = columns;
        _rows = rows;
        _context.Environment.Set("COLUMNS", columns.ToString(), export: false);
        _context.Environment.Set("LINES", rows.ToString(), export: false);
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            if (emitPrompt) Emit(Prompt);
            return;
        }

        commandLine = ExpandHistoryShortcuts(commandLine);
        string expanded = _aliases.Expand(commandLine);
        if (addHistory) _context.History.Add(expanded);
        _lastCommand = expanded;

        string withSubs;
        try { withSubs = await ExpandSubstitutionsAsync(expanded); }
        catch (Exception ex)
        {
            EmitError(ex.Message);
            RememberResult(1, string.Empty, error: ex.Message);
            if (emitPrompt) Emit(Prompt);
            return;
        }

        IReadOnlyList<ParsedCommandLine> commands;
        try { commands = _parser.ParseAll(withSubs); }
        catch (CommandParseException ex)
        {
            EmitError(ex.Message);
            RememberResult(1, string.Empty, error: ex.Message);
            if (emitPrompt) Emit(Prompt);
            return;
        }

        if (commands.Count == 0)
        {
            if (emitPrompt) Emit(Prompt);
            return;
        }

        int lastExit = int.TryParse(_context.Environment.Get("LASTEXITCODE"), out int code) ? code : 0;
        for (int i = 0; i < commands.Count; i++)
        {
            if (_disposed || IsInteractive) break;
            ParsedCommandLine command = commands[i];
            if (command.RunIf == CommandRunIf.PreviousSuccess && lastExit != 0) continue;
            if (command.RunIf == CommandRunIf.PreviousFailure && lastExit == 0) continue;
            bool last = i == commands.Count - 1;
            lastExit = await ExecuteParsedAsync(command, columns, rows, emitPrompt && last);
        }
    }

    private async Task<int> ExecuteParsedAsync(ParsedCommandLine parsed, short columns, short rows, bool emitPrompt)
    {
        CommandStage first = parsed.Stages[0];
        if (parsed.Stages.Count == 1 && _executor.TryResolveBuiltIn(first.Name, out string canonical) &&
            canonical.StartsWith("ai", StringComparison.Ordinal))
        {
            if (canonical == "ai")
            {
                bool started = StartNextIo(string.Join(' ', first.Arguments.Skip(1)), columns, rows);
                return started ? 0 : 1;
            }
            await ExecuteAiAsync(canonical, first.Arguments.Skip(1).ToArray());
            if (emitPrompt) Emit(Prompt);
            return 0;
        }

        if (parsed.Stages.Count == 1 && _executor.TryResolveBuiltIn(first.Name, out canonical))
        {
            if (canonical == "next-io")
            {
                bool started = StartNextIo(string.Join(' ', first.Arguments.Skip(1)), columns, rows);
                return started ? 0 : 1;
            }
            if (canonical == "alias") { ExecuteAlias(first.Arguments.Skip(1).ToArray()); if (emitPrompt) Emit(Prompt); return 0; }
            if (canonical == "unalias") { ExecuteUnalias(first.Arguments.Skip(1).ToArray()); if (emitPrompt) Emit(Prompt); return 0; }
            if (canonical == "jobs") { ExecuteJobs(); if (emitPrompt) Emit(Prompt); return 0; }
            if (canonical == "fg") { await ExecuteFgAsync(first.Arguments.Skip(1).ToArray()); if (emitPrompt) Emit(Prompt); return 0; }
            if (canonical == "source") { await ExecuteSourceAsync(first.Arguments.Skip(1).ToArray()); if (emitPrompt) Emit(Prompt); return 0; }
            if (canonical == "kill") { ExecuteKill(first.Arguments.Skip(1).ToArray()); if (emitPrompt) Emit(Prompt); return 0; }
            if (canonical == "copyout") { ExecuteCopyout(); if (emitPrompt) Emit(Prompt); return 0; }
            if (canonical == "retry")
            {
                string? previous = _context.History.Entries.Count > 0 ? _context.History.Entries[^1] : _lastCommand;
                if (string.IsNullOrEmpty(previous) || previous.Equals("retry", StringComparison.OrdinalIgnoreCase))
                {
                    EmitError("Nada para repetir.");
                    if (emitPrompt) Emit(Prompt);
                    return 1;
                }
                await SubmitCoreAsync(previous, columns, rows, emitPrompt, addHistory: false);
                return int.TryParse(_context.Environment.Get("LASTEXITCODE"), out int retryCode) ? retryCode : 0;
            }
            return await RunCapturedAsync(token => _executor.ExecuteBuiltInAsync(canonical, first, token: token), emitPrompt);
        }

        // Conversa reconhecida tem prioridade sobre executáveis com nomes curtos
        // encontrados no PATH (por exemplo, um em.exe não deve capturar "em qual pasta...").
        if (parsed.Stages.Count == 1 && LooksLikeNaturalLanguage(string.Join(' ', first.Arguments), first.Arguments))
        {
            if (_ai is null)
            {
                await ExecuteAiChatAsync(string.Join(' ', first.Arguments));
                if (emitPrompt) Emit(Prompt);
                return 0;
            }
            bool started = StartNextIo(string.Join(' ', first.Arguments), columns, rows);
            return started ? 0 : 1;
        }

        if (parsed.Stages.Count == 1 && first.Redirections.Count == 0 && !parsed.Background &&
            IsNativeScript(first.Name))
        {
            await ExecuteSourceAsync(first.Arguments.ToArray());
            int scriptExit = int.TryParse(_context.Environment.Get("LASTEXITCODE"), out int value) ? value : 0;
            if (emitPrompt) Emit(Prompt);
            return scriptExit;
        }

        ResolvedExecutable? executable = parsed.Stages.Count == 1 ? _executor.ResolveExecutable(first.Name) : null;
        bool useCaptured = CapturedCommands.Contains(first.Name);
        if (parsed.Stages.Count == 1 && executable is not null && first.Redirections.Count == 0 &&
            !parsed.Background && !useCaptured)
        {
            StartInteractive(executable, first, columns, rows);
            return 0;
        }

        if (parsed.Stages.Count > 1 || first.Redirections.Count > 0 || parsed.Background || useCaptured)
        {
            if (parsed.Background)
            {
                int jobId = _nextJobId++;
                var cts = new CancellationTokenSource();
                var job = new BackgroundJob(jobId, first.Name, cts);
                lock (_jobsLock) _backgroundJobs[jobId] = job;
                job.Task = RunBackgroundAsync(parsed, job);
                Emit($"[{jobId}] {first.Name}");
                if (emitPrompt) Emit("\r\n" + Prompt);
                return 0;
            }
            return await RunCapturedAsync(token => _executor.ExecutePipelineAsync(parsed, token), emitPrompt);
        }

        if (TryAutoCd(first))
        {
            RememberResult(0, string.Empty);
            if (emitPrompt) Emit(Prompt);
            return 0;
        }

        string joinedCandidate = string.Concat(first.Arguments);
        string? suggestion = joinedCandidate.Length > first.Name.Length &&
            (_executor.ResolveExecutable(joinedCandidate) is not null ||
             _executor.TryResolveBuiltIn(joinedCandidate, out _))
            ? joinedCandidate
            : _executor.SuggestCommand(first.Name);
        string errorMessage = suggestion is not null
            ? $"Comando não encontrado: {first.Name}. Você quis dizer '{suggestion}'?"
            : $"Comando não encontrado: {first.Name}";
        EmitError(errorMessage);
        RememberResult(1, string.Empty, error: errorMessage);
        if (emitPrompt) Emit(Prompt);
        return 1;
    }

    private async Task<string> ExpandSubstitutionsAsync(string input)
    {
        var result = new StringBuilder();
        int i = 0;
        while (i < input.Length)
        {
            if (input[i] is '\'' or '"')
            {
                char quote = input[i];
                result.Append(quote);
                i++;
                while (i < input.Length && input[i] != quote)
                {
                    if (quote == '"' && input[i] == '$' && i + 1 < input.Length && input[i + 1] == '(')
                    {
                        (string replacement, int next) = await RunSubstitutionAsync(input, i);
                        result.Append(replacement);
                        i = next;
                        continue;
                    }
                    if (input[i] == '\\' && i + 1 < input.Length)
                    {
                        result.Append(input[i]);
                        result.Append(input[++i]);
                        i++;
                        continue;
                    }
                    result.Append(input[i]);
                    i++;
                }
                if (i < input.Length)
                {
                    result.Append(input[i]);
                    i++;
                }
                continue;
            }

            if (input[i] == '$' && i + 1 < input.Length && input[i + 1] == '(')
            {
                (string replacement, int next) = await RunSubstitutionAsync(input, i);
                result.Append(replacement);
                i = next;
                continue;
            }

            result.Append(input[i]);
            i++;
        }
        return result.ToString();
    }

    private async Task<(string Text, int Next)> RunSubstitutionAsync(string input, int i)
    {
        int depth = 1;
        int start = i + 2;
        int pos = start;
        while (pos < input.Length && depth > 0)
        {
            if (input[pos] == '(') depth++;
            else if (input[pos] == ')') depth--;
            if (depth > 0) pos++;
        }
        if (depth != 0) return ("$", i + 1);
        string inner = await ExpandSubstitutionsAsync(input[start..pos]);
        IReadOnlyList<ParsedCommandLine> commands = _parser.ParseAll(inner);
        var output = new StringBuilder();
        foreach (ParsedCommandLine command in commands)
        {
            CommandExecutionResult result = await _executor.ExecutePipelineAsync(command, CancellationToken.None);
            output.Append(result.Output);
        }
        return (output.ToString().TrimEnd().Replace('\r', ' ').Replace('\n', ' '), pos + 1);
    }

    private async Task RunBackgroundAsync(ParsedCommandLine commandLine, BackgroundJob job)
    {
        try
        {
            var result = await _executor.ExecutePipelineAsync(commandLine, job.Cts.Token);
            if (result.Output.Length > 0) Emit("\r\n" + EnsureNewline(result.Output).TrimEnd());
            if (result.Error.Length > 0) EmitError(result.Error);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { EmitError(ex.Message); }
        finally
        {
            job.Completed = true;
            lock (_jobsLock) _backgroundJobs.Remove(job.Id);
            Emit($"\r\n[{job.Id}] Concluído  {job.Name}\r\n{Prompt}");
        }
    }

    private void ExecuteAlias(string[] args)
    {
        if (args.Length == 0) { Emit(_aliases.ListFormatted()); return; }
        string expr = string.Join(' ', args);
        int eq = expr.IndexOf('=');
        if (eq > 0) _aliases.Set(expr[..eq].Trim(), expr[(eq + 1)..].Trim().Trim('"', '\''));
        else Emit(_aliases.Get(args[0]) ?? $"alias: {args[0]} não encontrado");
    }

    private void ExecuteUnalias(string[] args)
    {
        if (args.Length == 0) { EmitError("Uso: unalias <nome>"); return; }
        if (!_aliases.Remove(args[0])) EmitError($"alias não encontrado: {args[0]}");
    }

    private void ExecuteJobs()
    {
        List<BackgroundJob> jobs;
        lock (_jobsLock) jobs = _backgroundJobs.Values.OrderBy(j => j.Id).ToList();
        if (jobs.Count == 0) { Emit("Nenhum job em background."); return; }
        var sb = new StringBuilder();
        foreach (BackgroundJob job in jobs)
            sb.AppendLine($"[{job.Id}] {(job.Completed ? "Concluído" : "Executando")}  {job.Name}");
        Emit(sb.ToString().TrimEnd());
    }

    private async Task ExecuteFgAsync(string[] args)
    {
        if (args.Length == 0) { EmitError("Uso: fg %<jobid>"); return; }
        string idStr = args[0].TrimStart('%');
        BackgroundJob? job;
        lock (_jobsLock)
        {
            if (!int.TryParse(idStr, out int id) || !_backgroundJobs.TryGetValue(id, out job))
            {
                EmitError($"Job não encontrado: {args[0]}");
                return;
            }
        }
        if (job.Task is not null && !job.Completed)
            await job.Task;
    }

    private void ExecuteKill(string[] args)
    {
        if (args.Length == 0) { EmitError("Uso: kill %<jobid>"); return; }
        string idStr = args[0].TrimStart('%');
        BackgroundJob? job;
        lock (_jobsLock)
        {
            if (!int.TryParse(idStr, out int id) || !_backgroundJobs.TryGetValue(id, out job))
            {
                EmitError($"Job não encontrado: {args[0]}");
                return;
            }
        }
        try { job.Cts.Cancel(); } catch { }
        lock (_jobsLock) _backgroundJobs.Remove(job.Id);
        Emit($"[{job.Id}] Morto");
    }

    private bool IsNativeScript(string name)
    {
        if (!name.EndsWith(".tsh", StringComparison.OrdinalIgnoreCase)) return false;
        try { return File.Exists(_context.ResolvePath(name)); }
        catch { return false; }
    }

    private async Task ExecuteSourceAsync(string[] args)
    {
        if (args.Length == 0) { EmitError("Uso: source <arquivo>"); return; }
        string path = _context.ResolvePath(args[0]);
        if (!File.Exists(path)) { EmitError($"Arquivo não encontrado: {path}"); return; }
        Dictionary<string, string> previousArguments = _context.Environment.Snapshot()
            .Where(pair => pair.Key.Equals("ARGS", StringComparison.OrdinalIgnoreCase) ||
                           pair.Key.StartsWith("ARG", StringComparison.OrdinalIgnoreCase) &&
                           int.TryParse(pair.Key[3..], out _))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        try
        {
            ClearScriptArguments();
            _context.Environment.Set("ARG0", path, export: false);
            _context.Environment.Set("ARGS", string.Join(' ', args.Skip(1)), export: false);
            for (int index = 1; index < args.Length; index++)
                _context.Environment.Set($"ARG{index}", args[index], export: false);

            string[] lines = await File.ReadAllLinesAsync(path);
            await ExecuteScriptRangeAsync(lines, 0, lines.Length);
        }
        catch (Exception ex) { EmitError(ex.Message); }
        finally
        {
            ClearScriptArguments();
            foreach ((string name, string value) in previousArguments)
                _context.Environment.Set(name, value, export: false);
        }
    }

    private void ClearScriptArguments()
    {
        string[] names = _context.Environment.Snapshot().Keys
            .Where(name => name.Equals("ARGS", StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith("ARG", StringComparison.OrdinalIgnoreCase) &&
                           int.TryParse(name[3..], out _))
            .ToArray();
        foreach (string name in names) _context.Environment.Unset(name, export: false);
    }

    private async Task ExecuteScriptRangeAsync(string[] lines, int start, int end)
    {
        for (int index = start; index < end; index++)
        {
            string command = lines[index].Trim();
            if (string.IsNullOrEmpty(command) || command.StartsWith('#')) continue;

            if (command.StartsWith("if ", StringComparison.OrdinalIgnoreCase))
            {
                (int elseLine, int endLine) = FindScriptBlock(lines, index, end, allowElse: true);
                await SubmitCoreAsync(command[3..].Trim(), _columns, _rows, emitPrompt: false, addHistory: false);
                bool success = _context.Environment.Get("LASTEXITCODE") == "0";
                if (success)
                    await ExecuteScriptRangeAsync(lines, index + 1, elseLine >= 0 ? elseLine : endLine);
                else if (elseLine >= 0)
                    await ExecuteScriptRangeAsync(lines, elseLine + 1, endLine);
                index = endLine;
                continue;
            }

            if (command.StartsWith("repeat ", StringComparison.OrdinalIgnoreCase))
            {
                (int _, int endLine) = FindScriptBlock(lines, index, end, allowElse: false);
                string expandedCount = _context.Environment.Expand(command[7..].Trim());
                if (!int.TryParse(expandedCount, out int count) || count < 0 || count > 10000)
                    throw new InvalidOperationException($"Repetição inválida na linha {index + 1}: use um número entre 0 e 10000.");
                string? previousIteration = _context.Environment.Get("ITERATION");
                try
                {
                    for (int iteration = 0; iteration < count && !_disposed && !IsInteractive; iteration++)
                    {
                        _context.Environment.Set("ITERATION", (iteration + 1).ToString(), export: false);
                        await ExecuteScriptRangeAsync(lines, index + 1, endLine);
                    }
                }
                finally
                {
                    if (previousIteration is null) _context.Environment.Unset("ITERATION", export: false);
                    else _context.Environment.Set("ITERATION", previousIteration, export: false);
                }
                index = endLine;
                continue;
            }

            if (command.Equals("else", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("end", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Bloco inesperado na linha {index + 1}: {command}");

            await SubmitCoreAsync(command, _columns, _rows, emitPrompt: false, addHistory: false);
            if (_disposed || IsInteractive) return;
        }
    }

    private static (int ElseLine, int EndLine) FindScriptBlock(
        string[] lines, int openingLine, int limit, bool allowElse)
    {
        int depth = 0;
        int elseLine = -1;
        for (int index = openingLine + 1; index < limit; index++)
        {
            string command = lines[index].Trim();
            if (command.StartsWith("if ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("repeat ", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
                continue;
            }
            if (command.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0) return (elseLine, index);
                depth--;
                continue;
            }
            if (allowElse && depth == 0 && command.Equals("else", StringComparison.OrdinalIgnoreCase))
            {
                if (elseLine >= 0) throw new InvalidOperationException($"Mais de um else para o bloco da linha {openingLine + 1}.");
                elseLine = index;
            }
        }
        throw new InvalidOperationException($"Bloco iniciado na linha {openingLine + 1} não possui end.");
    }

    private async Task<int> RunCapturedAsync(Func<CancellationToken, Task<CommandExecutionResult>> operation, bool emitPrompt = true)
    {
        using var cancellation = new CancellationTokenSource();
        _commandCancellation = cancellation;
        bool streamedOut = false;
        bool streamedErr = false;
        _executor.OutputSink = text => { streamedOut = true; Emit(text); };
        _executor.ErrorSink = text => { streamedErr = true; Emit("\x1b[93m" + text + "\x1b[0m"); };
        var clock = Stopwatch.StartNew();
        CommandExecutionResult result;
        try { result = await operation(cancellation.Token); }
        catch (Exception ex) { result = new CommandExecutionResult(1, Error: ex.Message); }
        finally
        {
            clock.Stop();
            _executor.OutputSink = null;
            _executor.ErrorSink = null;
            _commandCancellation = null;
        }

        RememberResult(result.ExitCode, result.Output, clock.ElapsedMilliseconds, result.Error);
        if (result.ClearRequested) ClearRequested?.Invoke();
        if (!streamedOut && result.Output.Length > 0) Emit(EnsureNewline(result.Output));
        else if (streamedOut && result.Output.Length > 0 && !result.Output.EndsWith('\n')) Emit(Environment.NewLine);
        if (!streamedErr && result.Error.Length > 0) EmitError(result.Error);
        if (result.ExitRequested) ExitRequested?.Invoke();
        else if (emitPrompt) Emit(Prompt);
        return result.ExitCode;
    }

    private void RememberResult(int exitCode, string output, long? milliseconds = null, string? error = null)
    {
        _lastOutput = output;
        _lastTerminalCommand = _lastCommand;
        _lastTerminalExitCode = exitCode;
        _lastTerminalElapsedMilliseconds = milliseconds;
        _lastTerminalOutput = string.IsNullOrWhiteSpace(error)
            ? output
            : output + (output.Length > 0 && !output.EndsWith('\n') ? Environment.NewLine : string.Empty) + error;
        _context.Environment.Set("LASTEXITCODE", exitCode.ToString());
        string stored = output.Length > 8000 ? output[^8000..] : output;
        _context.Environment.Set("LASTOUTPUT", stored.TrimEnd(), export: false);
        if (milliseconds is not null)
            _context.Environment.Set("LASTTIME", milliseconds.Value.ToString(), export: false);
    }

    private string ExpandHistoryShortcuts(string line)
    {
        IReadOnlyList<string> entries = _context.History.Entries;
        string last = entries.Count > 0 ? entries[^1] : string.Empty;
        if (line == "!!") return last.Length == 0 ? line : last;
        if (line.StartsWith("!!", StringComparison.Ordinal)) return last + line[2..];
        if (line.Contains("!$", StringComparison.Ordinal))
        {
            string lastArg = last.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            line = line.Replace("!$", lastArg, StringComparison.Ordinal);
        }
        return line;
    }

    private bool TryAutoCd(CommandStage first)
    {
        if (_context.TryEnterDirectoryExact(first.Name)) return true;
        if (first.Arguments.Count > 1 && _context.TryEnterDirectoryExact(string.Join(' ', first.Arguments)))
            return true;
        return false;
    }

    private void ExecuteCopyout()
    {
        if (_lastOutput.Length == 0) { Emit("Nada para copiar."); return; }
        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                System.Windows.Clipboard.SetText(_lastOutput));
            Emit("Saída copiada para a área de transferência.");
        }
        catch (Exception ex) { EmitError(ex.Message); }
    }

    private void StartInteractive(ResolvedExecutable executable, CommandStage stage, short columns, short rows)
    {
        string commandLine;
        if (executable.RequiresCmd)
        {
            string comspec = _context.Environment.Get("COMSPEC") ?? "cmd.exe";
            string script = WindowsCommandLine.BuildForCmd(executable.Path,
                stage.Arguments.Skip(1).Select(_context.Environment.Expand));
            commandLine = WindowsCommandLine.Build(comspec, ["/d", "/s"]) + $" /c \"{script}\"";
        }
        else
        {
            commandLine = WindowsCommandLine.Build(executable.Path,
                stage.Arguments.Skip(1).Select(_context.Environment.Expand));
        }

        StartInteractiveCommandLine(commandLine, stage.Name, columns, rows);
    }

    private bool StartNextIo(string initialPrompt, short columns, short rows)
    {
        if (_ai?.IsConfigured != true)
        {
            EmitError("Configure primeiro a chave da OpenRouter com ai-key.");
            Emit(Prompt);
            return false;
        }

        string? script = FindNextIoScript();
        if (script is null)
        {
            EmitError("O motor NEXT-IO não foi encontrado dentro da pasta do terminal.");
            Emit(Prompt);
            return false;
        }
        ResolvedExecutable? python = _executor.ResolveExecutable("python") ?? _executor.ResolveExecutable("py");
        if (python is null)
        {
            EmitError("Python não foi encontrado. Instale o Python 3 para usar a IA com tools.");
            Emit(Prompt);
            return false;
        }

        var previous = new Dictionary<string, string?>
        {
            ["NEXT_IO_WORKSPACE"] = Environment.GetEnvironmentVariable("NEXT_IO_WORKSPACE"),
            ["NEXT_IO_INITIAL_PROMPT"] = Environment.GetEnvironmentVariable("NEXT_IO_INITIAL_PROMPT"),
            ["NEXT_IO_FIXED_PROMPT"] = Environment.GetEnvironmentVariable("NEXT_IO_FIXED_PROMPT"),
            ["PYTHONUTF8"] = Environment.GetEnvironmentVariable("PYTHONUTF8")
        };
        try
        {
            Environment.SetEnvironmentVariable("NEXT_IO_WORKSPACE", _context.CurrentDirectory);
            Environment.SetEnvironmentVariable("NEXT_IO_INITIAL_PROMPT", initialPrompt.Trim());
            Environment.SetEnvironmentVariable("NEXT_IO_FIXED_PROMPT", "false");
            Environment.SetEnvironmentVariable("PYTHONUTF8", "1");
            string commandLine = WindowsCommandLine.Build(python.Path, [script]);
            return StartInteractiveCommandLine(commandLine, "NEXT-IO", columns, rows);
        }
        finally
        {
            foreach ((string name, string? value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static string? FindNextIoScript()
    {
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "next-cli", "runth.py")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "next-cli", "runth.py"))
        };
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 7 && directory is not null; depth++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, "next-cli", "runth.py"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private bool StartInteractiveCommandLine(string commandLine, string displayName, short columns, short rows)
    {
        var session = new ConPtySession();
        var captured = new StringBuilder();
        _interactiveSession = session;
        session.OutputReceived += text =>
        {
            captured.Append(text);
            if (captured.Length > 12000)
                captured.Remove(0, captured.Length - 12000);
            Emit(text);
        };
        session.Exited += exitCode =>
        {
            if (!ReferenceEquals(_interactiveSession, session)) return;
            _interactiveSession = null;
            RememberResult(exitCode, captured.ToString());
            session.Dispose();
            Emit("\r\n" + Prompt);
            InteractiveEnded?.Invoke();
        };
        try
        {
            session.Start(commandLine, _context.CurrentDirectory, columns, rows);
            return true;
        }
        catch (Exception ex)
        {
            _interactiveSession = null;
            session.Dispose();
            EmitError($"Não foi possível iniciar {displayName}: {ex.Message}");
            Emit(Prompt);
            return false;
        }
    }

    private async Task ExecuteAiAsync(string command, string[] args)
    {
        if (_ai is null)
        {
            EmitAiResult(AiBridgeServer.AiResult.Error("Cliente de IA não está conectado."));
            return;
        }
        AiBridgeServer.AiResult result = command switch
        {
            "ai-key" when HasRemoveFlag(args) => _ai.ClearKey(),
            "ai-key" => await _ai.ConfigureKeyAsync(),
            "ai-status" => _ai.Status(),
            "ai-prompt" when HasRemoveFlag(args) => _ai.ClearSystemPrompt(),
            "ai-prompt" => await _ai.ConfigureSystemPromptAsync(),
            _ => await SafeChatAsync(string.Join(' ', args))
        };
        EmitAiResult(result);
    }

    private async Task ExecuteAiChatAsync(string prompt) => EmitAiResult(await SafeChatAsync(prompt));

    private async Task<AiBridgeServer.AiResult> SafeChatAsync(string prompt)
    {
        if (_ai is null) return AiBridgeServer.AiResult.Error("Cliente de IA não está conectado.");
        if (SensitiveDataDetector.ContainsSensitiveData(prompt))
            return AiBridgeServer.AiResult.Error("Esse texto parece conter dado sensível e não foi enviado à IA.");
        using var cancellation = new CancellationTokenSource();
        _commandCancellation = cancellation;
        TerminalAiContext context = CreateTerminalAiContext();
        try { return await _ai.ChatAsync(prompt, context, cancellation.Token); }
        catch (OperationCanceledException) { return AiBridgeServer.AiResult.Error("Pedido de IA cancelado."); }
        finally { _commandCancellation = null; }
    }

    internal TerminalAiContext CreateTerminalAiContext() => new(
        _context.CurrentDirectory,
        _lastTerminalCommand,
        _lastTerminalExitCode,
        _lastTerminalElapsedMilliseconds,
        _lastTerminalOutput);

    private void EmitAiResult(AiBridgeServer.AiResult result)
    {
        string color = result.Ok ? "\x1b[97m" : "\x1b[93m";
        Emit(color + EnsureNewline(result.Message) + "\x1b[0m");
    }

    private static bool HasRemoveFlag(IEnumerable<string> args) => args.Any(value =>
        value.Equals("-remover", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--remove", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("-remove", StringComparison.OrdinalIgnoreCase));

    internal static bool LooksLikeNaturalLanguage(string line, IReadOnlyList<string> words)
    {
        if (line.Contains('?')) return true;
        if (words.Count == 0) return false;
        string first = words[0].Trim().ToLowerInvariant();
        if (first is "oi" or "olá" or "ola" or "ajuda" or "helpme") return true;
        if (words.Count < 2) return false;
        return first is "me" or "em" or "eu" or "você" or "voce" or "estou" or "está" or "esta" or
            "veja" or "verifique" or "analise" or "corrija" or "conserte" or "aonde" or
            "explique" or "explica" or "explique-me" or "como" or
            "porque" or "porquê" or "qual" or "quais" or "quem" or "quando" or "onde" or
            "quero" or "preciso" or "pode" or "poderia" or "mostre" or "ensine" or "ajude";
    }

    public List<string> GetCompletions(string prefix, bool isCommand)
    {
        var results = new List<string>();
        if (isCommand)
        {
            results.AddRange(_executor.GetBuiltInNames()
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            results.AddRange(_executor.GetExecutableNames()
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            results.AddRange(_aliases.All.Keys
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            results.AddRange(_executor.GetFilePathCompletions(prefix));
        }
        return results.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
    }

    public Task WriteInteractiveAsync(string text) => _interactiveSession?.WriteAsync(text) ?? Task.CompletedTask;
    public void Resize(short columns, short rows)
    {
        _columns = columns;
        _rows = rows;
        _interactiveSession?.Resize(columns, rows);
    }

    public async Task CancelAsync()
    {
        if (_interactiveSession is not null) await _interactiveSession.WriteAsync("\x03");
        else _commandCancellation?.Cancel();
    }

    private void EmitError(string value) => Emit("\x1b[93m" + EnsureNewline(value) + "\x1b[0m");
    private void Emit(string value) => OutputReceived?.Invoke(value);
    private static string EnsureNewline(string value) => value.EndsWith('\n') ? value : value + Environment.NewLine;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commandCancellation?.Cancel();
        _interactiveSession?.Dispose();
        _interactiveSession = null;
        lock (_jobsLock)
        {
            foreach (BackgroundJob job in _backgroundJobs.Values)
                try { job.Cts.Cancel(); } catch { }
            _backgroundJobs.Clear();
        }
    }

    private sealed class BackgroundJob(int id, string name, CancellationTokenSource cts)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public CancellationTokenSource Cts { get; } = cts;
        public Task? Task { get; set; }
        public bool Completed { get; set; }
    }
}
