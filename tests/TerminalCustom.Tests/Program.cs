using TerminalCustom;
using TerminalCustom.Shell;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("parser: aspas, espaços e operadores", TestParser),
    ("parser: aspas vazias e erro de aspas", TestParserEdges),
    ("cd: caminhos relativos, absolutos e variável", TestCd),
    ("PATH e PATHEXT", TestPathAndPathExt),
    ("aliases dos built-ins", TestAliases),
    ("variáveis de ambiente", TestEnvironment),
    ("compatibilidade: variável $env e operador &", TestPowerShellStyleInvocation),
    ("scripts nativos: argumentos posicionais", TestNativeScriptArguments),
    ("scripts nativos: execução, if e repeat", TestNativeScriptExecution),
    ("utilitários nativos de texto e sistema", TestNativeUtilities),
    ("ls respeita largura sem quebrar nomes", TestListWidth),
    ("redirecionamento > e >>", TestRedirection),
    ("redirecionamento de entrada <", TestInputRedirection),
    ("pipeline direto", TestPipeline),
    ("pipeline entre dois executáveis", TestExternalPipeline),
    ("histórico e proteção de segredos", TestHistory),
    ("edição e caracteres ABNT2", TestInputBuffer),
    ("buffer: nova linha sempre retorna à coluna inicial", TestTerminalLineFeed),
    ("linguagem natural versus erro de comando", TestNaturalLanguage),
    ("erro de comando fica disponível para a IA", TestCommandErrorContext),
    ("IA recebe contexto real e contexto remove segredos", TestAiTerminalContext),
    ("linguagem natural não entra em pasta automaticamente", TestNaturalLanguageBeforeAutoCd),
    ("linguagem natural tem prioridade sobre executável", TestNaturalLanguageBeforeExecutable),
    ("clear e exit sinalizam a aplicação", TestControlBuiltIns),
    ("parser: ponto-e-vírgula e background", TestParserSequenceAndBackground),
    ("cd: pasta com espaço sem aspas", TestCdUnquotedSpaces),
    ("parser: parênteses em caminho não são subshell", TestParenthesesInPath),
    ("parser: && || e 2>&1", TestAndOrAndMerge),
    ("expansão: ~ $? e histórico persistente", TestTildeExitAndHistory),
    ("cd: fuzzy e cd -", TestFuzzyAndBack)
};

int failures = 0;
foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL  {name}: {ex.Message}");
    }
}
Console.WriteLine($"\n{tests.Count - failures}/{tests.Count} testes passaram.");
return failures == 0 ? 0 : 1;

static Task TestParser()
{
    var parser = new CommandParser();
    ParsedCommandLine parsed = parser.Parse("echo \"hello world\" | findstr world >> \"resultado final.txt\"");
    Equal(2, parsed.Stages.Count);
    Equal("hello world", parsed.Stages[0].Arguments[1]);
    Equal(RedirectionKind.Append, parsed.Stages[1].Redirections[0].Kind);
    Equal("resultado final.txt", parsed.Stages[1].Redirections[0].Path);
    return Task.CompletedTask;
}

static Task TestParserEdges()
{
    var parser = new CommandParser();
    ParsedCommandLine parsed = parser.Parse("echo \"\"");
    Equal(2, parsed.Stages[0].Arguments.Count);
    Equal(string.Empty, parsed.Stages[0].Arguments[1]);
    Throws<CommandParseException>(() => parser.Parse("echo \"aberto"));
    return Task.CompletedTask;
}

