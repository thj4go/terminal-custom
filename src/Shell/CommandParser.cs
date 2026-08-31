using System.Text;

namespace TerminalCustom.Shell;

internal sealed class CommandParser
{
    private enum TokenKind { Word, Pipe, Input, Output, Append, SemiColon, Background, And, Or, ErrorOutput, ErrorAppend, MergeError }
    private sealed record Token(TokenKind Kind, string Text);

    public ParsedCommandLine Parse(string commandLine)
    {
        IReadOnlyList<ParsedCommandLine> all = ParseAll(commandLine);
        return all.Count == 0 ? new ParsedCommandLine([]) : all[0];
    }

    public IReadOnlyList<ParsedCommandLine> ParseAll(string commandLine)
    {
        List<Token> tokens = Tokenize(commandLine);
        if (tokens.Count == 0) return [];

        var commands = new List<ParsedCommandLine>();
        var stages = new List<CommandStage>();
        var arguments = new List<string>();
        var redirections = new List<Redirection>();
        CommandRunIf nextRunIf = CommandRunIf.Always;

        void FlushCommand(bool background)
        {
            if (arguments.Count == 0 && stages.Count == 0) return;
            AddStage(stages, arguments, redirections);
            commands.Add(new ParsedCommandLine(stages.ToArray(), background, nextRunIf));
            stages = [];
            arguments = [];
            redirections = [];
        }

        for (int index = 0; index < tokens.Count; index++)
        {
            Token token = tokens[index];
            if (token.Kind == TokenKind.Word)
            {
                arguments.Add(token.Text);
                continue;
            }

            if (token.Kind == TokenKind.Pipe)
            {
                AddStage(stages, arguments, redirections);
                arguments = [];
                redirections = [];
                continue;
            }

            if (token.Kind == TokenKind.SemiColon)
            {
                FlushCommand(false);
                nextRunIf = CommandRunIf.Always;
                continue;
            }

            if (token.Kind == TokenKind.And)
            {
                FlushCommand(false);
                nextRunIf = CommandRunIf.PreviousSuccess;
                continue;
            }

            if (token.Kind == TokenKind.Or)
            {
                FlushCommand(false);
                nextRunIf = CommandRunIf.PreviousFailure;
                continue;
            }

            if (token.Kind == TokenKind.Background)
            {
                FlushCommand(true);
                nextRunIf = CommandRunIf.Always;
                continue;
            }

            if (token.Kind == TokenKind.MergeError)
            {
                redirections.Add(new Redirection(RedirectionKind.MergeError, token.Text));
                continue;
            }

            if (token.Kind == TokenKind.Input)
            {
                if (token.Text is "<<" or "<<-")
                {
                    if (index + 1 < tokens.Count && tokens[index + 1].Kind == TokenKind.Word)
                    {
                        redirections.Add(new Redirection(RedirectionKind.Heredoc, tokens[index + 1].Text));
                        index++;
                    }
                    else redirections.Add(new Redirection(RedirectionKind.Heredoc, ""));
                    continue;
                }
                if (++index >= tokens.Count || tokens[index].Kind != TokenKind.Word)
                    throw new CommandParseException("Falta um arquivo depois de '<'.");
                redirections.Add(new Redirection(RedirectionKind.Input, tokens[index].Text));
                continue;
            }

            if (token.Kind is TokenKind.Output or TokenKind.Append or TokenKind.ErrorOutput or TokenKind.ErrorAppend)
            {
                if (++index >= tokens.Count || tokens[index].Kind != TokenKind.Word)
                    throw new CommandParseException($"Falta um arquivo depois de '{token.Text}'.");
                RedirectionKind kind = token.Kind switch
                {
                    TokenKind.Append => RedirectionKind.Append,
                    TokenKind.ErrorOutput => RedirectionKind.ErrorOutput,
                    TokenKind.ErrorAppend => RedirectionKind.ErrorAppend,
                    _ => RedirectionKind.Output
                };
                redirections.Add(new Redirection(kind, tokens[index].Text));
                if (token.Text is "&>" or "&>>")
                    redirections.Add(new Redirection(RedirectionKind.MergeError, token.Text));
            }
        }

        FlushCommand(false);
        return commands;
    }

