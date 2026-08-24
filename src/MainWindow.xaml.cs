using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TerminalCustom;

public partial class MainWindow : Window
{
    private ConPtySession? _session;
    private AiBridgeServer? _aiBridge;
    private TerminalBuffer _buffer = new();
    private bool _renderQueued;
    private readonly StringBuilder _currentInput = new();
    private int _inputCursor;
    private bool _inputTrackingReliable = true;
    private int _resizeVersion;
    private bool _repositionAfterResize;
    private short _columns = 100;
    private short _rows = 30;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => StartSession();
        Closed += (_, _) => StopSession();
    }

    private void StartSession()
    {
        StopSession();
        _buffer = new TerminalBuffer();
        ResetInputTracking();
        SetTerminalPlainText("");
        CalculateSize();
        _buffer.Resize(_columns, _rows);
        try
        {
            _aiBridge = new AiBridgeServer(this);
            _session = new ConPtySession();
            _session.OutputReceived += OnOutput;
            _session.Exited += OnExited;
            _session.Start(BuildPowerShellCommand(_aiBridge.PipeName),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _columns, _rows);
            TerminalView.Focus();
        }
        catch (Exception ex)
        {
            TerminalView.Foreground = Brush("#FDA4AF");
            SetTerminalPlainText($"Não foi possível iniciar o terminal.\r\n\r\n{ex.Message}");
        }
    }

    private void OnOutput(string text)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _buffer.Feed(text);
            if (_renderQueued) return;
            _renderQueued = true;
            Dispatcher.InvokeAsync(Render, System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    private void Render()
    {
        _renderQueued = false;
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            PageWidth = 10000,
            FontFamily = TerminalView.FontFamily,
            FontSize = TerminalView.FontSize
        };
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = TerminalView.FontSize * 1.35,
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

    private void OnExited() => Dispatcher.InvokeAsync(() =>
    {
        _buffer.Feed("\r\n[Sessão encerrada]");
        Render();
    });

    private void StopSession()
    {
        if (_session is not null)
        {
            _session.OutputReceived -= OnOutput;
            _session.Exited -= OnExited;
            _session.Dispose();
            _session = null;
        }
        _aiBridge?.Dispose();
        _aiBridge = null;
    }

    private static string BuildPowerShellCommand(string pipeName)
    {
        string startup = $$"""
            try { Set-PSReadLineOption -PredictionSource None -ErrorAction Stop } catch {}
            $global:TerminalAiPipe = '{{pipeName}}'

            function global:Invoke-TerminalAiBridge {
                param([hashtable]$Request)
                $pipe = $null
                $reader = $null
                $writer = $null
                try {
                    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $global:TerminalAiPipe, [System.IO.Pipes.PipeDirection]::InOut)
                    $pipe.Connect(5000)
                    $utf8 = [System.Text.UTF8Encoding]::new($false)
                    $reader = [System.IO.StreamReader]::new($pipe, $utf8, $false, 4096, $true)
                    $writer = [System.IO.StreamWriter]::new($pipe, $utf8, 4096, $true)
                    $writer.AutoFlush = $true
                    $writer.WriteLine(($Request | ConvertTo-Json -Compress))
                    $line = $reader.ReadLine()
                    if ([string]::IsNullOrWhiteSpace($line)) { throw 'A ponte da IA não respondeu.' }
                    return ($line | ConvertFrom-Json)
                }
                catch {
                    return [pscustomobject]@{ ok = $false; message = "Falha na IA: $($_.Exception.Message)" }
                }
                finally {
                    if ($writer) { $writer.Dispose() }
                    if ($reader) { $reader.Dispose() }
                    if ($pipe) { $pipe.Dispose() }
                }
            }

            function global:ai-key {
                param([switch]$remover)
                $type = if ($remover) { 'clear-key' } else { 'set-key' }
                $result = Invoke-TerminalAiBridge @{ type = $type }
                $color = if ($result.ok) { 'Cyan' } else { 'Yellow' }
                Write-Host $result.message -ForegroundColor $color
            }

            function global:ai-status {
                $result = Invoke-TerminalAiBridge @{ type = 'status' }
                $color = if ($result.ok) { 'Cyan' } else { 'Yellow' }
                Write-Host $result.message -ForegroundColor $color
            }

            function global:ai-prompt {
                param([switch]$remover)
                $type = if ($remover) { 'clear-prompt' } else { 'set-prompt' }
                $result = Invoke-TerminalAiBridge @{ type = $type }
                $color = if ($result.ok) { 'Cyan' } else { 'Yellow' }
                Write-Host $result.message -ForegroundColor $color
            }

            $ExecutionContext.InvokeCommand.CommandNotFoundAction = {
                param($commandName, $eventArgs)
                $originalName = if ($commandName.StartsWith('get-', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $commandName.Substring(4)
                } else { $commandName }
                $capturedName = $originalName
                $eventArgs.CommandScriptBlock = {
                    $words = @($capturedName) + @($args | ForEach-Object { [string]$_ })
                    $question = ($words -join ' ').Trim()
                    $result = Invoke-TerminalAiBridge @{ type = 'chat'; prompt = $question }
                    if ($result.ok) {
                        $white = [char]0xE000
                        $reset = [char]0xE001
                        Write-Host "$white$($result.message)$reset"
                    } else {
                        Write-Host $result.message -ForegroundColor Yellow
                    }
                }.GetNewClosure()
            }
            """;

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(startup));
        return $"powershell.exe -NoLogo -NoProfile -NoExit -EncodedCommand {encoded}";
    }

    private async void TerminalView_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;
        if (_inputTrackingReliable && e.Text.All(character => !char.IsControl(character)))
            InsertTrackedText(e.Text);
        if (_session is not null) await _session.WriteAsync(e.Text);
    }

    private async void TerminalView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        string? sequence = null;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (e.Key == Key.Enter)
        {
            string command = _currentInput.ToString().Trim();
            if (_inputTrackingReliable && (command.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                                           command.Equals("cls", StringComparison.OrdinalIgnoreCase) ||
                                           command.Equals("clear-host", StringComparison.OrdinalIgnoreCase)))
            {
                e.Handled = true;
                ResetInputTracking();
                await ClearTerminalAsync();
                return;
            }
            ResetInputTracking();
        }

        if (ctrl && shift && e.Key == Key.V)
        {
            if (Clipboard.ContainsText()) sequence = Clipboard.GetText().Replace("\r\n", "\r").Replace("\n", "\r");
        }
        else if (ctrl && shift && e.Key == Key.C)
        {
            CopySelection();
            e.Handled = true;
            return;
        }
        else if (ctrl && e.Key == Key.L)
        {
            e.Handled = true;
            ResetInputTracking();
            await ClearTerminalAsync();
            return;
        }
        else if (ctrl && e.Key is >= Key.A and <= Key.Z)
            sequence = ((char)(e.Key - Key.A + 1)).ToString();
        else sequence = e.Key switch
        {
            Key.Enter => "\r", Key.Back => "\x7f", Key.Tab => "\t", Key.Escape => "\x1b",
            Key.Up => "\x1b[A", Key.Down => "\x1b[B", Key.Right => "\x1b[C", Key.Left => "\x1b[D",
            Key.Home => "\x1b[H", Key.End => "\x1b[F", Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~", Key.PageDown => "\x1b[6~", _ => null
        };
        if (sequence is null && !ctrl && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            sequence = TranslateKey(e.Key);
        if (sequence is not null)
        {
            e.Handled = true;
            TrackInput(e.Key, sequence, ctrl);
            if (_session is not null) await _session.WriteAsync(sequence);
        }
    }

    private void TrackInput(Key key, string sequence, bool ctrl)
    {
        if (key == Key.Enter) return;
        if (key == Key.Back)
        {
            if (_inputTrackingReliable && _inputCursor > 0)
            {
                _currentInput.Remove(_inputCursor - 1, 1);
                _inputCursor--;
            }
            return;
        }
        if (key == Key.Delete)
        {
            if (_inputTrackingReliable && _inputCursor < _currentInput.Length)
                _currentInput.Remove(_inputCursor, 1);
            return;
        }
        if (ctrl && key == Key.C)
        {
            ResetInputTracking();
            return;
        }
        if (sequence.Any(character => character is '\r' or '\n'))
        {
            _currentInput.Clear();
            _inputCursor = 0;
            _inputTrackingReliable = false;
            return;
        }
        if (sequence.All(character => !char.IsControl(character)))
        {
            if (_inputTrackingReliable) InsertTrackedText(sequence);
            return;
        }
        if (_inputTrackingReliable && !ctrl)
        {
            if (key == Key.Left) { _inputCursor = Math.Max(0, _inputCursor - 1); return; }
            if (key == Key.Right) { _inputCursor = Math.Min(_currentInput.Length, _inputCursor + 1); return; }
            if (key == Key.Home) { _inputCursor = 0; return; }
            if (key == Key.End) { _inputCursor = _currentInput.Length; return; }
        }
        if (key is Key.Up or Key.Down or Key.Tab || ctrl)
            _inputTrackingReliable = false;
    }

    private void InsertTrackedText(string text)
    {
        _currentInput.Insert(_inputCursor, text);
        _inputCursor += text.Length;
    }

    private void ResetInputTracking()
    {
        _currentInput.Clear();
        _inputCursor = 0;
        _inputTrackingReliable = true;
    }

    private async Task ClearTerminalAsync()
    {
        if (_session is null)
        {
            _buffer.Clear();
            Render();
            return;
        }

        // Cancela a linha ainda mantida pelo PSReadLine e limpa também a tela
        // real do ConPTY. Assim o cursor interno e a tela desenhada permanecem
        // sincronizados mesmo após várias limpezas.
        await _session.WriteAsync("\x03");
        await Task.Delay(80);
        await _session.WriteAsync("$e=[char]27; Write-Host -NoNewline \"$e[2J$e[3J$e[H\"\r");
    }

    private async void TerminalView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrEmpty(TerminalView.Selection.Text))
        {
            Clipboard.SetText(TerminalView.Selection.Text);
        }
        else if (_session is not null && Clipboard.ContainsText())
        {
            string text = Clipboard.GetText().Replace("\r\n", "\r").Replace("\n", "\r");
            if (_inputTrackingReliable && !text.Contains('\r')) InsertTrackedText(text);
            else _inputTrackingReliable = false;
            await _session.WriteAsync(text);
        }
        TerminalView.Focus();
    }

    private void TerminalView_ContextMenuOpening(object sender, ContextMenuEventArgs e) => e.Handled = true;

    private void TerminalView_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private static string? TranslateKey(Key key)
    {
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0) return null;
        var keyboardState = new byte[256];
        if (!GetKeyboardState(keyboardState)) return null;
        uint scanCode = MapVirtualKey((uint)virtualKey, 0);
        var text = new StringBuilder(8);
        int length = ToUnicodeEx((uint)virtualKey, scanCode, keyboardState, text,
            text.Capacity, 0, GetKeyboardLayout(0));
        return length > 0 ? text.ToString(0, length) : null;
    }

    private async void TerminalView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_buffer.ContainsOnlyPowerShellPrompt()) _repositionAfterResize = true;
        CalculateSize();
        _buffer.Resize(_columns, _rows);
        _session?.Resize(_columns, _rows);
        int version = ++_resizeVersion;
        await Task.Delay(400);
        if (version == _resizeVersion && _repositionAfterResize && _session is not null)
        {
            _repositionAfterResize = false;
            await ClearTerminalAsync();
        }
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

    private void CopySelection() { if (!string.IsNullOrEmpty(TerminalView.Selection.Text)) Clipboard.SetText(TerminalView.Selection.Text); }

    private static SolidColorBrush TerminalBrush(TerminalColor color) => color switch
    {
        TerminalColor.Black => Brush("#0C0C0C"),
        TerminalColor.Red => Brush("#C50F1F"),
        TerminalColor.Green => Brush("#13A10E"),
        TerminalColor.Yellow => Brush("#FACC15"),
        TerminalColor.Blue => Brush("#3B82F6"),
        TerminalColor.Magenta => Brush("#C586C0"),
        TerminalColor.Cyan => Brush("#22D3EE"),
        TerminalColor.White => Brush("#F8FAFC"),
        TerminalColor.BrightBlack => Brush("#767676"),
        TerminalColor.BrightRed => Brush("#F14C4C"),
        TerminalColor.BrightGreen => Brush("#23D18B"),
        TerminalColor.BrightYellow => Brush("#FDE047"),
        TerminalColor.BrightBlue => Brush("#3B8EEA"),
        TerminalColor.BrightMagenta => Brush("#D670D6"),
        TerminalColor.BrightCyan => Brush("#29B8DB"),
        TerminalColor.BrightWhite => Brush("#FFFFFF"),
        _ => Brush("#22D3EE")
    };

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;

    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] keyboardState);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyboardState,
        StringBuilder buffer, int bufferSize, uint flags, IntPtr keyboardLayout);
}