static Task TestCd()
{
    string original = Directory.GetCurrentDirectory();
    string root = NewTempDirectory();
    string child = Directory.CreateDirectory(Path.Combine(root, "com espaço")).FullName;
    try
    {
        var environment = new EnvironmentManager();
        environment.Set("TERMINAL_TEST_ROOT", root);
        var context = new ShellContext(root, environment);
        context.ChangeDirectory("com espaço");
        Equal(child, context.CurrentDirectory);
        context.ChangeDirectory("..");
        Equal(root, context.CurrentDirectory);
        context.ChangeDirectory("%TERMINAL_TEST_ROOT%");
        Equal(root, context.CurrentDirectory);
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}

static async Task TestPathAndPathExt()
{
    string original = Directory.GetCurrentDirectory();
    string? originalPath = Environment.GetEnvironmentVariable("PATH");
    string? originalPathExt = Environment.GetEnvironmentVariable("PATHEXT");
    string root = NewTempDirectory();
    string script = Path.Combine(root, "ferramenta.CMD");
    File.WriteAllText(script, "@echo ok");
    string spacedScript = Path.Combine(root, "outra ferramenta.CMD");
    File.WriteAllText(spacedScript, "@echo %~1");
    try
    {
        var environment = new EnvironmentManager();
        environment.Set("PATH", root);
        environment.Set("PATHEXT", ".EXE;.CMD;.BAT");
        var context = new ShellContext(root, environment);
        ResolvedExecutable? resolved = new PathResolver(context).Resolve("ferramenta");
        True(resolved is not null);
        Equal(script, resolved!.Path);
        True(resolved.RequiresCmd);
        var builtIns = new BuiltInCommandRegistry();
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context), new AliasManager());
        CommandExecutionResult result = await executor.ExecutePipelineAsync(
            new CommandParser().Parse("ferramenta"), CancellationToken.None);
        Equal(0, result.ExitCode);
        True(result.Output.Contains("ok", StringComparison.OrdinalIgnoreCase));
        CommandExecutionResult spacedResult = await executor.ExecutePipelineAsync(
            new CommandParser().Parse("\"outra ferramenta\" \"hello world\""), CancellationToken.None);
        if (spacedResult.ExitCode != 0)
            throw new Exception($".cmd com espaços falhou: {spacedResult.Error} {spacedResult.Output}");
        True(spacedResult.Output.Contains("hello world", StringComparison.OrdinalIgnoreCase));
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
        Environment.SetEnvironmentVariable("PATH", originalPath, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("PATHEXT", originalPathExt, EnvironmentVariableTarget.Process);
        Directory.Delete(root, true);
    }
}

static Task TestAliases()
{
    var registry = new BuiltInCommandRegistry();
    True(registry.TryResolve("ls", out string ls) && ls == "dir");
    True(registry.TryResolve("cp", out string cp) && cp == "copy");
    True(registry.TryResolve("cat", out string cat) && cat == "type");
    True(registry.TryResolve("cls", out string clear) && clear == "clear");
    return Task.CompletedTask;
}

static async Task TestEnvironment()
{
    string original = Directory.GetCurrentDirectory();
    try
    {
        var context = new ShellContext(original);
        var builtIns = new BuiltInCommandRegistry();
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context), new AliasManager());
        var parser = new CommandParser();
        CommandStage set = parser.Parse("set TERMINAL_TEST_VALUE=123").Stages[0];
        await executor.ExecuteBuiltInAsync("set", set);
        CommandStage echo = parser.Parse("echo %TERMINAL_TEST_VALUE%").Stages[0];
        CommandExecutionResult result = await executor.ExecuteBuiltInAsync("echo", echo);
        Equal("123", result.Output);
    }
    finally { Directory.SetCurrentDirectory(original); }
}