    private static void AddStage(List<CommandStage> stages, List<string> arguments, List<Redirection> redirections)
    {
        if (arguments.Count == 0)
            throw new CommandParseException(stages.Count == 0 ? "Digite um comando." : "Pipeline incompleto.");
        stages.Add(new CommandStage(arguments.ToArray(), redirections.ToArray()));
    }

    private static List<Token> Tokenize(string input)
    {
        var result = new List<Token>();
        var word = new StringBuilder();
        bool wordStarted = false;
        char quote = '\0';

        void FlushWord()
        {
            if (!wordStarted) return;
            result.Add(new Token(TokenKind.Word, word.ToString()));
            word.Clear();
            wordStarted = false;
        }

        for (int index = 0; index < input.Length; index++)
        {
            char character = input[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                    continue;
                }
                if (character == '\\' && index + 1 < input.Length && input[index + 1] == quote)
                {
                    word.Append(input[++index]);
                    continue;
                }
                word.Append(character);
                wordStarted = true;
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                wordStarted = true;
                continue;
            }
            if (char.IsWhiteSpace(character))
            {
                FlushWord();
                continue;
            }
            if (!wordStarted && character == '2' && index + 1 < input.Length && input[index + 1] == '>')
            {
                index++;
                if (index + 1 < input.Length && input[index + 1] == '>')
                {
                    index++;
                    result.Add(new Token(TokenKind.ErrorAppend, "2>>"));
                }
                else if (index + 1 < input.Length && input[index + 1] == '&')
                {
                    index++;
                    if (index + 1 < input.Length && input[index + 1] is '1' or '2')
                    {
                        index++;
                        result.Add(new Token(TokenKind.MergeError, "2>&1"));
                    }
                    else result.Add(new Token(TokenKind.MergeError, "2>&1"));
                }
                else result.Add(new Token(TokenKind.ErrorOutput, "2>"));
                continue;
            }
            if (character == '|')
            {
                FlushWord();
                if (index + 1 < input.Length && input[index + 1] == '|')
                {
                    index++;
                    result.Add(new Token(TokenKind.Or, "||"));
                }
                else result.Add(new Token(TokenKind.Pipe, "|"));
                continue;
            }
            if (character == '&')
            {
                FlushWord();
                if (index + 1 < input.Length && input[index + 1] == '&')
                {
                    index++;
                    result.Add(new Token(TokenKind.And, "&&"));
                }
                else if (index + 1 < input.Length && input[index + 1] == '>')
                {
                    index++;
                    if (index + 1 < input.Length && input[index + 1] == '>')
                    {
                        index++;
                        result.Add(new Token(TokenKind.Append, "&>>"));
                    }
                    else result.Add(new Token(TokenKind.Output, "&>"));
                }
                else result.Add(new Token(TokenKind.Background, "&"));
                continue;
            }
            if (character == ';')
            {
                FlushWord();
                result.Add(new Token(TokenKind.SemiColon, ";"));
                continue;
            }
            if (character == '<')
            {
                FlushWord();
                if (index + 1 < input.Length && input[index + 1] == '<')
                {
                    index++;
                    if (index + 1 < input.Length && input[index + 1] == '-')
                    {
                        index++;
                        result.Add(new Token(TokenKind.Input, "<<-"));
                    }
                    else result.Add(new Token(TokenKind.Input, "<<"));
                }
                else result.Add(new Token(TokenKind.Input, "<"));
                continue;
            }
            if (character == '>')
            {
                FlushWord();
                if (index + 1 < input.Length && input[index + 1] == '>')
                {
                    index++;
                    result.Add(new Token(TokenKind.Append, ">>"));
                }
                else result.Add(new Token(TokenKind.Output, ">"));
                continue;
            }
            word.Append(character);
            wordStarted = true;
        }

        if (quote != '\0') throw new CommandParseException("Aspas não foram fechadas.");
        FlushWord();
        return result;
    }
}
