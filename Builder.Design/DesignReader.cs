using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Blueprints;

namespace Builder.Design;

/// <summary>Reads a project (and the blueprints it uses) as a design: names and paths only, defaults and empty fields left out.</summary>
public static class DesignReader
{
    /// <summary>The whole project, or the subtree at <paramref name="path"/> wrapped in its ancestors (so the paths stay valid).</summary>
    public static DesignDocument Read(Project project, string projectName, IReadOnlyCollection<Blueprint> blueprints, string? path = null)
    {
        var byId = blueprints.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        var library = Library(blueprints);
        List<DesignObject> objects;
        IEnumerable<ProjectObject> scope;
        if (string.IsNullOrWhiteSpace(path))
        {
            objects = Children(project, library, byId, null, null);
            scope = project.Objects;
        }
        else
        {
            var target = FindByPath(project, path.Trim()) ?? throw new DesignException($"{path} does not exist in the project.");
            var node = Node(project, library, byId, target, Inherited(project, target));
            for (var current = target; current.ParentId is { } parentId; current = project.Get(parentId))
            {
                var parent = project.Get(parentId);
                var wrapper = DesignObject.Named(KindOf(parent), parent.Name);
                if (parent is UnitInstance unit && unit.RoleMembers.FirstOrDefault(m => m.Value == current.Id).Key is { } role)
                    wrapper.Members = new Dictionary<string, DesignObject> { [role] = node };
                else
                    wrapper.Children = [node];
                node = wrapper;
            }
            objects = [node];
            scope = project.Descendants(target.Id);
        }
        var instances = scope.Where(o => o is ControlModule or UnitInstance).ToList();
        var used = new HashSet<Guid>();
        void Use(Guid id)
        {
            if (!used.Add(id) || !byId.TryGetValue(id, out var blueprint))
                return;
            foreach (var role in blueprint.Roles)
                Use(role.BlueprintId);
        }
        foreach (var instance in instances)
            Use(InstanceFactory.BlueprintIdOf(instance));
        var devices = string.IsNullOrWhiteSpace(path)
            ? project.Topology.Devices
            : project.Topology.Devices.Where(d => instances.OfType<ControlModule>().Any(c => c.ExecutionDeviceId == d.Id)).ToList();
        return new DesignDocument
        {
            Project = projectName,
            Devices = devices.Count == 0 ? null : devices.Select(ToDesign).ToList(),
            Blueprints = Blueprints(blueprints.Where(b => used.Contains(b.Id)), byId),
            Objects = objects.Count == 0 ? null : objects
        };
    }

