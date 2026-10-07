using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Blocks;
using Builder.Logic.Expressions;
using Builder.Logic.Model;
using ValueType = Builder.Logic.Expressions.ValueType;

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
    private static readonly System.Text.RegularExpressions.Regex RoleToken = new(@"\{role:([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\}");

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
        _program = new CmProgram(cm.Id, _path, type, stateTag is not null && memory.Slots.TryGetValue(stateTag.Id, out var slot) ? slot : -1);
    }

    public CmProgram Compile()
    {
        LogicModel model;
        try
        {
            model = LogicModelParser.Parse(_type.Logic);
        }
        catch (LogicException ex)
        {
            _errors.AddRange(ex.Errors.Select(Prefix));
            return _program;
        }

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
        if (_type.IsPriorityInputControl)
        {
            var pic = new PicProgram(_cm.Pic ?? PicConfiguration.Default,
                name => _registry.FindByPath($"{_path}.{name}") is { } tag ? _memory.Slots[tag.Id] : -1);
            if (pic.Complete && _program.StateSlot >= 0)
                _program.Pic = pic;
            else
                _errors.Add(new LogicError(Prefix("builtin"), "The PIC is missing OUT.set_on, OUT.set_off or its STS tags. Recreate it from its type."));
        }

        if (_cm.CommandInputs is { Rows.Count: > 0 } inputs)
        {
            var autoSlot = -1;
            if (_project.UnitOf(_cm.Id) is { } unit)
            {
                if (_registry.FindByPath($"{_project.GetPath(unit.Id)}.STS.auto") is { } auto && _memory.Slots.TryGetValue(auto.Id, out var s))
                    autoSlot = s;
            }
            var problems = new List<string>();
            _program.Inputs = new CommandInputProgram(inputs, name => _registry.FindByPath($"{_path}.{name}") is { } tag ? _memory.Slots[tag.Id] : -1,
                autoSlot, problems);
            foreach (var problem in problems.Distinct())
                _errors.Add(new LogicError(Prefix("commandInputs"), problem));
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
            int Command(string name) => _registry.FindByPath($"{_path}.CMD.{name}") is { } tag ? _memory.Slots[tag.Id] : -1;
            var commands = wire.Commands.Select(Command).ToList();
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
            _errors.Add(new LogicError(Prefix("logic.stateMachine"), "The CM has no STS.state tag."));

        foreach (var output in model.Outputs)
        {
            if (IsAbsentOptional(output.Target))
                continue;
            var target = Target(output.Target, $"{output.Location}", plant: false, requireGroup: TagGroup.Out);
            List<(int, int)>? states = null;
            if (output.States is not null)
            {
                states = [];
                foreach (var name in output.States)
                {
                    if (StateRange(name, output.Location) is { } range)
                        states.Add(range);
                }
            }
            var when = output.When is null ? null : CompileExpression(output.When, $"{output.Location}.when", ValueType.Bool);
            if (target is { } slot && (output.When is null || when is not null))
            {
                if (_memory.TypeOf(slot) != TagDataType.Bool)
                    _errors.Add(new LogicError(Prefix(output.Location), $"{output.Target} must be a Bool output."));
                else
                    _program.Outputs.Add(new CompiledOutput(slot, states, when));
            }
        }

        foreach (var alarm in _type.AlarmsFor(_cm.OptionalTags).Where(a => a.Plc && !a.Trip))
        {
            var location = $"alarms.{alarm.Name}";
            int Slot(string field) => _registry.FindByPath($"{_path}.ALM.{alarm.Name}.{field}") is { } tag ? _memory.Slots[tag.Id] : -1;
            var active = Slot("active");
            var enabled = Slot("enabled");
            if (active < 0 || enabled < 0)
            {
                _errors.Add(new LogicError(Prefix(location), $"The CM has no ALM.{alarm.Name}.active or .enabled tag. Recreate the CM from its type."));
                continue;
            }
            var condition = CompileExpression(alarm.Condition, $"{location}.condition", ValueType.Bool);
            var latch = CompileExpression(alarm.Latch, $"{location}.latch", ValueType.Bool);
            if (alarm.OnTransition is { } transition && model.Transitions.All(t => t.Name != transition))
            {
                _errors.Add(new LogicError(Prefix($"{location}.onTransition"), $"No transition is named '{transition}'."));
                continue;
            }
            if (condition is not null && latch is not null)
                _program.Alarms.Add(new CompiledAlarm(alarm.Name, condition, latch, active, enabled, Slot("raise_count"), alarm.OnTransition));
        }

        _program.Timers = Enumerable.Repeat(-1d, _program.TimerList.Count).ToArray();
        _program.State = _type.InitialState;
        return _program;
    }

    private void CompileSteps(IReadOnlyList<LogicStep> steps, List<CompiledStep> into, bool plant)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case AssignStep assign:
                {
                    if (IsAbsentOptional(assign.Target))
                        continue;
                    var target = Target(assign.Target, $"{assign.Location}.set", plant);
                    if (target is not { } slot)
                        continue;
                    var type = TagMemory.LogicType(_memory.TypeOf(slot));
                    var expression = CompileExpression(assign.Expression, $"{assign.Location}.expr", type);
                    if (expression is not null)
                        into.Add(assign.Target.StartsWith('@') ? new CommandCompiled(slot, expression) : new AssignCompiled(slot, expression));
                    break;
                }
                case BlockStep block:
                {
                    var definition = BlockLibrary.Get(block.Block);
                    var inputs = new Expression?[definition.Inputs.Count];
                    var defaults = new Value[definition.Inputs.Count];
                    var ok = true;
                    foreach (var pin in block.Inputs.Keys.Where(k => definition.Inputs.All(p => !string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase))))
                    {
                        _errors.Add(new LogicError(Prefix($"{block.Location}.in.{pin}"), $"{definition.Name} has no input '{pin}'."));
                        ok = false;
                    }
                    foreach (var pin in block.Outputs.Keys.Where(k => definition.Outputs.All(p => !string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase))))
                    {
                        _errors.Add(new LogicError(Prefix($"{block.Location}.out.{pin}"), $"{definition.Name} has no output '{pin}'."));
                        ok = false;
                    }
                    for (var i = 0; i < definition.Inputs.Count; i++)
                    {
                        var pin = definition.Inputs[i];
                        var source = block.Inputs.FirstOrDefault(kv => string.Equals(kv.Key, pin.Name, StringComparison.OrdinalIgnoreCase)).Value ?? pin.Default;
                        if (source is null)
                        {
                            _errors.Add(new LogicError(Prefix($"{block.Location}.in.{pin.Name}"), $"Input '{pin.Name}' of {definition.Name} is required."));
                            ok = false;
                            continue;
                        }
                        inputs[i] = CompileExpression(source, $"{block.Location}.in.{pin.Name}", pin.Type);
                        ok &= inputs[i] is not null;
                    }
                    var outputs = new int[definition.Outputs.Count];
                    for (var i = 0; i < definition.Outputs.Count; i++)
                    {
                        var pin = definition.Outputs[i];
                        var target = block.Outputs.FirstOrDefault(kv => string.Equals(kv.Key, pin.Name, StringComparison.OrdinalIgnoreCase)).Value;
                        if (target is null || IsAbsentOptional(target))
                        {
                            outputs[i] = -1;
                            continue;
                        }
                        var slot = Target(target, $"{block.Location}.out.{pin.Name}", plant);
                        if (slot is { } s && TagMemory.LogicType(_memory.TypeOf(s)) != pin.Type)
                        {
                            _errors.Add(new LogicError(Prefix($"{block.Location}.out.{pin.Name}"), $"{pin.Name} is {pin.Type}, but {target} is {_memory.TypeOf(s)}."));
                            ok = false;
                        }
                        outputs[i] = slot ?? -1;
                        ok &= slot is not null;
                    }
                    if (ok)
                        into.Add(new BlockCompiled(definition.Create(), inputs, defaults, outputs));
                    break;
                }
            }
        }
    }

    private bool IsAbsentOptional(string reference)
    {
        if (_registry.FindByPath($"{_path}.{reference}") is not null)
            return false;
        var dot = reference.IndexOf('.');
        return dot > 0 && TagGroupNames.TryParse(reference[..dot], out var group)
                       && _type.Tags.Any(t => t.Optional && t.Group == group && string.Equals(t.Name, reference[(dot + 1)..], StringComparison.OrdinalIgnoreCase));
    }

    private string Substitute(string text, string location) => _members is null ? text : RoleToken.Replace(text, m =>
    {
        var path = m.Groups[1].Value;
        var role = path.Split('.')[0];
        if (_members.TryGetValue(role, out var member) && member is not null
            && InterlockSources.ResolveRole(_project, _project.Get(member.Id), path.Contains('.') ? path[(role.Length + 1)..] : "") is { } target)
            return _project.GetPath(target.Id);
        if (_reported.Add(path))
            _errors.Add(new LogicError(Prefix(location), !_members.ContainsKey(role) ? $"Unknown role {role}."
                : path == role ? $"Role {role} is not filled; logic that uses it is not executed."
                : $"Role {path} is not filled; logic that uses it is not executed."));
        return $"unfilled_{path.Replace('.', '_')}";
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
        var path = Substitute($"{{role:{role}}}", location);
        if (_members is null || !_members.TryGetValue(role, out var member) || member is null)
            return null;
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

    private int? Target(string reference, string location, bool plant, TagGroup? requireGroup = null)
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
        if (requireGroup is { } group && tag.Group != group)
            allowed = false;
        if (!allowed)
        {
            var rule = plant
                ? "The plant model may only write FIN tags."
                : requireGroup == TagGroup.Out
                    ? "State outputs must be OUT tags."
                    : "Logic may write INT, OUT, STS (not state or enabled) and PMT tags.";
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

    private (int, int)? StateRange(string name, string location)
    {
        var state = _type.States.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (state is null)
        {
            _errors.Add(new LogicError(Prefix(location), $"Unknown state '{name}'."));
            return null;
        }
        return UniversalStates.IsUniversalCode(state.Code) && !_type.ExactStates
            ? UniversalStates.Range(state.Code)
            : (state.Code, state.Code + 1);
    }

    private int? StateCode(string name, string location)
    {
        var state = _type.States.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (state is null)
            _errors.Add(new LogicError(Prefix(location), $"Unknown state '{name}'."));
        return state?.Code;
    }

    private LogicError Prefix(LogicError error) => error with { Location = Prefix(error.Location) };

    private string Prefix(string location) => $"{_path} ({_type.Name}) {location}";
}
