using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using ApolloIQ.Core.Expressions;
using Builder.Logic.Runtime;

namespace Builder.Simulator.Scenarios;

public sealed class ScenarioRunner(CmLibrary library, double cycleSeconds = LogicProgram.DefaultCycleSeconds)
{
    public const string Folder = "SIM";

    public ScenarioReport RunAll(IEnumerable<ScenarioFile> files, IReadOnlyList<ScenarioError>? loadErrors = null)
    {
        var results = files.SelectMany(file => file.Scenarios.Select(s => Run(file, s))).ToList();
        return new ScenarioReport(results, Coverage(results), loadErrors ?? []);
    }

    public ScenarioResult Run(ScenarioFile file, Scenario scenario)
    {
        var failures = new List<ScenarioFailure>();
        var hits = new List<TransitionHit>();
        var trace = new List<TraceEntry>();

        var project = new Project();
        var folder = project.AddFolder(Folder);
        var created = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in scenario.Instances)
        {
            try
            {
                var parent = instance.Parent is { } p
                    ? created.TryGetValue(p, out var pid) ? pid : throw new ProjectException(ProjectErrors.NotFound, $"Parent '{p}' must be listed before its members.")
                    : folder.Id;
                var type = library.FindByName(instance.Type)
                           ?? throw new ProjectException(ProjectErrors.NotFound, $"Blueprint '{instance.Type}' does not exist or has errors.");
                created[instance.Name] = type.IsUnit
                    ? InstanceFactory.CreateUnit(project, library, type.Id, instance.Name, parent).Id
                    : InstanceFactory.Create(project, library, type.Id, instance.Name, parent).Id;
            }
            catch (ProjectException ex)
            {
                failures.Add(new ScenarioFailure(0, "instances", $"{instance.Name}: {ex.Message}"));
            }
        }
        foreach (var instance in scenario.Instances.Where(i => i.Parent is not null && created.ContainsKey(i.Name)))
        {
            try
            {
                InstanceFactory.Join(project, library, created[instance.Name], instance.Role);
            }
            catch (ProjectException ex)
            {
                failures.Add(new ScenarioFailure(0, "instances", $"{instance.Name}: {ex.Message}"));
            }
        }
        string InstancePath(string name) => created.TryGetValue(name, out var id) ? project.GetPath(id) : $"{Folder}.{name}";
        string PathOf(string reference)
        {
            var dot = reference.IndexOf('.');
            return dot < 0 ? InstancePath(reference) : $"{InstancePath(reference[..dot])}{reference[dot..]}";
        }
        foreach (var instance in scenario.Instances.Where(i => i.CommandInputs is not null))
        {
            try
            {
                var cm = project.Get(created[instance.Name]);
                CommandInputBehaviour.Configure(project, library, cm.Id, ReadInputs(instance.CommandInputs!.Value));
            }
            catch (Exception ex) when (ex is ProjectException or System.Text.Json.JsonException or InvalidOperationException)
            {
                failures.Add(new ScenarioFailure(0, "instances", $"{instance.Name}: {ex.Message}"));
            }
        }
        var registry = new TagRegistry(project);
        foreach (var instance in scenario.Instances.Where(i => i.Wires is { Count: > 0 }))
        {
            try
            {
                var cm = project.Objects.OfType<ControlModule>().Single(c => c.Name == instance.Name);
                project.SetCommandWires(cm.Id, instance.Wires!.Select(w => new CommandWire(
                    registry.FindByPath(PathOf(w.Source))?.Id ?? throw new ProjectException(ProjectErrors.NotFound, $"Unknown wire source '{w.Source}'."),
                    Enum.TryParse<WireMode>(w.Mode, true, out var mode) && Enum.IsDefined(mode)
                        ? mode
                        : throw new ProjectException(ProjectErrors.InvalidWire, $"Unknown wire mode '{w.Mode}'. Use On, Off, Toggle, Maintained or Direct."),
                    w.Command)));
            }
            catch (ProjectException ex)
            {
                failures.Add(new ScenarioFailure(0, "instances", $"{instance.Name}: {ex.Message}"));
            }
        }
        if (failures.Count > 0)
            return new ScenarioResult(file.FileName, file.Type, scenario.Name, false, 0, failures, [], hits, trace);

        var session = new SimulationSession(Guid.NewGuid(), project, library, cycleSeconds);
        var logicErrors = session.Errors.Select(e => e.ToString()).ToList();
        var types = session.Programs.ToDictionary(p => p.Path, p => p.Type.Name);
        var outputs = session.Tags.Where(t => t.Group is TagGroup.Out or TagGroup.Sts).ToList();
        var lastOutputs = new Dictionary<string, object?>();
        session.StateChanged += change =>
            hits.Add(new TransitionHit(change.ControlModule, types[change.ControlModule], change.TransitionIndex, change.Transition, change.From, change.To));

        var mainPath = InstancePath(scenario.Instances[0].Name);
        string Resolve(string reference)
        {
            var first = reference.Split('.')[0];
            return created.ContainsKey(first) ? PathOf(reference) : $"{mainPath}.{reference}";
        }

        Func<Value> Condition(string text)
        {
            var colon = text.IndexOf(':');
            if (colon > 0 && created.ContainsKey(text[..colon].Trim()))
                return session.Condition(InstancePath(text[..colon].Trim()), text[(colon + 1)..]);
            return session.Condition(mainPath, text);
        }