    /// <summary>Library blueprints as a design (all, or the one named).</summary>
    public static DesignDocument ReadBlueprints(IReadOnlyCollection<Blueprint> blueprints, string? name = null)
    {
        var byId = blueprints.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        var selected = name is null ? blueprints : blueprints.Where(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (name is not null && !selected.Any())
            throw new DesignException($"Blueprint '{name}' does not exist.");
        return new DesignDocument { Blueprints = Blueprints(selected, byId) };
    }

    /// <summary>A hash of the project and the library as designs: changes when anything a proposal can touch changes.</summary>
    public static string Revision(Project project, IReadOnlyCollection<Blueprint> blueprints)
    {
        var design = Read(project, "", blueprints);
        design.Blueprints = Blueprints(blueprints, blueprints.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First()));
        var text = JsonSerializer.Serialize(design, DesignDocument.Json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    public static DesignDevice ToDesign(Device device) => new()
    {
        Name = device.Name,
        Role = device.Role.ToString(),
        Description = BlueprintDesign.Text(device.Description)
    };

    private static List<DesignBlueprint>? Blueprints(IEnumerable<Blueprint> blueprints, Dictionary<Guid, Blueprint> byId)
    {
        var list = blueprints.OrderBy(b => b.Kind).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .Select(b => BlueprintDesign.ToDesign(b, id => byId.GetValueOrDefault(id)?.Name)).ToList();
        return list.Count == 0 ? null : list;
    }

    internal static CmLibrary Library(IEnumerable<Blueprint> blueprints)
    {
        var all = blueprints.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        var library = new CmLibrary();
        foreach (var blueprint in all.Values)
            if (!BlueprintValidator.Validate(blueprint.Clone(), id => all.GetValueOrDefault(id)).Any(i => i.Severity == "Error"))
                library.Replace(BlueprintTypes.ToCmType(blueprint.Clone()));
        return library;
    }

    public static ProjectObject? FindByPath(Project project, string path) =>
        project.Objects.FirstOrDefault(o => o is not Tag && string.Equals(project.GetPath(o.Id), path, StringComparison.OrdinalIgnoreCase));

    internal static DesignObjectKind KindOf(ProjectObject obj) => obj switch
    {
        Folder => DesignObjectKind.Folder,
        UnitInstance { IsEquipmentModule: true } => DesignObjectKind.Em,
        UnitInstance => DesignObjectKind.Unit,
        _ => DesignObjectKind.Cm
    };

    private static List<DesignObject> Children(Project project, CmLibrary library, Dictionary<Guid, Blueprint> byId, Guid? parentId, Guid? inherited) =>
        project.GetChildren(parentId).Where(c => c is not Tag)
            .OrderBy(c => c.Kind switch { ObjectKind.Folder => 0, ObjectKind.Unit => 1, _ => 2 })
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => Node(project, library, byId, c, inherited)).ToList();

    /// <summary>The device a member inherits from its EM / Unit (the container's uniform device).</summary>
    private static Guid? Inherited(Project project, ProjectObject obj)
    {
        Guid? device = null;
        for (var parent = obj.ParentId is { } p ? project.Find(p) : null; parent is UnitInstance; parent = parent.ParentId is { } q ? project.Find(q) : null)
            device ??= UniformDevice(project, parent);
        return device;
    }

    /// <summary>The device all CMs below a container run on, when they all run on the same one.</summary>
    internal static Guid? UniformDevice(Project project, ProjectObject container)
    {
        var devices = project.Descendants(container.Id).OfType<ControlModule>().Select(c => c.ExecutionDeviceId).Distinct().ToList();
        return devices is [{ } only] ? only : null;
    }

    private static DesignObject Node(Project project, CmLibrary library, Dictionary<Guid, Blueprint> byId, ProjectObject obj, Guid? inherited)
    {
        var node = DesignObject.Named(KindOf(obj), obj.Name);
        node.Description = BlueprintDesign.Text(Project.DescriptionOf(obj));
        if (obj is Folder)
        {
            var children = Children(project, library, byId, obj.Id, null);
            node.Children = children.Count == 0 ? null : children;
            return node;
        }
        var blueprintId = InstanceFactory.BlueprintIdOf(obj);
        var isMember = project.RoleOf(obj.Id) is not null;
        if (!isMember)
            node.Blueprint = byId.GetValueOrDefault(blueprintId)?.Name ?? blueprintId.ToString();
        Guid? device = obj switch
        {
            ControlModule cm => cm.ExecutionDeviceId,
            _ => UniformDevice(project, obj)
        };
        if (device is { } d && device != inherited)
            node.Device = project.Topology.Device(d)?.Name;
        node.Values = Values(project, library, obj);
        if (obj is ControlModule control && control.AlarmPriorities.Count > 0)
            node.AlarmPriorities = control.AlarmPriorities.OrderBy(a => a.Key, StringComparer.Ordinal).ToDictionary(a => a.Key, a => a.Value);
        node.CommandInputs = CommandInputs(library, obj);
        var interlocks = InterlockSources.OwnRules(obj).Select(r => Interlock(project, obj, r)).ToList();
        node.Interlocks = interlocks.Count == 0 ? null : interlocks;
        if (obj is UnitInstance unit)
        {
            var below = device ?? inherited;
            var members = new Dictionary<string, DesignObject>(StringComparer.Ordinal);
            foreach (var (role, id) in unit.RoleMembers.OrderBy(m => m.Key, StringComparer.Ordinal))
                if (project.Find(id) is { } member)
                    members[role] = Node(project, library, byId, member, below);
            node.Members = members.Count == 0 ? null : members;
            var others = project.GetChildren(unit.Id).Where(c => c is not Tag && !unit.RoleMembers.Values.Contains(c.Id))
                .Select(c => Node(project, library, byId, c, below)).ToList();
            node.Children = others.Count == 0 ? null : others;
        }
        return node;
    }

    public static DesignInterlock Interlock(Project project, ProjectObject owner, InterlockRule rule)
    {
        var line = BlueprintDesign.ToDesign(rule);
        line.Condition = ExpressionReferences.ToDisplay(project, rule.Condition);
        line.Target = rule.TargetId is { } target && target != owner.Id && project.Find(target) is not null
            ? project.GetPath(target)[(project.GetPath(owner.Id).Length + 1)..]
            : null;
        return line;
    }

    /// <summary>The command inputs of an instance when they differ from its blueprint's defaults (the generated Unit / EM row left out).</summary>
    internal static DesignCommandInputs? CommandInputs(CmLibrary library, ProjectObject obj)
    {
        var own = WithoutUnitRow(CommandInputBehaviour.InputsOf(obj));
        var defaults = WithoutUnitRow(library.Find(InstanceFactory.BlueprintIdOf(obj))?.DefaultCommandInputs);
        var ownJson = DesignDocument.ToJson(CommandInputDesign.ToDesign(own));
        var defaultJson = DesignDocument.ToJson(CommandInputDesign.ToDesign(defaults));
        if (JsonNode.DeepEquals(ownJson, defaultJson))
            return null;
        return CommandInputDesign.ToDesign(own) ?? new DesignCommandInputs();
    }

    internal static CommandInputConfig? WithoutUnitRow(CommandInputConfig? config) =>
        config is null ? null : config with { Rows = config.Rows.Where(r => r.Source != CommandSource.Unit).ToList() };

    /// <summary>The initial values of PAR and SET tags that differ from what the blueprint (and the command inputs) give.</summary>
    internal static Dictionary<string, string>? Values(Project project, CmLibrary library, ProjectObject obj)
    {
        var defaults = TagValues.Defaults(library, obj);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in project.GetChildren(obj.Id).OfType<Tag>().Where(t => t.Group is TagGroup.Par or TagGroup.Set))
        {
            var key = $"{tag.Group.Code()}.{tag.Name}";
            if (!defaults.TryGetValue(key, out var initial))
                continue;
            var actual = TagValues.Format(tag.InitialValue);
            if (actual != TagValues.Format(initial))
                result[key] = actual;
        }
        return result.Count == 0 ? null : new Dictionary<string, string>(result, StringComparer.Ordinal);
    }
}

