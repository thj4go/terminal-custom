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
        _buffer.Feed("\r\x1b[2K" + _shell.Prompt + _input.Text + (tail > 0 ? $"\x1b[{tail}D" : string.Empty));
        QueueRender();
    }

    private async void TerminalView_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        if (_shell?.IsInteractive == true) await _shell.WriteInteractiveAsync(e.Text);
        else if (_shell is { IsBusy: false })
        {
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
        if (ctrl && alt) return; // AltGr: o caractere real chega em PreviewTextInput.

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

        if (e.Key == Key.Space)
        {
            e.Handled = true;
            if (_shell.IsInteractive) await _shell.WriteInteractiveAsync(" ");
            else if (!_shell.IsBusy)
            {
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

        bool changed = true;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
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
        if (changed)
        {
            e.Handled = true;
            RenderInputLine();
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
        if (!string.IsNullOrEmpty(TerminalView.Selection.Text)) Clipboard.SetText(TerminalView.Selection.Text);
        else if (Clipboard.ContainsText()) await PasteAsync(Clipboard.GetText());
        TerminalView.Focus();
    }

    private void TerminalView_ContextMenuOpening(object sender, ContextMenuEventArgs e) => e.Handled = true;
    private void TerminalView_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

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

    private static SolidColorBrush TerminalBrush(TerminalColor color) => color switch
    {
        TerminalColor.Black => Brush("#0C0C0C"), TerminalColor.Red => Brush("#C50F1F"),
        TerminalColor.Green => Brush("#13A10E"), TerminalColor.Yellow => Brush("#FACC15"),
        TerminalColor.Blue => Brush("#3B82F6"), TerminalColor.Magenta => Brush("#C586C0"),
        TerminalColor.Cyan => Brush("#22D3EE"), TerminalColor.White => Brush("#F8FAFC"),
        TerminalColor.BrightBlack => Brush("#767676"), TerminalColor.BrightRed => Brush("#F14C4C"),
        TerminalColor.BrightGreen => Brush("#23D18B"), TerminalColor.BrightYellow => Brush("#FDE047"),
        TerminalColor.BrightBlue => Brush("#3B8EEA"), TerminalColor.BrightMagenta => Brush("#D670D6"),
        TerminalColor.BrightCyan => Brush("#29B8DB"), TerminalColor.BrightWhite => Brush("#FFFFFF"),
        _ => Brush("#22D3EE")
    };
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
}
