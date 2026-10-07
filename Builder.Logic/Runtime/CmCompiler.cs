using ApolloIQ.Core.Alarms;
using ApolloIQ.Core.Conventions;
using ApolloIQ.Core.Expressions;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Model;
using ValueType = ApolloIQ.Core.Expressions.ValueType;

namespace Builder.Logic.Runtime;

internal sealed class CmCompiler
{
    private readonly Project _project;
    private readonly TagRegistry _registry;
    private readonly TagMemory _memory;
    private readonly ControlModule _cm;
    private readonly CmType _type;
    private readonly CmScope _scope;
    private readonly CmProgram _program;
    private readonly List<LogicError> _errors;
    private readonly string _path;
    private readonly Action<Guid> _dependsOn;
    private readonly IReadOnlyDictionary<string, ControlModule?>? _members;
    private readonly HashSet<string> _reported = [];

    public CmCompiler(Project project, TagRegistry registry, CmLibrary library, TagMemory memory, ControlModule cm, CmType type,
        List<LogicError> errors, Action<Guid> dependsOn, IReadOnlyDictionary<string, ControlModule?>? members = null)
    {
        _members = members;
        _project = project;
        _registry = registry;
        _memory = memory;
        _cm = cm;
        _type = type;
        _errors = errors;
        _path = project.GetPath(cm.Id);
        _dependsOn = dependsOn;
        _scope = new CmScope(project, registry, library, memory, cm, type, dependsOn);
        var stateTag = registry.FindByPath($"{_path}.STS.state");
        _program = new CmProgram(cm.Id, _path, type, stateTag is not null && memory.Slots.TryGetValue(stateTag.Id, out var slot) ? slot : -1)
        {
            Name = cm.Name,
            Priorities = cm.AlarmPriorities
        };
    }

    public CmProgram Compile()
    {
        var model = _type.Logic;
        foreach (var tag in _registry.ForControlModule(_cm.Id))
        {
            if (tag.Group == TagGroup.Cmd && tag.DataType == TagDataType.Bool)
                _program.Commands.Add(_memory.Slots[tag.Id]);
            if (tag.Group != TagGroup.Fin || tag.DataType != TagDataType.Bool)
                continue;
            var conditioned = _registry.FindByPath($"{_path}.INT.{tag.Name}");
            if (conditioned is null)
                continue;
            var invert = _registry.FindByPath($"{_path}.SET.invert_{tag.Name}");
            _program.Conditioning.Add((_memory.Slots[tag.Id], invert is null ? null : _memory.Slots[invert.Id], _memory.Slots[conditioned.Id]));
        }

        if (_registry.FindByPath($"{_path}.CMD.reset") is { DataType: TagDataType.Bool } reset)
            _program.ResetSlot = _memory.Slots[reset.Id];

        if (_cm.CommandInputs is { Rows.Count: > 0 } inputs)
        {
            var autoSlot = -1;
            if (_project.UnitOf(_cm.Id) is { } unit)
            {
                if (_registry.FindByPath($"{_project.GetPath(unit.Id)}.STS.auto") is { } auto && _memory.Slots.TryGetValue(auto.Id, out var s))
                    autoSlot = s;
            }
            var problems = new List<string>();
            _program.Inputs = new CommandInputProgram(inputs, name => Slot($"{_path}.{name}"), autoSlot, problems);
            foreach (var problem in problems.Distinct())
                _errors.Add(new LogicError(Prefix("commandInputs"), problem));
            foreach (var row in inputs.Rows.Where(r => r.IsPhysical && r.StuckTime is not null))
                _program.TagAlarms.Add((CommandInputBehaviour.StuckAlarm(row, _type.Id), Slot($"{_path}.ALM.{row.Name}_stuck.active")));
        }

        foreach (var wire in _cm.CommandWires)
        {
            if (_project.Find(wire.SourceTagId) is not Tag source || source.ParentId is not { } owner)
            {
                _errors.Add(new LogicError(Prefix("wires"), $"The source tag {wire.SourceTagId} of a command wire does not exist."));
                continue;
            }
            if (owner != _cm.Id)
                _dependsOn(owner);
            var commands = wire.Commands.Select(name => Slot($"{_path}.CMD.{name}")).ToList();
            if (commands.Any(c => c < 0))
            {
                _errors.Add(new LogicError(Prefix("wires"), $"A command wire from {_project.GetPath(source.Id)} writes a command this CM does not have."));
                continue;
            }
            _program.Wires.Add(new CompiledWire(_memory.Slots[source.Id], wire.Mode, commands[0], commands.Count > 1 ? commands[1] : commands[0]));
        }

        CompileSteps(model.Plant, _program.Plant, plant: true);
        CompileSteps(model.Before, _program.Before, plant: false);
        CompileSteps(model.After, _program.After, plant: false);

        for (var i = 0; i < model.Transitions.Count; i++)
        {
            var t = model.Transitions[i];
            var from = new List<(int, int)>();
            foreach (var name in t.From)
            {
                if (name == "*")
                    from.Add((int.MinValue, int.MaxValue));
                else if (StateRange(name, $"{t.Location}.from") is { } range)
                    from.Add(range);
            }
            var to = StateCode(t.To, $"{t.Location}.to");
            var guard = CompileExpression(t.Guard, $"{t.Location}.guard", ValueType.Bool);
            if (to is null || guard is null || from.Count != t.From.Count)
                continue;
            _program.TransitionList.Add(new CompiledTransition(t.Name, Prefix(t.Location), from, to.Value, t.Priority, i, guard));
        }
        _program.TransitionList.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Order.CompareTo(b.Order));
        if (model.HasStateMachine && _program.StateSlot < 0)
            _errors.Add(new LogicError(Prefix("stateMachine"), "The CM has no STS.state tag."));

