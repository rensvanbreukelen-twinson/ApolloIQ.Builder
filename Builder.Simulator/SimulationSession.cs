using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using ApolloIQ.Core.Expressions;
using Builder.Logic.Model;
using Builder.Logic.Runtime;

namespace Builder.Simulator;

public sealed class SimulationSession : IAsyncDisposable
{
    private static readonly HashSet<TagGroup> WritableGroups = [TagGroup.Cmd, TagGroup.Par, TagGroup.Set, TagGroup.Lok, TagGroup.Fin];

    private readonly Lock _gate = new();
    private readonly CmLibrary _library;
    private readonly double _cycleSeconds;
    private LogicProgram _program = null!;
    private Dictionary<string, SimTag> _byReference = null!;
    private SimTag[] _bySlot = null!;
    private (Value Value, bool Forced, string? Text)[] _published = null!;
    private CancellationTokenSource? _loop;
    private Task _loopTask = Task.CompletedTask;
    private double _speed = 1;
    private readonly HashSet<Guid> _downLinks = [];
    private List<(Guid Link, int Slot)> _linkUses = [];
    private List<Link> _links = [];
    private Topology? _topology;

    public SimulationSession(Guid projectId, Project project, CmLibrary library, double cycleSeconds = LogicProgram.DefaultCycleSeconds)
    {
        ProjectId = projectId;
        _library = library;
        _cycleSeconds = cycleSeconds;
        Load(project, null);
    }

    public Guid ProjectId { get; }

    public SimulationStatus Status { get; private set; } = SimulationStatus.Paused;

    public event Action<SimChangeBatch>? ValuesChanged;

    public event Action<StateChange>? StateChanged;

    public event Action<Diagnostic>? DiagnosticAdded;

    public event Action<SimStatus>? StatusChanged;

    public IReadOnlyList<LogicError> Errors
    {
        get
        {
            lock (_gate)
                return _program.Errors;
        }
    }

    public IReadOnlyList<Diagnostic> Diagnostics
    {
        get
        {
            lock (_gate)
                return _program.Diagnostics.Entries.ToList();
        }
    }

    public IReadOnlyCollection<SimTag> Tags
    {
        get
        {
            lock (_gate)
                return _bySlot;
        }
    }

    public double Speed
    {
        get => _speed;
        set
        {
            if (!(value is > 0 and <= 100))
                throw new SimulationException("The speed must be more than 0 and at most 100.");
            _speed = value;
            PublishStatus();
        }
    }

