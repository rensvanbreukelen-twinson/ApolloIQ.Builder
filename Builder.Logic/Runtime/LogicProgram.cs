using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Expressions;
using Builder.Logic.Model;

namespace Builder.Logic.Runtime;

public sealed class LogicProgram
{
    private readonly Action<string, string> _report;
    private Project? _project;
    private TagRegistry? _registry;
    private CmLibrary? _library;
    private List<object> _sequence = [];

    private LogicProgram(TagMemory memory, IReadOnlyList<CmProgram> programs, IReadOnlyList<LogicError> errors, double cycleSeconds)
    {
        Memory = memory;
        Programs = programs;
        Errors = errors;
        CycleSeconds = cycleSeconds;
        _report = (cm, message) => Diagnostics.Report(Cycle, cm, message);
    }

    public const double DefaultCycleSeconds = 0.05;

    public TagMemory Memory { get; }

    public IReadOnlyList<CmProgram> Programs { get; }

    public IReadOnlyList<LogicError> Errors { get; }

    public double CycleSeconds { get; }

    public long Cycle { get; private set; }

    public double TimeSeconds => Cycle * CycleSeconds;

    public DiagnosticLog Diagnostics { get; } = new();

    public event Action<StateChange>? StateChanged;

    public static LogicProgram Build(Project project, CmLibrary library, double cycleSeconds = DefaultCycleSeconds)
    {
        var registry = new TagRegistry(project);
        var memory = CreateMemory(project, library);

        var errors = new List<LogicError>();
        var compiled = new List<CmProgram>();
        var compilers = new Dictionary<Guid, CmCompiler>();
        var dependencies = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var cm in project.Objects.OfType<ControlModule>().OrderBy(c => project.GetPath(c.Id), StringComparer.Ordinal))
        {
            var type = library.Find(cm.TypeName);
            if (type is null)
            {
                errors.Add(new LogicError(project.GetPath(cm.Id), $"CM type '{cm.TypeName}' is not in the library."));
                continue;
            }
            var deps = dependencies[cm.Id] = [];
            var compiler = new CmCompiler(project, registry, library, memory, cm, type, errors, id => deps.Add(id));
            compilers[cm.Id] = compiler;
            compiled.Add(compiler.Compile());
        }

        var memberOf = new List<(Guid Member, Guid Parent)>();
        foreach (var unit in project.Objects.OfType<UnitInstance>().OrderBy(u => project.GetPath(u.Id), StringComparer.Ordinal))
        {
            var type = library.Find(unit.BlueprintName);
            var path = project.GetPath(unit.Id);
            if (type is null || !type.IsUnit)
            {
                errors.Add(new LogicError(path, $"Unit blueprint '{unit.BlueprintName}' is not published (save it without errors in the Blueprint editor)."));
                continue;
            }
            var members = type.Roles.Keys.ToDictionary(role => role,
                role => unit.RoleMembers.TryGetValue(role, out var id) ? project.Find(id) switch
                {
                    ControlModule c => c,
                    UnitInstance u => ControlModule.ForUnit(u),
                    _ => null
                } : null, StringComparer.Ordinal);
            dependencies[unit.Id] = [];
            var unitCompiler = new CmCompiler(project, registry, library, memory, ControlModule.ForUnit(unit), type, errors, _ => { }, members);
            compilers[unit.Id] = unitCompiler;
            var unitProgram = unitCompiler.Compile();
            int Slot(string reference) => registry.FindByPath(reference) is { } tag && memory.Slots.TryGetValue(tag.Id, out var s) ? s : -1;
            var filled = members.Values.OfType<ControlModule>().ToList();
            unitProgram.Unit = new UnitModeProgram(Slot($"{path}.STS.auto"), Slot($"{path}.CMD.set_auto"), Slot($"{path}.CMD.set_manual"),
                [.. filled.Select(m => Slot($"{project.GetPath(m.Id)}.STS.override")).Where(s => s >= 0)],
                [.. filled.Select(m => Slot($"{project.GetPath(m.Id)}.STS.remote_ok")).Where(s => s >= 0),
                    .. filled.Where(m => project.Find(m.Id) is UnitInstance).Select(m => Slot($"{project.GetPath(m.Id)}.STS.auto")).Where(s => s >= 0)],
                Slot($"{path}.ALM.{Blueprints.BlueprintTypes.UnitOverrideAlarm}.active"), Slot($"{path}.ALM.{Blueprints.BlueprintTypes.UnitOverrideAlarm}.enabled"),
                Slot($"{path}.ALM.{Blueprints.BlueprintTypes.UnitOverrideAlarm}.raise_count"), Slot($"{path}.INT.{Blueprints.BlueprintTypes.PreviousState}"));
            compiled.Add(unitProgram);
            foreach (var member in filled)
                memberOf.Add((member.Id, unit.Id));
        }
        foreach (var (member, parent) in memberOf)
            if (dependencies.TryGetValue(member, out var memberDeps))
                memberDeps.Add(parent);

