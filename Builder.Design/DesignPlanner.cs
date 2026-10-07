using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApolloIQ.Core.Blueprints;
using TagDataType = Builder.Core.Tags.TagDataType;
using ApolloIQ.Core.Expressions;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Blueprints;

namespace Builder.Design;

/// <summary>
/// Turns a design fragment into change items against the current project and blueprints, matched by name and path. Planning
/// changes nothing; <see cref="DesignApplier"/> applies a selection of the items to a copy.
/// </summary>
public sealed partial class DesignPlanner
{
    private const int DevicePhase = 0;
    private const int BlueprintPhase = 1;
    private const int StructurePhase = 2;
    private const int ObjectPhase = 3;
    private const int CommandInputPhase = 4;
    private const int ValuePhase = 5;
    internal const int InterlockPhase = 6;
    private const int ObjectDeletePhase = 7;
    private const int BlueprintDeletePhase = 8;
    private const int DeviceDeletePhase = 9;

    private readonly Project _project;
    private readonly IReadOnlyCollection<Blueprint> _blueprints;
    private readonly CmLibrary _currentLibrary;
    private readonly Dictionary<Guid, Blueprint> _working;
    private readonly DesignPlan _plan;
    private readonly Dictionary<string, string> _blueprintItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _deviceItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pathItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProjectObject> _byPath;
    private CmLibrary? _workingLibrary;
    private int _order;

    private DesignPlanner(Project project, IReadOnlyCollection<Blueprint> blueprints, DesignDocument design)
    {
        _project = project;
        _blueprints = blueprints;
        _currentLibrary = DesignReader.Library(blueprints);
        _working = blueprints.Select(b => b.Clone()).GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        _plan = new DesignPlan { Questions = design.Questions ?? [] };
        _byPath = project.Objects.Where(o => o is not Tag).ToDictionary(o => project.GetPath(o.Id), StringComparer.OrdinalIgnoreCase);
    }

    public static DesignPlan Plan(DesignDocument design, Project project, IReadOnlyCollection<Blueprint> blueprints)
    {
        var planner = new DesignPlanner(project, blueprints, design);
        planner.Devices(design.Devices ?? []);
        planner.Blueprints(design.Blueprints ?? []);
        foreach (var node in design.Objects ?? [])
            planner.Walk(node, Parent.Root, null);
        return planner._plan;
    }

    private CmLibrary WorkingLibrary => _workingLibrary ??= DesignReader.Library(_working.Values);

    private ChangeItem Add(string id, string kind, string area, string target, string group, string summary, JsonNode? before, JsonNode? after,
        int phase, Action<ApplyContext> apply, IEnumerable<string?>? dependsOn = null, string? section = null, IEnumerable<string>? problems = null)
    {
        var unique = id;
        for (var n = 2; _plan.Items.Any(i => i.Id == unique); n++)
            unique = $"{id}#{n}";
        var item = new ChangeItem
        {
            Id = unique,
            Kind = kind,
            Area = area,
            Target = target,
            Group = group,
            Section = section,
            Summary = summary,
            Before = before,
            After = after,
            Phase = phase,
            Order = _order++,
            Apply = apply,
            DependsOn = (dependsOn ?? []).OfType<string>().Where(d => d != unique).Distinct().ToList(),
            Problems = problems?.ToList() ?? []
        };
        _plan.Items.Add(item);
        return item;
    }

    // ---------------------------------------------------------------- devices

