using System.IO;

namespace TerminalCustom.Shell;

internal sealed class HistoryManager
{
    private readonly List<string> _entries = [];
    private readonly string? _persistPath;
    private int _navigationIndex;

    public HistoryManager(string? persistPath = null)
    {
        _persistPath = persistPath;
        if (_persistPath is not null) Load();
    }

    public IReadOnlyList<string> Entries => _entries;

    public void Add(string command)
    {
        command = command.Trim();
        if (command.Length == 0 || SensitiveDataDetector.ContainsSensitiveData(command))
        {
            ResetNavigation();
            return;
        }
        if (_entries.Count == 0 || !string.Equals(_entries[^1], command, StringComparison.Ordinal))
            _entries.Add(command);
        if (_entries.Count > 1000) _entries.RemoveAt(0);
        ResetNavigation();
        Persist();
    }

    public string Previous(string current)
    {
        if (_entries.Count == 0) return current;
        _navigationIndex = Math.Max(0, _navigationIndex - 1);
        return _entries[_navigationIndex];
    }

    public string Next()
    {
        if (_entries.Count == 0) return string.Empty;
        _navigationIndex = Math.Min(_entries.Count, _navigationIndex + 1);
        return _navigationIndex == _entries.Count ? string.Empty : _entries[_navigationIndex];
    }

    public void ResetNavigation() => _navigationIndex = _entries.Count;

    private void Load()
    {
        try
        {
            if (_persistPath is null || !File.Exists(_persistPath)) return;
            foreach (string line in File.ReadAllLines(_persistPath))
            {
                if (line.Length == 0 || SensitiveDataDetector.ContainsSensitiveData(line)) continue;
                _entries.Add(line);
            }
            if (_entries.Count > 1000) _entries.RemoveRange(0, _entries.Count - 1000);
        }
        catch { }
        ResetNavigation();
    }

    private void Persist()
    {
        if (_persistPath is null) return;
        try
        {
            string? dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(_persistPath, _entries);
        }
        catch { }
    }
}
