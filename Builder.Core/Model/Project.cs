using ApolloIQ.Core.Conventions;
using ApolloIQ.Core.Versioning;
using Builder.Core.Tags;
using Builder.Core.Types;

namespace Builder.Core.Model;

public sealed class Project
{
    private readonly Dictionary<Guid, ProjectObject> _objects = new();
    private readonly Dictionary<Guid, (double X, double Y)> _layout = new();
    private readonly Dictionary<Guid, List<Guid>> _children = new();
    private readonly HashSet<string> _symbolKeys = new(StringComparer.Ordinal);
    private readonly Func<Guid> _newId;

    public Project(ProjectSettings? settings = null, Func<Guid>? newId = null)
    {
        Settings = settings ?? new ProjectSettings();
        _newId = newId ?? Guid.NewGuid;
    }

    public ProjectSettings Settings { get; }

    public Topology Topology { get; } = new();

    public long Revision { get; private set; }

    public IEnumerable<ProjectObject> Objects => _objects.Values;

    public IEnumerable<Tag> Tags => _objects.Values.OfType<Tag>();

    public ProjectObject Get(Guid id) =>
        _objects.TryGetValue(id, out var obj) ? obj : throw new ProjectException(ProjectErrors.NotFound, $"Object {id} does not exist.");

    public T Get<T>(Guid id) where T : ProjectObject =>
        Get(id) as T ?? throw new ProjectException(ProjectErrors.NotFound, $"Object {id} is not a {typeof(T).Name}.");

    public ProjectObject? Find(Guid id) => _objects.GetValueOrDefault(id);

    public IReadOnlyList<ProjectObject> GetChildren(Guid? parentId) =>
        _children.TryGetValue(parentId ?? Guid.Empty, out var ids) ? ids.Select(id => _objects[id]).ToList() : [];

    public Folder AddFolder(string name, Guid? parentId = null, Guid? id = null)
    {
        CheckName(name);
        CheckParent(ObjectKind.Folder, parentId);
        return Insert(new Folder(id ?? _newId(), name, parentId));
    }

    public ControlModule AddControlModule(string name, Guid? parentId, Guid blueprintId, BlueprintVersion blueprintVersion, Guid? id = null)
    {
        CheckName(name);
        CheckParent(ObjectKind.ControlModule, parentId);
        return Insert(new ControlModule(id ?? _newId(), name, parentId, blueprintId, blueprintVersion));
    }

    public UnitInstance AddUnit(string name, Guid? parentId, Guid blueprintId, BlueprintVersion blueprintVersion, Guid? id = null, bool equipmentModule = false)
    {
        CheckName(name);
        CheckParent(ObjectKind.Unit, parentId, equipmentModule);
        return Insert(new UnitInstance(id ?? _newId(), name, parentId, blueprintId, blueprintVersion, equipmentModule));
    }

    public void SetUnitMember(Guid unitId, string role, Guid? memberId)
    {
        var unit = Get<UnitInstance>(unitId);
        var previous = unit.Members.TryGetValue(role, out var old) ? old : (Guid?)null;
        if (memberId is { } id)
        {
            var member = Get(id);
            if (member is UnitInstance em && (!em.IsEquipmentModule || unit.IsEquipmentModule))
                throw new ProjectException(ProjectErrors.InvalidUnit, unit.IsEquipmentModule
                    ? $"An Equipment module's members are CMs; {em.Name} is not a CM (G-151)."
                    : $"{em.Name} is a Unit; a Unit's members are Equipment modules and CMs.", "controlModuleId");
            if (member is not (ControlModule or UnitInstance))
                throw new ProjectException(ProjectErrors.InvalidUnit, $"{member.Name} cannot be a member.", "controlModuleId");
            if (member.ParentId != unitId)
                Move(id, unitId);
            foreach (var other in Objects.OfType<UnitInstance>())
                foreach (var key in other.Members.Where(m => m.Value == id).Select(m => m.Key).ToList())
                    other.Members.Remove(key);
            unit.Members[role] = id;
        }
        else
            unit.Members.Remove(role);
        if (previous is { } p && p != memberId && Find(p) is { } left && left.ParentId == unitId)
        {
            var target = unit.ParentId;
            while (target is { } t && Find(t) is UnitInstance outer)
                target = outer.ParentId;
            Move(p, target);
        }
        Revision++;
    }

