using System.IO;

namespace TerminalCustom.Shell;

internal sealed class ShellEngine : IDisposable
{
    private readonly CommandParser _parser = new();
    private readonly ShellContext _context;
    private readonly CommandExecutor _executor;
    private readonly AiBridgeServer _ai;
    private CancellationTokenSource? _commandCancellation;
    private ConPtySession? _interactiveSession;
    private bool _disposed;

    public event Action<string>? OutputReceived;
    public event Action? ClearRequested;
    public event Action? ExitRequested;
    public event Action? InteractiveEnded;

    public string Prompt => _context.Prompt;
    public HistoryManager History => _context.History;
    public bool IsInteractive => _interactiveSession is not null;
    public bool IsBusy => _commandCancellation is not null;

    public ShellEngine(string initialDirectory, AiBridgeServer ai)
    {
        _context = new ShellContext(initialDirectory);
        var builtIns = new BuiltInCommandRegistry();
        var resolver = new PathResolver(_context);
        _executor = new CommandExecutor(_context, builtIns, resolver);
        _ai = ai;
    }

    public void Start() => Emit(Prompt);

    public async Task SubmitAsync(string commandLine, short columns, short rows)
    {
        if (_disposed || IsBusy || IsInteractive) return;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            Emit(Prompt);
            return;
        }

        _context.History.Add(commandLine);
        ParsedCommandLine parsed;
        try
        {
            parsed = _parser.Parse(commandLine);
        }
        catch (CommandParseException ex)
        {
            EmitError(ex.Message);
            Emit(Prompt);
            return;
        }

        CommandStage first = parsed.Stages[0];
        if (parsed.Stages.Count == 1 && _executor.TryResolveBuiltIn(first.Name, out string canonical) &&
            canonical.StartsWith("ai", StringComparison.Ordinal))
        {
            await ExecuteAiAsync(canonical, first.Arguments.Skip(1).ToArray());
            Emit(Prompt);
            return;
        }

        if (parsed.Stages.Count == 1 && _executor.TryResolveBuiltIn(first.Name, out canonical))
        {
            await RunCapturedAsync(token => _executor.ExecuteBuiltInAsync(canonical, first, token: token));
            return;
        }

        ResolvedExecutable? executable = parsed.Stages.Count == 1 ? _executor.ResolveExecutable(first.Name) : null;
        if (parsed.Stages.Count == 1 && executable is not null && first.Redirections.Count == 0)
        {
            StartInteractive(executable, first, columns, rows);
            return;
        }

        if (parsed.Stages.Count > 1 || first.Redirections.Count > 0)
        {
            await RunCapturedAsync(token => _executor.ExecutePipelineAsync(parsed, token));
            return;
        }

        if (LooksLikeNaturalLanguage(commandLine, first.Arguments))
        {
            await ExecuteAiChatAsync(commandLine);
            Emit(Prompt);
            return;
        }

        string? suggestion = _executor.SuggestCommand(first.Name);
        if (suggestion is not null)
        {
            EmitError($"Comando não encontrado: {first.Name}. Você quis dizer '{suggestion}'?");
            Emit(Prompt);
            return;
        }

        EmitError($"Comando não encontrado: {first.Name}");
        Emit(Prompt);
    }

    private async Task RunCapturedAsync(Func<CancellationToken, Task<CommandExecutionResult>> operation)
    {
        using var cancellation = new CancellationTokenSource();
        _commandCancellation = cancellation;
        CommandExecutionResult result;
        try { result = await operation(cancellation.Token); }
        catch (Exception ex) { result = new CommandExecutionResult(1, Error: ex.Message); }
        finally { _commandCancellation = null; }

        if (result.ClearRequested) ClearRequested?.Invoke();
        if (result.Output.Length > 0) Emit(EnsureNewline(result.Output));
        if (result.Error.Length > 0) EmitError(result.Error);
        if (result.ExitRequested) ExitRequested?.Invoke();
        else Emit(Prompt);
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

        var session = new ConPtySession();
        _interactiveSession = session;
        session.OutputReceived += Emit;
        session.Exited += () =>
        {
            if (!ReferenceEquals(_interactiveSession, session)) return;
            _interactiveSession = null;
            session.Dispose();
            Emit("\r\n" + Prompt);
            InteractiveEnded?.Invoke();
        };
        try
        {
            session.Start(commandLine, _context.CurrentDirectory, columns, rows);
        }
        catch (Exception ex)
        {
            _interactiveSession = null;
            session.Dispose();
            EmitError($"Não foi possível iniciar {stage.Name}: {ex.Message}");
            Emit(Prompt);
        }
    }

    private async Task ExecuteAiAsync(string command, string[] args)
    {
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
        if (SensitiveDataDetector.ContainsSensitiveData(prompt))
            return AiBridgeServer.AiResult.Error("Esse texto parece conter dado sensível e não foi enviado à IA.");
        using var cancellation = new CancellationTokenSource();
        _commandCancellation = cancellation;
        try { return await _ai.ChatAsync(prompt, cancellation.Token); }
        catch (OperationCanceledException) { return AiBridgeServer.AiResult.Error("Pedido de IA cancelado."); }
        finally { _commandCancellation = null; }
    }

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
        return first is "me" or "explique" or "explica" or "explique-me" or "como" or
            "porque" or "porquê" or "qual" or "quais" or "quem" or "quando" or "onde" or
            "quero" or "preciso" or "pode" or "poderia" or "mostre" or "ensine" or "ajude";
    }

    public Task WriteInteractiveAsync(string text) => _interactiveSession?.WriteAsync(text) ?? Task.CompletedTask;
    public void Resize(short columns, short rows) => _interactiveSession?.Resize(columns, rows);

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
    }
}
