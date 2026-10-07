using System.Globalization;
using System.Text.RegularExpressions;
using ApolloIQ.Core.Alarms;
using ApolloIQ.Core.Blueprints;
using ApolloIQ.Core.Conventions;
using ApolloIQ.Core.Expressions;
using Builder.Core.Model;
using Builder.Core.Types;
using ValueType = ApolloIQ.Core.Expressions.ValueType;

namespace Builder.Logic.Blueprints;

public sealed record BlueprintIssue(string Severity, string Where, string Message)
{
    public static BlueprintIssue Error(string where, string message) => new("Error", where, message);

    public static BlueprintIssue Warning(string where, string message) => new("Warning", where, message);
}

/// <summary>
/// Checks a blueprint: names, tags, states (identifiers, not a category or standard alias), transitions, alarms (the shared
/// <see cref="AlarmRules"/> plus the Builder extras), interlocks, command inputs and every expression (ApolloIQ.Core syntax).
/// </summary>
public static partial class BlueprintValidator
{
    private static readonly HashSet<string> WritableByLogic = new(["OUT", "STS", "INT"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^ALM\.([A-Za-z0-9_]+)\.active$", RegexOptions.IgnoreCase)]
    private static partial Regex AlarmReference();

    public static IReadOnlyList<BlueprintIssue> Validate(Blueprint blueprint, Func<Guid, Blueprint?>? lookup = null)
    {
        var issues = new List<BlueprintIssue>();
        var symbols = new Dictionary<string, ValueType>(StringComparer.OrdinalIgnoreCase);

        if (!NamePattern().IsMatch(blueprint.Name))
            issues.Add(BlueprintIssue.Error("General", "The name must start with a letter and contain only letters, digits and _."));
        foreach (var name in blueprint.Interfaces.Where(n => BlueprintCatalog.Interface(n) is null))
            issues.Add(BlueprintIssue.Error("General", $"Unknown interface '{name}'."));

        foreach (var tag in BlueprintRules.InterfaceTags(blueprint))
            if (!symbols.TryAdd(tag.Key, TypeOf(tag.DataType.ToString())))
                issues.Add(BlueprintIssue.Error("General", $"Interface tag {tag.Key} exists twice."));

        for (var i = 0; i < blueprint.Tags.Count; i++)
        {
            var tag = blueprint.Tags[i];
            var where = $"Tags › {tag.Key}";
            if (!BlueprintRules.Groups.Contains(tag.Group))
                issues.Add(BlueprintIssue.Error(where, $"Unknown group '{tag.Group}'."));
            if (!NamePattern().IsMatch(tag.Name))
                issues.Add(BlueprintIssue.Error(where, "Invalid tag name."));
            if (!BlueprintRules.DataTypes.Contains(tag.DataType))
                issues.Add(BlueprintIssue.Error(where, $"Unknown data type '{tag.DataType}'."));
            if (tag.Group == "FIN" && tag.Source is null)
                issues.Add(BlueprintIssue.Error(where, "Every input needs a source: local I/O or external (G-122)."));
            if (tag.Min is { } min && tag.Max is { } max && min > max)
                issues.Add(BlueprintIssue.Error(where, "Min is larger than max."));
            if (!symbols.TryAdd(tag.Key, TypeOf(tag.DataType)))
                issues.Add(BlueprintIssue.Error(where, "This tag exists twice, or clashes with an interface tag."));
        }
        AddGeneratedInputSymbols(blueprint.Tags, "", symbols);
        if (blueprint.Tags.Where(t => t.Id != Guid.Empty).GroupBy(t => t.Id).Any(g => g.Count() > 1))
            issues.Add(BlueprintIssue.Error("Tags", "Two tags share the same id."));
        if (blueprint.Tags.Count(t => t.Primary) > 1)
            issues.Add(BlueprintIssue.Error("Tags", "Only one tag can be the primary value."));

        if (blueprint.Kind != BlueprintKind.CM)
            AddRoles(blueprint, lookup, symbols, issues);
        else if (blueprint.Roles.Count > 0)
            issues.Add(BlueprintIssue.Error("Roles", "Only a Unit or an Equipment module has member roles."));

        // Alarm names: blueprint alarms, state timeout alarms and trip alarms share one name space. Value: PLC reactive.
        var alarms = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var alarm in blueprint.Alarms)
            if (NamePattern().IsMatch(alarm.Alarm.Name) && !alarms.TryAdd(alarm.Alarm.Name, alarm.Alarm.PlcReactive))
                issues.Add(BlueprintIssue.Error($"Alarms › {alarm.Alarm.Name}", "This alarm exists twice."));
        foreach (var state in blueprint.States)
            if (state.Timeout?.Alarm is { Length: > 0 } name && !alarms.TryAdd(name, true))
                issues.Add(BlueprintIssue.Error($"States › {state.Name} › timeout", $"Alarm '{name}' already exists."));
        var interlocks = InterlockRule.WithAlarmNames(blueprint.Interlocks);
        for (var i = 0; i < interlocks.Count; i++)
            if (interlocks[i].Kind == InterlockKind.Trip && !alarms.TryAdd(interlocks[i].Alarm!, true))
                issues.Add(BlueprintIssue.Error($"Interlocks › line {i + 1}", $"Alarm '{interlocks[i].Alarm}' already exists."));
        if (blueprint.Kind != BlueprintKind.CM && !alarms.TryAdd(BlueprintTypes.UnitOverrideAlarm, true))
            issues.Add(BlueprintIssue.Error("Alarms", $"{BlueprintTypes.UnitOverrideAlarm} is the alarm every EM and Unit has; choose another name."));
        foreach (var (name, plcReactive) in alarms.Where(a => a.Value))
            symbols[$"ALM.{name}.active"] = ValueType.Bool;
        foreach (var state in blueprint.States.Where(s => s.Timeout is not null))
            symbols[$"PAR.{BlueprintTypes.TimeoutParameter(state)}"] = ValueType.Number;

        BlueprintRules.AssignCodes(blueprint);
        var stateLists = new Dictionary<string, IReadOnlyList<StateDefinition>>(StringComparer.OrdinalIgnoreCase)
        {
            ["STS.state"] = StatesOf(blueprint)
        };
        var memberAliases = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var members = new Dictionary<string, Blueprint>(StringComparer.Ordinal);
        if (blueprint.Kind != BlueprintKind.CM)
            CollectMembers(blueprint, "", lookup, members, 0);
        foreach (var (path, member) in members)
        {
            stateLists[$"{path}.STS.state"] = StatesOf(member);
            memberAliases[path + "."] = member.Aliases;
        }
        if (blueprint.CommandInputs is { } inputs)
        {
            var hasPair = symbols.ContainsKey("CMD.set_on") && symbols.ContainsKey("CMD.set_off");
            var fins = blueprint.Tags.Where(t => t.Group == "FIN" && t.DataType == "Bool").Select(t => $"FIN.{t.Name}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var problem in CommandInputBehaviour.Problems(inputs, BlueprintTypes.SingleCommands(blueprint), hasPair, fins.Contains))
                issues.Add(BlueprintIssue.Error("Command inputs", problem));
            foreach (var tag in CommandInputBehaviour.Tags(inputs))
                if (blueprint.Tags.Any(t => t.Group == tag.Group.ToString().ToUpperInvariant() && t.Name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(BlueprintIssue.Error("Command inputs", $"{tag.Group.ToString().ToUpperInvariant()}.{tag.Name} is generated by a command input; remove the tag or rename the input."));
        }
        var scope = new Scope(symbols, stateLists, blueprint.Aliases, memberAliases);
        foreach (var (alias, target) in blueprint.Aliases)
            if (!NamePattern().IsMatch(alias) || UniversalStates.StandardAliases.ContainsKey(alias)
                || !(UniversalStates.StandardAliases.ContainsKey(target) || stateLists["STS.state"].Any(s => s.Name.Equals(target, StringComparison.OrdinalIgnoreCase))))
                issues.Add(BlueprintIssue.Error("General", $"Alias '{alias}' must point to a state or a standard alias such as is_running, and must not be a standard alias itself."));
        ValidateStates(blueprint, scope, symbols, issues);
        ValidateTransitions(blueprint, scope, alarms, issues);
        ValidateAlarms(blueprint, scope, issues);

        for (var i = 0; i < blueprint.Always.Count; i++)
            CheckAction(scope, symbols, blueprint.Always[i], $"Always-running › line {i + 1}", issues);
        for (var i = 0; i < blueprint.Plant.Count; i++)
        {
            var action = blueprint.Plant[i];
            var where = $"Simulation › line {i + 1}";
            if (!action.Tag.StartsWith("FIN.", StringComparison.OrdinalIgnoreCase) || !symbols.TryGetValue(action.Tag, out var type))
                issues.Add(BlueprintIssue.Error(where, "The plant model only writes this blueprint's inputs (FIN tags)."));
            else
                Check(scope, action.Value, where, type, issues);
        }

        ValidateInterlocks(blueprint, interlocks, members, scope, issues);

        if (blueprint.States.Count > 0 && blueprint.States.All(s => !UniversalStates.IsFault(s.Category)) && blueprint.Kind == BlueprintKind.CM
            && BlueprintRules.Has(blueprint, BlueprintCatalog.Switchable))
            issues.Add(BlueprintIssue.Warning("States", "There is no Fault state."));
        return issues;
    }

    private static void ValidateAlarms(Blueprint blueprint, Scope scope, List<BlueprintIssue> issues)
    {
        foreach (var alarm in blueprint.Alarms)
        {
            var where = $"Alarms › {alarm.Alarm.Name}";
            var copy = alarm.Alarm.Clone();
            if (!string.IsNullOrWhiteSpace(alarm.OnTransition) && copy.Trigger == AlarmTrigger.State && string.IsNullOrWhiteSpace(copy.Condition))
                copy.Condition = "TRUE";
            var errors = AlarmRules.Clean([copy], inBlueprint: true, allowPlcByte: false);
            foreach (var error in errors)
                issues.Add(BlueprintIssue.Error(where, error));
            if (!string.IsNullOrWhiteSpace(alarm.OnTransition))
            {
                if (!alarm.Alarm.PlcReactive)
                    issues.Add(BlueprintIssue.Error(where, "Only a PLC reactive alarm can be raised on a transition (SCADA does not see transitions)."));
                if (blueprint.Transitions.All(t => !t.Name.Equals(alarm.OnTransition, StringComparison.Ordinal)))
                    issues.Add(BlueprintIssue.Error(where, $"Transition '{alarm.OnTransition}' does not exist."));
            }
            if (alarm.Latched && !alarm.Alarm.PlcReactive)
                issues.Add(BlueprintIssue.Error(where, "Only a PLC reactive alarm latches in the logic; SCADA alarms stay until acknowledged."));
            if (alarm.Latched && !BlueprintRules.Has(blueprint, BlueprintCatalog.Resettable))
                issues.Add(BlueprintIssue.Error(where, "A latched alarm needs the Resettable interface (CMD.reset clears it)."));
            if (errors.Count > 0)
                continue;
            try
            {
                AlarmTriggerLogic.Compile(copy, scope);
            }
            catch (ExpressionException ex)
            {
                issues.Add(BlueprintIssue.Error(where, ex.Message));
            }
        }
    }

    private static void AddRoles(Blueprint blueprint, Func<Guid, Blueprint?>? lookup, Dictionary<string, ValueType> symbols, List<BlueprintIssue> issues)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in blueprint.Roles)
        {
            var where = $"Roles › {role.Name}";
            if (!NamePattern().IsMatch(role.Name) || !names.Add(role.Name))
                issues.Add(BlueprintIssue.Error(where, "Invalid or duplicate role name."));
            if (BlueprintCatalog.ReservedNames.Contains(role.Name))
                issues.Add(BlueprintIssue.Error(where, "A role cannot be named after a tag group."));
            var member = role.BlueprintId == blueprint.Id ? null : lookup?.Invoke(role.BlueprintId);
            if (member is null)
            {
                issues.Add(BlueprintIssue.Error(where, role.BlueprintId == Guid.Empty ? "Choose the member's blueprint." : $"Blueprint {role.BlueprintId} does not exist."));
                continue;
            }
            if (member.Kind == BlueprintKind.Unit || (member.Kind == BlueprintKind.EM && blueprint.Kind == BlueprintKind.EM))
                issues.Add(BlueprintIssue.Error(where, blueprint.Kind == BlueprintKind.EM
                    ? $"An Equipment module's members are CMs; '{member.Name}' is a {member.Kind} (G-151)."
                    : $"A Unit's members are Equipment modules and CMs; '{member.Name}' is a Unit."));
            AddMemberSymbols(role.Name, member, lookup, symbols, 0);
        }
        if (blueprint.Roles.Count == 0)
            issues.Add(BlueprintIssue.Warning("Roles", $"{(blueprint.Kind == BlueprintKind.EM ? "An Equipment module" : "A Unit")} without member roles cannot control anything."));
    }

    /// <summary>The PLC reactive alarms of a blueprint (the ones with ALM tags): its own, its state timeouts and its trips.</summary>
    public static IEnumerable<string> PlcAlarmNames(Blueprint blueprint)
    {
        var trips = InterlockRule.WithAlarmNames(blueprint.Interlocks).Where(r => r.Kind == InterlockKind.Trip).Select(r => r.Alarm!);
        var own = blueprint.Alarms.Where(a => a.Alarm.PlcReactive).Select(a => a.Alarm.Name);
        var timeouts = blueprint.States.Select(s => s.Timeout?.Alarm).OfType<string>().Where(a => a.Length > 0);
        var unit = blueprint.Kind == BlueprintKind.CM ? [] : new[] { BlueprintTypes.UnitOverrideAlarm };
        return own.Concat(timeouts).Concat(trips).Concat(unit);
    }

    /// <summary>
    /// The tags the runtime generates for a Bool input (<c>CmType.ExpandTags</c>): the conditioned copy <c>INT.&lt;name&gt;</c> and, for
    /// local I/O, <c>SET.invert_&lt;name&gt;</c>. A blueprint may declare the INT copy itself; it does not have to.
    /// </summary>
    private static void AddGeneratedInputSymbols(IEnumerable<BlueprintTag> tags, string prefix, Dictionary<string, ValueType> symbols)
    {
        foreach (var input in tags.Where(t => t.Group == "FIN" && t.DataType == "Bool").ToList())
        {
            symbols.TryAdd($"{prefix}INT.{input.Name}", ValueType.Bool);
            if (input.Source != InputSource.External)
                symbols.TryAdd($"{prefix}SET.invert_{input.Name}", ValueType.Bool);
        }
    }

    private static void AddMemberSymbols(string prefix, Blueprint member, Func<Guid, Blueprint?>? lookup, Dictionary<string, ValueType> symbols, int depth)
    {
        foreach (var tag in BlueprintRules.InterfaceTags(member))
            symbols[$"{prefix}.{tag.Key}"] = TypeOf(tag.DataType.ToString());
        foreach (var tag in member.Tags)
            symbols[$"{prefix}.{tag.Key}"] = TypeOf(tag.DataType);
        AddGeneratedInputSymbols(member.Tags, $"{prefix}.", symbols);
        foreach (var alarm in PlcAlarmNames(member))
            symbols[$"{prefix}.ALM.{alarm}.active"] = ValueType.Bool;
        if (depth < 3 && member.Kind != BlueprintKind.CM)
            foreach (var role in member.Roles)
                if (lookup?.Invoke(role.BlueprintId) is { } sub)
                    AddMemberSymbols($"{prefix}.{role.Name}", sub, lookup, symbols, depth + 1);
    }

    private static void CollectMembers(Blueprint blueprint, string prefix, Func<Guid, Blueprint?>? lookup, Dictionary<string, Blueprint> into, int depth)
    {
        foreach (var role in blueprint.Roles)
        {
            if (role.BlueprintId == blueprint.Id || lookup?.Invoke(role.BlueprintId) is not { } member)
                continue;
            var path = prefix.Length == 0 ? role.Name : $"{prefix}.{role.Name}";
            into[path] = member;
            if (depth < 3 && member.Kind != BlueprintKind.CM)
                CollectMembers(member, path, lookup, into, depth + 1);
        }
    }

    private static void ValidateInterlocks(Blueprint blueprint, IReadOnlyList<InterlockRule> interlocks, Dictionary<string, Blueprint> members,
        Scope scope, List<BlueprintIssue> issues)
    {
        var counts = new Dictionary<(string, InterlockKind), int>();
        for (var i = 0; i < interlocks.Count; i++)
        {
            var rule = interlocks[i];
            var where = $"Interlocks › line {i + 1}";
            var target = rule.Target.Trim();
            Blueprint? targetBlueprint = blueprint;
            if (target.Length > 0)
            {
                if (blueprint.Kind == BlueprintKind.CM)
                {
                    issues.Add(BlueprintIssue.Error(where, "A CM blueprint can only put interlocks on itself; leave the target empty."));
                    continue;
                }
                if (!members.TryGetValue(target, out targetBlueprint))
                {
                    issues.Add(BlueprintIssue.Error(where, $"'{target}' is not a role (or role path such as GEN1.BREAKER) of this blueprint."));
                    continue;
                }
            }
            if (!BlueprintRules.Has(targetBlueprint, BlueprintCatalog.Interlocks))
                issues.Add(BlueprintIssue.Error(where, $"{(target.Length == 0 ? "This blueprint" : $"{target} ({targetBlueprint.Name})")} has no Interlocks interface (LOK.can_on, can_off, trip)."));
            var key = (target, rule.Kind);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (rule.Kind != InterlockKind.Trip && counts[key] == InterlockRule.MaxPerKind + 1)
                issues.Add(BlueprintIssue.Error(where, $"At most {InterlockRule.MaxPerKind} {rule.Kind} interlocks per target."));
            if (string.IsNullOrWhiteSpace(rule.Condition))
                issues.Add(BlueprintIssue.Error(where, "The condition is empty."));
            else
            {
                try
                {
                    Expression.Compile(rule.Condition, scope, ExpressionOptions.Interlock, ValueType.Bool);
                }
                catch (ExpressionException ex)
                {
                    issues.Add(BlueprintIssue.Error(where, ex.Message));
                }
            }
            if (rule.Kind != InterlockKind.Trip)
                continue;
            if (!NamePattern().IsMatch(rule.Alarm!))
                issues.Add(BlueprintIssue.Error(where, $"Invalid alarm name '{rule.Alarm}'."));
            if (!AlarmPriority.IsValid(rule.Priority))
                issues.Add(BlueprintIssue.Error(where, AlarmPriority.RangeText));
            if (target.Length == 0 && blueprint.Kind == BlueprintKind.CM && !BlueprintRules.Has(blueprint, BlueprintCatalog.Resettable))
                issues.Add(BlueprintIssue.Warning(where, "Trips latch; without the Resettable interface only a container's reset clears this one."));
            if (rule.Escalate == TripEscalation.EM && blueprint.Kind == BlueprintKind.Unit && (target.Length == 0 || members.GetValueOrDefault(target.Split('.')[0])?.Kind != BlueprintKind.EM))
                issues.Add(BlueprintIssue.Warning(where, "Escalate to EM: the target is not inside an Equipment module of this Unit; it only works if the project puts it in one."));
            if (rule.Escalate != TripEscalation.None && blueprint.Kind != BlueprintKind.CM
                && (rule.Escalate == TripEscalation.EM) == (blueprint.Kind == BlueprintKind.EM)
                && blueprint.States.All(s => !UniversalStates.IsFault(s.Category)))
                issues.Add(BlueprintIssue.Error(where, "The trip escalates to this blueprint, which has no Fault state (Shutdown) to go to."));
        }
    }

    private static void ValidateStates(Blueprint blueprint, Scope scope, Dictionary<string, ValueType> symbols, List<BlueprintIssue> issues)
    {
        if (blueprint.States.Count == 0)
        {
            issues.Add(BlueprintIssue.Error("States", "Add at least one state."));
            return;
        }
        foreach (var error in StateNames.Validate(blueprint.States.Select(s => s.ToDefinition()).ToList()))
            issues.Add(BlueprintIssue.Error("States", error));
        foreach (var state in blueprint.States)
        {
            var where = $"States › {state.Name}";
            if (BlueprintRules.Categories.All(c => c.Code != state.Category))
                issues.Add(BlueprintIssue.Error(where, "Pick a category."));
            foreach (var (list, kind) in new[] { (state.Entry, "entry"), (state.Run, "run"), (state.Exit, "exit") })
                for (var i = 0; i < list.Count; i++)
                    CheckAction(scope, symbols, list[i], $"{where} › {kind} {i + 1}", issues);
            if (state.Timeout is { } timeout)
            {
                var at = $"{where} › timeout";
                if (!double.TryParse(timeout.Time, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    if (string.IsNullOrWhiteSpace(timeout.Time))
                        issues.Add(BlueprintIssue.Error(at, "The time must be a number of seconds or a numeric expression such as [PAR.max_time]."));
                    else
                        Check(scope, timeout.Time, at, ValueType.Number, issues, ExpressionOptions.Interlock);
                }
                else if (seconds <= 0)
                    issues.Add(BlueprintIssue.Error(at, "The time must be more than 0 s."));
                if (string.IsNullOrWhiteSpace(timeout.GoTo) && string.IsNullOrWhiteSpace(timeout.Alarm))
                    issues.Add(BlueprintIssue.Error(at, "Choose what happens on overflow: go to a state, raise an alarm, or both."));
                if (!string.IsNullOrWhiteSpace(timeout.GoTo) && blueprint.States.All(s => !s.Name.Equals(timeout.GoTo, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(BlueprintIssue.Error(at, $"State '{timeout.GoTo}' does not exist."));
                if (!string.IsNullOrWhiteSpace(timeout.Alarm))
                {
                    if (!NamePattern().IsMatch(timeout.Alarm))
                        issues.Add(BlueprintIssue.Error(at, $"Invalid alarm name '{timeout.Alarm}'."));
                    if (!AlarmPriority.IsValid(timeout.Priority))
                        issues.Add(BlueprintIssue.Error(at, AlarmPriority.RangeText));
                }
            }
        }
        var initial = blueprint.States.Count(s => s.Initial);
        if (initial != 1)
            issues.Add(BlueprintIssue.Error("States", initial == 0 ? "Mark one state as the initial state." : "Only one state can be the initial state."));

        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(blueprint.States.Where(s => s.Initial).Select(s => s.Name));
        while (queue.TryDequeue(out var current))
        {
            if (!reached.Add(current))
                continue;
            foreach (var t in blueprint.Transitions.Where(t => t.From.Contains(BlueprintRules.AnyState) || t.From.Contains(current, StringComparer.OrdinalIgnoreCase)))
                queue.Enqueue(t.To);
            if (blueprint.States.FirstOrDefault(s => s.Name.Equals(current, StringComparison.OrdinalIgnoreCase))?.Timeout?.GoTo is { Length: > 0 } next)
                queue.Enqueue(next);
        }
        if (initial == 1)
            foreach (var state in blueprint.States.Where(s => !reached.Contains(s.Name)))
                issues.Add(BlueprintIssue.Warning($"States › {state.Name}", "This state can never be reached."));
        foreach (var state in blueprint.States)
        {
            var leaves = blueprint.Transitions.Any(t => t.From.Contains(BlueprintRules.AnyState) && !t.To.Equals(state.Name, StringComparison.OrdinalIgnoreCase)
                                                        || t.From.Contains(state.Name, StringComparer.OrdinalIgnoreCase))
                         || !string.IsNullOrWhiteSpace(state.Timeout?.GoTo);
            if (!leaves && blueprint.States.Count > 1)
                issues.Add(BlueprintIssue.Warning($"States › {state.Name}", "There is no way out of this state."));
        }

        var entryOnly = blueprint.States.SelectMany(s => s.Entry.Concat(s.Exit).Where(a => !a.Tag.Contains("CMD.", StringComparison.OrdinalIgnoreCase)).Select(a => (s.Name, a.Tag))).ToList();
        var runWritten = blueprint.States.SelectMany(s => s.Run.Select(a => a.Tag)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in entryOnly.Select(e => e.Tag).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (runWritten.Contains(tag))
                issues.Add(BlueprintIssue.Warning("States", $"{tag} is written by a run action in one state and by entry or exit actions in another."));
            else if (entryOnly.Count(e => e.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)) == 1)
                issues.Add(BlueprintIssue.Warning("States", $"{tag} is set by one entry or exit action and never set back (stale output)."));
        }
    }

    private static void ValidateTransitions(Blueprint blueprint, Scope scope, Dictionary<string, bool> alarms, List<BlueprintIssue> issues)
    {
        var states = blueprint.States.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in blueprint.Transitions)
        {
            var where = $"Transitions › {t.Name}";
            if (!NamePattern().IsMatch(t.Name) || !names.Add(t.Name))
                issues.Add(BlueprintIssue.Error(where, "Invalid or duplicate transition name."));
            if (t.From.Count == 0)
                issues.Add(BlueprintIssue.Error(where, "Choose at least one 'from' state, or any state."));
            foreach (var from in t.From.Where(f => f != BlueprintRules.AnyState && !states.Contains(f)))
                issues.Add(BlueprintIssue.Error(where, $"State '{from}' does not exist."));
            if (!states.Contains(t.To))
                issues.Add(BlueprintIssue.Error(where, $"Target state '{t.To}' does not exist."));
            foreach (var reference in Expression.References(t.Guard))
                if (AlarmReference().Match(reference) is { Success: true } match && alarms.TryGetValue(match.Groups[1].Value, out var plcReactive) && !plcReactive)
                    issues.Add(BlueprintIssue.Error(where, $"Alarm {match.Groups[1].Value} is not PLC reactive (SCADA evaluates it), so it cannot change this object's state."));
            Check(scope, t.Guard, where, ValueType.Bool, issues);
        }
    }

    private static void CheckAction(Scope scope, Dictionary<string, ValueType> symbols, BlueprintAction action, string where, List<BlueprintIssue> issues)
    {
        if (action.Tag.Contains('[') || !symbols.TryGetValue(action.Tag, out var type))
        {
            issues.Add(BlueprintIssue.Error(where, $"Tag '{action.Tag}' does not exist (write the target without brackets, for example OUT.coil_on)."));
            return;
        }
        var group = action.Tag.Split('.')[^2];
        if (action.Tag.Split('.').Length > 2 && group != "CMD")
            issues.Add(BlueprintIssue.Error(where, "A Unit may only write its members' commands."));
        else if (action.Tag.Split('.').Length == 2 && !WritableByLogic.Contains(group))
            issues.Add(BlueprintIssue.Error(where, $"{group} tags are not written by the logic."));
        else if (action.Tag.Equals("STS.state", StringComparison.OrdinalIgnoreCase) || action.Tag.Equals("STS.enabled", StringComparison.OrdinalIgnoreCase))
            issues.Add(BlueprintIssue.Error(where, $"{action.Tag} is written by the Builder, not by actions."));
        Check(scope, action.Value, where, type, issues);
    }

    private static void Check(Scope scope, string source, string where, ValueType expected, List<BlueprintIssue> issues, ExpressionOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            issues.Add(BlueprintIssue.Error(where, "The expression is empty."));
            return;
        }
        try
        {
            Expression.Compile(source, scope, options ?? ExpressionOptions.Logic, expected);
        }
        catch (ExpressionException ex)
        {
            issues.Add(BlueprintIssue.Error(where, ex.Message));
        }
    }

    /// <summary>The names usable in a state comparison: the categories (whole range) and the blueprint's states (exact code).</summary>
    public static IReadOnlyList<StateDefinition> StatesOf(Blueprint blueprint)
    {
        BlueprintRules.AssignCodes(blueprint);
        return StateNames.For(blueprint.States.Select(s => s.ToDefinition()));
    }

    private static ValueType TypeOf(string dataType) => dataType == "Bool" ? ValueType.Bool : ValueType.Number;

    private sealed class Scope(Dictionary<string, ValueType> symbols, Dictionary<string, IReadOnlyList<StateDefinition>> states, Dictionary<string, string> aliases,
        Dictionary<string, Dictionary<string, string>> memberAliases) : ISymbolScope
    {
        private readonly Dictionary<string, int> _slots = new(StringComparer.OrdinalIgnoreCase);

        public TagSymbol? ResolveTag(string reference, bool bracketed)
        {
            if (!bracketed || !symbols.TryGetValue(reference, out var type))
                return null;
            if (!_slots.TryGetValue(reference, out var slot))
                _slots[reference] = slot = _slots.Count;
            return new TagSymbol(slot, type, reference, states.GetValueOrDefault(reference));
        }

        public AliasSymbol? ResolveAlias(string reference, bool bracketed)
        {
            if (!bracketed)
                return null;
            var dot = reference.LastIndexOf('.');
            var owner = dot < 0 ? "" : reference[..(dot + 1)];
            var key = owner + "STS.state";
            if (!states.TryGetValue(key, out var list) || ResolveTag(key, true) is not { } tag)
                return null;
            var ranges = Ranges(reference[(dot + 1)..], list, owner.Length == 0 ? aliases : memberAliases.GetValueOrDefault(owner) ?? []);
            return ranges is null ? null : new AliasSymbol(tag, ranges);
        }

        private static IReadOnlyList<(int, int)>? Ranges(string alias, IReadOnlyList<StateDefinition> list, Dictionary<string, string> aliases)
        {
            if (UniversalStates.StandardAliases.TryGetValue(alias, out var codes))
                return codes.Select(UniversalStates.Range).ToList();
            if (!aliases.TryGetValue(alias, out var target))
                return null;
            if (UniversalStates.StandardAliases.TryGetValue(target, out var targetCodes))
                return targetCodes.Select(UniversalStates.Range).ToList();
            var state = list.FirstOrDefault(s => string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase));
            return state is null ? null : [state.Range];
        }
    }
}