    public string? RoleOf(Guid memberId) =>
        Find(memberId)?.ParentId is { } parent && Find(parent) is UnitInstance unit
            ? unit.Members.FirstOrDefault(m => m.Value == memberId).Key
            : null;

    public void SetUnitCommandInputs(Guid unitId, CommandInputConfig? configuration)
    {
        Get<UnitInstance>(unitId).CommandInputs = configuration;
        Revision++;
    }

    public UnitInstance? UnitOf(Guid controlModuleId) =>
        Objects.OfType<UnitInstance>().FirstOrDefault(u => u.Members.ContainsValue(controlModuleId));

    public IReadOnlyDictionary<Guid, (double X, double Y)> Layout => _layout;

    public void SetPosition(Guid id, double x, double y)
    {
        Get(id);
        _layout[id] = (Math.Round(x), Math.Round(y));
        Revision++;
    }

    public ControlModule AsControlModule(Guid id) => Get(id) switch
    {
        ControlModule cm => cm,
        UnitInstance unit => ControlModule.ForUnit(unit),
        var other => throw new ProjectException(ProjectErrors.NotFound, $"{other.Name} is not a control module, Equipment module or Unit.")
    };

    public IEnumerable<ControlModule> ControlModulesAndUnits() =>
        Objects.OfType<ControlModule>().Concat(Objects.OfType<UnitInstance>().Select(ControlModule.ForUnit));

