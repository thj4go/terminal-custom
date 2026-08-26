using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TerminalCustom;

internal sealed class AiBridgeServer : IDisposable
{
    private const string Model = "deepseek/deepseek-v4-pro";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string DefaultSystemPrompt = "Você é a IA integrada a um terminal Windows. Responda em português do Brasil, de forma clara e direta. Não finja que executou comandos. Quando sugerir um comando, explique brevemente e formate-o com clareza.";
    private readonly Window _owner;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly List<ChatMessage> _history = [];
    private string? _apiKey;
    private string _systemPrompt = DefaultSystemPrompt;
    private bool _customSystemPrompt;
    private bool _disposed;

    public AiBridgeServer(Window owner)
    {
        _owner = owner;
    }

    public async Task<AiResult> ConfigureKeyAsync()
    {
        string? key = await _owner.Dispatcher.InvokeAsync(ShowKeyDialog);
        if (string.IsNullOrWhiteSpace(key))
            return AiResult.Error("Configuração cancelada. A chave não foi alterada.");

        _apiKey = key.Trim();
        _history.Clear();
        return AiResult.Success("Chave da OpenRouter configurada somente nesta sessão.");
    }

    public AiResult ClearKey()
    {
        _apiKey = null;
        _history.Clear();
        return AiResult.Success("Chave removida da memória.");
    }

    public async Task<AiResult> ConfigureSystemPromptAsync()
    {
        string? prompt = await _owner.Dispatcher.InvokeAsync(() => ShowSystemPromptDialog(_systemPrompt));
        if (string.IsNullOrWhiteSpace(prompt))
            return AiResult.Error("Alteração cancelada. A personalidade não foi modificada.");

        _systemPrompt = prompt.Trim();
        _customSystemPrompt = true;
        _history.Clear();
        return AiResult.Success("Personalidade da IA alterada somente nesta sessão.");
    }

    public AiResult ClearSystemPrompt()
    {
        _systemPrompt = DefaultSystemPrompt;
        _customSystemPrompt = false;
        _history.Clear();
        return AiResult.Success("Personalidade padrão da IA restaurada.");
    }

    public AiResult Status()
    {
        string personality = _customSystemPrompt ? "personalidade personalizada" : "personalidade padrão";
        return string.IsNullOrWhiteSpace(_apiKey)
            ? AiResult.Error($"IA sem chave; {personality}. Use ai-key para configurar.")
            : AiResult.Success($"IA ativa: {Model} via OpenRouter; {personality}.");
    }

