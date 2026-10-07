using ApolloIQ.Core.Alarms;
using ApolloIQ.Core.Conventions;
using ApolloIQ.Core.Expressions;
using Builder.Core.Model;
using Builder.Core.Types;

namespace Builder.Logic.Runtime;

public sealed record StateChange(long Cycle, string ControlModule, int From, int To, string Transition, int TransitionIndex = -1);

public sealed class CmProgram
{
    internal CmProgram(Guid id, string path, CmType type, int stateSlot)
    {
        Id = id;
        Path = path;
        Type = type;
        StateSlot = stateSlot;
    }

    public Guid Id { get; }

    public string Path { get; }

    public CmType Type { get; }

    public int StateSlot { get; }

    public int State { get; internal set; }

    public long StateCycles { get; internal set; }

    public IReadOnlyList<CompiledTransition> Transitions => TransitionList;

    internal List<CompiledStep> Plant { get; } = [];

    internal List<CompiledStep> Before { get; } = [];

    internal List<CompiledStep> After { get; } = [];

    internal List<CompiledTransition> TransitionList { get; } = [];

    internal List<(int Field, int? Invert, int Conditioned)> Conditioning { get; } = [];

    internal List<int> Commands { get; } = [];

    internal InterlockState? Interlocks { get; set; }

    internal CmProgram? Parent { get; set; }

    internal bool ResetThisCycle { get; set; }

    /// <summary>Trips below that escalate to this container; read one cycle late (G-172).</summary>
    internal List<CompiledInterlock> Escalations { get; } = [];

    internal int EscalatedSlot { get; set; } = -1;

    internal List<int> MemberTripSlots { get; } = [];

    internal int MemberTrippedSlot { get; set; } = -1;

    internal List<CompiledWire> Wires { get; } = [];

    internal List<CompiledAlarm> Alarms { get; } = [];

    /// <summary>Alarms written by other logic (trips, auto/manual, command inputs), with their ALM.*.active slot.</summary>
    internal List<(CmAlarm Alarm, int Active)> TagAlarms { get; } = [];

    /// <summary>Alarm priorities of this instance that differ from the blueprint.</summary>
    internal IReadOnlyDictionary<string, int> Priorities { get; set; } = new Dictionary<string, int>();

    internal string Name { get; set; } = "";

    internal string? TakenThisCycle { get; set; }

    internal int ResetSlot { get; set; } = -1;

    internal CommandInputProgram? Inputs { get; set; }

    internal UnitModeProgram? Unit { get; set; }


    internal List<double> TimerList { get; } = [];

    internal double[] Timers = [];

    internal bool HasStateMachine => TransitionList.Count > 0 || StateSlot >= 0;

    public string StateName(int code) => Type.StateName(code);

    /// <summary>The text the operator sees for a state: the object's own state text, otherwise the category.</summary>
    public string StateText(int code) => StateNames.DisplayText(code, Type.ObjectStates);

    /// <summary>All alarms of this object and whether they are active now.</summary>
    public IEnumerable<AlarmStatus> AlarmStatuses(TagMemory memory)
    {
        foreach (var alarm in Alarms)
            yield return Status(alarm.Alarm, alarm.Active, alarm.RangeLevel);
        foreach (var (alarm, active) in TagAlarms)
            yield return Status(alarm, active >= 0 && memory.Get(active).IsTrue, null);
    }

    private AlarmStatus Status(CmAlarm alarm, bool active, AlarmLevel? rangeLevel)
    {
        var priority = Priorities.TryGetValue(alarm.Name, out var custom) ? custom : alarm.Definition.Priority;
        return new AlarmStatus(Path, alarm.Name, priority, AlarmPriority.LevelOf(priority), AlarmRules.FormatMessage(alarm.Definition.Message, Name),
            alarm.PlcReactive, alarm.Source, active, rangeLevel);
    }
}

/// <summary>An alarm of an object in the simulator: PLC reactive ones come from the PLC logic, the others are SCADA's (evaluated here as SCADA will).</summary>
public sealed record AlarmStatus(string Object, string Name, int Priority, AlarmLevel Level, string Message, bool PlcReactive, AlarmSource Source,
    bool Active, AlarmLevel? RangeLevel);

internal sealed class CompiledInterlock(InterlockKind kind, Expression condition, string owner, int active, int count)
{
    private bool _previous;

    public InterlockKind Kind { get; } = kind;

    public string Owner { get; } = owner;

    public bool Latched { get; set; }

    public bool Met(CmContext context) => condition.IsTrue(context);

    /// <summary>latched = (armed AND condition) OR (latched AND NOT reset); writes the owner's trip alarm.</summary>
    public void Trip(CmContext context, bool armed, bool reset)
    {
        Latched = (armed && condition.IsTrue(context)) || (Latched && !reset);
        if (active >= 0)
            context.Memory.Set(active, Value.Of(Latched));
        if (Latched && !_previous && count >= 0)
            context.Memory.Set(count, Value.Of(context.Memory.Get(count).Number + 1));
        _previous = Latched;
    }
}

/// <summary>The interlocks acting on one CM, in export order, and its LOK tags.</summary>
internal sealed class InterlockState(int canOn, int canOff, int trip, int onStatus, int offStatus)
{
    public List<CompiledInterlock> SwitchOn { get; } = [];

    public List<CompiledInterlock> SwitchOff { get; } = [];

    public List<CompiledInterlock> Trips { get; } = [];

    public int TripSlot { get; } = trip;