    /// <summary>
    /// Replaces the project-level interlocks of a CM, EM or Unit (G-172). Targets must be the object itself or an object below it.
    /// Each trip gets alarm tags on the owner; tags of trips that are gone are removed.
    /// </summary>
    public void SetInterlocks(Guid ownerId, IEnumerable<InterlockRule> rules, bool adoptTags = false)
    {
        var owner = Get(ownerId);
        var current = owner switch
        {
            ControlModule cm => cm.Rules,
            UnitInstance unit => unit.Rules,
            _ => throw new ProjectException(ProjectErrors.InvalidInterlock, $"{owner.Name} is not a control module, Equipment module or Unit.")
        };
        var list = InterlockRule.WithAlarmNames(rules).ToList();
        var below = Descendants(ownerId).Select(o => o.Id).ToHashSet();
        var tags = GetChildren(ownerId).OfType<Tag>().ToList();
        var oldTrips = current.Where(r => r.Kind == InterlockKind.Trip).Select(r => r.Alarm!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            var rule = list[i];
            var field = $"interlocks[{i}]";
            if (rule.TargetId == ownerId)
                rule.TargetId = null;
            if (rule.TargetId is { } target && !below.Contains(target))
                throw new ProjectException(ProjectErrors.InvalidInterlock, $"Interlock {i + 1}: the target must be {owner.Name} or an object below it.", $"{field}.target");
            if (rule.TargetId is { } t && Get(t) is not (ControlModule or UnitInstance))
                throw new ProjectException(ProjectErrors.InvalidInterlock, $"Interlock {i + 1}: the target must be a control module, Equipment module or Unit.", $"{field}.target");
            if (string.IsNullOrWhiteSpace(rule.Condition))
                throw new ProjectException(ProjectErrors.InvalidInterlock, $"Interlock {i + 1} has no condition.", $"{field}.condition");
            rule.Condition = rule.Condition.Trim();
            rule.Text = rule.Text.Trim();
            rule.Target = "";
            if (rule.Kind != InterlockKind.Trip)
                continue;
            if (!AlarmPriority.IsValid(rule.Priority))
                throw new ProjectException(ProjectErrors.InvalidPriority, AlarmPriority.RangeText, $"{field}.priority");
            if (rule.AlarmId is null || rule.AlarmId == Guid.Empty)
                rule.AlarmId = current.FirstOrDefault(r => r.Kind == InterlockKind.Trip && string.Equals(r.Alarm, rule.Alarm, StringComparison.OrdinalIgnoreCase))?.AlarmId ?? _newId();
            if (NameRules.Check(rule.Alarm!, ProjectSettings.TagNameMaxLength - 12) is { } error)
                throw new ProjectException(ProjectErrors.InvalidName, $"Interlock {i + 1}: {error}", $"{field}.alarm");
            if (!names.Add(rule.Alarm!) || (!adoptTags && !oldTrips.Contains(rule.Alarm!) && tags.Any(tag => tag.Group == TagGroup.Alm && tag.Name.StartsWith($"{rule.Alarm}.", StringComparison.OrdinalIgnoreCase))))
                throw new ProjectException(ProjectErrors.DuplicateName, $"Interlock {i + 1}: {owner.Name} already has an alarm '{rule.Alarm}'.", $"{field}.alarm");
        }
        foreach (var kind in new[] { InterlockKind.SwitchOn, InterlockKind.SwitchOff })
        {
            if (list.GroupBy(r => r.TargetId).Any(g => g.Count(r => r.Kind == kind) > InterlockRule.MaxPerKind))
                throw new ProjectException(ProjectErrors.InvalidInterlock, $"A target has at most {InterlockRule.MaxPerKind} {kind} interlocks.", "interlocks");
        }
        foreach (var tag in tags.Where(t => t.Group == TagGroup.Alm && oldTrips.Any(a => t.Name.StartsWith($"{a}.", StringComparison.OrdinalIgnoreCase)) && !names.Any(a => t.Name.StartsWith($"{a}.", StringComparison.OrdinalIgnoreCase))))
            Delete(tag.Id);
        foreach (var rule in list.Where(r => r.Kind == InterlockKind.Trip))
            foreach (var template in BaseBehaviour.AlarmTags(rule.Alarm!))
                if (!tags.Any(t => t.Group == TagGroup.Alm && string.Equals(t.Name, template.Name, StringComparison.OrdinalIgnoreCase)))
                    AddTag(ownerId, template.ToDefinition());
        current.Clear();
        current.AddRange(list);
        Revision++;
    }

    public void SetCommandWires(Guid controlModuleId, IEnumerable<CommandWire> wires)
    {
        var cm = Get<ControlModule>(controlModuleId);
        var list = wires.ToList();
        var commands = GetChildren(cm.Id).OfType<Tag>().Where(t => t.Group == TagGroup.Cmd && t.DataType == TagDataType.Bool)
            .Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
        {
            var wire = list[i];
            var field = $"wires[{i}]";
            if (Find(wire.SourceTagId) is not Tag { DataType: TagDataType.Bool } source)
                throw new ProjectException(ProjectErrors.InvalidWire, $"Wire {i}: the source must be a Bool tag.", $"{field}.source");
            if (source.ParentId == cm.Id && source.Group == TagGroup.Cmd)
                throw new ProjectException(ProjectErrors.InvalidWire, $"Wire {i}: a CM cannot be wired from its own commands.", $"{field}.source");
            if (wire.Mode == WireMode.Direct && string.IsNullOrWhiteSpace(wire.Command))
                throw new ProjectException(ProjectErrors.InvalidWire, $"Wire {i}: a Direct wire needs the command it writes.", $"{field}.command");
            if (wire.Mode != WireMode.Direct && wire.Command is not null)
                throw new ProjectException(ProjectErrors.InvalidWire, $"Wire {i}: only a Direct wire names a command.", $"{field}.command");
            foreach (var command in wire.Commands.Where(c => !commands.Contains(c)))
                throw new ProjectException(ProjectErrors.InvalidWire, $"Wire {i}: {GetPath(cm.Id)} has no command CMD.{command}.", $"{field}.command");
        }
        cm.Wires.Clear();
        cm.Wires.AddRange(list);
        Revision++;
    }

