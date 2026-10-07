using System.Text.Json.Nodes;
using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Logic.Blueprints;

namespace Builder.Design;

public static class ChangeKinds
{
    public const string Create = "Create";
    public const string Update = "Update";
    public const string Delete = "Delete";
    public const string Rename = "Rename";
    public const string Move = "Move";
}

public static class ChangeAreas
{
    public const string Device = "Device";
    public const string Blueprint = "Blueprint";
    public const string Object = "Object";
    public const string Values = "Values";
    public const string AlarmPriorities = "AlarmPriorities";
    public const string CommandInputs = "CommandInputs";
    public const string Interlock = "Interlock";
}

/// <summary>
/// One change a design fragment makes to the project, matched by name / path. <see cref="Before"/> and <see cref="After"/> are
/// design fragments (for the diff); <see cref="DependsOn"/> lists the items it needs (by <see cref="Id"/>, stable within a proposal).
/// </summary>
public sealed class ChangeItem
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Area { get; init; } = "";
    public string Target { get; init; } = "";

    /// <summary>What the item is grouped under in the review: "Blueprint Pump", "Device MainPlc" or an object path.</summary>
    public string Group { get; init; } = "";

    public string? Section { get; init; }
    public string Summary { get; init; } = "";
    public JsonNode? Before { get; init; }
    public JsonNode? After { get; init; }
    public List<string> DependsOn { get; init; } = [];

    /// <summary>Problems found while reading the fragment; the item cannot be accepted until the fragment is fixed.</summary>
    public List<string> Problems { get; init; } = [];

    internal int Phase { get; init; }
    internal int Order { get; init; }
    internal Action<ApplyContext>? Apply { get; init; }
}

/// <summary>The change items of a design fragment against a project, plus what is not a change: problems, warnings, questions.</summary>
public sealed class DesignPlan
{
    public List<ChangeItem> Items { get; } = [];
    public List<string> Problems { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<DesignQuestion> Questions { get; init; } = [];

    public ChangeItem? Find(string id) => Items.FirstOrDefault(i => i.Id == id);
}

/// <summary>The working state while a selection of items is applied to a copy of the project and the blueprints.</summary>
public sealed class ApplyContext
{
    private CmLibrary? _library;
    private readonly Dictionary<string, Action> _finalizers = new(StringComparer.Ordinal);

    public ApplyContext(Project project, IEnumerable<Blueprint> blueprints, IEnumerable<string> selected)
    {
        Project = project;
        Blueprints = blueprints.Select(b => b.Clone()).GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        Selected = new HashSet<string>(selected, StringComparer.Ordinal);
    }

    public Project Project { get; }

    public Dictionary<Guid, Blueprint> Blueprints { get; }

    public HashSet<Guid> ChangedBlueprints { get; } = [];

    public HashSet<Guid> DeletedBlueprints { get; } = [];

    public HashSet<string> Selected { get; }

    /// <summary>Objects created by this apply, by their (new) path.</summary>
    public Dictionary<string, Guid> Created { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The published blueprints of the working set (those without errors) as runtime types.</summary>
    public CmLibrary Library => _library ??= DesignReader.Library(Blueprints.Values);

    public Blueprint? FindBlueprint(string name) =>
        Blueprints.Values.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

    public Guid? IdOf(string name) => FindBlueprint(name)?.Id;

    public void Changed(Blueprint blueprint)
    {
        Blueprints[blueprint.Id] = blueprint;
        ChangedBlueprints.Add(blueprint.Id);
        _library = null;
    }

    public void Removed(Guid id)
    {
        Blueprints.Remove(id);
        ChangedBlueprints.Remove(id);
        DeletedBlueprints.Add(id);
        _library = null;
    }

    public Guid DeviceId(string name) =>
        Project.Topology.Devices.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))?.Id
        ?? throw new DesignException($"Device '{name}' does not exist.");

    /// <summary>Runs once after the items that edit lists (interlock lines) were applied.</summary>
    public void OnFinish(string key, Action action) => _finalizers.TryAdd(key, action);

    internal IReadOnlyList<Action> TakeFinalizers()
    {
        var list = _finalizers.Values.ToList();
        _finalizers.Clear();
        return list;
    }
}