static Task TestPowerShellStyleInvocation()
{
    string original = Directory.GetCurrentDirectory();
    string root = NewTempDirectory();
    try
    {
        string localAppData = Path.Combine(root, "AppData Local");
        string expected = Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllBytes(expected, []);

        var environment = new EnvironmentManager();
        environment.Set("LOCALAPPDATA", localAppData, export: false);
        Equal(expected, environment.Expand(@"$env:LOCALAPPDATA\Programs\Ollama\ollama.exe"));
        Equal(expected, environment.Expand(@"${env:LOCALAPPDATA}\Programs\Ollama\ollama.exe"));

        ParsedCommandLine parsed = new CommandParser().Parse(
            @"& ""$env:LOCALAPPDATA\Programs\Ollama\ollama.exe"" run livre-br --think=false");
        Equal(1, parsed.Stages.Count);
        Equal(@"$env:LOCALAPPDATA\Programs\Ollama\ollama.exe", parsed.Stages[0].Name);
        True(!parsed.Background);

        var context = new ShellContext(root, environment);
        ResolvedExecutable? resolved = new PathResolver(context).Resolve(parsed.Stages[0].Name);
        True(resolved is not null);
        Equal(expected, resolved!.Path);
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}

static Task TestNativeScriptArguments()
{
    var environment = new EnvironmentManager();
    environment.Set("ARG1", "primeiro", export: false);
    environment.Set("ARG2", "segundo", export: false);
    environment.Set("ARGS", "primeiro segundo", export: false);
    Equal("primeiro/segundo", environment.Expand("$1/$2"));
    Equal("argumentos: primeiro segundo", environment.Expand("argumentos: $@"));
    environment.Unset("ARG1", export: false);
    Equal("$1", environment.Expand("$1"));
    return Task.CompletedTask;
}

static async Task TestNativeScriptExecution()
{
    string original = Directory.GetCurrentDirectory();
    string root = NewTempDirectory();
    try
    {
        string script = Path.Combine(root, "automacao.tsh");
        await File.WriteAllLinesAsync(script,
        [
            "echo inicio-$1",
            "repeat 3",
            "echo volta-%ITERATION%",
            "end",
            "if echo condicao",
            "echo verdadeiro",
            "else",
            "echo falso",
            "end"
        ]);
        using var shell = new ShellEngine(root, ai: null, persistHistory: false);
        var output = new System.Text.StringBuilder();
        shell.OutputReceived += text => output.Append(text);
        await shell.SubmitAsync("automacao.tsh valor", 100, 30);
        string text = output.ToString();
        True(text.Contains("inicio-valor", StringComparison.Ordinal));
        True(text.Contains("volta-1", StringComparison.Ordinal));
        True(text.Contains("volta-2", StringComparison.Ordinal));
        True(text.Contains("volta-3", StringComparison.Ordinal));
        True(text.Contains("verdadeiro", StringComparison.Ordinal));
        True(!text.Contains("falso", StringComparison.Ordinal));
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
        Directory.Delete(root, true);
    }
}

static async Task TestNativeUtilities()
{
    string original = Directory.GetCurrentDirectory();
    string root = NewTempDirectory();
    try
    {
        await File.WriteAllTextAsync(Path.Combine(root, "dados.txt"), "beta\nalpha\nalpha\ngamma\n");
        var context = new ShellContext(root);
        var executor = new CommandExecutor(context, new BuiltInCommandRegistry(), new PathResolver(context), new AliasManager());
        var parser = new CommandParser();

        CommandExecutionResult found = await executor.ExecutePipelineAsync(
            parser.Parse("type dados.txt | find -n alpha"), CancellationToken.None);
        Equal(0, found.ExitCode);
        True(found.Output.Contains("2:alpha") && found.Output.Contains("3:alpha"));

        CommandExecutionResult head = await executor.ExecuteBuiltInAsync(
            "head", parser.Parse("head -n 2 dados.txt").Stages[0]);
        Equal("beta" + Environment.NewLine + "alpha", head.Output);

        CommandExecutionResult sorted = await executor.ExecuteBuiltInAsync(
            "sort", parser.Parse("sort -u dados.txt").Stages[0]);
        Equal("alpha" + Environment.NewLine + "beta" + Environment.NewLine + "gamma", sorted.Output);

        CommandExecutionResult count = await executor.ExecuteBuiltInAsync(
            "wc", parser.Parse("wc dados.txt").Stages[0]);
        string[] totals = count.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Equal("4", totals[0]);
        Equal("4", totals[1]);
        Equal("23", totals[2]);

        CommandExecutionResult sleep = await executor.ExecuteBuiltInAsync(
            "sleep", parser.Parse("sleep 0").Stages[0]);
        Equal(0, sleep.ExitCode);
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
        Directory.Delete(root, true);
    }
}

static async Task TestListWidth()
{
    string original = Directory.GetCurrentDirectory();
    string root = NewTempDirectory();
    try
    {
        string longName = "arquivo-com-um-nome-extremamente-grande-que-nao-deve-quebrar.txt";
        await File.WriteAllTextAsync(Path.Combine(root, longName), "x");
        var environment = new EnvironmentManager();
        environment.Set("COLUMNS", "40", export: false);
        var context = new ShellContext(root, environment);
        var executor = new CommandExecutor(context, new BuiltInCommandRegistry(), new PathResolver(context), new AliasManager());
        CommandExecutionResult result = await executor.ExecuteBuiltInAsync(
            "dir", new CommandParser().Parse("ls").Stages[0]);
        string line = result.Output.Split(Environment.NewLine)[0];
        True(line.Length <= 40);
        True(line.EndsWith('…'));
        True(!line.Contains(Environment.NewLine));
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
        Directory.Delete(root, true);
    }
}

static async Task TestRedirection()
{
    string original = Directory.GetCurrentDirectory();
    string root = NewTempDirectory();
    try
    {
        var context = new ShellContext(root);
        var builtIns = new BuiltInCommandRegistry();
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context), new AliasManager());
        var parser = new CommandParser();
        CommandStage first = parser.Parse("echo teste > arquivo.txt").Stages[0];
        await executor.ExecuteBuiltInAsync("echo", first);
        CommandStage second = parser.Parse("echo linha2 >> arquivo.txt").Stages[0];
        await executor.ExecuteBuiltInAsync("echo", second);
        string value = await File.ReadAllTextAsync(Path.Combine(root, "arquivo.txt"));
        True(value.Contains("teste") && value.Contains("linha2"));
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
        Directory.Delete(root, true);
    }
}