    public void SetTopology(IEnumerable<Device> devices, IEnumerable<Link> links)
    {
        var deviceList = devices.ToList();
        Topology.Replace(deviceList, links);
        var ids = deviceList.Select(d => d.Id).ToHashSet();
        foreach (var cm in Objects.OfType<ControlModule>().Where(c => c.ExecutionDeviceId is { } d && !ids.Contains(d)))
            cm.ExecutionDeviceId = null;
        foreach (var tag in Tags.Where(t => t.Origin is { } o && !ids.Contains(o.DeviceId)))
            tag.Origin = null;
        Revision++;
    }

    public void SetExecutionDevice(Guid objectId, Guid? deviceId)
    {
        if (deviceId is { } id)
        {
            var device = Topology.Device(id) ?? throw new ProjectException(ProjectErrors.NotFound, $"Device {id} does not exist.", "deviceId");
            if (!device.CanExecuteLogic)
                throw new ProjectException(ProjectErrors.InvalidTopology, $"{device.Name} is a {device.Role} device and cannot run logic.", "deviceId");
        }
        switch (Get(objectId))
        {
            case ControlModule cm:
                cm.ExecutionDeviceId = deviceId;
                break;
            default:
                throw new ProjectException(ProjectErrors.InvalidTopology, "Only control modules run on a device.");
        }
        Revision++;
    }

    public void SetInitialValue(Guid tagId, System.Text.Json.Nodes.JsonNode? value)
    {
        Get<Tag>(tagId).InitialValue = value?.DeepClone();
        Revision++;
    }

    public void SetOrigin(Guid tagId, TagOrigin? origin)
    {
        var tag = Get<Tag>(tagId);
        if (origin is not null)
        {
            if (tag.Group != TagGroup.Fin)
                throw new ProjectException(ProjectErrors.InvalidTopology, $"{GetPath(tag.Id)}: only FIN tags have an origin.", "tag");
            if (Topology.Device(origin.DeviceId) is null)
                throw new ProjectException(ProjectErrors.NotFound, $"Device {origin.DeviceId} does not exist.", "deviceId");
            if (string.IsNullOrWhiteSpace(origin.Address))
                throw new ProjectException(ProjectErrors.InvalidTopology, $"{GetPath(tag.Id)}: the origin needs an address.", "address");
        }
        tag.Origin = origin;
        Revision++;
    }

    public void SetCommandInputs(Guid controlModuleId, CommandInputConfig? configuration)
    {
        Get<ControlModule>(controlModuleId).CommandInputs = configuration;
        Revision++;
    }

    /// <summary>Overrides the priority of one blueprint alarm on this CM (null = the blueprint's).</summary>
    public void SetAlarmPriority(Guid controlModuleId, string alarm, int? priority)
    {
        var cm = Get<ControlModule>(controlModuleId);
        if (priority is { } value)
        {
            if (!AlarmPriority.IsValid(value))
                throw new ProjectException(ProjectErrors.InvalidPriority, AlarmPriority.RangeText, "priority");
            cm.Priorities[alarm] = value;
        }
        else
            cm.Priorities.Remove(alarm);
        Revision++;
    }

    public void CheckNewChild(string name, Guid? parentId, ObjectKind kind, bool equipmentModule = false)
    {
        CheckName(name);
        CheckParent(kind, parentId, equipmentModule);
        if (kind != ObjectKind.Tag && parentId is { } pid && Find(pid) is UnitInstance && ReservedInContainer(name))
            throw new ProjectException(ProjectErrors.InvalidName, $"'{name}' is a tag group; a member of a Unit or Equipment module cannot use that name.");
        if (GetChildren(parentId).Any(s => s.Kind != ObjectKind.Tag && string.Equals(s.PathSegment, name, StringComparison.OrdinalIgnoreCase)))
            throw new ProjectException(ProjectErrors.DuplicateName, $"The name '{name}' is already used here.");
    }

