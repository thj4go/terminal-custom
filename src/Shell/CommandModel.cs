namespace TerminalCustom.Shell;

internal enum RedirectionKind
{
    Input,
    Output,
    Append,
    Heredoc,
    ErrorOutput,
    ErrorAppend,
    MergeError
}

internal enum CommandRunIf
{
    Always,
    PreviousSuccess,
    PreviousFailure
}

internal sealed record Redirection(RedirectionKind Kind, string Path);

internal sealed record CommandStage(IReadOnlyList<string> Arguments, IReadOnlyList<Redirection> Redirections)
{
    public string Name => Arguments.Count == 0 ? string.Empty : Arguments[0];
}

internal sealed record ParsedCommandLine(
    IReadOnlyList<CommandStage> Stages,
    bool Background = false,
    CommandRunIf RunIf = CommandRunIf.Always)
{
    public bool IsPipeline => Stages.Count > 1;
}

internal sealed class CommandParseException(string message) : Exception(message);
