using System.Text;

namespace TerminalCustom;

internal enum TerminalColor
{
    Default, Black, Red, Green, Yellow, Blue, Magenta, Cyan, White,
    BrightBlack, BrightRed, BrightGreen, BrightYellow, BrightBlue, BrightMagenta, BrightCyan, BrightWhite
}

internal readonly record struct TerminalSegment(string Text, TerminalColor Color);

internal sealed class TerminalBuffer
{
    private readonly List<StringBuilder> _scrollback = [];
    private readonly List<StringBuilder> _screen = [];
    private readonly List<List<TerminalColor>> _scrollbackColors = [];
    private readonly List<List<TerminalColor>> _screenColors = [];
    private readonly StringBuilder _escape = new();
    private int _rows = 30;
    private int _columns = 100;
    private int _row;
    private int _column;
    private int _savedRow;
    private int _savedColumn;
    private int _scrollTop;
    private int _scrollBottom = 29;
    private bool _oscEscape;
    private bool _cursorVisible = true;
    private TerminalColor _currentColor = TerminalColor.Default;
    private ParseState _state;
    private const int MaxScrollback = 5000;

    private enum ParseState { Normal, Escape, Csi, Osc }

    public TerminalBuffer() => ResetScreen();

    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 20, 300);
        rows = Math.Clamp(rows, 8, 150);
        _columns = columns;

        if (rows > _rows)
        {
            while (_screen.Count < rows)
            {
                _screen.Add(new StringBuilder());
                _screenColors.Add([]);
            }
        }
        else if (rows < _rows)
        {
            int totalRemove = Math.Min(_rows - rows, Math.Max(0, _screen.Count - 1));
            int lastUsedRow = _row;
            for (int i = _screen.Count - 1; i >= 0; i--)
            {
                if (_screen[i].Length == 0) continue;
                lastUsedRow = Math.Max(lastUsedRow, i);
                break;
            }
            int removeFromTop = Math.Clamp(lastUsedRow - (rows - 1), 0, totalRemove);
            for (int i = 0; i < removeFromTop; i++)
            {
                if (_screen[0].Length > 0 || _scrollback.Any(line => line.Length > 0))
                {
                    _scrollback.Add(_screen[0]);
                    _scrollbackColors.Add(_screenColors[0]);
                }
                _screen.RemoveAt(0);
                _screenColors.RemoveAt(0);
            }
            _row = Math.Max(0, _row - removeFromTop);
            TrimScrollback();
        }

        _rows = rows;
        while (_screen.Count < _rows)
        {
            _screen.Add(new StringBuilder());
            _screenColors.Add([]);
        }
        while (_screen.Count > _rows)
        {
            _screen.RemoveAt(_screen.Count - 1);
            _screenColors.RemoveAt(_screenColors.Count - 1);
        }
        _row = Math.Clamp(_row, 0, _rows - 1);
        _column = Math.Clamp(_column, 0, _columns - 1);
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
    }

    public void Feed(string value)
    {
        foreach (char ch in value)
        {
            switch (_state)
            {
                case ParseState.Normal: HandleNormal(ch); break;
                case ParseState.Escape: HandleEscape(ch); break;
                case ParseState.Csi: HandleCsi(ch); break;
                case ParseState.Osc: HandleOsc(ch); break;
            }
        }
    }

    public override string ToString()
    {
        var lines = new List<string>(_scrollback.Count + _rows);
        bool hasScrollbackContent = _scrollback.Any(line => line.Length > 0);
        if (hasScrollbackContent)
            lines.AddRange(_scrollback.Select(line => line.ToString().TrimEnd()));

        int lastScreenLine = _row;
        for (int i = _screen.Count - 1; i >= 0; i--)
        {
            if (_screen[i].Length > 0) { lastScreenLine = Math.Max(lastScreenLine, i); break; }
        }
        int firstScreenLine = 0;
        if (!hasScrollbackContent)
            while (firstScreenLine < lastScreenLine && _screen[firstScreenLine].Length == 0)
                firstScreenLine++;
        for (int i = firstScreenLine; i <= lastScreenLine && i < _screen.Count; i++)
        {
            string text = _screen[i].ToString().TrimEnd();
            if (_cursorVisible && i == _row)
            {
                int cursor = Math.Min(_column, Math.Max(0, _columns - 1));
                if (text.Length < cursor) text = text.PadRight(cursor);
                if (cursor < text.Length)
                    text = text[..cursor] + "▌" + text[(cursor + 1)..];
                else text += "▌";
            }
            lines.Add(text);
        }

        return string.Join(Environment.NewLine, lines);
    }

    public IReadOnlyList<TerminalSegment> GetStyledSegments()
    {
        var result = new List<TerminalSegment>();
        var text = new StringBuilder();
        TerminalColor? activeColor = null;

        void Flush()
        {
            if (text.Length == 0 || activeColor is null) return;
            result.Add(new TerminalSegment(text.ToString(), activeColor.Value));
            text.Clear();
        }

        void Append(char character, TerminalColor color)
        {
            if (activeColor != color)
            {
                Flush();
                activeColor = color;
            }
            text.Append(character);
        }

        var lines = new List<(StringBuilder Text, List<TerminalColor> Colors, bool Cursor)>();
        bool hasScrollbackContent = _scrollback.Any(line => line.Length > 0);
        if (hasScrollbackContent)
            for (int i = 0; i < _scrollback.Count; i++)
                lines.Add((_scrollback[i], _scrollbackColors[i], false));

        int lastScreenLine = _row;
        for (int i = _screen.Count - 1; i >= 0; i--)
        {
            if (_screen[i].Length > 0) { lastScreenLine = Math.Max(lastScreenLine, i); break; }
        }
        int firstScreenLine = 0;
        if (!hasScrollbackContent)
            while (firstScreenLine < lastScreenLine && _screen[firstScreenLine].Length == 0)
                firstScreenLine++;
        for (int i = firstScreenLine; i <= lastScreenLine && i < _screen.Count; i++)
            lines.Add((_screen[i], _screenColors[i], _cursorVisible && i == _row));

        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            int length = line.Text.Length;
            while (length > 0 && char.IsWhiteSpace(line.Text[length - 1])) length--;
            int cursor = line.Cursor ? Math.Min(_column, Math.Max(0, _columns - 1)) : -1;
            int visibleLength = Math.Max(length, cursor >= 0 ? cursor + 1 : 0);
            for (int column = 0; column < visibleLength; column++)
            {
                char character = column < length ? line.Text[column] : ' ';
                TerminalColor color = column < line.Colors.Count ? line.Colors[column] : TerminalColor.Default;
                if (column == cursor)
                {
                    character = '▌';
                    color = TerminalColor.Default;
                }
                Append(character, color);
            }
            if (lineIndex < lines.Count - 1) Append('\n', TerminalColor.Default);
        }
        Flush();
        return result;
    }

    public void Clear()
    {
        _scrollback.Clear();
        _scrollbackColors.Clear();
        _currentColor = TerminalColor.Default;
        ResetScreen();
    }

    private void ResetScreen()
    {
        _screen.Clear();
        _screenColors.Clear();
        for (int i = 0; i < _rows; i++)
        {
            _screen.Add(new StringBuilder());
            _screenColors.Add([]);
        }
        _row = _column = _savedRow = _savedColumn = 0;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
    }

    private void HandleNormal(char ch)
    {
        switch (ch)
        {
            case '\x1b': _state = ParseState.Escape; break;
            case '\r': _column = 0; break;
            case '\n': Index(); break;
            case '\b': _column = Math.Max(0, _column - 1); break;
            case '\t': _column = Math.Min(_columns - 1, ((_column / 8) + 1) * 8); break;
            case '\uE000': _currentColor = TerminalColor.White; break;
            case '\uE001': _currentColor = TerminalColor.Default; break;
            case '\0': case '\a': break;
            default: if (!char.IsControl(ch)) Put(ch); break;
        }
    }

    private void HandleEscape(char ch)
    {
        _escape.Clear();
        switch (ch)
        {
            case '[': _state = ParseState.Csi; break;
            case ']': _oscEscape = false; _state = ParseState.Osc; break;
            case '7': _savedRow = _row; _savedColumn = _column; _state = ParseState.Normal; break;
            case '8': _row = _savedRow; _column = _savedColumn; ClampCursor(); _state = ParseState.Normal; break;
            case 'D': Index(); _state = ParseState.Normal; break;
            case 'E': _column = 0; Index(); _state = ParseState.Normal; break;
            case 'M': ReverseIndex(); _state = ParseState.Normal; break;
            case 'c': Clear(); _state = ParseState.Normal; break;
            default: _state = ParseState.Normal; break;
        }
    }

    private void HandleOsc(char ch)
    {
        if (ch == '\a') { _state = ParseState.Normal; _oscEscape = false; return; }
        if (_oscEscape && ch == '\\') { _state = ParseState.Normal; _oscEscape = false; return; }
        _oscEscape = ch == '\x1b';
    }

    private void HandleCsi(char ch)
    {
        if (ch is >= '@' and <= '~')
        {
            ApplyCsi(ch, _escape.ToString());
            _escape.Clear();
            _state = ParseState.Normal;
        }
        else if (_escape.Length < 128) _escape.Append(ch);
        else { _escape.Clear(); _state = ParseState.Normal; }
    }

    private void ApplyCsi(char command, string raw)
    {
        bool privateMode = raw.StartsWith('?');
        string clean = raw.TrimStart('?', '>', '!', '=');
        int[] values = clean.Split(';', StringSplitOptions.None)
            .Select(x => int.TryParse(x, out int n) ? n : 0).ToArray();
        int P(int index, int fallback = 1) => index < values.Length && values[index] != 0 ? values[index] : fallback;
        int Mode(int fallback = 0) => values.Length > 0 ? values[0] : fallback;

        switch (command)
        {
            case 'A': _row = Math.Max(_scrollTop, _row - P(0)); break;
            case 'B': _row = Math.Min(_scrollBottom, _row + P(0)); break;
            case 'C': _column = Math.Min(_columns - 1, _column + P(0)); break;
            case 'D': _column = Math.Max(0, _column - P(0)); break;
            case 'E': _row = Math.Min(_scrollBottom, _row + P(0)); _column = 0; break;
            case 'F': _row = Math.Max(_scrollTop, _row - P(0)); _column = 0; break;
            case 'G': case '`': _column = Math.Clamp(P(0) - 1, 0, _columns - 1); break;
            case 'H': case 'f':
                _row = Math.Clamp(P(0) - 1, 0, _rows - 1);
                _column = Math.Clamp(P(1) - 1, 0, _columns - 1);
                break;
            case 'd': _row = Math.Clamp(P(0) - 1, 0, _rows - 1); break;
            case 'J': EraseDisplay(Mode()); break;
            case 'K': EraseLine(Mode()); break;
            case 'P': DeleteChars(P(0)); break;
            case '@': InsertSpaces(P(0)); break;
            case 'L': InsertLines(P(0)); break;
            case 'M': DeleteLines(P(0)); break;
            case 'S': ScrollUp(P(0), false); break;
            case 'T': ScrollDown(P(0)); break;
            case 'X': EraseChars(P(0)); break;
            case 'm': SetGraphicsRendition(values); break;
            case 'r':
                if (!privateMode)
                {
                    _scrollTop = Math.Clamp(P(0) - 1, 0, _rows - 1);
                    _scrollBottom = Math.Clamp(P(1, _rows) - 1, _scrollTop, _rows - 1);
                    _row = _scrollTop; _column = 0;
                }
                break;
            case 's': _savedRow = _row; _savedColumn = _column; break;
            case 'u': _row = _savedRow; _column = _savedColumn; ClampCursor(); break;
            case 'h': if (privateMode && values.Contains(25)) _cursorVisible = true; break;
            case 'l': if (privateMode && values.Contains(25)) _cursorVisible = false; break;
        }
    }

    private void Put(char ch)
    {
        if (_column >= _columns) { _column = 0; Index(); }
        StringBuilder line = _screen[_row];
        List<TerminalColor> colors = _screenColors[_row];
        while (line.Length < _column)
        {
            line.Append(' ');
            colors.Add(_currentColor);
        }
        if (_column < line.Length)
        {
            line[_column] = ch;
            while (colors.Count <= _column) colors.Add(TerminalColor.Default);
            colors[_column] = _currentColor;
        }
        else
        {
            line.Append(ch);
            colors.Add(_currentColor);
        }
        _column++;
    }

    private void Index()
    {
        if (_row == _scrollBottom) ScrollUp(1, _scrollTop == 0);
        else _row = Math.Min(_rows - 1, _row + 1);
    }

    private void ReverseIndex()
    {
        if (_row == _scrollTop) ScrollDown(1);
        else _row = Math.Max(0, _row - 1);
    }

    private void ScrollUp(int count, bool addToScrollback)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);
        for (int i = 0; i < count; i++)
        {
            StringBuilder removed = _screen[_scrollTop];
            List<TerminalColor> removedColors = _screenColors[_scrollTop];
            _screen.RemoveAt(_scrollTop);
            _screenColors.RemoveAt(_scrollTop);
            _screen.Insert(_scrollBottom, new StringBuilder());
            _screenColors.Insert(_scrollBottom, []);
            if (addToScrollback && (removed.Length > 0 || _scrollback.Any(line => line.Length > 0)))
            {
                _scrollback.Add(removed);
                _scrollbackColors.Add(removedColors);
            }
        }
        TrimScrollback();
    }

    private void ScrollDown(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);
        for (int i = 0; i < count; i++)
        {
            _screen.RemoveAt(_scrollBottom);
            _screenColors.RemoveAt(_scrollBottom);
            _screen.Insert(_scrollTop, new StringBuilder());
            _screenColors.Insert(_scrollTop, []);
        }
    }

    private void EraseDisplay(int mode)
    {
        if (mode == 3) { _scrollback.Clear(); _scrollbackColors.Clear(); return; }
        if (mode == 2)
        {
            _scrollback.Clear();
            _scrollbackColors.Clear();
            for (int i = 0; i < _rows; i++)
            {
                _screen[i].Clear();
                _screenColors[i].Clear();
            }
            return;
        }
        if (mode == 0)
        {
            EraseLine(0);
            for (int i = _row + 1; i < _rows; i++)
            {
                _screen[i].Clear();
                _screenColors[i].Clear();
            }
        }
        else if (mode == 1)
        {
            for (int i = 0; i < _row; i++)
            {
                _screen[i].Clear();
                _screenColors[i].Clear();
            }
            EraseLine(1);
        }
    }

    private void EraseLine(int mode)
    {
        StringBuilder line = _screen[_row];
        List<TerminalColor> colors = _screenColors[_row];
        if (mode == 2) { line.Clear(); colors.Clear(); }
        else if (mode == 0 && _column < line.Length)
        {
            line.Length = _column;
            if (colors.Count > _column) colors.RemoveRange(_column, colors.Count - _column);
        }
        else if (mode == 1)
        {
            int length = Math.Min(_column + 1, line.Length);
            for (int i = 0; i < length; i++)
            {
                line[i] = ' ';
                if (i < colors.Count) colors[i] = _currentColor;
            }
        }
    }

    private void DeleteChars(int count)
    {
        StringBuilder line = _screen[_row];
        List<TerminalColor> colors = _screenColors[_row];
        if (_column < line.Length)
        {
            int remove = Math.Min(count, line.Length - _column);
            line.Remove(_column, remove);
            if (_column < colors.Count) colors.RemoveRange(_column, Math.Min(remove, colors.Count - _column));
        }
    }

    private void InsertSpaces(int count)
    {
        StringBuilder line = _screen[_row];
        List<TerminalColor> colors = _screenColors[_row];
        while (line.Length < _column) { line.Append(' '); colors.Add(_currentColor); }
        int insert = Math.Min(count, _columns - _column);
        line.Insert(_column, new string(' ', insert));
        colors.InsertRange(_column, Enumerable.Repeat(_currentColor, insert));
        if (line.Length > _columns) line.Length = _columns;
        if (colors.Count > _columns) colors.RemoveRange(_columns, colors.Count - _columns);
    }

    private void EraseChars(int count)
    {
        StringBuilder line = _screen[_row];
        List<TerminalColor> colors = _screenColors[_row];
        int end = Math.Min(line.Length, _column + count);
        for (int i = _column; i < end; i++)
        {
            line[i] = ' ';
            if (i < colors.Count) colors[i] = _currentColor;
        }
    }

    private void InsertLines(int count)
    {
        if (_row < _scrollTop || _row > _scrollBottom) return;
        count = Math.Min(count, _scrollBottom - _row + 1);
        for (int i = 0; i < count; i++)
        {
            _screen.Insert(_row, new StringBuilder());
            _screenColors.Insert(_row, []);
            _screen.RemoveAt(_scrollBottom + 1);
            _screenColors.RemoveAt(_scrollBottom + 1);
        }
    }

    private void DeleteLines(int count)
    {
        if (_row < _scrollTop || _row > _scrollBottom) return;
        count = Math.Min(count, _scrollBottom - _row + 1);
        for (int i = 0; i < count; i++)
        {
            _screen.RemoveAt(_row);
            _screenColors.RemoveAt(_row);
            _screen.Insert(_scrollBottom, new StringBuilder());
            _screenColors.Insert(_scrollBottom, []);
        }
    }

    private void SetGraphicsRendition(int[] values)
    {
        if (values.Length == 0) { _currentColor = TerminalColor.Default; return; }
        foreach (int value in values)
        {
            _currentColor = value switch
            {
                0 or 39 => TerminalColor.Default,
                30 => TerminalColor.Black, 31 => TerminalColor.Red, 32 => TerminalColor.Green,
                33 => TerminalColor.Yellow, 34 => TerminalColor.Blue, 35 => TerminalColor.Magenta,
                36 => TerminalColor.Cyan, 37 => TerminalColor.White,
                90 => TerminalColor.BrightBlack, 91 => TerminalColor.BrightRed, 92 => TerminalColor.BrightGreen,
                93 => TerminalColor.BrightYellow, 94 => TerminalColor.BrightBlue, 95 => TerminalColor.BrightMagenta,
                96 => TerminalColor.BrightCyan, 97 => TerminalColor.BrightWhite,
                _ => _currentColor
            };
        }
    }

    private void ClampCursor()
    {
        _row = Math.Clamp(_row, 0, _rows - 1);
        _column = Math.Clamp(_column, 0, _columns - 1);
    }

    private void TrimScrollback()
    {
        if (_scrollback.Count > MaxScrollback)
        {
            int remove = _scrollback.Count - MaxScrollback;
            _scrollback.RemoveRange(0, _scrollback.Count - MaxScrollback);
            _scrollbackColors.RemoveRange(0, remove);
        }
    }
}
