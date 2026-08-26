namespace TerminalCustom.Shell;

internal sealed record CommandExecutionResult(
    int ExitCode = 0,
    string Output = "",
    string Error = "",
    bool ClearRequested = false,
    bool ExitRequested = false);