static async Task TestInputRedirection()
{
    string original = Directory.GetCurrentDirectory();
    string root = NewTempDirectory();
    try
    {
        await File.WriteAllTextAsync(Path.Combine(root, "entrada.txt"), "conteúdo de entrada");
        var context = new ShellContext(root);
        var builtIns = new BuiltInCommandRegistry();
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context), new AliasManager());
        CommandStage stage = new CommandParser().Parse("type < entrada.txt").Stages[0];
        CommandExecutionResult result = await executor.ExecuteBuiltInAsync("type", stage);
        Equal("conteúdo de entrada", result.Output);
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
        Directory.Delete(root, true);
    }
}

static async Task TestPipeline()
{
    string original = Directory.GetCurrentDirectory();
    try
    {
        var context = new ShellContext(original);
        var builtIns = new BuiltInCommandRegistry();
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context), new AliasManager());
        ParsedCommandLine parsed = new CommandParser().Parse("echo IPv4 | findstr IPv4");
        CommandExecutionResult result = await executor.ExecutePipelineAsync(parsed, CancellationToken.None);
        Equal(0, result.ExitCode);
        True(result.Output.Contains("IPv4", StringComparison.Ordinal));
    }
    finally { Directory.SetCurrentDirectory(original); }
}

static async Task TestExternalPipeline()
{
    string original = Directory.GetCurrentDirectory();
    try
    {
        var context = new ShellContext(original);
        var builtIns = new BuiltInCommandRegistry();
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context), new AliasManager());
        ParsedCommandLine parsed = new CommandParser().Parse("where.exe cmd.exe | findstr.exe cmd.exe");
        CommandExecutionResult result = await executor.ExecutePipelineAsync(parsed, CancellationToken.None);
        Equal(0, result.ExitCode);
        True(result.Output.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
    }
    finally { Directory.SetCurrentDirectory(original); }
}

static Task TestHistory()
{
    var history = new HistoryManager();
    history.Add("git status");
    history.Add("ai sk-1234567890abcdefghijklmnop");
    history.Add("set OPENROUTER_API_KEY=segredo");
    Equal(1, history.Entries.Count);
    Equal("git status", history.Previous(string.Empty));
    Equal(string.Empty, history.Next());
    True(SensitiveDataDetector.ContainsSensitiveData("me explique este .env"));
    True(SensitiveDataDetector.ContainsSensitiveData("Authorization: Bearer segredo-muito-longo"));
    True(!SensitiveDataDetector.ContainsSensitiveData("me explique variáveis de ambiente"));
    return Task.CompletedTask;
}

static Task TestInputBuffer()
{
    var input = new InputBuffer();
    input.Insert("@\\|€");
    input.MoveLeft();
    input.Insert("X");
    Equal("@\\|X€", input.Text);
    input.Backspace();
    Equal("@\\|€", input.Text);
    return Task.CompletedTask;
}

static Task TestTerminalLineFeed()
{
    var buffer = new TerminalBuffer();
    buffer.Resize(40, 8);
    buffer.Feed("primeira linha\nsegunda linha");
    string rendered = buffer.ToString();
    True(rendered.StartsWith("primeira linha" + Environment.NewLine + "segunda linha", StringComparison.Ordinal));
    True(!rendered.Contains(Environment.NewLine + "              segunda", StringComparison.Ordinal));
    return Task.CompletedTask;
}

