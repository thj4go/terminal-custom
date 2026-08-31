using System.Text;

namespace TerminalCustom.Shell;

internal sealed class InputBuffer
{
    private readonly StringBuilder _text = new();
    private readonly Stack<(string Text, int Cursor)> _undoStack = new();
    private readonly Stack<(string Text, int Cursor)> _redoStack = new();
    private string _yankBuffer = string.Empty;
    public int Cursor { get; set; }
    public string Text => _text.ToString();

    private void SaveState()
    {
        _undoStack.Push((_text.ToString(), Cursor));
        _redoStack.Clear();
    }

    public void Insert(string value)
    {
        SaveState();
        _text.Insert(Cursor, value);
        Cursor += value.Length;
    }

    public void Backspace()
    {
        if (Cursor == 0) return;
        SaveState();
        _text.Remove(--Cursor, 1);
    }

    public void Delete()
    {
        if (Cursor < _text.Length)
        {
            SaveState();
            _text.Remove(Cursor, 1);
        }
    }

    public void MoveLeft() => Cursor = Math.Max(0, Cursor - 1);
    public void MoveRight() => Cursor = Math.Min(_text.Length, Cursor + 1);
    public void MoveHome() => Cursor = 0;
    public void MoveEnd() => Cursor = _text.Length;

    public void MoveWordLeft()
    {
        if (Cursor == 0) return;
        int i = Cursor - 1;
        while (i > 0 && char.IsWhiteSpace(_text[i])) i--;
        while (i > 0 && !char.IsWhiteSpace(_text[i - 1])) i--;
        Cursor = i;
    }

    public void MoveWordRight()
    {
        if (Cursor >= _text.Length) return;
        int i = Cursor;
        while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;
        while (i < _text.Length && !char.IsWhiteSpace(_text[i])) i++;
        Cursor = i;
    }

    public void KillLineStart()
    {
        if (Cursor == 0) return;
        SaveState();
        _yankBuffer = _text.ToString()[..Cursor];
        _text.Remove(0, Cursor);
        Cursor = 0;
    }

    public void KillLineEnd()
    {
        if (Cursor >= _text.Length) return;
        SaveState();
        _yankBuffer = _text.ToString()[Cursor..];
        _text.Remove(Cursor, _text.Length - Cursor);
    }

    public void DeleteWordBack()
    {
        if (Cursor == 0) return;
        SaveState();
        int i = Cursor - 1;
        while (i > 0 && char.IsWhiteSpace(_text[i])) i--;
        while (i > 0 && !char.IsWhiteSpace(_text[i - 1])) i--;
        _yankBuffer = _text.ToString()[i..Cursor];
        _text.Remove(i, Cursor - i);
        Cursor = i;
    }

    public void Yank()
    {
        if (_yankBuffer.Length == 0) return;
        Insert(_yankBuffer);
    }

    public bool Undo()
    {
        if (_undoStack.Count == 0) return false;
        _redoStack.Push((_text.ToString(), Cursor));
        var state = _undoStack.Pop();
        _text.Clear();
        _text.Append(state.Text);
        Cursor = state.Cursor;
        return true;
    }

    public bool Redo()
    {
        if (_redoStack.Count == 0) return false;
        _undoStack.Push((_text.ToString(), Cursor));
        var state = _redoStack.Pop();
        _text.Clear();
        _text.Append(state.Text);
        Cursor = state.Cursor;
        return true;
    }

    public void Replace(string value)
    {
        SaveState();
        _text.Clear();
        _text.Append(value);
        Cursor = _text.Length;
    }

    public string Take()
    {
        string value = Text;
        Clear();
        return value;
    }

    public void Clear()
    {
        _text.Clear();
        Cursor = 0;
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