        void Cycle()
        {
            session.Step();
            var changes = new Dictionary<string, object?>();
            foreach (var tag in outputs)
            {
                var value = session.Read(tag.Path);
                var shown = value.Good ? value.Value : "bad";
                if (lastOutputs.TryGetValue(tag.Path, out var previous) && Equals(previous, shown))
                    continue;
                lastOutputs[tag.Path] = shown;
                changes[tag.Path[(Folder.Length + 1)..]] = shown;
            }
            var cycle = session.Snapshot().Cycle;
            trace.Add(new TraceEntry(cycle, session.ControlModules().ToDictionary(c => c.Path[(Folder.Length + 1)..], c => c.State), changes));
        }

        foreach (var step in scenario.Steps)
        {
            var cycle = session.Snapshot().Cycle;
            try
            {
                switch (step)
                {
                    case SetStep set:
                        foreach (var (tag, value) in set.Values)
                        {
                            if (set.Force)
                                session.Force(Resolve(tag), value);
                            else
                                session.Write(Resolve(tag), value);
                        }
                        break;
                    case ReleaseStep release:
                        foreach (var tag in release.Tags)
                            session.Unforce(Resolve(tag));
                        break;
                    case QualityStep quality:
                        foreach (var tag in quality.Tags)
                            session.SetBadQuality(Resolve(tag), quality.Bad);
                        break;
                    case RunStep run:
                        for (var i = run.Duration.ToCycles(cycleSeconds); i > 0; i--)
                            Cycle();
                        break;
                    case UntilStep until:
                    {
                        var condition = Condition(until.Condition);
                        var max = until.Within.ToCycles(cycleSeconds);
                        var done = 0;
                        while (!condition().IsTrue && done < max)
                        {
                            Cycle();
                            done++;
                        }
                        if (!condition().IsTrue)
                            failures.Add(new ScenarioFailure(session.Snapshot().Cycle, step.Location,
                                $"'{until.Condition}' did not become true within {max} cycles ({max * cycleSeconds:0.###} s). {Describe(session)}"));
                        break;
                    }
                    case ExpectStep expect:
                        foreach (var text in expect.Conditions)
                        {
                            var value = Condition(text)();
                            if (!value.IsTrue)
                                failures.Add(new ScenarioFailure(cycle, step.Location,
                                    $"Expected '{text}', but it is {(value.Good ? "false" : "bad quality")}. {Describe(session)}"));
                        }
                        break;
                }
            }
            catch (SimulationException ex)
            {
                failures.Add(new ScenarioFailure(cycle, step.Location, ex.Message));
            }
            if (failures.Count > 0)
                break;
        }

        if (logicErrors.Count > 0)
            failures.Add(new ScenarioFailure(0, "logic", $"The logic has {logicErrors.Count} errors."));
        return new ScenarioResult(file.FileName, file.Type, scenario.Name, failures.Count == 0, session.Snapshot().Cycle,
            failures, logicErrors, hits, trace);
    }

    public IReadOnlyList<TypeCoverage> Coverage(IEnumerable<ScenarioResult> results)
    {
        var hits = results.SelectMany(r => r.Transitions).ToList();
        var coverage = new List<TypeCoverage>();
        foreach (var typeName in hits.Select(h => h.Type).Concat(results.Select(r => r.Type)).Distinct().Order(StringComparer.Ordinal))
        {
            var type = library.FindByName(typeName);
            if (type is null)
                continue;
            var project = new Project();
            var root = Instantiate(project, type, "CM", project.AddFolder(Folder).Id);
            var program = LogicProgram.Build(project, library, cycleSeconds).Programs.Single(p => p.Id == root);
            var transitions = program.Transitions
                .OrderBy(t => t.Order)
                .Select(t => new TransitionCoverage(t.Order, t.Name,
                    string.Join(", ", t.From.Select(r => Name(program, r))), program.StateName(t.To),
                    hits.Count(h => h.Type == typeName && h.Index == t.Order)))
                .ToList();
            coverage.Add(new TypeCoverage(typeName, transitions.Count(t => t.Hits > 0), transitions.Count, transitions));
        }
        return coverage;
    }

    private Guid Instantiate(Project project, CmType type, string name, Guid parentId)
    {
        if (!type.IsUnit)
            return InstanceFactory.Create(project, library, type.Id, name, parentId).Id;
        var unit = InstanceFactory.CreateUnit(project, library, type.Id, name, parentId);
        foreach (var (role, memberType) in type.Roles)
        {
            if (library.Find(memberType) is not { } member)
                continue;
            var id = Instantiate(project, member, role, unit.Id);
            InstanceFactory.Join(project, library, id, role);
        }
        return unit.Id;
    }

    private static readonly System.Text.Json.JsonSerializerOptions InputJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static CommandInputConfig ReadInputs(System.Text.Json.JsonElement element) =>
        System.Text.Json.JsonSerializer.Deserialize<CommandInputConfig>(element, InputJson) ?? throw new System.Text.Json.JsonException("A command input configuration is required.");

    private static string Name(CmProgram program, (int From, int To) range) =>
        range.From == int.MinValue ? "*" : program.StateName(range.From);

    private static string Describe(SimulationSession session) =>
        "States: " + string.Join(", ", session.ControlModules().Select(c => $"{c.Path[(Folder.Length + 1)..]} = {c.State} {c.StateName}")) + ".";
}