    public IReadOnlyList<ProjectObject> Descendants(Guid id)
    {
        var result = new List<ProjectObject>();
        Collect(Get(id), result);
        return result;
    }

    public Tag AddTag(Guid parentId, TagDefinition definition, Guid? id = null, string? symbolKey = null)
    {
        if (definition.Group == TagGroup.Alm)
            CheckAlarmTagName(definition.Name);
        else
            CheckName(definition.Name, ProjectSettings.TagNameMaxLength);
        CheckParent(ObjectKind.Tag, parentId);
        var tagId = id ?? _newId();
        var key = symbolKey ?? NewSymbolKey(tagId);
        if (!_symbolKeys.Add(key))
            throw new ProjectException(ProjectErrors.DuplicateName, $"Symbol key {key} is already in use.");
        try
        {
            return Insert(new Tag(tagId, parentId, key, definition));
        }
        catch
        {
            _symbolKeys.Remove(key);
            throw;
        }
    }

    public void Rename(Guid id, string newName)
    {
        var obj = Get(id);
        if (obj.Name == newName)
            return;
        CheckName(newName, obj.Kind == ObjectKind.Tag ? ProjectSettings.TagNameMaxLength : null);
        var oldName = obj.Name;
        obj.Name = newName;
        try
        {
            CheckUniqueSegment(obj, obj.ParentId);
        }
        catch
        {
            obj.Name = oldName;
            throw;
        }
        Revision++;
    }

    public void Move(Guid id, Guid? newParentId)
    {
        var obj = Get(id);
        if (obj.ParentId == newParentId)
            return;
        CheckParent(obj.Kind, newParentId, obj is UnitInstance { IsEquipmentModule: true });
        for (var current = newParentId; current is not null; current = Get(current.Value).ParentId)
        {
            if (current == id)
                throw new ProjectException(ProjectErrors.Cycle, "An object cannot be moved into itself.");
        }
        CheckUniqueSegment(obj, newParentId);
        if (obj.ParentId is { } oldParent && Find(oldParent) is UnitInstance container)
            foreach (var key in container.Members.Where(m => m.Value == id).Select(m => m.Key).ToList())
                container.Members.Remove(key);
        ChildList(obj.ParentId).Remove(id);
        obj.ParentId = newParentId;
        ChildList(newParentId).Add(id);
        Revision++;
    }

    public IReadOnlyList<ProjectObject> Delete(Guid id)
    {
        var root = Get(id);
        var removed = new List<ProjectObject>();
        Collect(root, removed);
        ChildList(root.ParentId).Remove(id);
        var removedTags = removed.OfType<Tag>().Select(t => t.Id).ToHashSet();
        foreach (var cm in Objects.OfType<ControlModule>())
            cm.Wires.RemoveAll(w => removedTags.Contains(w.SourceTagId));
        var removedIds = removed.Select(o => o.Id).ToHashSet();
        foreach (var unit in Objects.OfType<UnitInstance>())
            foreach (var key in unit.Members.Where(m => removedIds.Contains(m.Value)).Select(m => m.Key).ToList())
                unit.Members.Remove(key);
        foreach (var rules in Objects.OfType<ControlModule>().Select(c => c.Rules).Concat(Objects.OfType<UnitInstance>().Select(u => u.Rules)))
            rules.RemoveAll(r => r.TargetId is { } target && removedIds.Contains(target));
        foreach (var obj in removed)
        {
            _layout.Remove(obj.Id);
            _objects.Remove(obj.Id);
            _children.Remove(obj.Id);
            if (obj is Tag tag)
                _symbolKeys.Remove(tag.SymbolKey);
        }
        Revision++;
        return removed;
    }

