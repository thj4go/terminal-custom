using System.Text;

namespace TerminalCustom.Shell;

internal sealed class CommandParser
{
    private enum TokenKind { Word, Pipe, Input, Output, Append }
    private sealed record Token(TokenKind Kind, string Text);

    public ParsedCommandLine Parse(string commandLine)
    {
        List<Token> tokens = Tokenize(commandLine);
        if (tokens.Count == 0) return new ParsedCommandLine([]);

        var stages = new List<CommandStage>();
        var arguments = new List<string>();
        var redirections = new List<Redirection>();

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

            if (++index >= tokens.Count || tokens[index].Kind != TokenKind.Word)
                throw new CommandParseException($"Falta um arquivo depois de '{token.Text}'.");

            RedirectionKind kind = token.Kind switch
            {
                TokenKind.Input => RedirectionKind.Input,
                TokenKind.Append => RedirectionKind.Append,
                _ => RedirectionKind.Output
            };
            redirections.Add(new Redirection(kind, tokens[index].Text));
        }

        AddStage(stages, arguments, redirections);
        return new ParsedCommandLine(stages);
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
            if (character == '|')
            {
                FlushWord();
                result.Add(new Token(TokenKind.Pipe, "|"));
                continue;
            }
            if (character == '<')
            {
                FlushWord();
                result.Add(new Token(TokenKind.Input, "<"));
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