static Task TestNaturalLanguage()
{
    True(ShellEngine.LooksLikeNaturalLanguage("oi", ["oi"]));
    True(ShellEngine.LooksLikeNaturalLanguage("me explica uma API", ["me", "explica", "uma", "API"]));
    True(ShellEngine.LooksLikeNaturalLanguage("como funciona isso?", ["como", "funciona", "isso?"]));
    True(ShellEngine.LooksLikeNaturalLanguage("em qual caminho nos ta", ["em", "qual", "caminho", "nos", "ta"]));
    True(ShellEngine.LooksLikeNaturalLanguage("veja aonde eu errei", ["veja", "aonde", "eu", "errei"]));
    True(!ShellEngine.LooksLikeNaturalLanguage("gitt status", ["gitt", "status"]));
    True(!ShellEngine.LooksLikeNaturalLanguage("comandoinexistente", ["comandoinexistente"]));
    return Task.CompletedTask;
}

static async Task TestCommandErrorContext()
{
    string original = Directory.GetCurrentDirectory();
    try
    {
        using var shell = new ShellEngine(original, ai: null, persistHistory: false);
        var output = new System.Text.StringBuilder();
        shell.OutputReceived += text => output.Append(text);
        await shell.SubmitAsync("ip config", 100, 30);
        TerminalAiContext context = shell.CreateTerminalAiContext();
        True(output.ToString().Contains("ipconfig", StringComparison.OrdinalIgnoreCase));
        Equal(1, context.LastExitCode);
        True(context.LastOutput?.Contains("ipconfig", StringComparison.OrdinalIgnoreCase) == true);
    }
    finally { Directory.SetCurrentDirectory(original); }
}

static Task TestAiTerminalContext()
{
    string longOutput = new string('x', 9000) + Environment.NewLine + "OPENROUTER_API_KEY=segredo";
    string formatted = AiBridgeServer.FormatTerminalContext(new TerminalAiContext(
        @"C:\Users\teste\Downloads", "dir", 1, 42, longOutput));
    True(formatted.Contains(@"C:\Users\teste\Downloads", StringComparison.Ordinal));
    True(formatted.Contains("Último comando real: dir", StringComparison.Ordinal));
    True(formatted.Contains("Código de saída: 1", StringComparison.Ordinal));
    True(formatted.Contains("42 ms", StringComparison.Ordinal));
    True(formatted.Contains("[DADO SENSÍVEL OCULTADO]", StringComparison.Ordinal));
    True(!formatted.Contains("segredo", StringComparison.Ordinal));
    True(formatted.Length < 9000);
    Equal("texto vermelho", SensitiveDataDetector.Redact("\x1b[31mtexto vermelho\x1b[0m"));
    return Task.CompletedTask;
}

static async Task TestNaturalLanguageBeforeAutoCd()
{
    string original = Directory.GetCurrentDirectory();
    string root = NewTempDirectory();
    Directory.CreateDirectory(Path.Combine(root, "oi"));
    try
    {
        using var shell = new ShellEngine(root, ai: null, persistHistory: false);
        var output = new System.Text.StringBuilder();
        shell.OutputReceived += text => output.Append(text);
        await shell.SubmitAsync("oi", 100, 30);
        Equal(root, Directory.GetCurrentDirectory());
        True(shell.Prompt.Contains(root, StringComparison.OrdinalIgnoreCase));
        True(output.ToString().Contains("Cliente de IA não está conectado", StringComparison.OrdinalIgnoreCase));
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
        Directory.Delete(root, true);
    }
}

static async Task TestNaturalLanguageBeforeExecutable()
{
    string original = Directory.GetCurrentDirectory();
    string root = NewTempDirectory();
    try
    {
        using var shell = new ShellEngine(root, ai: null, persistHistory: false);
        var output = new System.Text.StringBuilder();
        shell.OutputReceived += text => output.Append(text);
        await shell.SubmitAsync("em qual pasta estamos", 100, 30);
        True(!shell.IsInteractive);
        True(output.ToString().Contains("Cliente de IA não está conectado", StringComparison.OrdinalIgnoreCase));
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
        Directory.Delete(root, true);
    }
}

static async Task TestControlBuiltIns()
{
    string original = Directory.GetCurrentDirectory();
    try
    {
        var context = new ShellContext(original);
        var builtIns = new BuiltInCommandRegistry();
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context), new AliasManager());
        var parser = new CommandParser();
        CommandExecutionResult clear = await executor.ExecuteBuiltInAsync("clear", parser.Parse("clear").Stages[0]);
        CommandExecutionResult exit = await executor.ExecuteBuiltInAsync("exit", parser.Parse("exit").Stages[0]);
        True(clear.ClearRequested);
        True(exit.ExitRequested);
    }
    finally { Directory.SetCurrentDirectory(original); }
}

