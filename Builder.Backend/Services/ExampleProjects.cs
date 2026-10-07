using System.Text.Json;
using Builder.Core.Model;
using Builder.Core.Types;

namespace Builder.Backend.Services;

public sealed record ExampleSummary(string File, string Name, string Description);

public sealed class ExampleFile
{
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Folders { get; set; } = [];
    public List<ExampleCm> ControlModules { get; set; } = [];
    public List<ExampleInterlock> Interlocks { get; set; } = [];
    public List<ExampleUnit> Units { get; set; } = [];
    public List<ExampleLayout> Layout { get; set; } = [];
}

public sealed record ExampleCm(string Folder, string Name, string Blueprint, CommandInputConfig? CommandInputs = null);

/// <summary>A project-level interlock: defined on <c>Owner</c>, acting on <c>Target</c> (empty = the owner). Conditions use paths.</summary>
public sealed record ExampleInterlock(string Owner, string? Target, InterlockKind Kind, string Condition, string? Text = null, string? Alarm = null,
    int? Severity = null, TripEscalation Escalate = TripEscalation.None);

/// <summary><c>Hmi</c> replaces the HMI command input row of the blueprint (for example a reset-only row on an EM the Unit drives).</summary>
public sealed record ExampleUnit(string Folder, string Name, string Blueprint, Dictionary<string, string> Members, CommandInput? Hmi = null);

public sealed record ExampleLayout(string Object, double X, double Y);

public sealed class ExampleProjects(string root)
{
    public const string Schema = "apolloiq.example/1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public IReadOnlyList<ExampleSummary> List() =>
        !Directory.Exists(root) ? [] : Directory.GetFiles(root, "*.example.json").Order(StringComparer.Ordinal)
            .Select(f => (File: Path.GetFileName(f)[..^".example.json".Length], Data: Load(f)))
            .Where(e => e.Data is not null).Select(e => new ExampleSummary(e.File, e.Data!.Name, e.Data.Description)).ToList();

    public ExampleFile Find(string file)
    {
        if (file.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-'))
            throw new ProjectException(ProjectErrors.NotFound, $"Example '{file}' does not exist.");
        return Load(Path.Combine(root, file + ".example.json")) ?? throw new ProjectException(ProjectErrors.NotFound, $"Example '{file}' does not exist.");
    }

    public static void Apply(Project project, CmLibrary library, BlueprintStore blueprints, ExampleFile example)
    {
        Guid? ContainerId(string path) => string.IsNullOrEmpty(path) ? null
            : project.Objects.First(o => o is Folder or UnitInstance && project.GetPath(o.Id) == path).Id;
        Guid ObjectId(string path) => project.Objects.First(o => o is not Core.Tags.Tag && project.GetPath(o.Id) == path).Id;
        foreach (var path in example.Folders)
        {
            var dot = path.LastIndexOf('.');
            project.AddFolder(path[(dot + 1)..], dot < 0 ? null : ContainerId(path[..dot]));
        }
        var units = new List<(UnitInstance Unit, ExampleUnit Data)>();
        foreach (var unit in example.Units)
        {
            var blueprint = blueprints.Find(unit.Blueprint) ?? throw new ProjectException(ProjectErrors.InvalidUnit, $"Blueprint '{unit.Blueprint}' does not exist.");
            units.Add((UnitSupport.Create(project, blueprint, unit.Name, ContainerId(unit.Folder)), unit));
        }
        foreach (var cm in example.ControlModules)
        {
            var created = InstanceFactory.Create(project, library, cm.Blueprint, cm.Name, ContainerId(cm.Folder));
            if (cm.CommandInputs is { } inputs)
                CommandInputBehaviour.Configure(project, library, created.Id, inputs);
        }
        foreach (var (unit, data) in units)
            foreach (var (role, member) in data.Members)
                UnitSupport.SetMember(project, library, unit.Id, role, ObjectId(member));
        foreach (var (unit, data) in units.Where(u => u.Data.Hmi is not null))
        {
            var config = CommandInputBehaviour.InputsOf(unit) ?? CommandInputConfig.Empty;
            var rows = config.Rows.Where(r => r.Source != CommandSource.Hmi).Prepend(data.Hmi!).ToList();
            CommandInputBehaviour.Configure(project, library, unit.Id, config with { Rows = rows });
        }
        foreach (var owner in example.Interlocks.GroupBy(i => i.Owner))
            project.SetInterlocks(ObjectId(owner.Key), owner.Select(i => new InterlockRule
            {
                TargetId = string.IsNullOrEmpty(i.Target) ? null : ObjectId(i.Target),
                Kind = i.Kind,
                Condition = ExpressionReferences.ToStored(project, i.Condition),
                Text = i.Text ?? "",
                Alarm = i.Alarm,
                Severity = i.Severity ?? SeverityBands.DefaultSeverity,
                Escalate = i.Escalate
            }).ToList());
        foreach (var item in example.Layout)
            project.SetPosition(ObjectId(item.Object), item.X, item.Y);
    }

    private static ExampleFile? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var data = JsonSerializer.Deserialize<ExampleFile>(File.ReadAllText(path), Json);
            return data?.Schema == Schema ? data : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
