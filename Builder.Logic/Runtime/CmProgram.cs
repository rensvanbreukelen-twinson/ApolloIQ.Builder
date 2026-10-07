using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Logic.Blocks;
using Builder.Logic.Expressions;

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

    internal List<CompiledOutput> Outputs { get; } = [];

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

    internal PicProgram? Pic { get; set; }

    internal List<CompiledAlarm> Alarms { get; } = [];

    internal string? TakenThisCycle { get; set; }

    internal int ResetSlot { get; set; } = -1;

    internal CommandInputProgram? Inputs { get; set; }

    internal UnitModeProgram? Unit { get; set; }


    internal List<double> TimerList { get; } = [];

    internal double[] Timers = [];

    internal bool HasStateMachine => TransitionList.Count > 0 || StateSlot >= 0;

    public string StateName(int code) =>
        Type.States.FirstOrDefault(s => s.Code == code)?.Name ?? code.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

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

internal sealed class CompiledAlarm(string name, Expression condition, Expression latch, int active, int enabled, int count, string? onTransition)
{
    private bool _latched;
    private bool _previous;

    public string Name { get; } = name;

    public void Execute(CmContext context, bool reset, string? taken)
    {
        var memory = context.Memory;
        var met = condition.IsTrue(context) && (onTransition is null || string.Equals(onTransition, taken, StringComparison.Ordinal));
        var enabledNow = memory.Get(enabled).IsTrue;
        var latching = latch.IsTrue(context);
        if (!enabledNow || !latching)
            _latched = false;
        else
            _latched = met || (_latched && !reset);
        var now = enabledNow && (latching ? _latched : met);
        memory.Set(active, Value.Of(now));
        if (now && !_previous && count >= 0)
            memory.Set(count, Value.Of(memory.Get(count).Number + 1));
        _previous = now;
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

internal sealed class BlockCompiled(IBlock block, Expression?[] inputs, Value[] defaults, int[] outputs) : CompiledStep
{
    private readonly Value[] _inputValues = new Value[inputs.Length];
    private readonly Value[] _outputValues = new Value[outputs.Length];

    public override void Execute(CmContext context)
    {
        for (var i = 0; i < inputs.Length; i++)
            _inputValues[i] = inputs[i]?.Evaluate(context) ?? defaults[i];
        for (var i = 0; i < outputs.Length; i++)
            _outputValues[i] = outputs[i] >= 0 ? context.Memory.Get(outputs[i]) : Value.BadNumber;
        block.Execute(_inputValues, _outputValues, context.CycleSeconds);
        for (var i = 0; i < outputs.Length; i++)
        {
            if (outputs[i] >= 0)
                context.Write(outputs[i], _outputValues[i]);
        }
    }
}

internal sealed class CompiledOutput(int target, IReadOnlyList<(int From, int To)>? states, Expression? when)
{
    public void Execute(CmContext context, int state)
    {
        var inState = states is null || states.Any(r => state >= r.From && state < r.To);
        var condition = when?.Evaluate(context) ?? Value.True;
        context.Memory.Set(target, Value.Of(inState && condition.IsTrue));
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