static Task TestParserSequenceAndBackground()
{
    var parser = new CommandParser();
    IReadOnlyList<ParsedCommandLine> commands = parser.ParseAll("echo a; echo b | findstr b");
    Equal(2, commands.Count);
    True(!commands[0].Background);
    Equal("echo", commands[0].Stages[0].Name);
    Equal(2, commands[1].Stages.Count);
    True(!commands[1].Background);

    ParsedCommandLine bg = parser.Parse("echo hello &");
    True(bg.Background);
    Equal("echo", bg.Stages[0].Name);
    Equal("hello", bg.Stages[0].Arguments[1]);

    IReadOnlyList<ParsedCommandLine> mixed = parser.ParseAll("echo a & echo b");
    Equal(2, mixed.Count);
    True(mixed[0].Background);
    True(!mixed[1].Background);
    return Task.CompletedTask;
}

static async Task TestCdUnquotedSpaces()
{
    string original = Directory.GetCurrentDirectory();
    string root = NewTempDirectory();
    string child = Directory.CreateDirectory(Path.Combine(root, "mod menu")).FullName;
    try
    {
        var context = new ShellContext(root);
        var executor = new CommandExecutor(context, new BuiltInCommandRegistry(), new PathResolver(context), new AliasManager());
        CommandStage stage = new CommandParser().Parse("cd mod menu").Stages[0];
        CommandExecutionResult result = await executor.ExecuteBuiltInAsync("cd", stage);
        Equal(0, result.ExitCode);
        Equal(child, context.CurrentDirectory);
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
        Directory.Delete(root, true);
    }
}

static Task TestParenthesesInPath()
{
    var parser = new CommandParser();
    ParsedCommandLine parsed = parser.Parse(@"cd ""C:\Program Files (x86)""");
    Equal(1, parsed.Stages.Count);
    Equal(@"C:\Program Files (x86)", parsed.Stages[0].Arguments[1]);
    True(!parsed.Background);
    return Task.CompletedTask;
}

static Task TestAndOrAndMerge()
{
    var parser = new CommandParser();
    IReadOnlyList<ParsedCommandLine> and = parser.ParseAll("echo a && echo b");
    Equal(2, and.Count);
    Equal(CommandRunIf.Always, and[0].RunIf);
    Equal(CommandRunIf.PreviousSuccess, and[1].RunIf);

    IReadOnlyList<ParsedCommandLine> or = parser.ParseAll("echo a || echo b");
    Equal(CommandRunIf.PreviousFailure, or[1].RunIf);

    ParsedCommandLine merge = parser.Parse("where cmd 2>&1");
    True(merge.Stages[0].Redirections.Any(item => item.Kind == RedirectionKind.MergeError));
    return Task.CompletedTask;
}

static Task TestTildeExitAndHistory()
{
    var environment = new EnvironmentManager();
    environment.Set("LASTEXITCODE", "7");
    Equal("7", environment.Expand("$?"));
    environment.Set("LASTOUTPUT", "saida", export: false);
    Equal("saida", environment.Expand("$_"));

    string original = Directory.GetCurrentDirectory();
    string root = NewTempDirectory();
    string historyPath = Path.Combine(root, "hist.txt");
    try
    {
        var first = new HistoryManager(historyPath);
        first.Add("git status");
        var second = new HistoryManager(historyPath);
        Equal(1, second.Entries.Count);
        Equal("git status", second.Entries[0]);

        var context = new ShellContext(root, environment);
        string home = environment.Get("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        context.ChangeDirectory("~");
        Equal(Path.GetFullPath(home), context.CurrentDirectory);
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}

static Task TestFuzzyAndBack()
{
    string original = Directory.GetCurrentDirectory();
    string root = NewTempDirectory();
    string child = Directory.CreateDirectory(Path.Combine(root, "mod menu")).FullName;
    try
    {
        var context = new ShellContext(root);
        context.ChangeDirectory("menu");
        Equal(child, context.CurrentDirectory);
        context.ChangeDirectory("-");
        Equal(root, context.CurrentDirectory);
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}

static string NewTempDirectory()
{
    string path = Path.Combine(Path.GetTempPath(), "TerminalCustom.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"esperado '{expected}', recebido '{actual}'");
}
static void True(bool condition)
{
    if (!condition) throw new Exception("condição esperada não foi atendida");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"exceção {typeof(T).Name} não foi lançada");
}