/// <summary>Initial values of tags as the design writes them: TRUE / FALSE, numbers in invariant culture, text as it is.</summary>
public static class TagValues
{
    public static string Format(JsonNode? value)
    {
        if (value is null)
            return "";
        return value.GetValueKind() switch
        {
            JsonValueKind.True => "TRUE",
            JsonValueKind.False => "FALSE",
            JsonValueKind.Number => double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
            JsonValueKind.String => value.GetValue<string>(),
            _ => value.ToJsonString()
        };
    }

    public static JsonNode? Parse(string text, TagDataType type, string where)
    {
        var trimmed = text.Trim();
        switch (type)
        {
            case TagDataType.Bool:
                if (trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || trimmed == "1")
                    return JsonValue.Create(true);
                if (trimmed.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || trimmed == "0")
                    return JsonValue.Create(false);
                throw new DesignException($"{where}: '{text}' is not TRUE or FALSE.");
            case TagDataType.Int16 or TagDataType.Int32:
                return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)
                    ? JsonValue.Create(whole)
                    : throw new DesignException($"{where}: '{text}' is not a whole number.");
            case TagDataType.Real or TagDataType.LReal:
                return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? JsonValue.Create(number)
                    : throw new DesignException($"{where}: '{text}' is not a number.");
            default:
                return JsonValue.Create(text);
        }
    }

    /// <summary>Normalises a value text for comparison (TRUE, 5 and 5.0 the same way).</summary>
    public static string Normalize(string text, TagDataType? type)
    {
        if (type is null)
            return text.Trim();
        try
        {
            return Format(Parse(text, type.Value, ""));
        }
        catch (DesignException)
        {
            return text.Trim();
        }
    }

    /// <summary>The initial value each PAR / SET tag of an instance gets from its blueprint and its command inputs, with its type.</summary>
    public static Dictionary<string, JsonNode?> Defaults(CmLibrary library, ProjectObject obj) =>
        Templates(library, InstanceFactory.BlueprintIdOf(obj), CommandInputBehaviour.InputsOf(obj)).ToDictionary(t => t.Key, t => t.Value.Initial, StringComparer.Ordinal);

    public static Dictionary<string, (TagDataType Type, JsonNode? Initial)> Templates(CmLibrary library, Guid blueprintId, CommandInputConfig? inputs)
    {
        var result = new Dictionary<string, (TagDataType, JsonNode?)>(StringComparer.Ordinal);
        if (library.Find(blueprintId) is { } type)
            foreach (var template in type.ExpandTags().Where(t => t.Group is TagGroup.Par or TagGroup.Set))
                result[template.Key] = (template.DataType, template.InitialValue);
        foreach (var tag in CommandInputBehaviour.Tags(inputs ?? CommandInputConfig.Empty).Where(t => t.Group is TagGroup.Par or TagGroup.Set))
            result[$"{tag.Group.Code()}.{tag.Name}"] = (tag.DataType, tag.InitialValue);
        return result;
    }
}
