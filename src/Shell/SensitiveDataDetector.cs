using System.Text.RegularExpressions;

namespace TerminalCustom.Shell;

internal static partial class SensitiveDataDetector
{
    public static bool ContainsSensitiveData(string text) =>
        SecretPattern().IsMatch(text) || PrivateKeyPattern().IsMatch(text);

    [GeneratedRegex("(?ix)(sk-(?:or-v1-|proj-)?[a-z0-9_-]{16,}|bearer\\s+[a-z0-9._~+/-]{12,}|authorization\\s*:|(?:password|senha|cookie|openai_api_key|openrouter_api_key|api[_-]?key)\\s*[=:]|(?:^|[\\/\\s])\\.env(?:$|\\s))")]
    private static partial Regex SecretPattern();

    [GeneratedRegex("-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyPattern();
}