    public void Execute(CmContext context, int state, bool hasState, bool reset)
    {
        var memory = context.Memory;
        var on = 0;
        var allOn = true;
        for (var i = 0; i < SwitchOn.Count; i++)
        {
            if (SwitchOn[i].Met(context))
                on |= 1 << i;
            else
                allOn = false;
        }
        var off = 0;
        var allOff = true;
        for (var i = 0; i < SwitchOff.Count; i++)
        {
            if (SwitchOff[i].Met(context))
                off |= 1 << i;
            else
                allOff = false;
        }
        var armed = !hasState || UniversalStates.HeadsOn(state);
        var tripped = false;
        foreach (var t in Trips)
        {
            t.Trip(context, armed, reset);
            tripped |= t.Latched;
        }
        memory.Set(canOn, Value.Of(allOn && !tripped));
        memory.Set(canOff, Value.Of(allOff));
        memory.Set(TripSlot, Value.Of(tripped));
        if (onStatus >= 0)
            memory.Set(onStatus, Value.Of((double)on));
        if (offStatus >= 0)
            memory.Set(offStatus, Value.Of((double)off));
    }
}

internal sealed class CompiledWire(int source, WireMode mode, int on, int off)
{
    private bool _previous;

    public void Execute(TagMemory memory, int state)
    {
        var now = memory.Get(source).IsTrue;
        var rising = now && !_previous;
        var falling = !now && _previous;
        _previous = now;
        switch (mode)
        {
            case WireMode.On when rising:
                memory.Set(on, Value.True);
                break;
            case WireMode.Off when rising:
                memory.Set(off, Value.True);
                break;
            case WireMode.Toggle when rising:
                memory.Set(UniversalStates.HeadsOn(state) ? off : on, Value.True);
                break;
            case WireMode.Maintained when rising:
                memory.Set(on, Value.True);
                break;
            case WireMode.Maintained when falling:
                memory.Set(off, Value.True);
                break;
            case WireMode.Direct when now:
                memory.Set(on, Value.True);
                break;
        }
    }
}

/// <summary>
/// One alarm evaluated with the shared trigger logic (<see cref="AlarmTriggerLogic"/>, as SCADA does) plus the on-delay and, for PLC
/// reactive alarms, the Builder extras: enabled, latch until reset, raised on a transition. PLC reactive alarms write their ALM tags.
/// </summary>
internal sealed class CompiledAlarm(CmAlarm alarm, AlarmTriggerLogic? trigger, int active, int enabled, int count)
{
    private bool _latched;
    private bool _previous;
    private double? _metSince;

    public CmAlarm Alarm { get; } = alarm;

    public bool Active { get; private set; }

    public AlarmLevel? RangeLevel { get; private set; }

    public void Execute(CmContext context, double now, bool reset, string? taken)
    {
        var memory = context.Memory;
        bool met;
        if (Alarm.OnTransition is { } transition && (Alarm.Source == AlarmSource.StateTimeout || trigger is null))
            met = string.Equals(transition, taken, StringComparison.Ordinal);
        else
        {
            var evaluation = trigger!.Evaluate(context, now, Active);
            if (evaluation.Active is not { } value)
                return;
            met = value && (Alarm.OnTransition is null || string.Equals(Alarm.OnTransition, taken, StringComparison.Ordinal));
            RangeLevel = met ? evaluation.RangeLevel : null;
        }

        if (met)
            _metSince ??= now;
        else
            _metSince = null;
        var delayed = met && now - _metSince!.Value + 1e-6 >= Alarm.Definition.OnDelaySeconds;

        var enabledNow = enabled < 0 || memory.Get(enabled).IsTrue;
        if (!enabledNow || !Alarm.Latched)
            _latched = false;
        else
            _latched = delayed || (_latched && !reset);
        var now2 = enabledNow && (Alarm.Latched ? _latched : delayed);
        Active = now2;
        if (active >= 0)
            memory.Set(active, Value.Of(now2));
        if (now2 && !_previous && count >= 0)
            memory.Set(count, Value.Of(memory.Get(count).Number + 1));
        _previous = now2;
    }
}

public sealed class CompiledTransition
{
    internal CompiledTransition(string name, string location, IReadOnlyList<(int From, int To)> from, int to, int priority, int order, Expression guard)
    {
        Name = name;
        Location = location;
        From = from;
        To = to;
        Priority = priority;
        Order = order;
        Guard = guard;
    }

    public string Name { get; }

    public string Location { get; }

    public IReadOnlyList<(int From, int To)> From { get; }

    public int To { get; }

    public int Priority { get; }

    public int Order { get; }

    public Expression Guard { get; }

    public bool Applies(int state) => From.Any(r => state >= r.From && state < r.To) && state != To;
}

internal abstract class CompiledStep
{
    public abstract void Execute(CmContext context);
}

internal sealed class AssignCompiled(int target, Expression expression) : CompiledStep
{
    public override void Execute(CmContext context) => context.Write(target, expression.Evaluate(context));
}

internal sealed class CommandCompiled(int target, Expression expression) : CompiledStep
{
    public override void Execute(CmContext context)
    {
        if (expression.IsTrue(context))
            context.Write(target, Value.True);
    }
}

internal sealed class CmContext(TagMemory memory, CmProgram program, double cycleSeconds, Action<string> report, bool plant = false) : IEvalContext
{
    public void Write(int slot, Value value) => Memory.Set(slot, plant ? value with { Good = true } : value);

    public TagMemory Memory { get; } = memory;

    public Value Read(int slot) => Memory.Get(slot);

    public double CycleSeconds { get; } = cycleSeconds;

    public double StateTimeSeconds => program.StateCycles * CycleSeconds;

    public double[] Timers => program.Timers;

    public void Report(string message) => report(message);
}
