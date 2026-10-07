namespace Builder.Logic.Runtime;

public sealed record Diagnostic(long Cycle, string ControlModule, string Message);

public sealed class DiagnosticLog
{
    private readonly List<Diagnostic> _entries = [];
    private readonly HashSet<string> _seen = [];

    public IReadOnlyList<Diagnostic> Entries => _entries;

    public event Action<Diagnostic>? Added;

    public void Report(long cycle, string controlModule, string message)
    {
        if (!_seen.Add($"{controlModule}|{message}"))
            return;
        var entry = new Diagnostic(cycle, controlModule, message);
        _entries.Add(entry);
        if (_entries.Count > 500)
            _entries.RemoveAt(0);
        Added?.Invoke(entry);
    }

    public void Clear()
    {
        _entries.Clear();
        _seen.Clear();
    }
}