    public async Task<AiResult> ChatAsync(string? prompt, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return AiResult.Error("Escreva uma pergunta para a IA.");
        if (string.IsNullOrWhiteSpace(_apiKey))
            return AiResult.Error("IA sem chave. Use ai-key para configurar.");

        var messages = new List<ChatMessage>
        {
            new("system", _systemPrompt)
        };
        messages.AddRange(_history);
        messages.Add(new ChatMessage("user", prompt.Trim()));

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.TryAddWithoutValidation("X-OpenRouter-Title", "Terminal Custom");
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = Model,
            messages,
            max_tokens = 1600,
            temperature = 0.6
        }), Encoding.UTF8, "application/json");

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, token);
            string body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                return AiResult.Error(ReadApiError(body, (int)response.StatusCode));

            using JsonDocument document = JsonDocument.Parse(body);
            string? answer = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            if (string.IsNullOrWhiteSpace(answer))
                return AiResult.Error("A IA respondeu sem texto.");

            _history.Add(new ChatMessage("user", prompt.Trim()));
            _history.Add(new ChatMessage("assistant", answer.Trim()));
            if (_history.Count > 16)
                _history.RemoveRange(0, _history.Count - 16);

            return AiResult.Success($"IA: {answer.Trim()}");
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            return AiResult.Error("A OpenRouter demorou demais para responder.");
        }
        catch (HttpRequestException ex)
        {
            return AiResult.Error($"Não foi possível acessar a OpenRouter: {ex.Message}");
        }
        catch (JsonException)
        {
            return AiResult.Error("A OpenRouter devolveu uma resposta inválida.");
        }
    }

    private static string ReadApiError(string body, int statusCode)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.TryGetProperty("message", out JsonElement message))
                return $"OpenRouter ({statusCode}): {message.GetString()}";
        }
        catch (JsonException) { }
        return $"A OpenRouter recusou a solicitação (código {statusCode}).";
    }

    private string? ShowKeyDialog()
    {
        var dialog = new Window
        {
            Owner = _owner,
            Title = "Chave da OpenRouter",
            Width = 430,
            Height = 225,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false
        };

        var password = new PasswordBox
        {
            Height = 38,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            Background = new SolidColorBrush(Color.FromRgb(11, 24, 40)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(82, 109, 145)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            MaxLength = 512
        };

        var confirm = CreateDialogButton("Usar chave", Color.FromRgb(8, 145, 178));
        var cancel = CreateDialogButton("Cancelar", Color.FromRgb(35, 48, 68));
        confirm.Click += (_, _) => { dialog.DialogResult = !string.IsNullOrWhiteSpace(password.Password); };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        password.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && !string.IsNullOrWhiteSpace(password.Password))
                dialog.DialogResult = true;
            else if (e.Key == System.Windows.Input.Key.Escape)
                dialog.DialogResult = false;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        var content = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        content.Children.Add(new TextBlock
        {
            Text = "Chave da OpenRouter",
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 7)
        });
        content.Children.Add(new TextBlock
        {
            Text = "Ela ficará somente na memória até o terminal fechar.",
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 14)
        });
        content.Children.Add(password);
        content.Children.Add(new Border { Height = 15 });
        content.Children.Add(buttons);

        dialog.Content = new Border
        {
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(117, 139, 170)),
            Background = new SolidColorBrush(Color.FromRgb(12, 24, 40)),
            Child = content
        };
        dialog.Loaded += (_, _) => password.Focus();
        return dialog.ShowDialog() == true ? password.Password : null;
    }

    private string? ShowSystemPromptDialog(string currentPrompt)
    {
        var dialog = new Window
        {
            Owner = _owner,
            Title = "Personalidade da IA",
            Width = 520,
            Height = 390,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false
        };

        var prompt = new TextBox
        {
            Text = currentPrompt,
            Height = 205,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            Background = new SolidColorBrush(Color.FromRgb(11, 24, 40)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(82, 109, 145)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            MaxLength = 4000
        };

        var confirm = CreateDialogButton("Usar personalidade", Color.FromRgb(8, 145, 178));
        var cancel = CreateDialogButton("Cancelar", Color.FromRgb(35, 48, 68));
        confirm.Click += (_, _) => { dialog.DialogResult = !string.IsNullOrWhiteSpace(prompt.Text); };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        prompt.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter &&
                System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) &&
                !string.IsNullOrWhiteSpace(prompt.Text))
            {
                e.Handled = true;
                dialog.DialogResult = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                dialog.DialogResult = false;
            }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        var content = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        content.Children.Add(new TextBlock
        {
            Text = "Personalidade da IA",
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 7)
        });
        content.Children.Add(new TextBlock
        {
            Text = "Escreva o system prompt. Ele ficará somente na memória desta sessão. Use Ctrl+Enter para confirmar.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 14)
        });
        content.Children.Add(prompt);
        content.Children.Add(new Border { Height = 15 });
        content.Children.Add(buttons);

        dialog.Content = new Border
        {
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(117, 139, 170)),
            Background = new SolidColorBrush(Color.FromRgb(12, 24, 40)),
            Child = content
        };
        dialog.Loaded += (_, _) =>
        {
            prompt.Focus();
            prompt.CaretIndex = prompt.Text.Length;
        };
        return dialog.ShowDialog() == true ? prompt.Text : null;
    }

    private static Button CreateDialogButton(string text, Color background) => new()
    {
        Content = text,
        MinWidth = 98,
        Height = 34,
        Margin = new Thickness(8, 0, 0, 0),
        Padding = new Thickness(12, 3, 12, 3),
        Foreground = Brushes.White,
        Background = new SolidColorBrush(background),
        BorderThickness = new Thickness(0),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _apiKey = null;
        _systemPrompt = DefaultSystemPrompt;
        _customSystemPrompt = false;
        _history.Clear();
        _http.Dispose();
    }

    public sealed record AiResult(bool Ok, string Message)
    {
        public static AiResult Success(string message) => new(true, message);
        public static AiResult Error(string message) => new(false, message);
    }
    private sealed record ChatMessage(string role, string content);
}