    public SimStatus Snapshot()
    {
        lock (_gate)
            return new SimStatus(Status, _program.Cycle, _program.TimeSeconds, _program.CycleSeconds, _speed, _program.Errors.Count);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (Status == SimulationStatus.Running)
                return;
            Status = SimulationStatus.Running;
            _loop = new CancellationTokenSource();
            _loopTask = RunLoop(_loop.Token);
        }
        PublishStatus();
    }

    public async Task PauseAsync()
    {
        CancellationTokenSource? loop;
        Task task;
        lock (_gate)
        {
            if (Status == SimulationStatus.Paused)
                return;
            Status = SimulationStatus.Paused;
            loop = _loop;
            task = _loopTask;
            _loop = null;
        }
        if (loop is not null)
        {
            await loop.CancelAsync();
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            loop.Dispose();
        }
        PublishStatus();
    }

    public void Step(int cycles = 1)
    {
        if (cycles is < 1 or > 100_000)
            throw new SimulationException("Step between 1 and 100000 cycles.");
        lock (_gate)
        {
            if (Status == SimulationStatus.Running)
                throw new SimulationException("Pause the simulation before stepping.");
        }
        for (var i = 0; i < cycles; i++)
            Cycle();
        PublishStatus();
    }

    public void Reload(Project project)
    {
        lock (_gate)
            Load(project, _program);
        PublishAll();
        PublishStatus();
    }

    public void Reset(Project project)
    {
        lock (_gate)
            Load(project, null);
        PublishAll();
        PublishStatus();
    }

    public SimTag Find(string reference)
    {
        lock (_gate)
            return _byReference.TryGetValue(reference, out var tag)
                ? tag
                : throw new SimulationException($"Unknown tag '{reference}'.");
    }

    public SimTagValue Read(string reference)
    {
        lock (_gate)
            return ValueOf(Find(reference));
    }

    public IReadOnlyList<SimTagValue> ReadAll()
    {
        lock (_gate)
            return _bySlot.Select(ValueOf).ToList();
    }

    public IReadOnlyList<SimControlModule> ControlModules()
    {
        lock (_gate)
            return _program.Programs
                .Select(p => new SimControlModule(p.Id, p.Path, p.Type.Name, p.State, p.StateName(p.State), p.StateText(p.State), p.StateCycles * _program.CycleSeconds))
                .ToList();
    }

    /// <summary>
    /// Every alarm in the simulation: PLC reactive alarms as the PLC logic raises them, the others evaluated as SCADA will
    /// (the shared alarm trigger logic).
    /// </summary>
    public IReadOnlyList<AlarmStatus> Alarms()
    {
        lock (_gate)
            return _program.AlarmStatuses();
    }

    public Func<Value> Condition(string controlModulePath, string expression)
    {
        LogicProgram program;
        lock (_gate)
            program = _program;
        Func<Value> evaluate;
        try
        {
            evaluate = program.CompileCondition(controlModulePath, expression);
        }
        catch (ExpressionException ex)
        {
            throw new SimulationException(ex.Message);
        }
        return () =>
        {
            lock (_gate)
                return evaluate();
        };
    }

    public IReadOnlyList<CmProgram> Programs
    {
        get
        {
            lock (_gate)
                return _program.Programs;
        }
    }

    public SimTagValue Write(string reference, object? value)
    {
        var tag = Find(reference);
        lock (_gate)
        {
            if (!WritableGroups.Contains(tag.Group) && !(tag.Group == TagGroup.Alm && tag.Path.EndsWith(".enabled", StringComparison.Ordinal)))
                throw new SimulationException($"{tag.Path} is written by the logic. Force it to override its value.");
            var (converted, text) = ValueConversion.FromObject(value, tag);
            _program.Memory.Set(tag.Slot, converted);
            _program.Memory.SetText(tag.Slot, text);
        }
        return PublishChanges(tag);
    }

    public SimTagValue Force(string reference, object? value)
    {
        var tag = Find(reference);
        lock (_gate)
        {
            var (converted, text) = ValueConversion.FromObject(value, tag);
            _program.Memory.Force(tag.Slot, converted);
            if (text is not null)
                _program.Memory.SetText(tag.Slot, text);
        }
        return PublishChanges(tag);
    }

    public SimTagValue Unforce(string reference)
    {
        var tag = Find(reference);
        lock (_gate)
            _program.Memory.Unforce(tag.Slot);
        return PublishChanges(tag);
    }

    public SimTagValue SetBadQuality(string reference, bool bad)
    {
        var tag = Find(reference);
        lock (_gate)
            _program.Memory.SetBadQuality(tag.Slot, bad);
        return PublishChanges(tag);
    }

    public IReadOnlyList<SimLink> Links()
    {
        lock (_gate)
            return _links.Select(l => new SimLink(l.Id, _topology?.Device(l.From)?.Name ?? "", _topology?.Device(l.To)?.Name ?? "",
                l.Protocol, l.Class.ToString(), _downLinks.Contains(l.Id), _linkUses.Count(u => u.Link == l.Id))).ToList();
    }

    public void SetLinkDown(Guid linkId, bool down)
    {
        lock (_gate)
        {
            if (_links.All(l => l.Id != linkId))
                throw new SimulationException($"Link {linkId} does not exist.");
            if (down)
                _downLinks.Add(linkId);
            else
                _downLinks.Remove(linkId);
            _program.Memory.SetLinkBadQuality(_linkUses.Where(u => _downLinks.Contains(u.Link)).Select(u => u.Slot));
        }
        PublishChanges();
    }

    public void UnforceAll()
    {
        lock (_gate)
        {
            foreach (var slot in _program.Memory.Forced.Keys.ToList())
                _program.Memory.Unforce(slot);
            foreach (var slot in _program.Memory.BadQuality.ToList())
                _program.Memory.SetBadQuality(slot, false);
        }
        PublishChanges();
    }

    public async ValueTask DisposeAsync() => await PauseAsync();

    private void Load(Project project, LogicProgram? previous)
    {
        var program = LogicProgram.Build(project, _library, _cycleSeconds);
        if (previous is not null)
            program.CopyStateFrom(previous);
        program.StateChanged += change => StateChanged?.Invoke(change);
        program.Diagnostics.Added += diagnostic => DiagnosticAdded?.Invoke(diagnostic);

        var slots = new SimTag[program.Memory.Count];
        var byReference = new Dictionary<string, SimTag>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in project.Tags)
        {
            if (!program.Memory.Slots.TryGetValue(tag.Id, out var slot))
                continue;
            var sim = new SimTag(tag.Id, project.GetPath(tag.Id), tag.SymbolKey, tag.Group, tag.DataType, tag.EnumType, slot);
            slots[slot] = sim;
            byReference[sim.Path] = sim;
            byReference[sim.SymbolKey] = sim;
            byReference[sim.Id.ToString()] = sim;
        }

        var report = BindingResolver.Resolve(project);
        _linkUses = report.Accesses.Where(a => a.Critical)
            .SelectMany(a => a.Links.Select(l => (l, program.Memory.Slots.TryGetValue(a.TagId, out var slot) ? slot : -1)))
            .Where(u => u.Item2 >= 0).Distinct().ToList();
        _links = project.Topology.Links.ToList();
        _topology = project.Topology;
        _downLinks.IntersectWith(_links.Select(l => l.Id));
        program.Memory.SetLinkBadQuality(_linkUses.Where(u => _downLinks.Contains(u.Link)).Select(u => u.Slot));

        _program = program;
        _bySlot = slots;
        _byReference = byReference;
        _published = new (Value, bool, string?)[slots.Length];
        for (var i = 0; i < slots.Length; i++)
            _published[i] = Current(i);
    }

    private async Task RunLoop(CancellationToken token)
    {
        await Task.Yield();
        var next = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!token.IsCancellationRequested)
        {
            Cycle();
            next += (long)(System.Diagnostics.Stopwatch.Frequency * _cycleSeconds / _speed);
            var wait = System.Diagnostics.Stopwatch.GetElapsedTime(System.Diagnostics.Stopwatch.GetTimestamp(), next);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, token);
            else if (wait < TimeSpan.FromSeconds(-1))
                next = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    private void Cycle()
    {
        lock (_gate)
            _program.Step();
        PublishChanges();
    }

    private (Value, bool, string?) Current(int slot)
    {
        var memory = _program.Memory;
        return (memory.Get(slot), memory.Forced.ContainsKey(slot), memory.GetText(slot));
    }

    private SimTagValue ValueOf(SimTag tag)
    {
        var (value, forced, text) = Current(tag.Slot);
        return new SimTagValue(tag.Id, tag.Path, ValueConversion.ToObject(value, tag.DataType, text), value.Good, forced);
    }

    private SimTagValue PublishChanges(SimTag focus)
    {
        PublishChanges();
        lock (_gate)
            return ValueOf(focus);
    }

    private void PublishChanges()
    {
        SimChangeBatch? batch;
        lock (_gate)
        {
            var changes = new List<SimTagValue>();
            for (var slot = 0; slot < _bySlot.Length; slot++)
            {
                var current = Current(slot);
                if (current == _published[slot])
                    continue;
                _published[slot] = current;
                changes.Add(ValueOf(_bySlot[slot]));
            }
            batch = changes.Count == 0 ? null : new SimChangeBatch(_program.Cycle, _program.TimeSeconds, changes);
        }
        if (batch is not null)
            ValuesChanged?.Invoke(batch);
    }

    private void PublishAll()
    {
        SimChangeBatch batch;
        lock (_gate)
        {
            for (var slot = 0; slot < _bySlot.Length; slot++)
                _published[slot] = Current(slot);
            batch = new SimChangeBatch(_program.Cycle, _program.TimeSeconds, _bySlot.Select(ValueOf).ToList());
        }
        ValuesChanged?.Invoke(batch);
    }

    private void PublishStatus() => StatusChanged?.Invoke(Snapshot());
}