        foreach (var alarm in _type.Alarms)
        {
            if (!alarm.Evaluated)
            {
                if (alarm.Source != AlarmSource.StuckInput)
                    _program.TagAlarms.Add((alarm, Slot($"{_path}.ALM.{alarm.Name}.active")));
                continue;
            }
            CompileAlarm(alarm, model);
        }
        foreach (var rule in InterlockRule.WithAlarmNames(_cm.Interlocks).Where(r => r.Kind == InterlockKind.Trip))
            _program.TagAlarms.Add((InterlockRule.TripAlarm(rule, $"{{instance_name}}: trip {rule.Alarm}"), Slot($"{_path}.ALM.{rule.Alarm}.active")));

        _program.Timers = Enumerable.Repeat(-1d, _program.TimerList.Count).ToArray();
        _program.State = _type.InitialState;
        return _program;
    }

    private void CompileAlarm(CmAlarm alarm, LogicModel model)
    {
        var location = $"alarms.{alarm.Name}";
        int active = -1, enabled = -1, count = -1;
        if (alarm.PlcReactive)
        {
            active = Slot($"{_path}.ALM.{alarm.Name}.active");
            enabled = Slot($"{_path}.ALM.{alarm.Name}.enabled");
            count = Slot($"{_path}.ALM.{alarm.Name}.raise_count");
            if (active < 0 || enabled < 0)
            {
                _errors.Add(new LogicError(Prefix(location), $"The object has no ALM.{alarm.Name}.active or .enabled tag. Recreate it from its blueprint."));
                return;
            }
        }
        if (alarm.OnTransition is { } transition && model.Transitions.All(t => t.Name != transition))
        {
            _errors.Add(new LogicError(Prefix($"{location}.onTransition"), $"No transition is named '{transition}'."));
            return;
        }
        AlarmTriggerLogic? trigger = null;
        if (!(alarm.OnTransition is not null && alarm.Source == AlarmSource.StateTimeout))
        {
            var definition = alarm.Definition.Clone();
            definition.Condition = Substitute(definition.Condition, location);
            definition.Input = Substitute(definition.Input, location);
            definition.HighCaution = Substitute(definition.HighCaution, location);
            definition.HighWarning = Substitute(definition.HighWarning, location);
            definition.HighAlarm = Substitute(definition.HighAlarm, location);
            definition.LowCaution = Substitute(definition.LowCaution, location);
            definition.LowWarning = Substitute(definition.LowWarning, location);
            definition.LowAlarm = Substitute(definition.LowAlarm, location);
            definition.Running = Substitute(definition.Running, location);
            definition.TriggerExpr = Substitute(definition.TriggerExpr, location);
            definition.Stop = Substitute(definition.Stop, location);
            definition.Timeout = Substitute(definition.Timeout, location);
            try
            {
                trigger = AlarmTriggerLogic.Compile(definition, _scope);
            }
            catch (ExpressionException ex)
            {
                _errors.Add(new LogicError(Prefix(location), ex.Message));
                return;
            }
        }
        _program.Alarms.Add(new CompiledAlarm(alarm, trigger, active, enabled, count));
    }

    private int Slot(string path) => _registry.FindByPath(path) is { } tag && _memory.Slots.TryGetValue(tag.Id, out var slot) ? slot : -1;

    private void CompileSteps(IReadOnlyList<AssignStep> steps, List<CompiledStep> into, bool plant)
    {
        foreach (var assign in steps)
        {
            var target = Target(assign.Target, assign.Location, plant);
            if (target is not { } slot)
                continue;
            var type = TagMemory.LogicType(_memory.TypeOf(slot));
            var expression = CompileExpression(assign.Expression, assign.Location, type);
            if (expression is not null)
                into.Add(assign.Target.StartsWith('@') ? new CommandCompiled(slot, expression) : new AssignCompiled(slot, expression));
        }
    }

    /// <summary>Replaces <c>[{role:ROLE.SUB}.rest]</c> with the member's path (EM and Unit).</summary>
    private string Substitute(string text, string location) => _members is null || text.Length == 0 ? text : Expression.RewriteReferences(text, reference =>
    {
        if (!reference.StartsWith(InterlockDisplay.RolePrefix, StringComparison.Ordinal))
            return reference;
        var close = reference.IndexOf('}');
        var path = reference[InterlockDisplay.RolePrefix.Length..close];
        var rest = reference[(close + 1)..];
        var role = path.Split('.')[0];
        if (_members.TryGetValue(role, out var member) && member is not null
            && InterlockSources.ResolveRole(_project, _project.Get(member.Id), path.Contains('.') ? path[(role.Length + 1)..] : "") is { } target)
            return _project.GetPath(target.Id) + rest;
        if (_reported.Add(path))
            _errors.Add(new LogicError(Prefix(location), !_members.ContainsKey(role) ? $"Unknown role {role}."
                : path == role ? $"Role {role} is not filled; logic that uses it is not executed."
                : $"Role {path} is not filled; logic that uses it is not executed."));
        return $"unfilled_{path.Replace('.', '_')}{rest}";
    });

    /// <summary>Compiles an interlock condition in this object's scope (its roles and tags); the target CM runs it (G-172).</summary>
    public Expression? CompileInterlock(string condition, string location)
    {
        var text = Substitute(condition, location);
        try
        {
            return Expression.Compile(text, _scope, ExpressionOptions.Interlock, ValueType.Bool);
        }
        catch (ExpressionException ex)
        {
            _errors.Add(new LogicError(Prefix(location), ex.Message));
            return null;
        }
    }

    public string Path => _path;

    private int? MemberCommand(string reference, string location)
    {
        var dot = reference.IndexOf('.');
        var role = reference[1..dot];
        var rest = reference[(dot + 1)..];
        if (_members is null || !_members.TryGetValue(role, out var member) || member is null)
        {
            Substitute($"[{InterlockDisplay.RolePrefix}{role}}}.{rest}]", location);
            return null;
        }
        var path = _project.GetPath(member.Id);
        if (!rest.StartsWith("CMD.", StringComparison.Ordinal))
        {
            _errors.Add(new LogicError(Prefix(location), $"A Unit may only write its members' commands, not {reference[1..]}."));
            return null;
        }
        var command = rest[4..];
        var row = member.CommandInputs?.Rows.FirstOrDefault(r => r.Source == CommandSource.Unit);
        var routed = row is null ? command : command switch
        {
            "set_on" when row.GivesOn => $"{row.Name}_on",
            "set_off" when row.GivesOff => $"{row.Name}_off",
            _ when row.SingleCommands.Contains(command) => $"{row.Name}_{command}",
            _ => command
        };
        if (row is not null && routed == command)
        {
            _errors.Add(new LogicError(Prefix(location), $"{member.Name}'s {row.Name} input does not give {command}; add it to the {row.Name} input."));
            return null;
        }
        if (_registry.FindByPath($"{path}.CMD.{routed}") is not { DataType: TagDataType.Bool } tag)
        {
            _errors.Add(new LogicError(Prefix(location), $"{member.Name} has no command {routed}."));
            return null;
        }
        return _memory.Slots[tag.Id];
    }

    private int? Target(string reference, string location, bool plant)
    {
        if (reference.StartsWith('@'))
            return plant ? null : MemberCommand(reference, location);
        var tag = _registry.FindByPath($"{_path}.{reference}");
        if (tag is null)
        {
            _errors.Add(new LogicError(Prefix(location), $"'{reference}' is not a tag of this CM."));
            return null;
        }
        if (!TagMemory.IsLogicType(tag.DataType))
        {
            _errors.Add(new LogicError(Prefix(location), $"'{reference}' is a {tag.DataType} tag; logic can only write Bool and numeric tags."));
            return null;
        }
        var allowed = plant
            ? tag.Group == TagGroup.Fin
            : tag.Group is TagGroup.Int or TagGroup.Out or TagGroup.Sts or TagGroup.Pmt
              && !(tag.Group == TagGroup.Sts && (tag.Name == "state" || tag.Name == "enabled"));
        if (!allowed)
        {
            var rule = plant ? "The plant model may only write FIN tags." : "Logic may write INT, OUT, STS (not state or enabled) and PMT tags.";
            _errors.Add(new LogicError(Prefix(location), $"'{reference}' cannot be written here. {rule}"));
            return null;
        }
        return _memory.Slots[tag.Id];
    }

    private Expression? CompileExpression(string text, string location, ValueType expected)
    {
        text = Substitute(text, location);
        try
        {
            return Expression.Compile(text, _scope, ExpressionOptions.Logic, expected, () =>
            {
                _program.TimerList.Add(-1);
                return _program.TimerList.Count - 1;
            });
        }
        catch (ExpressionException ex)
        {
            _errors.Add(new LogicError(Prefix(location), ex.Message));
            return null;
        }
    }

    /// <summary>A category matches its whole range, an object state its own code.</summary>
    private (int, int)? StateRange(string name, string location)
    {
        var state = _type.States.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (state is null)
            _errors.Add(new LogicError(Prefix(location), $"Unknown state '{name}'."));
        return state?.Range;
    }

    private int? StateCode(string name, string location)
    {
        var state = _type.States.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (state is null)
            _errors.Add(new LogicError(Prefix(location), $"Unknown state '{name}'."));
        return state?.Code;
    }

    private string Prefix(string location) => $"{_path} ({_type.Name}) {location}";
}