    public string GetPath(Guid id)
    {
        var segments = new Stack<string>();
        for (ProjectObject? obj = Get(id); obj is not null; obj = obj.ParentId is { } p ? Get(p) : null)
            segments.Push(obj.PathSegment);
        return string.Join('.', segments);
    }

    private void Collect(ProjectObject obj, List<ProjectObject> into)
    {
        into.Add(obj);
        foreach (var child in GetChildren(obj.Id))
            Collect(child, into);
    }

    private T Insert<T>(T obj) where T : ProjectObject
    {
        if (_objects.ContainsKey(obj.Id))
            throw new ProjectException(ProjectErrors.DuplicateName, $"Object id {obj.Id} is already in use.");
        CheckUniqueSegment(obj, obj.ParentId);
        _objects.Add(obj.Id, obj);
        ChildList(obj.ParentId).Add(obj.Id);
        Revision++;
        return obj;
    }

    private List<Guid> ChildList(Guid? parentId)
    {
        var key = parentId ?? Guid.Empty;
        if (!_children.TryGetValue(key, out var list))
            _children[key] = list = [];
        return list;
    }

    private static void CheckAlarmTagName(string name)
    {
        var parts = name.Split('.');
        if (parts.Length != 2 || name.Length > ProjectSettings.TagNameMaxLength
            || parts.Any(p => NameRules.Check(p, ProjectSettings.TagNameMaxLength) is not null))
            throw new ProjectException(ProjectErrors.InvalidName, $"Alarm tag '{name}' must look like <alarm>.<field>.");
    }

    private void CheckName(string name, int? maxLength = null)
    {
        if (NameRules.Check(name, maxLength ?? Settings.MaxNameLength) is { } error)
            throw new ProjectException(ProjectErrors.InvalidName, error);
    }

    private void CheckUniqueSegment(ProjectObject obj, Guid? parentId)
    {
        if (obj is not Tag && parentId is { } pid && Find(pid) is UnitInstance && ReservedInContainer(obj.Name))
            throw new ProjectException(ProjectErrors.InvalidName, $"'{obj.Name}' is a tag group; a member of a Unit or Equipment module cannot use that name.");
        var clash = GetChildren(parentId).Any(sibling =>
            sibling.Id != obj.Id && string.Equals(sibling.PathSegment, obj.PathSegment, StringComparison.OrdinalIgnoreCase));
        if (clash)
            throw new ProjectException(ProjectErrors.DuplicateName, $"The name '{obj.Name}' is already used here.");
    }

    private static bool ReservedInContainer(string name) => ApolloIQ.Core.Blueprints.BlueprintCatalog.ReservedNames.Contains(name);

    private void CheckParent(ObjectKind kind, Guid? parentId, bool equipmentModule = false)
    {
        var parent = parentId is { } id ? Get(id) : null;
        var allowed = kind switch
        {
            ObjectKind.Folder => parent is null or Folder,
            ObjectKind.ControlModule => parent is null or Folder or UnitInstance,
            ObjectKind.Unit => parent is null or Folder || (equipmentModule && parent is UnitInstance { IsEquipmentModule: false }),
            ObjectKind.Tag => parent is ControlModule or UnitInstance,
            _ => false
        };
        if (!allowed)
            throw new ProjectException(ProjectErrors.InvalidParent,
                $"A {kind} cannot be placed under {(parent is null ? "the project root" : $"a {parent.Kind}")}.");
    }

    private string NewSymbolKey(Guid id)
    {
        var hex = id.ToString("N").ToUpperInvariant();
        for (var length = 12; length <= hex.Length; length++)
        {
            var key = $"T_{hex[..length]}";
            if (!_symbolKeys.Contains(key))
                return key;
        }
        throw new ProjectException(ProjectErrors.DuplicateName, $"No free symbol key for tag {id}.");
    }
}