        CompileInterlocks(project, registry, library, memory, compiled, compilers, dependencies, errors);

        var sequence = Order([.. compiled.Select(c => (c.Id, (object)c))], dependencies);
        var program = new LogicProgram(memory, sequence.OfType<CmProgram>().ToList(), errors, cycleSeconds)
        {
            _project = project,
            _registry = registry,
            _library = library,
            _sequence = sequence
        };
        program.Initialise();
        return program;
    }

    private static TagMemory CreateMemory(Project project, CmLibrary library)
    {
        var memory = new TagMemory();
        foreach (var tag in project.Tags.OrderBy(t => project.GetPath(t.Id), StringComparer.Ordinal))
        {
            var owner = tag.ParentId is { } p ? project.Find(p) as ControlModule : null;
            var type = owner is null ? null : library.Find(owner.TypeName);
            var initial = TagMemory.IsLogicType(tag.DataType)
                ? InitialValues.From(tag.InitialValue, tag.DataType, tag.EnumType, type)
                : Value.Of(0d);
            memory.Add(tag.Id, tag.DataType, initial, tag.InitialValue?.ToString());
        }
        return memory;
    }

    /// <summary>Checks one interlock condition in the scope of the object that defines it (G-172). Null when it compiles.</summary>
    public static string? CheckInterlockCondition(Project project, CmLibrary library, Guid ownerId, string display)
    {
        var registry = new TagRegistry(project);
        var memory = CreateMemory(project, library);
        var owner = project.AsControlModule(ownerId);
        var type = library.Find(owner.TypeName);
        if (type is null)
            return $"Type '{owner.TypeName}' is not in the library.";
        var errors = new List<LogicError>();
        var compiler = new CmCompiler(project, registry, library, memory, owner, type, errors, _ => { }, Members(project, type, owner.Id));
        return compiler.CompileInterlock(display, "condition") is null ? errors.LastOrDefault()?.Message ?? "Invalid condition." : null;
    }

    private static Dictionary<string, ControlModule?>? Members(Project project, CmType type, Guid id) =>
        project.Find(id) is not UnitInstance unit ? null : type.Roles.Keys.ToDictionary(role => role,
            role => unit.RoleMembers.TryGetValue(role, out var member) ? project.Find(member) switch
            {
                ControlModule c => c,
                UnitInstance u => ControlModule.ForUnit(u),
                _ => null
            } : null, StringComparer.Ordinal);

    private static void CompileInterlocks(Project project, TagRegistry registry, CmLibrary library, TagMemory memory, List<CmProgram> programs,
        Dictionary<Guid, CmCompiler> compilers, Dictionary<Guid, HashSet<Guid>> dependencies, List<LogicError> errors)
    {
        var byId = programs.ToDictionary(p => p.Id);
        int Slot(string path) => registry.FindByPath(path) is { } tag && memory.Slots.TryGetValue(tag.Id, out var s) ? s : -1;
        foreach (var program in programs)
        {
            if (project.UnitOf(program.Id) is { } parent && byId.TryGetValue(parent.Id, out var parentProgram))
                program.Parent = parentProgram;
            if (project.Find(program.Id) is UnitInstance)
            {
                program.EscalatedSlot = Slot($"{program.Path}.INT.escalated");
                program.MemberTrippedSlot = Slot($"{program.Path}.INT.member_tripped");
            }
        }
        foreach (var program in programs)
        {
            var sources = InterlockSources.Targeting(project, library, program.Id);
            var canOn = Slot($"{program.Path}.LOK.can_on");
            var canOff = Slot($"{program.Path}.LOK.can_off");
            var trip = Slot($"{program.Path}.LOK.trip");
            if (canOn < 0 || canOff < 0 || trip < 0)
            {
                if (sources.Count > 0)
                    errors.Add(new LogicError($"{program.Path} ({program.Type.Name})",
                        $"{sources.Count} interlock{(sources.Count == 1 ? " acts" : "s act")} on {program.Path}, but its blueprint has no Interlocks interface (LOK.can_on, can_off, trip)."));
                continue;
            }
            var state = new InterlockState(canOn, canOff, trip, Slot($"{program.Path}.LOK.can_on_status"), Slot($"{program.Path}.LOK.can_off_status"));
            program.Interlocks = state;
            foreach (var container in InterlockSources.SelfAndContainers(project, program.Id).Skip(1))
                if (byId.TryGetValue(container.Id, out var containerProgram))
                    containerProgram.MemberTripSlots.Add(trip);
            for (var i = 0; i < sources.Count; i++)
            {
                var (owner, rule, fromBlueprint) = sources[i];
                if (!compilers.TryGetValue(owner.Id, out var compiler))
                    continue;
                var location = $"interlocks[{i}] ({(fromBlueprint ? "blueprint" : "instance")} of {compiler.Path})";
                var text = fromBlueprint ? rule.Condition : ExpressionReferences.ToDisplay(project, rule.Condition);
                var deps = dependencies[program.Id];
                var before = errors.Count;
                var condition = compiler.CompileInterlock(text, location);
                if (condition is null)
                {
                    for (var e = before; e < errors.Count; e++)
                        errors[e] = errors[e] with { Location = $"{program.Path} {errors[e].Location}" };
                    continue;
                }
                foreach (var tagRef in ReferencedOwners(project, registry, text, compiler.Path))
                    if (tagRef != program.Id)
                        deps.Add(tagRef);
                var compiled = new CompiledInterlock(rule.Kind, condition, compiler.Path,
                    rule.Kind == InterlockKind.Trip ? Slot($"{compiler.Path}.ALM.{rule.Alarm}.active") : -1,
                    rule.Kind == InterlockKind.Trip ? Slot($"{compiler.Path}.ALM.{rule.Alarm}.raise_count") : -1);
                switch (rule.Kind)
                {
                    case InterlockKind.SwitchOn when state.SwitchOn.Count >= InterlockRule.MaxPerKind:
                    case InterlockKind.SwitchOff when state.SwitchOff.Count >= InterlockRule.MaxPerKind:
                        errors.Add(new LogicError($"{program.Path} {location}", $"At most {InterlockRule.MaxPerKind} {rule.Kind} interlocks act on one object."));
                        continue;
                    case InterlockKind.SwitchOn:
                        state.SwitchOn.Add(compiled);
                        break;
                    case InterlockKind.SwitchOff:
                        state.SwitchOff.Add(compiled);
                        break;
                    default:
                        state.Trips.Add(compiled);
                        break;
                }
                if (rule.Kind != InterlockKind.Trip || rule.Escalate == TripEscalation.None)
                    continue;
                var target = InterlockSources.EscalationTarget(project, program.Id, rule.Escalate);
                if (target is null || !byId.TryGetValue(target.Id, out var escalated))
                    errors.Add(new LogicError($"{program.Path} {location}", $"The trip escalates to the {rule.Escalate}, but {program.Path} is not in an {rule.Escalate}."));
                else if (escalated.EscalatedSlot < 0 || escalated.Type.States.All(s => !UniversalStates.IsFault(s.Code) || s.Code == UniversalStates.UnavailableCode))
                    errors.Add(new LogicError($"{program.Path} {location}", $"The trip escalates to {escalated.Path}, which has no fault state (Shutdown) to go to."));
                else
                    escalated.Escalations.Add(compiled);
            }
        }
    }

    private static IEnumerable<Guid> ReferencedOwners(Project project, TagRegistry registry, string text, string ownerPath)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\[([^\[\]]+)\]"))
        {
            var reference = m.Groups[1].Value;
            if (registry.FindByPath(reference) is { ParentId: { } parent })
                yield return parent;
        }
        if (registry.FindByPath(ownerPath + ".STS.state") is { ParentId: { } self })
            yield return self;
    }

    private static List<object> Order(List<(Guid Id, object Node)> nodes, Dictionary<Guid, HashSet<Guid>> dependencies)
    {
        var remaining = nodes.ToList();
        var ordered = new List<object>();
        var done = new HashSet<Guid>();
        while (remaining.Count > 0)
        {
            var index = remaining.FindIndex(n => dependencies[n.Id].All(d => d == n.Id || done.Contains(d) || remaining.All(r => r.Id != d)));
            var next = remaining[index < 0 ? 0 : index];
            remaining.Remove(next);
            ordered.Add(next.Node);
            done.Add(next.Id);
        }
        return ordered;
    }

    private void Initialise()
    {
        foreach (var program in Programs.Where(p => p.StateSlot >= 0))
            Memory.Set(program.StateSlot, Value.Of(program.State));
    }

    public Func<Value> CompileCondition(string controlModulePath, string expression)
    {
        var program = Programs.FirstOrDefault(p => string.Equals(p.Path, controlModulePath, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ExpressionException($"Unknown control module '{controlModulePath}'", 0, expression);
        var cm = _project!.Find(program.Id) switch { ControlModule c => c, UnitInstance u => ControlModule.ForUnit(u), _ => throw new ExpressionException($"Unknown control module '{controlModulePath}'", 0, expression) };
        var scope = new CmScope(_project, _registry!, _library!, Memory, cm, program.Type, _ => { });
        var compiled = Expression.Compile(expression, scope, new ExpressionOptions(AllowFunctions: true, AllowTimers: false), Expressions.ValueType.Bool);
        return () => compiled.Evaluate(Context(program));
    }

    public void CopyStateFrom(LogicProgram previous)
    {
        foreach (var (id, slot) in Memory.Slots)
        {
            if (!previous.Memory.Slots.TryGetValue(id, out var old))
                continue;
            Memory.Set(slot, previous.Memory.GetUnforced(old));
            Memory.SetText(slot, previous.Memory.GetText(old));
            if (previous.Memory.Forced.TryGetValue(old, out var forced))
                Memory.Force(slot, forced);
            Memory.SetBadQuality(slot, previous.Memory.BadQuality.Contains(old));
        }
        foreach (var program in Programs)
        {
            var match = previous.Programs.FirstOrDefault(p => p.Id == program.Id);
            if (match is null || program.Type.States.All(s => s.Code != match.State))
                continue;
            program.State = match.State;
            program.StateCycles = match.StateCycles;
            if (program.StateSlot >= 0)
                Memory.Set(program.StateSlot, Value.Of(program.State));
        }
        Cycle = previous.Cycle;
    }

    public void RunPlant()
    {
        foreach (var program in Programs)
        {
            var context = Context(program, plant: true);
            foreach (var step in program.Plant)
                step.Execute(context);
        }
    }

    public void RunLogic()
    {
        foreach (var node in _sequence)
        {
            if (node is CmProgram program)
                Execute(program);
        }
        Cycle++;
    }

    public void Step()
    {
        RunPlant();
        RunLogic();
    }

    private void Execute(CmProgram program)
    {
        foreach (var wire in program.Wires)
            wire.Execute(Memory, program.State);
        program.Inputs?.Execute(Memory, TimeSeconds, program.State);
        if (program.Unit is { } unitMode && unitMode.Execute(Memory))
        {
            if (unitMode.PreviousStateSlot >= 0)
                Memory.Set(unitMode.PreviousStateSlot, Value.Of(-1d));
            var from = program.State;
            program.State = program.Type.InitialState;
            program.StateCycles = 0;
            Memory.Set(program.StateSlot, Value.Of(program.State));
            if (from != program.State)
                StateChanged?.Invoke(new StateChange(Cycle, program.Path, from, program.State, "auto"));
        }

        var reset = (program.ResetSlot >= 0 && Memory.Get(program.ResetSlot).IsTrue) || program.Parent?.ResetThisCycle == true;
        if (reset && program.ResetSlot >= 0)
            Memory.Set(program.ResetSlot, Value.True);
        program.ResetThisCycle = reset;
        if (program.EscalatedSlot >= 0)
            Memory.Set(program.EscalatedSlot, Value.Of(program.Escalations.Any(e => e.Latched)));
        if (program.MemberTrippedSlot >= 0)
            Memory.Set(program.MemberTrippedSlot, Value.Of(program.MemberTripSlots.Any(slot => Memory.Get(slot).IsTrue)));
        program.Interlocks?.Execute(Context(program), program.State, program.StateSlot >= 0, reset);

        if (program.Pic is not null)
        {
            var from = program.State;
            program.State = program.Pic.Execute(Memory, TimeSeconds);
            if (program.State != from)
            {
                program.StateCycles = 0;
                StateChanged?.Invoke(new StateChange(Cycle, program.Path, from, program.State, program.State == UniversalStates.UnavailableCode ? "disabled" : "enabled"));
            }
            else
                program.StateCycles++;
            Memory.Set(program.StateSlot, Value.Of(program.State));
        }

        var context = Context(program);
        foreach (var (field, invert, conditioned) in program.Conditioning)
        {
            var value = Memory.Get(field);
            var inverted = invert is { } i && Memory.Get(i).IsTrue;
            Memory.Set(conditioned, Value.Of(value.Bool ^ inverted, value.Good));
        }

        foreach (var step in program.Before)
            step.Execute(context);

        if (program.StateSlot >= 0)
        {
            CompiledTransition? taken = null;
            foreach (var transition in program.TransitionList)
            {
                var fires = transition.Guard.IsTrue(context);
                if (taken is null && fires && transition.Applies(program.State))
                    taken = transition;
            }
            program.TakenThisCycle = taken?.Name;
            if (taken is not null)
            {
                var from = program.State;
                program.State = taken.To;
                program.StateCycles = 0;
                StateChanged?.Invoke(new StateChange(Cycle, program.Path, from, taken.To, taken.Name, taken.Order));
            }
            else
                program.StateCycles++;
            Memory.Set(program.StateSlot, Value.Of(program.State));
        }

        foreach (var output in program.Outputs)
            output.Execute(context, program.State);

        foreach (var step in program.After)
            step.Execute(context);

        foreach (var alarm in program.Alarms)
            alarm.Execute(context, program.ResetThisCycle, program.TakenThisCycle);

        foreach (var command in program.Commands)
            Memory.Set(command, Value.False);
    }

    private CmContext Context(CmProgram program, bool plant = false) =>
        new(Memory, program, CycleSeconds, message => _report(program.Path, message), plant);
}