    private void Devices(List<DesignDevice> devices)
    {
        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.Name))
            {
                _plan.Problems.Add("A device needs a name.");
                continue;
            }
            var name = device.Name.Trim();
            var existing = Device(device.RenamedFrom ?? name);
            var group = $"Device {name}";
            if (device.Delete == true)
            {
                if (existing is null)
                    _plan.Problems.Add($"Device {name} cannot be deleted: it does not exist.");
                else
                    Add($"device:{name}", ChangeKinds.Delete, ChangeAreas.Device, name, group, $"Delete device {name}",
                        DesignDocument.ToJson(DesignReader.ToDesign(existing)), null, DeviceDeletePhase, ctx => SetDevices(ctx, ctx.DeviceId(name), null));
                continue;
            }
            var problems = new List<string>();
            DeviceRole? role = null;
            if (device.Role is { } roleText)
            {
                try
                {
                    role = BlueprintDesign.Parse<DeviceRole>(roleText, $"Device {name}: role");
                }
                catch (DesignException ex)
                {
                    problems.Add(ex.Message);
                }
            }
            if (existing is null)
            {
                if (device.RenamedFrom is not null)
                    problems.Add($"Device {name}: renamedFrom {device.RenamedFrom} does not exist.");
                var created = new Device(Guid.Empty, name, role ?? DeviceRole.Plc, device.Description?.Trim() ?? "");
                _deviceItems[name] = Add($"device:{name}", ChangeKinds.Create, ChangeAreas.Device, name, group, $"Create device {name} ({created.Role})",
                    null, DesignDocument.ToJson(DesignReader.ToDesign(created)), DevicePhase,
                    ctx => SetDevices(ctx, null, created with { Id = Guid.NewGuid() }), problems: problems).Id;
                continue;
            }
            string? renameItem = null;
            if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                var id = existing.Id;
                renameItem = _deviceItems[name] = Add($"device:{name}:rename", ChangeKinds.Rename, ChangeAreas.Device, name, group,
                    $"Rename device {existing.Name} to {name}", new JsonObject { ["name"] = existing.Name }, new JsonObject { ["name"] = name },
                    DevicePhase, ctx => SetDevices(ctx, id, ctx.Project.Topology.Device(id)! with { Name = name })).Id;
            }
            var updated = existing with { Name = name, Role = role ?? existing.Role, Description = device.Description?.Trim() ?? existing.Description };
            if (updated.Role != existing.Role || updated.Description != existing.Description || problems.Count > 0)
            {
                var id = existing.Id;
                Add($"device:{name}", ChangeKinds.Update, ChangeAreas.Device, name, group, $"Change device {name}",
                    DesignDocument.ToJson(DesignReader.ToDesign(existing with { Name = name })), DesignDocument.ToJson(DesignReader.ToDesign(updated)),
                    DevicePhase, ctx => SetDevices(ctx, id, ctx.Project.Topology.Device(id)! with { Role = updated.Role, Description = updated.Description }),
                    [renameItem], problems: problems);
            }
        }
    }

    private Device? Device(string name) =>
        _project.Topology.Devices.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces (or adds, or with null removes) one device; links of a removed device go too.</summary>
    private static void SetDevices(ApplyContext ctx, Guid? replace, Device? with)
    {
        var devices = ctx.Project.Topology.Devices.Where(d => d.Id != replace).ToList();
        if (with is not null)
        {
            var index = replace is { } id ? ctx.Project.Topology.Devices.ToList().FindIndex(d => d.Id == id) : -1;
            devices.Insert(index < 0 ? devices.Count : index, with);
        }
        var ids = devices.Select(d => d.Id).ToHashSet();
        ctx.Project.SetTopology(devices, ctx.Project.Topology.Links.Where(l => ids.Contains(l.From) && ids.Contains(l.To)));
    }

    // ---------------------------------------------------------------- blueprints

    private Blueprint? Working(string name) =>
        _working.Values.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

    private Guid? WorkingId(string name) => Working(name)?.Id;

    private string? WorkingName(Guid id) => _working.GetValueOrDefault(id)?.Name;

    private void Blueprints(List<DesignBlueprint> designs)
    {
        var created = new Dictionary<DesignBlueprint, Guid>();
        var renamed = new HashSet<DesignBlueprint>();
        foreach (var design in designs)
        {
            if (string.IsNullOrWhiteSpace(design.Name))
            {
                _plan.Problems.Add("A blueprint needs a name.");
                continue;
            }
            var source = design.RenamedFrom ?? design.Name;
            var existing = _blueprints.FirstOrDefault(b => string.Equals(b.Name, source, StringComparison.OrdinalIgnoreCase));
            if (design.Delete == true)
            {
                if (existing is null)
                    _plan.Problems.Add($"Blueprint {design.Name} cannot be deleted: it does not exist.");
                else
                {
                    var id = existing.Id;
                    Add($"blueprint:{design.Name}", ChangeKinds.Delete, ChangeAreas.Blueprint, design.Name, $"Blueprint {design.Name}",
                        $"Delete blueprint {design.Name}", DesignDocument.ToJson(BlueprintDesign.ToDesign(existing, WorkingName)), null,
                        BlueprintDeletePhase, ctx => ctx.Removed(id));
                }
                continue;
            }
            if (existing is null)
            {
                var shell = new Blueprint { Id = Guid.NewGuid(), Name = design.Name.Trim() };
                _working[shell.Id] = shell;
                created[design] = shell.Id;
                _blueprintItems[design.Name] = $"blueprint:{design.Name}";
            }
            else if (!string.Equals(existing.Name, design.Name, StringComparison.Ordinal))
            {
                if (_blueprints.Any(b => b.Id != existing.Id && string.Equals(b.Name, design.Name, StringComparison.OrdinalIgnoreCase)))
                    _plan.Problems.Add($"Blueprint {existing.Name} cannot be renamed to {design.Name}: that name is in use.");
                _working[existing.Id].Name = design.Name.Trim();
                renamed.Add(design);
                _blueprintItems[design.Name] = $"blueprint:{design.Name}:rename";
            }
        }
        foreach (var design in designs.Where(d => !string.IsNullOrWhiteSpace(d.Name) && d.Delete != true))
        {
            var group = $"Blueprint {design.Name}";
            var roleDependencies = (design.Roles ?? []).Values.Where(n => !string.Equals(n, design.Name, StringComparison.OrdinalIgnoreCase))
                .Select(n => _blueprintItems.GetValueOrDefault(n)).ToList();
            if (created.TryGetValue(design, out var newId))
            {
                var problems = new List<string>();
                if (design.RenamedFrom is not null)
                    problems.Add($"Blueprint {design.Name}: renamedFrom {design.RenamedFrom} does not exist.");
                JsonNode? after;
                try
                {
                    var blueprint = BlueprintDesign.Create(design, newId, WorkingId);
                    _working[newId] = blueprint;
                    after = DesignDocument.ToJson(BlueprintDesign.ToDesign(blueprint, WorkingName));
                }
                catch (DesignException ex)
                {
                    problems.Add(ex.Message);
                    after = DesignDocument.ToJson(design);
                }
                Add($"blueprint:{design.Name}", ChangeKinds.Create, ChangeAreas.Blueprint, design.Name, group,
                    $"Create {design.Kind ?? "CM"} blueprint {design.Name}", null, after, BlueprintPhase,
                    ctx => ctx.Changed(BlueprintDesign.Create(design, Guid.NewGuid(), ctx.IdOf)), roleDependencies, problems: problems);
                continue;
            }
            var existing = _working.Values.First(b => string.Equals(b.Name, design.Name, StringComparison.OrdinalIgnoreCase));
            var id = existing.Id;
            string? renameItem = null;
            if (renamed.Contains(design))
            {
                var oldName = design.RenamedFrom!;
                var newName = design.Name.Trim();
                renameItem = Add($"blueprint:{design.Name}:rename", ChangeKinds.Rename, ChangeAreas.Blueprint, design.Name, group,
                    $"Rename blueprint {oldName} to {newName}", new JsonObject { ["name"] = oldName }, new JsonObject { ["name"] = newName },
                    BlueprintPhase, ctx =>
                    {
                        var blueprint = ctx.Blueprints[id];
                        blueprint.Name = newName;
                        ctx.Changed(blueprint);
                    }).Id;
            }
            var contentChanged = false;
            var versionChanged = false;
            foreach (var section in BlueprintDesign.Sections)
            {
                if (BlueprintDesign.Section(design, section) is null)
                    continue;
                var current = _working[id];
                var before = BlueprintDesign.SectionJson(BlueprintDesign.ToDesign(current, WorkingName), section);
                var copy = current.Clone();
                var problems = new List<string>();
                JsonNode after;
                try
                {
                    BlueprintDesign.Apply(copy, design, section, WorkingId);
                    after = BlueprintDesign.SectionJson(BlueprintDesign.ToDesign(copy, WorkingName), section);
                }
                catch (DesignException ex)
                {
                    problems.Add(ex.Message);
                    after = DesignDocument.ToJson(BlueprintDesign.Section(design, section))!;
                }
                if (problems.Count == 0 && JsonNode.DeepEquals(before, after))
                    continue;
                if (problems.Count == 0)
                    _working[id] = copy;
                if (section == BlueprintDesign.General)
                    versionChanged = copy.Version != current.Version;
                else
                    contentChanged = true;
                Add($"blueprint:{design.Name}:{section}", ChangeKinds.Update, ChangeAreas.Blueprint, design.Name, group,
                    $"Change {BlueprintDesign.Title(section)} of blueprint {design.Name}", before, after, BlueprintPhase, ctx =>
                    {
                        var blueprint = ctx.Blueprints[id];
                        BlueprintDesign.Apply(blueprint, design, section, ctx.IdOf);
                        ctx.Changed(blueprint);
                    }, section == "roles" ? [renameItem, .. roleDependencies] : [renameItem], section, problems);
            }
            if (contentChanged && !versionChanged)
                _plan.Warnings.Add($"Blueprint {design.Name} changes but its version stays {_working[id].Version}; raise it before the next export to SCADA.");
        }
        _workingLibrary = null;
    }

    // ---------------------------------------------------------------- objects

    /// <summary>Where a node sits: its parent's new path, the parent's object (existing or created by an item) and what it passes on.</summary>
    private sealed record Parent(string? NewPath, Guid? ExistingId, string? CurrentPath, bool Exists, string? ItemId, DesignObjectKind Kind,
        string? Blueprint, string? Device)
    {
        public static readonly Parent Root = new(null, null, null, true, null, DesignObjectKind.None, null, null);

        public bool IsRoot => NewPath is null;

        public Guid? Resolve(ApplyContext ctx) => IsRoot ? null : ExistingId ?? ctx.Created.GetValueOrDefault(NewPath!);
    }

    private static string Join(string? parent, string name) => parent is null ? name : $"{parent}.{name}";

    private void Walk(DesignObject node, Parent parent, string? role)
    {
        if (node.KindCount() != 1 || string.IsNullOrWhiteSpace(node.Name))
        {
            _plan.Problems.Add($"An object under {parent.NewPath ?? "the project root"} must have exactly one of folder, unit, em or cm with its name.");
            return;
        }
        var name = node.Name.Trim();
        var kind = node.Kind;
        var newPath = Join(parent.NewPath, name);
        string? currentPath = node.MovedFrom?.Trim()
                              ?? (parent.Exists ? Join(parent.CurrentPath, node.RenamedFrom?.Trim() ?? name) : null);
        var existing = currentPath is not null ? _byPath.GetValueOrDefault(currentPath) : null;
        var problems = new List<string>();
        if (existing is null && (node.MovedFrom is not null || node.RenamedFrom is not null))
            problems.Add($"{newPath}: {(node.MovedFrom is not null ? $"movedFrom {node.MovedFrom}" : $"renamedFrom {node.RenamedFrom}")} does not exist.");
        if (existing is not null && DesignReader.KindOf(existing) != kind)
        {
            problems.Add($"{newPath} is a {DesignReader.KindOf(existing).ToString().ToLowerInvariant()}, not a {kind.ToString().ToLowerInvariant()}; delete it and create it again.");
        }

        if (node.Delete == true)
        {
            if (existing is null)
                _plan.Problems.Add($"{newPath} cannot be deleted: it does not exist.");
            else
            {
                var id = existing.Id;
                Add($"object:{currentPath}", ChangeKinds.Delete, ChangeAreas.Object, currentPath!, currentPath!, $"Delete {currentPath}",
                    DesignDocument.ToJson(Head(existing)), null, ObjectDeletePhase, ctx => ctx.Project.Delete(id));
            }
            return;
        }

        // The blueprint: given, from the role of the container, or the existing object's.
        string? blueprint = null;
        if (kind != DesignObjectKind.Folder)
        {
            string? roleBlueprint = null;
            if (role is not null)
            {
                var container = parent.Blueprint is { } b ? Working(b) : null;
                var roleEntry = container?.Roles.FirstOrDefault(r => r.Name == role);
                if (container is not null && roleEntry is null)
                    problems.Add($"{newPath}: blueprint {container.Name} has no role {role}.");
                roleBlueprint = roleEntry is null ? null : WorkingName(roleEntry.BlueprintId);
            }
            var existingBlueprint = existing is ControlModule or UnitInstance ? WorkingName(InstanceFactory.BlueprintIdOf(existing)) : null;
            blueprint = node.Blueprint?.Trim() ?? roleBlueprint ?? existingBlueprint;
            if (node.Blueprint is not null && roleBlueprint is not null && !string.Equals(node.Blueprint, roleBlueprint, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{newPath}: role {role} takes a {roleBlueprint}, not a {node.Blueprint}.");
            if (blueprint is null)
                problems.Add($"{newPath}: give the blueprint.");
            else if (Working(blueprint) is null)
                problems.Add($"{newPath}: blueprint {blueprint} does not exist.");
            else if (existingBlueprint is not null && !string.Equals(existingBlueprint, blueprint, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{newPath} is a {existingBlueprint}; the blueprint of an existing object cannot change. Delete it and create it again.");
            else if (Working(blueprint) is { } found && kind != KindFor(found.Kind))
                problems.Add($"{newPath}: {blueprint} is a{(found.Kind == BlueprintKind.EM ? "n" : "")} {found.Kind} blueprint; write it as '{KindFor(found.Kind).ToString().ToLowerInvariant()}: {name}'.");
        }

        var device = node.Device?.Trim() ?? parent.Device;
        string? objectItem;
        Guid? existingId = existing?.Id;
        if (existing is null)
        {
            var effectiveDevice = kind == DesignObjectKind.Cm ? device ?? ProjectDevice(parent) : null;
            var head = node.Head();
            head.Device = node.Device;
            var blueprintName = blueprint;
            objectItem = Add($"object:{newPath}", ChangeKinds.Create, ChangeAreas.Object, newPath, newPath,
                $"Create {Title(kind)} {newPath}{(blueprint is null || kind == DesignObjectKind.Folder ? "" : $" ({blueprint})")}", null, DesignDocument.ToJson(head),
                StructurePhase, ctx => Create(ctx, kind, name, newPath, parent, role, blueprintName, node.Description, effectiveDevice),
                [parent.ItemId, blueprint is null ? null : _blueprintItems.GetValueOrDefault(blueprint), effectiveDevice is null ? null : _deviceItems.GetValueOrDefault(effectiveDevice)],
                problems: problems).Id;
            _pathItems[newPath] = objectItem;
        }
        else
        {
            var id = existing.Id;
            string? structureItem = null;
            var renamed = !string.Equals(existing.Name, name, StringComparison.Ordinal);
            var moved = node.MovedFrom is not null && (!parent.Exists || existing.ParentId != parent.ExistingId);
            if (renamed || moved)
            {
                structureItem = Add($"object:{newPath}:{(moved ? "move" : "rename")}", moved ? ChangeKinds.Move : ChangeKinds.Rename, ChangeAreas.Object,
                    newPath, newPath, moved ? $"Move {currentPath} to {newPath}" : $"Rename {currentPath} to {name}",
                    new JsonObject { ["path"] = currentPath }, new JsonObject { ["path"] = newPath }, StructurePhase,
                    ctx => MoveAndRename(ctx, id, parent, role, name), [parent.ItemId], problems: problems).Id;
                _pathItems[newPath] = structureItem;
                problems = [];
            }
            var before = Head(existing);
            var after = Head(existing);
            SetName(before, kind, name);
            SetName(after, kind, name);
            var descriptionChanged = node.Description is { } description
                                     && BlueprintDesign.Text(description.Trim()) != BlueprintDesign.Text(Project.DescriptionOf(existing));
            if (descriptionChanged)
                after.Description = BlueprintDesign.Text(node.Description!.Trim());
            var newDevice = node.Device?.Trim();
            if (newDevice is not null && kind == DesignObjectKind.Folder)
                problems.Add($"{newPath}: a folder has no device; give it to the CMs, EMs or Units.");
            var deviceChanged = newDevice is not null && kind != DesignObjectKind.Folder && (existing is ControlModule control
                ? !string.Equals(DeviceName(control.ExecutionDeviceId), newDevice, StringComparison.OrdinalIgnoreCase)
                : _project.Descendants(existing.Id).OfType<ControlModule>().Any(c => !string.Equals(DeviceName(c.ExecutionDeviceId), newDevice, StringComparison.OrdinalIgnoreCase)));
            if (deviceChanged)
                after.Device = newDevice;
            if (problems.Count > 0 || descriptionChanged || deviceChanged)
            {
                var newDescription = descriptionChanged ? node.Description : null;
                var setDevice = deviceChanged ? newDevice : null;
                Add($"object:{newPath}", ChangeKinds.Update, ChangeAreas.Object, newPath, newPath, $"Change {newPath}",
                    DesignDocument.ToJson(before), DesignDocument.ToJson(after), ObjectPhase,
                    ctx => Update(ctx, id, newDescription, setDevice),
                    [structureItem, setDevice is null ? null : _deviceItems.GetValueOrDefault(setDevice)], problems: problems);
            }
            objectItem = structureItem;
        }

        if (kind != DesignObjectKind.Folder)
            Overrides(node, kind, newPath, existing, existingId, objectItem, blueprint, parent);

        var self = new Parent(newPath, existingId, existing is null ? null : currentPath, existing is not null, objectItem, kind, blueprint,
            kind == DesignObjectKind.Folder ? null : device);
        foreach (var (memberRole, member) in node.Members ?? [])
        {
            if (kind is not (DesignObjectKind.Em or DesignObjectKind.Unit))
            {
                _plan.Problems.Add($"{newPath}: only an EM or a Unit has members.");
                break;
            }
            Walk(member, self, memberRole);
        }
        foreach (var child in node.Children ?? [])
            Walk(child, self, null);
    }

    private static DesignObjectKind KindFor(BlueprintKind kind) => kind switch
    {
        BlueprintKind.EM => DesignObjectKind.Em,
        BlueprintKind.Unit => DesignObjectKind.Unit,
        _ => DesignObjectKind.Cm
    };

    private static string Title(DesignObjectKind kind) => kind switch
    {
        DesignObjectKind.Folder => "folder",
        DesignObjectKind.Unit => "Unit",
        DesignObjectKind.Em => "EM",
        _ => "CM"
    };

    private static string ParentPath(string path) => path.LastIndexOf('.') is var dot and > 0 ? path[..dot] : "";

    private static void SetName(DesignObject node, DesignObjectKind kind, string name)
    {
        node.Folder = node.Unit = node.Em = node.Cm = null;
        switch (kind)
        {
            case DesignObjectKind.Folder: node.Folder = name; break;
            case DesignObjectKind.Unit: node.Unit = name; break;
            case DesignObjectKind.Em: node.Em = name; break;
            default: node.Cm = name; break;
        }
    }

    private string? DeviceName(Guid? id) => id is { } d ? _project.Topology.Device(d)?.Name : null;

    /// <summary>The device a new CM gets from the existing container it is created in.</summary>
    private string? ProjectDevice(Parent parent)
    {
        for (var id = parent.ExistingId; id is { } current && _project.Find(current) is UnitInstance unit; id = unit.ParentId)
            if (DesignReader.UniformDevice(_project, unit) is { } device)
                return DeviceName(device);
        return null;
    }

    /// <summary>An existing object's own fields as the design writes them.</summary>
    private DesignObject Head(ProjectObject obj)
    {
        var head = DesignObject.Named(DesignReader.KindOf(obj), obj.Name);
        head.Description = BlueprintDesign.Text(Project.DescriptionOf(obj));
        if (obj is ControlModule or UnitInstance)
            head.Blueprint = WorkingName(InstanceFactory.BlueprintIdOf(obj));
        var device = obj switch
        {
            ControlModule cm => DeviceName(cm.ExecutionDeviceId),
            UnitInstance => DesignReader.UniformDevice(_project, obj) is { } d ? DeviceName(d) : null,
            _ => null
        };
        head.Device = device;
        return head;
    }

    private static void Create(ApplyContext ctx, DesignObjectKind kind, string name, string newPath, Parent parent, string? role, string? blueprintName,
        string? description, string? device)
    {
        var parentId = parent.Resolve(ctx);
        if (!parent.IsRoot && parentId is null)
            throw new DesignException($"{parent.NewPath} does not exist yet.");
        ProjectObject created;
        if (kind == DesignObjectKind.Folder)
            created = ctx.Project.AddFolder(name, parentId);
        else
        {
            var blueprint = ctx.FindBlueprint(blueprintName ?? "") ?? throw new DesignException($"Blueprint {blueprintName} does not exist.");
            if (ctx.Library.Find(blueprint.Id) is null)
                throw new DesignException($"Blueprint {blueprint.Name} has errors; an instance can only be made from a blueprint without errors.");
            created = kind == DesignObjectKind.Cm
                ? InstanceFactory.Create(ctx.Project, ctx.Library, blueprint.Id, name, parentId)
                : InstanceFactory.CreateUnit(ctx.Project, ctx.Library, blueprint.Id, name, parentId);
            if (parentId is { } p && ctx.Project.Find(p) is UnitInstance)
                InstanceFactory.Join(ctx.Project, ctx.Library, created.Id, role);
            if (device is not null && created is ControlModule)
                ctx.Project.SetExecutionDevice(created.Id, ctx.DeviceId(device));
        }
        if (!string.IsNullOrWhiteSpace(description))
            ctx.Project.SetDescription(created.Id, description);
        ctx.Created[newPath] = created.Id;
    }

    private static void MoveAndRename(ApplyContext ctx, Guid id, Parent parent, string? role, string name)
    {
        var parentId = parent.Resolve(ctx);
        if (!parent.IsRoot && parentId is null)
            throw new DesignException($"{parent.NewPath} does not exist yet.");
        var project = ctx.Project;
        if (project.Get(id).ParentId != parentId)
        {
            var wasMember = project.UnitOf(id) is not null;
            project.Move(id, parentId);
            if (wasMember && project.UnitOf(id) is null && project.Get(id) is ControlModule or UnitInstance)
                CommandInputBehaviour.SetUnitRow(project, ctx.Library, id, member: false);
            if (parentId is { } p && project.Find(p) is UnitInstance)
                InstanceFactory.Join(project, ctx.Library, id, role);
        }
        if (project.Get(id).Name != name)
            project.Rename(id, name);
    }

    private static void Update(ApplyContext ctx, Guid id, string? description, string? device)
    {
        if (description is not null)
            ctx.Project.SetDescription(id, description);
        if (device is null)
            return;
        var deviceId = ctx.DeviceId(device);
        var obj = ctx.Project.Get(id);
        var targets = obj is ControlModule ? [obj] : ctx.Project.Descendants(id).OfType<ControlModule>().Cast<ProjectObject>().ToList();
        foreach (var cm in targets)
            ctx.Project.SetExecutionDevice(cm.Id, deviceId);
    }

    // ---------------------------------------------------------------- overrides and interlocks

    private void Overrides(DesignObject node, DesignObjectKind kind, string path, ProjectObject? existing, Guid? existingId, string? objectItem, string? blueprint,
        Parent parent)
    {
        Guid? Resolve(ApplyContext ctx) => existingId ?? ctx.Created.GetValueOrDefault(path);
        Guid Need(ApplyContext ctx) => Resolve(ctx) ?? throw new DesignException($"{path} does not exist.");
        var workingBlueprint = blueprint is null ? null : Working(blueprint);

        DesignCommandInputs? inputsAfter = null;
        if (node.CommandInputs is { } inputs)
        {
            var problems = new List<string>();
            CommandInputConfig? config = null;
            try
            {
                config = CommandInputDesign.FromDesign(inputs, path);
            }
            catch (DesignException ex)
            {
                problems.Add(ex.Message);
            }
            inputsAfter = CommandInputDesign.ToDesign(config) ?? new DesignCommandInputs();
            var before = CommandInputDesign.ToDesign(DesignReader.WithoutUnitRow(existing is null
                ? workingBlueprint is null ? null : WorkingLibrary.Find(workingBlueprint.Id)?.DefaultCommandInputs
                : CommandInputBehaviour.InputsOf(existing)));
            var beforeJson = new JsonObject { ["commandInputs"] = DesignDocument.ToJson(before ?? new DesignCommandInputs()) };
            var afterJson = new JsonObject { ["commandInputs"] = DesignDocument.ToJson(inputsAfter) };
            if (problems.Count > 0 || !JsonNode.DeepEquals(beforeJson, afterJson))
            {
                Add($"object:{path}:commandInputs", existing is null ? ChangeKinds.Create : ChangeKinds.Update, ChangeAreas.CommandInputs, path, path,
                    $"Set the command inputs of {path}", existing is null ? null : beforeJson, afterJson, CommandInputPhase, ctx =>
                    {
                        var id = Need(ctx);
                        var unitRows = CommandInputBehaviour.InputsOf(ctx.Project.Get(id))?.Rows.Where(r => r.Source == CommandSource.Unit) ?? [];
                        var wanted = config ?? CommandInputConfig.Empty;
                        CommandInputBehaviour.Configure(ctx.Project, ctx.Library, id, wanted with { Rows = [.. wanted.Rows, .. unitRows] });
                    }, [objectItem], problems: problems);
            }
        }

        if (node.Values is { } values)
        {
            var templates = workingBlueprint is null
                ? []
                : TagValues.Templates(WorkingLibrary, workingBlueprint.Id,
                    existing is not null ? CommandInputBehaviour.InputsOf(existing) : CommandInputDesign.FromDesign(inputsAfter, path) ?? WorkingLibrary.Find(workingBlueprint.Id)?.DefaultCommandInputs);
            var after = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, text) in values)
            {
                var template = templates.TryGetValue(key, out var t) ? t : ((TagDataType Type, JsonNode? Initial)?)null;
                var normalized = TagValues.Normalize(text, template?.Type);
                if (template is null || normalized != TagValues.Format(template.Value.Initial))
                    after[key] = normalized;
            }
            var before = existing is null ? null : DesignReader.Values(_project, _currentLibrary, existing);
            var beforeJson = new JsonObject { ["values"] = DesignDocument.ToJson(new SortedDictionary<string, string>(before ?? [], StringComparer.Ordinal)) };
            var afterJson = new JsonObject { ["values"] = DesignDocument.ToJson(after) };
            if (!JsonNode.DeepEquals(beforeJson, afterJson))
                Add($"object:{path}:values", existing is null ? ChangeKinds.Create : ChangeKinds.Update, ChangeAreas.Values, path, path,
                    $"Set initial values of {path}: {string.Join(", ", after.Select(v => $"{v.Key} = {v.Value}").DefaultIfEmpty("blueprint defaults"))}",
                    existing is null ? null : beforeJson, afterJson, ValuePhase, ctx => SetValues(ctx, Need(ctx), path, values), [objectItem]);
        }

        if (node.AlarmPriorities is { } priorities)
        {
            var problems = new List<string>();
            if (kind != DesignObjectKind.Cm)
                problems.Add($"{path}: alarm priorities can only be overridden on a CM; change the priority in the {blueprint} blueprint instead.");
            var before = existing is ControlModule cm ? new SortedDictionary<string, int>(cm.AlarmPriorities.ToDictionary(), StringComparer.Ordinal) : [];
            var after = new SortedDictionary<string, int>(priorities, StringComparer.Ordinal);
            var beforeJson = new JsonObject { ["alarmPriorities"] = DesignDocument.ToJson(before) };
            var afterJson = new JsonObject { ["alarmPriorities"] = DesignDocument.ToJson(after) };
            if (problems.Count > 0 || !JsonNode.DeepEquals(beforeJson, afterJson))
                Add($"object:{path}:alarmPriorities", existing is null ? ChangeKinds.Create : ChangeKinds.Update, ChangeAreas.AlarmPriorities, path, path,
                    $"Set alarm priorities of {path}", existing is null ? null : beforeJson, afterJson, ValuePhase, ctx =>
                    {
                        var id = Need(ctx);
                        var control = ctx.Project.Get<ControlModule>(id);
                        var type = ctx.Library.Find(control.BlueprintId);
                        var names = (type?.Alarms.Select(a => a.Name) ?? []).Concat((control.CommandInputs?.Rows ?? []).Select(r => $"{r.Name}_stuck")).ToHashSet(StringComparer.Ordinal);
                        foreach (var alarm in control.AlarmPriorities.Keys.Where(k => !after.ContainsKey(k)).ToList())
                            ctx.Project.SetAlarmPriority(id, alarm, null);
                        foreach (var (alarm, priority) in after)
                        {
                            if (!names.Contains(alarm))
                                throw new DesignException($"{path} has no alarm {alarm}.");
                            ctx.Project.SetAlarmPriority(id, alarm, priority);
                        }
                    }, [objectItem], problems: problems);
        }

        if (node.Interlocks is { } interlocks)
            Interlocks(path, interlocks, existing, objectItem, Resolve);
    }

    private static void SetValues(ApplyContext ctx, Guid id, string path, Dictionary<string, string> values)
    {
        var obj = ctx.Project.Get(id);
        var tags = ctx.Project.GetChildren(id).OfType<Tag>().ToDictionary(t => $"{t.Group.Code()}.{t.Name}", StringComparer.OrdinalIgnoreCase);
        foreach (var (key, text) in values)
        {
            if (!tags.TryGetValue(key, out var tag))
                throw new DesignException($"{path} has no tag {key}.");
            if (tag.Group is not (TagGroup.Par or TagGroup.Set))
                throw new DesignException($"{path}: {key} is not a PAR or SET tag; only parameters and settings have an initial value per object.");
            ctx.Project.SetInitialValue(tag.Id, TagValues.Parse(text, tag.DataType, $"{path}.{key}"));
        }
        var defaults = TagValues.Templates(ctx.Library, InstanceFactory.BlueprintIdOf(obj), CommandInputBehaviour.InputsOf(obj));
        foreach (var (key, tag) in tags.Where(t => !values.ContainsKey(t.Key) && defaults.ContainsKey(t.Key)))
            if (TagValues.Format(tag.InitialValue) != TagValues.Format(defaults[key].Initial))
                ctx.Project.SetInitialValue(tag.Id, defaults[key].Initial);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static string LineKey(DesignInterlock line) => line.Kind == nameof(InterlockKind.Trip)
        ? $"trip:{line.Alarm}"
        : $"{line.Kind}:{line.Target ?? ""}:{Spaces().Replace(line.Condition ?? "", " ").Trim()}";

    private void Interlocks(string path, List<DesignInterlock> lines, ProjectObject? existing, string? objectItem, Func<ApplyContext, Guid?> resolve)
    {
        var problems = new List<string>();
        var rules = new List<(InterlockRule Rule, DesignInterlock Line)>();
        for (var i = 0; i < lines.Count; i++)
        {
            try
            {
                rules.Add((BlueprintDesign.ToRule(lines[i], $"{path}: interlock {i + 1}"), lines[i]));
            }
            catch (DesignException ex)
            {
                problems.Add(ex.Message);
            }
        }
        var named = InterlockRule.WithAlarmNames(rules.Select(r => r.Rule));
        var after = named.Select((rule, i) =>
        {
            var line = BlueprintDesign.ToDesign(rule);
            line.Target = BlueprintDesign.Text(rules[i].Line.Target?.Trim());
            return (Key: LineKey(line), Line: line);
        }).ToList();
        var current = existing is null
            ? []
            : InterlockSources.OwnRules(existing).Select(r => (Rule: r, Line: DesignReader.Interlock(_project, existing, r))).Select(x => (Key: LineKey(x.Line), x.Line, x.Rule)).ToList();
        foreach (var duplicate in after.GroupBy(a => a.Key).Where(g => g.Count() > 1))
            problems.Add($"{path}: interlock {duplicate.Key} is given twice.");

        var itemsByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var created = new List<ChangeItem>();
        void Line(string key, string kind, DesignInterlock? before, DesignInterlock? line)
        {
            var shown = line ?? before!;
            var dependencies = new List<string?> { objectItem };
            if (line is not null)
            {
                if (line.Target is { } target)
                    dependencies.Add(_pathItems.GetValueOrDefault(Join(path, target)));
                foreach (var reference in Expression.References(line.Condition ?? ""))
                    dependencies.Add(_pathItems.Where(p => reference.StartsWith(p.Key + ".", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(p => p.Key.Length).Select(p => p.Value).FirstOrDefault());
            }
            var verb = kind switch { ChangeKinds.Create => "Add", ChangeKinds.Delete => "Remove", _ => "Change" };
            var on = shown.Target is null ? path : Join(path, shown.Target);
            var item = Add($"interlock:{path}:{key}", kind, ChangeAreas.Interlock, path, path,
                $"{verb} {shown.Kind} interlock on {on}: {shown.Condition}",
                before is null ? null : DesignDocument.ToJson(before), line is null ? null : DesignDocument.ToJson(line), InterlockPhase,
                ctx => ctx.OnFinish($"interlocks:{path}", () => FinishInterlocks(ctx, path, resolve, after, current, itemsByKey)),
                dependencies, problems: created.Count == 0 ? problems : null);
            itemsByKey[key] = item.Id;
            created.Add(item);
        }
        foreach (var (key, line) in after)
        {
            var old = current.FirstOrDefault(c => c.Key == key);
            if (old.Line is null)
                Line(key, ChangeKinds.Create, null, line);
            else if (!JsonNode.DeepEquals(DesignDocument.ToJson(old.Line), DesignDocument.ToJson(line)))
                Line(key, ChangeKinds.Update, old.Line, line);
        }
        foreach (var old in current.Where(c => after.All(a => a.Key != c.Key)))
            Line(old.Key, ChangeKinds.Delete, old.Line, null);
        if (created.Count == 0 && problems.Count > 0)
            _plan.Problems.AddRange(problems);
    }

    /// <summary>Writes the interlock list of one object: accepted lines in the fragment's order, the rest as they were.</summary>
    private static void FinishInterlocks(ApplyContext ctx, string path, Func<ApplyContext, Guid?> resolve, List<(string Key, DesignInterlock Line)> after,
        List<(string Key, DesignInterlock Line, InterlockRule Rule)> current, Dictionary<string, string> items)
    {
        var ownerId = resolve(ctx) ?? throw new DesignException($"{path} does not exist.");
        var ownerPath = ctx.Project.GetPath(ownerId);
        var final = new List<InterlockRule>();
        InterlockRule ToProject(DesignInterlock line)
        {
            var rule = BlueprintDesign.ToRule(line, $"{path}: interlock");
            rule.Condition = ExpressionReferences.ToStored(ctx.Project, rule.Condition);
            if (line.Target is { } target)
                rule.TargetId = DesignReader.FindByPath(ctx.Project, Join(ownerPath, target))?.Id
                                ?? throw new DesignException($"{path}: interlock target {target} does not exist below {ownerPath}.");
            return rule;
        }
        foreach (var (key, line) in after)
        {
            var old = current.FirstOrDefault(c => c.Key == key);
            if (!items.TryGetValue(key, out var item))
                final.Add(old.Rule.Clone());
            else if (ctx.Selected.Contains(item))
                final.Add(ToProject(line));
            else if (old.Rule is not null)
                final.Add(old.Rule.Clone());
        }
        foreach (var old in current.Where(c => after.All(a => a.Key != c.Key)))
            if (!items.TryGetValue(old.Key, out var item) || !ctx.Selected.Contains(item))
                final.Add(old.Rule.Clone());
        ctx.Project.SetInterlocks(ownerId, final);
    }
}
