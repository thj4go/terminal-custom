using TerminalCustom.Shell;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("parser: aspas, espaços e operadores", TestParser),
    ("parser: aspas vazias e erro de aspas", TestParserEdges),
    ("cd: caminhos relativos, absolutos e variável", TestCd),
    ("PATH e PATHEXT", TestPathAndPathExt),
    ("aliases dos built-ins", TestAliases),
    ("variáveis de ambiente", TestEnvironment),
    ("redirecionamento > e >>", TestRedirection),
    ("redirecionamento de entrada <", TestInputRedirection),
    ("pipeline direto", TestPipeline),
    ("pipeline entre dois executáveis", TestExternalPipeline),
    ("histórico e proteção de segredos", TestHistory),
    ("edição e caracteres ABNT2", TestInputBuffer),
    ("linguagem natural versus erro de comando", TestNaturalLanguage),
    ("clear e exit sinalizam a aplicação", TestControlBuiltIns)
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
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context));
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
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context));
        var parser = new CommandParser();
        CommandStage set = parser.Parse("set TERMINAL_TEST_VALUE=123").Stages[0];
        await executor.ExecuteBuiltInAsync("set", set);
        CommandStage echo = parser.Parse("echo %TERMINAL_TEST_VALUE%").Stages[0];
        CommandExecutionResult result = await executor.ExecuteBuiltInAsync("echo", echo);
        Equal("123", result.Output);
    }
    finally { Directory.SetCurrentDirectory(original); }
}

static async Task TestRedirection()
{
    string original = Directory.GetCurrentDirectory();
    string root = NewTempDirectory();
    try
    {
        var context = new ShellContext(root);
        var builtIns = new BuiltInCommandRegistry();
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context));
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
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context));
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
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context));
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
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context));
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

static Task TestNaturalLanguage()
{
    True(ShellEngine.LooksLikeNaturalLanguage("oi", ["oi"]));
    True(ShellEngine.LooksLikeNaturalLanguage("me explica uma API", ["me", "explica", "uma", "API"]));
    True(ShellEngine.LooksLikeNaturalLanguage("como funciona isso?", ["como", "funciona", "isso?"]));
    True(!ShellEngine.LooksLikeNaturalLanguage("gitt status", ["gitt", "status"]));
    True(!ShellEngine.LooksLikeNaturalLanguage("comandoinexistente", ["comandoinexistente"]));
    return Task.CompletedTask;
}

static async Task TestControlBuiltIns()
{
    string original = Directory.GetCurrentDirectory();
    try
    {
        var context = new ShellContext(original);
        var builtIns = new BuiltInCommandRegistry();
        var executor = new CommandExecutor(context, builtIns, new PathResolver(context));
        var parser = new CommandParser();
        CommandExecutionResult clear = await executor.ExecuteBuiltInAsync("clear", parser.Parse("clear").Stages[0]);
        CommandExecutionResult exit = await executor.ExecuteBuiltInAsync("exit", parser.Parse("exit").Stages[0]);
        True(clear.ClearRequested);
        True(exit.ExitRequested);
    }
    finally { Directory.SetCurrentDirectory(original); }
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
