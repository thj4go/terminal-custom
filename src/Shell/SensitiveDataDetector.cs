using System.Text.RegularExpressions;

namespace TerminalCustom.Shell;

internal static partial class SensitiveDataDetector
{
    public static bool ContainsSensitiveData(string text) =>
        SecretPattern().IsMatch(text) || PrivateKeyPattern().IsMatch(text);

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        string cleaned = AnsiPattern().Replace(text, string.Empty);
        cleaned = new string(cleaned.Where(character =>
            character is '\r' or '\n' or '\t' || !char.IsControl(character)).ToArray());
        string withoutPrivateKeys = PrivateKeyBlockPattern().Replace(
            cleaned, "[CHAVE PRIVADA OCULTADA]");
        string[] lines = withoutPrivateKeys.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (ContainsSensitiveData(lines[i]))
                lines[i] = "[DADO SENSÍVEL OCULTADO]";
        }
        return string.Join(Environment.NewLine, lines);
    }

    [GeneratedRegex("(?ix)(sk-(?:or-v1-|proj-)?[a-z0-9_-]{16,}|bearer\\s+[a-z0-9._~+/-]{12,}|authorization\\s*:|(?:password|senha|cookie|openai_api_key|openrouter_api_key|api[_-]?key)\\s*[=:]|(?:^|[\\/\\s])\\.env(?:$|\\s))")]
    private static partial Regex SecretPattern();

    [GeneratedRegex("-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex("-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\\s\\S]*?-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyBlockPattern();

    [GeneratedRegex("\\x1B(?:\\][^\\x07]*(?:\\x07|\\x1B\\\\)|\\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiPattern();
}
