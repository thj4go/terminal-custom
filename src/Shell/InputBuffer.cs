using System.Text;

namespace TerminalCustom.Shell;

internal sealed class InputBuffer
{
    private readonly StringBuilder _text = new();
    public int Cursor { get; private set; }
    public string Text => _text.ToString();

    public void Insert(string value)
    {
        _text.Insert(Cursor, value);
        Cursor += value.Length;
    }

    public void Backspace()
    {
        if (Cursor == 0) return;
        _text.Remove(--Cursor, 1);
    }

    public void Delete()
    {
        if (Cursor < _text.Length) _text.Remove(Cursor, 1);
    }

    public void MoveLeft() => Cursor = Math.Max(0, Cursor - 1);
    public void MoveRight() => Cursor = Math.Min(_text.Length, Cursor + 1);
    public void MoveHome() => Cursor = 0;
    public void MoveEnd() => Cursor = _text.Length;

    public void Replace(string value)
    {
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
    }
}
