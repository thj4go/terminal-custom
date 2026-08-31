using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TerminalCustom.Shell;

namespace TerminalCustom;

public partial class MainWindow : Window
{
    private TerminalBuffer _buffer = new();
    private readonly InputBuffer _input = new();
    private AiBridgeServer? _ai;
    private ShellEngine? _shell;
    private bool _renderQueued;
    private short _columns = 100;
    private short _rows = 30;
    private bool _searchMode;
    private string _searchQuery = string.Empty;
    private string _originalInput = string.Empty;
    private int _searchIndex = -1;
    private int _tabIndex = -1;
    private List<string> _tabCompletions = [];

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => StartShell();
        Closed += (_, _) => StopShell();
    }

    private void StartShell()
    {
        StopShell();
        _buffer = new TerminalBuffer();
        _input.Clear();
        SetTerminalPlainText(string.Empty);
        CalculateSize();
        _buffer.Resize(_columns, _rows);
        try
        {
            _ai = new AiBridgeServer(this);
            _shell = new ShellEngine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _ai);
            _shell.OutputReceived += OnOutput;
            _shell.ClearRequested += OnClearRequested;
            _shell.ExitRequested += OnExitRequested;
            _shell.InteractiveEnded += OnInteractiveEnded;
            _shell.Start();
            TerminalView.Focus();
        }
        catch (Exception ex)
        {
            TerminalView.Foreground = Brush("#FDA4AF");
            SetTerminalPlainText($"Não foi possível iniciar o terminal.\r\n\r\n{ex.Message}");
        }
    }

    private void StopShell()
    {
        if (_shell is not null)
        {
            _shell.OutputReceived -= OnOutput;
            _shell.ClearRequested -= OnClearRequested;
            _shell.ExitRequested -= OnExitRequested;
            _shell.InteractiveEnded -= OnInteractiveEnded;
            _shell.Dispose();
            _shell = null;
        }
        _ai?.Dispose();
        _ai = null;
    }

    private void OnOutput(string text) => Dispatcher.InvokeAsync(() =>
    {
        _buffer.Feed(text);
        QueueRender();
    });
    private void OnClearRequested() => Dispatcher.InvokeAsync(() => ClearLocally(false));
    private void OnExitRequested() => Dispatcher.InvokeAsync(Close);
    private void OnInteractiveEnded() => Dispatcher.InvokeAsync(_input.Clear);

    private void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.InvokeAsync(Render, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Render()
    {
        _renderQueued = false;
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0), PageWidth = 10000,
            FontFamily = TerminalView.FontFamily, FontSize = TerminalView.FontSize
        };
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0), LineHeight = TerminalView.FontSize * 1.35,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        foreach (TerminalSegment segment in _buffer.GetStyledSegments())
            paragraph.Inlines.Add(new Run(segment.Text) { Foreground = TerminalBrush(segment.Color) });
        document.Blocks.Add(paragraph);
        TerminalView.Document = document;
        TerminalView.CaretPosition = document.ContentEnd;
        TerminalView.ScrollToEnd();
        TerminalView.Focus();
    }

    private void SetTerminalPlainText(string text)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), PageWidth = 10000 };
        document.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0) });
        TerminalView.Document = document;
    }

    private void RenderInputLine()
    {
        if (_shell is null || _shell.IsInteractive) return;
        int tail = _input.Text.Length - _input.Cursor;
        string prompt = _searchMode ? $"(reverse-i-search)'{_searchQuery}': " : _shell.Prompt;
        _buffer.Feed("\r\x1b[2K" + prompt + _input.Text + (tail > 0 ? $"\x1b[{tail}D" : string.Empty));
        QueueRender();
    }

    private async void TerminalView_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        if (_shell?.IsInteractive == true) await _shell.WriteInteractiveAsync(e.Text);
        else if (_shell is { IsBusy: false })
        {
            if (_searchMode) { HandleSearchInput(e.Text); return; }
            _tabIndex = -1;
            _input.Insert(e.Text);
            RenderInputLine();
        }
    }

    private async void TerminalView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_shell is null) return;
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool alt = modifiers.HasFlag(ModifierKeys.Alt);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && alt) return;

        if (_searchMode)
        {
            if (e.Key == Key.Escape) { ExitSearchMode(); e.Handled = true; return; }
            if (e.Key == Key.Enter) { ExitSearchMode(true); e.Handled = true; return; }
            if (ctrl && e.Key == Key.R) { SearchNext(); e.Handled = true; return; }
            if (e.Key == Key.Back)
            {
                if (_searchQuery.Length > 0) _searchQuery = _searchQuery[..^1];
                SearchInHistory();
                RenderInputLine();
            }
            e.Handled = true;
            return;
        }

        if (ctrl && shift && e.Key == Key.C)
        {
            e.Handled = true;
            CopySelection();
            return;
        }
        if (ctrl && shift && e.Key == Key.V)
        {
            e.Handled = true;
            if (Clipboard.ContainsText()) await PasteAsync(Clipboard.GetText());
            return;
        }
        if (ctrl && e.Key == Key.L)
        {
            e.Handled = true;
            ClearLocally(!_shell.IsInteractive && !_shell.IsBusy);
            return;
        }
        if (ctrl && e.Key == Key.C)
        {
            e.Handled = true;
            if (_shell.IsInteractive || _shell.IsBusy) await _shell.CancelAsync();
            else
            {
                _buffer.Feed("^C\r\n" + _shell.Prompt);
                _input.Clear();
                QueueRender();
            }
            return;
        }
        if (ctrl && e.Key == Key.R)
        {
            e.Handled = true;
            if (!_shell.IsInteractive && !_shell.IsBusy) EnterSearchMode();
            return;
        }
        if (ctrl && e.Key == Key.Z)
        {
            e.Handled = true;
            if (!_shell.IsInteractive && !_shell.IsBusy && _input.Undo()) RenderInputLine();
            return;
        }
        if (ctrl && e.Key == Key.Y)
        {
            e.Handled = true;
            if (!_shell.IsInteractive && !_shell.IsBusy && _input.Redo()) RenderInputLine();
            return;
        }

        if (e.Key == Key.Space)
        {
            e.Handled = true;
            if (_shell.IsInteractive) await _shell.WriteInteractiveAsync(" ");
            else if (!_shell.IsBusy)
            {
                _tabIndex = -1;
                _input.Insert(" ");
                RenderInputLine();
            }
            return;
        }

        if (_shell.IsInteractive)
        {
            string? sequence = e.Key switch
            {
                Key.Enter => "\r", Key.Back => "\x7f", Key.Tab => "\t", Key.Escape => "\x1b",
                Key.Up => "\x1b[A", Key.Down => "\x1b[B", Key.Right => "\x1b[C", Key.Left => "\x1b[D",
                Key.Home => "\x1b[H", Key.End => "\x1b[F", Key.Delete => "\x1b[3~",
                Key.PageUp => "\x1b[5~", Key.PageDown => "\x1b[6~", _ => null
            };
            if (sequence is not null)
            {
                e.Handled = true;
                await _shell.WriteInteractiveAsync(sequence);
            }
            return;
        }
        if (_shell.IsBusy) return;

        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            HandleTabCompletion();
            return;
        }

        bool changed = true;
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.A: _input.MoveHome(); break;
                case Key.E: _input.MoveEnd(); break;
                case Key.U: _input.KillLineStart(); break;
                case Key.K: _input.KillLineEnd(); break;
                case Key.W: _input.DeleteWordBack(); break;
                case Key.Left: _input.MoveWordLeft(); break;
                case Key.Right: _input.MoveWordRight(); break;
                default: changed = false; break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    _tabIndex = -1;
                    string command = _input.Take();
                    _buffer.Feed("\r\x1b[2K" + _shell.Prompt + command + "\r\n");
                    QueueRender();
                    await _shell.SubmitAsync(command, _columns, _rows);
                    return;
                case Key.Back: _input.Backspace(); break;
                case Key.Delete: _input.Delete(); break;
                case Key.Left: _input.MoveLeft(); break;
                case Key.Right: _input.MoveRight(); break;
                case Key.Home: _input.MoveHome(); break;
                case Key.End: _input.MoveEnd(); break;
                case Key.Up: _input.Replace(_shell.History.Previous(_input.Text)); break;
                case Key.Down: _input.Replace(_shell.History.Next()); break;
                default: changed = false; break;
            }
        }
        if (changed)
        {
            e.Handled = true;
            _tabIndex = -1;
            RenderInputLine();
        }
    }

    private void HandleTabCompletion()
    {
        if (_shell is null) return;
        string input = _input.Text;
        int cursor = _input.Cursor;
        (string token, int start, bool isCommand) = TokenAtCursor(input, cursor);

        List<string> candidates = _tabIndex >= 0 && _tabCompletions.Count > 0
            ? _tabCompletions
            : _shell.GetCompletions(token.Trim('"'), isCommand);
        if (candidates.Count == 0) return;

        _tabCompletions = candidates;
        _tabIndex = (_tabIndex + 1) % candidates.Count;
        string completion = _tabCompletions[_tabIndex];
        if (completion.Contains(' ') && !completion.StartsWith('"'))
        {
            bool dir = completion.EndsWith('\\');
            completion = "\"" + completion.TrimEnd('\\') + "\"" + (dir ? "\\" : "");
        }

        string newText = input[..start] + completion + input[cursor..];
        _input.Replace(newText);
        _input.Cursor = start + completion.Length;
        RenderInputLine();
    }

    private static (string Token, int Start, bool IsCommand) TokenAtCursor(string input, int cursor)
    {
        int start = 0;
        bool inQuotes = false;
        bool seenWord = false;
        int tokenStart = 0;
        for (int i = 0; i < cursor; i++)
        {
            char ch = input[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                if (inQuotes && (i == 0 || char.IsWhiteSpace(input[i - 1]))) tokenStart = i;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                seenWord = true;
                start = i + 1;
                tokenStart = i + 1;
            }
        }
        return (input[tokenStart..cursor], tokenStart, !seenWord);
    }

    private void EnterSearchMode()
    {
        _searchMode = true;
        _searchQuery = string.Empty;
        _originalInput = _input.Text;
        _searchIndex = -1;
        RenderInputLine();
    }

    private void ExitSearchMode(bool accept = false)
    {
        _searchMode = false;
        if (!accept) _input.Replace(_originalInput);
        RenderInputLine();
    }

    private void HandleSearchInput(string text)
    {
        _searchQuery += text;
        SearchInHistory();
        RenderInputLine();
    }

    private void SearchNext()
    {
        SearchInHistory();
        RenderInputLine();
    }

    private void SearchInHistory()
    {
        if (_shell is null || string.IsNullOrEmpty(_searchQuery)) return;
        var entries = _shell.History.Entries;
        for (int i = _searchIndex - 1; i >= 0; i--)
        {
            if (entries[i].Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
            {
                _searchIndex = i;
                _input.Replace(entries[i]);
                return;
            }
        }
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
            {
                _searchIndex = i;
                _input.Replace(entries[i]);
                return;
            }
        }
    }

    private async Task PasteAsync(string text)
    {
        if (_shell is null) return;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (_shell.IsInteractive)
        {
            await _shell.WriteInteractiveAsync(text.Replace("\n", "\r"));
            return;
        }
        if (_shell.IsBusy) return;
        string[] lines = text.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            _input.Insert(lines[index]);
            if (index == lines.Length - 1) break;
            string command = _input.Take();
            _buffer.Feed("\r\x1b[2K" + _shell.Prompt + command + "\r\n");
            QueueRender();
            await _shell.SubmitAsync(command, _columns, _rows);
            if (_shell.IsInteractive || _shell.IsBusy) break;
        }
        RenderInputLine();
    }

    private void ClearLocally(bool redrawInput)
    {
        _buffer.Clear();
        if (redrawInput && _shell is not null)
        {
            int tail = _input.Text.Length - _input.Cursor;
            _buffer.Feed(_shell.Prompt + _input.Text + (tail > 0 ? $"\x1b[{tail}D" : string.Empty));
        }
        QueueRender();
    }

    private async void TerminalView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        string? selection = null;
        try { selection = TerminalView.Selection?.Text; } catch { }
        if (!string.IsNullOrEmpty(selection)) Clipboard.SetText(selection);
        else if (Clipboard.ContainsText()) await PasteAsync(Clipboard.GetText());
        TerminalView.Focus();
    }

    private void TerminalView_ContextMenuOpening(object sender, ContextMenuEventArgs e) => e.Handled = true;
    private void TerminalView_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private async void TerminalView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_shell?.IsInteractive == true && _buffer.IsMouseTracking)
        {
            e.Handled = true;
            var pos = e.GetPosition(TerminalView);
            int col = (int)(pos.X / (TerminalView.FontSize * 0.61)) + 1;
            int row = (int)(pos.Y / (TerminalView.FontSize * 1.35)) + 1;
            col = Math.Clamp(col, 1, _columns);
            row = Math.Clamp(row, 1, _rows);
            string seq = _buffer.IsSgrMouseMode
                ? $"\x1b[<0;{col};{row}M"
                : $"\x1b[M{(char)0}{(char)(32 + col)}{(char)(32 + row)}";
            await _shell.WriteInteractiveAsync(seq);
        }
    }

    private async void TerminalView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_shell?.IsInteractive == true && _buffer.IsMouseTracking)
        {
            e.Handled = true;
            var pos = e.GetPosition(TerminalView);
            int col = (int)(pos.X / (TerminalView.FontSize * 0.61)) + 1;
            int row = (int)(pos.Y / (TerminalView.FontSize * 1.35)) + 1;
            col = Math.Clamp(col, 1, _columns);
            row = Math.Clamp(row, 1, _rows);
            string seq = _buffer.IsSgrMouseMode
                ? $"\x1b[<0;{col};{row}m"
                : $"\x1b[M{(char)3}{(char)(32 + col)}{(char)(32 + row)}";
            await _shell.WriteInteractiveAsync(seq);
        }
    }

    private async void TerminalView_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_shell?.IsInteractive == true && _buffer.IsMouseTracking && e.LeftButton == MouseButtonState.Pressed)
        {
            var pos = e.GetPosition(TerminalView);
            int col = (int)(pos.X / (TerminalView.FontSize * 0.61)) + 1;
            int row = (int)(pos.Y / (TerminalView.FontSize * 1.35)) + 1;
            col = Math.Clamp(col, 1, _columns);
            row = Math.Clamp(row, 1, _rows);
            string seq = _buffer.IsSgrMouseMode
                ? $"\x1b[<32;{col};{row}M"
                : $"\x1b[M{(char)32}{(char)(32 + col)}{(char)(32 + row)}";
            await _shell.WriteInteractiveAsync(seq);
        }
    }

    private async void TerminalView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_shell?.IsInteractive == true && _buffer.IsMouseTracking)
        {
            e.Handled = true;
            var pos = e.GetPosition(TerminalView);
            int col = (int)(pos.X / (TerminalView.FontSize * 0.61)) + 1;
            int row = (int)(pos.Y / (TerminalView.FontSize * 1.35)) + 1;
            col = Math.Clamp(col, 1, _columns);
            row = Math.Clamp(row, 1, _rows);
            int button = e.Delta > 0 ? 64 : 65;
            string seq = _buffer.IsSgrMouseMode
                ? $"\x1b[<{button};{col};{row}M"
                : $"\x1b[M{(char)button}{(char)(32 + col)}{(char)(32 + row)}";
            await _shell.WriteInteractiveAsync(seq);
        }
    }

    private void TerminalView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        CalculateSize();
        _buffer.Resize(_columns, _rows);
        _shell?.Resize(_columns, _rows);
        QueueRender();
    }

    private void CalculateSize()
    {
        double fontSize = Math.Max(10, TerminalView.FontSize);
        _columns = (short)Math.Clamp((int)(TerminalView.ActualWidth / (fontSize * 0.61)), 20, 240);
        _rows = (short)Math.Clamp((int)(TerminalView.ActualHeight / (fontSize * 1.35)), 8, 100);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) ToggleMaximize(); else DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        WindowSurface.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(28);
        WindowSurface.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
    }
    private void CopySelection()
    {
        if (!string.IsNullOrEmpty(TerminalView.Selection.Text)) Clipboard.SetText(TerminalView.Selection.Text);
    }

    private static SolidColorBrush TerminalBrush(TerminalColor color) =>
        new(Color.FromRgb(color.R, color.G, color.B));
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
}
