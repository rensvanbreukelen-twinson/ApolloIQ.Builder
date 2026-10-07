using System.Globalization;
using System.Text.RegularExpressions;
using Builder.Core.Types;
using Builder.Logic.Expressions;
using ValueType = Builder.Logic.Expressions.ValueType;

namespace Builder.Logic.Blueprints;

public sealed record BlueprintIssue(string Severity, string Where, string Message)
{
    public static BlueprintIssue Error(string where, string message) => new("Error", where, message);

    public static BlueprintIssue Warning(string where, string message) => new("Warning", where, message);
}

public static partial class BlueprintValidator
{
    private static readonly HashSet<string> WritableByLogic = new(["OUT", "STS", "INT"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"ALM\.([A-Za-z0-9_]+)\.active", RegexOptions.IgnoreCase)]
    private static partial Regex AlarmReference();

    public static IReadOnlyList<BlueprintIssue> Validate(Blueprint blueprint, Func<string, Blueprint?>? lookup = null)
    {
        var issues = new List<BlueprintIssue>();
        var symbols = new Dictionary<string, ValueType>(StringComparer.OrdinalIgnoreCase);

        if (!NamePattern().IsMatch(blueprint.Name))
            issues.Add(BlueprintIssue.Error("General", "The name must start with a letter and contain only letters, digits and _."));
        foreach (var name in blueprint.Interfaces.Where(n => BlueprintCatalog.Interfaces.All(i => i.Name != n)))
            issues.Add(BlueprintIssue.Error("General", $"Unknown interface '{name}'."));

        foreach (var tag in BlueprintCatalog.InterfaceTags(blueprint))
            if (!symbols.TryAdd($"{tag.Group}.{tag.Name}", TypeOf(tag.DataType)))
                issues.Add(BlueprintIssue.Error("General", $"Interface tag {tag.Group}.{tag.Name} exists twice."));

        for (var i = 0; i < blueprint.Tags.Count; i++)
        {
            var tag = blueprint.Tags[i];
            var where = $"Tags › {tag.Group}.{tag.Name}";
            if (!BlueprintCatalog.Groups.Contains(tag.Group))
                issues.Add(BlueprintIssue.Error(where, $"Unknown group '{tag.Group}'."));
            if (!NamePattern().IsMatch(tag.Name))
                issues.Add(BlueprintIssue.Error(where, "Invalid tag name."));
            if (!BlueprintCatalog.DataTypes.Contains(tag.DataType))
                issues.Add(BlueprintIssue.Error(where, $"Unknown data type '{tag.DataType}'."));
            if (tag.Group == "FIN" && tag.Source is null)
                issues.Add(BlueprintIssue.Error(where, "Every input needs a source: local I/O or external (G-122)."));
            if (tag.Min is { } min && tag.Max is { } max && min > max)
                issues.Add(BlueprintIssue.Error(where, "Min is larger than max."));
            if (!symbols.TryAdd($"{tag.Group}.{tag.Name}", TypeOf(tag.DataType)))
                issues.Add(BlueprintIssue.Error(where, "This tag exists twice, or clashes with an interface tag."));
        }
        if (blueprint.Tags.Count(t => t.Primary) > 1)
            issues.Add(BlueprintIssue.Error("Tags", "Only one tag can be the primary value."));

        if (blueprint.Kind != BlueprintKind.CM)
            AddRoles(blueprint, lookup, symbols, issues);
        else if (blueprint.Roles.Count > 0)
            issues.Add(BlueprintIssue.Error("Roles", "Only a Unit or an Equipment module has member roles."));

        var alarms = new Dictionary<string, AlarmReaction>(StringComparer.OrdinalIgnoreCase);
        foreach (var alarm in blueprint.Alarms)
        {
            if (!NamePattern().IsMatch(alarm.Name))
                issues.Add(BlueprintIssue.Error($"Alarms › {alarm.Name}", "Invalid alarm name."));
            else if (!alarms.TryAdd(alarm.Name, alarm.Kind))
                issues.Add(BlueprintIssue.Error($"Alarms › {alarm.Name}", "This alarm exists twice."));
        }
        foreach (var state in blueprint.States)
            if (state.Timeout?.Alarm is { Length: > 0 } name && !alarms.TryAdd(name, state.Timeout.AlarmKind))
                issues.Add(BlueprintIssue.Error($"States › {state.Name} › timeout", $"Alarm '{name}' already exists."));
        var interlocks = Builder.Core.Model.InterlockRule.WithAlarmNames(blueprint.Interlocks);
        for (var i = 0; i < interlocks.Count; i++)
            if (interlocks[i].Kind == Builder.Core.Model.InterlockKind.Trip && !alarms.TryAdd(interlocks[i].Alarm!, AlarmReaction.NonReactive))
                issues.Add(BlueprintIssue.Error($"Interlocks › line {i + 1}", $"Alarm '{interlocks[i].Alarm}' already exists."));
        foreach (var name in alarms.Keys)
            symbols[$"ALM.{name}.active"] = ValueType.Bool;
        foreach (var state in blueprint.States.Where(s => s.Timeout is not null))
            symbols[$"PAR.{state.Name}_timeout"] = ValueType.Number;

        BlueprintCatalog.AssignCodes(blueprint);
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
            foreach (var problem in Builder.Core.Types.CommandInputBehaviour.Problems(inputs, BlueprintTypes.SingleCommands(blueprint), hasPair, fins.Contains))
                issues.Add(BlueprintIssue.Error("Command inputs", problem));
            foreach (var tag in Builder.Core.Types.CommandInputBehaviour.Tags(inputs))
                if (blueprint.Tags.Any(t => t.Group == tag.Group.ToString().ToUpperInvariant() && t.Name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(BlueprintIssue.Error("Command inputs", $"{tag.Group.ToString().ToUpperInvariant()}.{tag.Name} is generated by a command input; remove the tag or rename the input."));
        }
        var scope = new Scope(symbols, stateLists, blueprint.Aliases, memberAliases);
        foreach (var (alias, target) in blueprint.Aliases)
            if (!NamePattern().IsMatch(alias) || !(UniversalStates.StandardAliases.ContainsKey(target) || blueprint.States.Any(s => s.Name.Equals(target, StringComparison.OrdinalIgnoreCase))))
                issues.Add(BlueprintIssue.Error("General", $"Alias '{alias}' must point to a state or a standard alias such as is_running."));
        ValidateStates(blueprint, scope, symbols, issues);
        ValidateTransitions(blueprint, scope, alarms, issues);

        foreach (var alarm in blueprint.Alarms)
        {
            if (!string.IsNullOrWhiteSpace(alarm.OnTransition) && blueprint.Transitions.All(t => !t.Name.Equals(alarm.OnTransition, StringComparison.Ordinal)))
                issues.Add(BlueprintIssue.Error($"Alarms › {alarm.Name}", $"Transition '{alarm.OnTransition}' does not exist."));
            if (alarm.Latched && !blueprint.Interfaces.Contains("Resettable"))
                issues.Add(BlueprintIssue.Error($"Alarms › {alarm.Name}", "A latched alarm needs the Resettable interface (CMD.reset clears it)."));
            if (!string.IsNullOrWhiteSpace(alarm.Condition) || string.IsNullOrWhiteSpace(alarm.OnTransition))
                Check(scope, alarm.Condition, $"Alarms › {alarm.Name}", ValueType.Bool, issues);
            if (!SeverityBands.IsValid(alarm.Severity))
                issues.Add(BlueprintIssue.Error($"Alarms › {alarm.Name}", SeverityBands.RangeText));
        }
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

        if (blueprint.States.Count > 0 && blueprint.States.All(s => !UniversalStates.IsFault(s.Category)) && blueprint.Kind == BlueprintKind.CM && blueprint.Interfaces.Contains("Switchable"))
            issues.Add(BlueprintIssue.Warning("States", "There is no Fault state."));
        return issues;
    }

    private static void AddRoles(Blueprint blueprint, Func<string, Blueprint?>? lookup, Dictionary<string, ValueType> symbols, List<BlueprintIssue> issues)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in blueprint.Roles)
        {
            var where = $"Roles › {role.Name}";
            if (!NamePattern().IsMatch(role.Name) || !names.Add(role.Name))
                issues.Add(BlueprintIssue.Error(where, "Invalid or duplicate role name."));
            if (BlueprintCatalog.Groups.Contains(role.Name.ToUpperInvariant()) || role.Name.Equals("ALM", StringComparison.OrdinalIgnoreCase))
                issues.Add(BlueprintIssue.Error(where, "A role cannot be named after a tag group."));
            var member = lookup?.Invoke(role.Blueprint);
            if (member is null)
            {
                issues.Add(BlueprintIssue.Error(where, $"Blueprint '{role.Blueprint}' does not exist."));
                continue;
            }
            if (member.Kind == BlueprintKind.Unit || (member.Kind == BlueprintKind.EM && blueprint.Kind == BlueprintKind.EM))
                issues.Add(BlueprintIssue.Error(where, blueprint.Kind == BlueprintKind.EM
                    ? $"An Equipment module's members are CMs; '{role.Blueprint}' is a {member.Kind} (G-151)."
                    : $"A Unit's members are Equipment modules and CMs; '{role.Blueprint}' is a Unit."));
            AddMemberSymbols(role.Name, member, lookup, symbols, 0);
        }
        if (blueprint.Roles.Count == 0)
            issues.Add(BlueprintIssue.Warning("Roles", $"{(blueprint.Kind == BlueprintKind.EM ? "An Equipment module" : "A Unit")} without member roles cannot control anything."));
    }

    private static void AddMemberSymbols(string prefix, Blueprint member, Func<string, Blueprint?>? lookup, Dictionary<string, ValueType> symbols, int depth)
    {
        foreach (var tag in BlueprintCatalog.InterfaceTags(member))
            symbols[$"{prefix}.{tag.Group}.{tag.Name}"] = TypeOf(tag.DataType);
        foreach (var tag in member.Tags)
            symbols[$"{prefix}.{tag.Group}.{tag.Name}"] = TypeOf(tag.DataType);
        var trips = Builder.Core.Model.InterlockRule.WithAlarmNames(member.Interlocks).Where(r => r.Kind == Builder.Core.Model.InterlockKind.Trip).Select(r => r.Alarm!);
        foreach (var alarm in member.Alarms.Select(a => a.Name).Concat(member.States.Select(s => s.Timeout?.Alarm).OfType<string>()).Concat(trips))
            symbols[$"{prefix}.ALM.{alarm}.active"] = ValueType.Bool;
        if (depth < 3 && member.Kind != BlueprintKind.CM)
            foreach (var role in member.Roles)
                if (lookup?.Invoke(role.Blueprint) is { } sub)
                    AddMemberSymbols($"{prefix}.{role.Name}", sub, lookup, symbols, depth + 1);
    }

    private static void CollectMembers(Blueprint blueprint, string prefix, Func<string, Blueprint?>? lookup, Dictionary<string, Blueprint> into, int depth)
    {
        foreach (var role in blueprint.Roles)
        {
            if (lookup?.Invoke(role.Blueprint) is not { } member)
                continue;
            var path = prefix.Length == 0 ? role.Name : $"{prefix}.{role.Name}";
            into[path] = member;
            if (depth < 3 && member.Kind != BlueprintKind.CM)
                CollectMembers(member, path, lookup, into, depth + 1);
        }
    }

    private static void ValidateInterlocks(Blueprint blueprint, IReadOnlyList<Builder.Core.Model.InterlockRule> interlocks, Dictionary<string, Blueprint> members,
        Scope scope, List<BlueprintIssue> issues)
    {
        var counts = new Dictionary<(string, Builder.Core.Model.InterlockKind), int>();
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
            if (!targetBlueprint.Interfaces.Contains(BlueprintCatalog.InterlocksInterface))
                issues.Add(BlueprintIssue.Error(where, $"{(target.Length == 0 ? "This blueprint" : $"{target} ({targetBlueprint.Name})")} has no Interlocks interface (LOK.can_on, can_off, trip)."));
            var key = (target, rule.Kind);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (rule.Kind != Builder.Core.Model.InterlockKind.Trip && counts[key] == Builder.Core.Model.InterlockRule.MaxPerKind + 1)
                issues.Add(BlueprintIssue.Error(where, $"At most {Builder.Core.Model.InterlockRule.MaxPerKind} {rule.Kind} interlocks per target."));
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
            if (rule.Kind != Builder.Core.Model.InterlockKind.Trip)
                continue;
            if (!NamePattern().IsMatch(rule.Alarm!))
                issues.Add(BlueprintIssue.Error(where, $"Invalid alarm name '{rule.Alarm}'."));
            if (!SeverityBands.IsValid(rule.Severity))
                issues.Add(BlueprintIssue.Error(where, SeverityBands.RangeText));
            if (target.Length == 0 && blueprint.Kind == BlueprintKind.CM && !blueprint.Interfaces.Contains("Resettable"))
                issues.Add(BlueprintIssue.Warning(where, "Trips latch; without the Resettable interface only a container's reset clears this one."));
            if (rule.Escalate == Builder.Core.Model.TripEscalation.EM && blueprint.Kind == BlueprintKind.Unit && (target.Length == 0 || members.GetValueOrDefault(target.Split('.')[0])?.Kind != BlueprintKind.EM))
                issues.Add(BlueprintIssue.Warning(where, "Escalate to EM: the target is not inside an Equipment module of this Unit; it only works if the project puts it in one."));
            if (rule.Escalate != Builder.Core.Model.TripEscalation.None && blueprint.Kind != BlueprintKind.CM
                && (rule.Escalate == Builder.Core.Model.TripEscalation.EM) == (blueprint.Kind == BlueprintKind.EM)
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
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in blueprint.States)
        {
            var where = $"States › {state.Name}";
            if (!NamePattern().IsMatch(state.Name) || !names.Add(state.Name))
                issues.Add(BlueprintIssue.Error(where, "Invalid or duplicate state name."));
            if (BlueprintCatalog.Categories.All(c => c.Code != state.Category))
                issues.Add(BlueprintIssue.Error(where, "Pick a category."));
            foreach (var (list, kind) in new[] { (state.Entry, "entry"), (state.Run, "run"), (state.Exit, "exit") })
                for (var i = 0; i < list.Count; i++)
                    CheckAction(scope, symbols, list[i], $"{where} › {kind} {i + 1}", issues);
            if (state.Timeout is { } timeout)
            {
                var at = $"{where} › timeout";
                if (!double.TryParse(timeout.Time, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    if (!symbols.ContainsKey(timeout.Time) || !timeout.Time.StartsWith("PAR.", StringComparison.OrdinalIgnoreCase))
                        issues.Add(BlueprintIssue.Error(at, "The time must be a number of seconds or a PAR tag."));
                }
                else if (seconds <= 0)
                    issues.Add(BlueprintIssue.Error(at, "The time must be more than 0 s."));
                if (string.IsNullOrWhiteSpace(timeout.GoTo) && string.IsNullOrWhiteSpace(timeout.Alarm))
                    issues.Add(BlueprintIssue.Error(at, "Choose what happens on overflow: go to a state, raise an alarm, or both."));
                if (!string.IsNullOrWhiteSpace(timeout.GoTo) && blueprint.States.All(s => !s.Name.Equals(timeout.GoTo, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(BlueprintIssue.Error(at, $"State '{timeout.GoTo}' does not exist."));
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
            foreach (var t in blueprint.Transitions.Where(t => t.From.Contains(BlueprintCatalog.AnyState) || t.From.Contains(current, StringComparer.OrdinalIgnoreCase)))
                queue.Enqueue(t.To);
            if (blueprint.States.FirstOrDefault(s => s.Name.Equals(current, StringComparison.OrdinalIgnoreCase))?.Timeout?.GoTo is { Length: > 0 } next)
                queue.Enqueue(next);
        }
        if (initial == 1)
            foreach (var state in blueprint.States.Where(s => !reached.Contains(s.Name)))
                issues.Add(BlueprintIssue.Warning($"States › {state.Name}", "This state can never be reached."));
        foreach (var state in blueprint.States)
        {
            var leaves = blueprint.Transitions.Any(t => t.From.Contains(BlueprintCatalog.AnyState) && !t.To.Equals(state.Name, StringComparison.OrdinalIgnoreCase)
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

    private static void ValidateTransitions(Blueprint blueprint, Scope scope, Dictionary<string, AlarmReaction> alarms, List<BlueprintIssue> issues)
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
            foreach (var from in t.From.Where(f => f != BlueprintCatalog.AnyState && !states.Contains(f)))
                issues.Add(BlueprintIssue.Error(where, $"State '{from}' does not exist."));
            if (!states.Contains(t.To))
                issues.Add(BlueprintIssue.Error(where, $"Target state '{t.To}' does not exist."));
            Check(scope, t.Guard, where, ValueType.Bool, issues);
            foreach (Match match in AlarmReference().Matches(t.Guard))
                if (!t.Guard[..match.Index].EndsWith('.') && alarms.TryGetValue(match.Groups[1].Value, out var kind) && kind == AlarmReaction.NonReactive)
                    issues.Add(BlueprintIssue.Error(where, $"ALM.{match.Groups[1].Value} is non-reactive, so it cannot change this CM's state."));
        }
    }

    private static void CheckAction(Scope scope, Dictionary<string, ValueType> symbols, BlueprintAction action, string where, List<BlueprintIssue> issues)
    {
        if (!symbols.TryGetValue(action.Tag, out var type))
        {
            issues.Add(BlueprintIssue.Error(where, $"Tag '{action.Tag}' does not exist."));
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

    private static void Check(Scope scope, string source, string where, ValueType expected, List<BlueprintIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            issues.Add(BlueprintIssue.Error(where, "The expression is empty."));
            return;
        }
        try
        {
            Expression.Compile(source, scope, ExpressionOptions.Logic, expected);
        }
        catch (ExpressionException ex)
        {
            issues.Add(BlueprintIssue.Error(where, ex.Message));
        }
    }

    private static IReadOnlyList<StateDefinition> StatesOf(Blueprint blueprint)
    {
        BlueprintCatalog.AssignCodes(blueprint);
        return [.. blueprint.States.Select(s => new StateDefinition(s.Code, s.Name)), new StateDefinition(UniversalStates.UnavailableCode, "Unavailable")];
    }

    private static ValueType TypeOf(string dataType) => dataType == "Bool" ? ValueType.Bool : ValueType.Number;

    private sealed class Scope(Dictionary<string, ValueType> symbols, Dictionary<string, IReadOnlyList<StateDefinition>> states, Dictionary<string, string> aliases,
        Dictionary<string, Dictionary<string, string>>? memberAliases = null) : ISymbolScope
    {
        private readonly Dictionary<string, int> _slots = new(StringComparer.OrdinalIgnoreCase);

        public TagSymbol? ResolveTag(string reference, bool bracketed)
        {
            if (!symbols.TryGetValue(reference, out var type))
                return null;
            if (!_slots.TryGetValue(reference, out var slot))
                _slots[reference] = slot = _slots.Count;
            return new TagSymbol(slot, type, reference, states.GetValueOrDefault(reference));
        }

        public AliasSymbol? ResolveAlias(string reference, bool bracketed)
        {
            var dot = reference.LastIndexOf('.');
            var owner = dot < 0 ? "" : reference[..(dot + 1)];
            var key = owner + "STS.state";
            if (!states.TryGetValue(key, out var list) || ResolveTag(key, false) is not { } tag)
                return null;
            var ranges = Ranges(reference[(dot + 1)..], list, owner.Length == 0 ? aliases : memberAliases?.GetValueOrDefault(owner) ?? []);
            return ranges is null ? null : new AliasSymbol(tag with { States = list }, ranges);
        }

        public static IReadOnlyList<(int, int)>? Ranges(string alias, IReadOnlyList<StateDefinition> list, Dictionary<string, string> aliases)
        {
            if (UniversalStates.StandardAliases.TryGetValue(alias, out var codes))
                return codes.Select(UniversalStates.Range).ToList();
            if (!aliases.TryGetValue(alias, out var target))
                target = alias;
            else if (UniversalStates.StandardAliases.TryGetValue(target, out var targetCodes))
                return targetCodes.Select(UniversalStates.Range).ToList();
            var state = list.FirstOrDefault(s => string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase));
            if (state is null || ReferenceEquals(target, alias))
                return null;
            return UniversalStates.IsUniversalCode(state.Code) ? [UniversalStates.Range(state.Code)] : [(state.Code, state.Code + 1)];
        }
    }
}
