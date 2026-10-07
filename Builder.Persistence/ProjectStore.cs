using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Persistence.Files;

namespace Builder.Persistence;

public sealed record LoadedProject(Guid Id, string Name, Project Project);

public static class ProjectStore
{
    public const string Schema = "apolloiq.project/1";
    public const string FolderSchema = "apolloiq.folder/1";
    public const string ControlModuleSchema = "apolloiq.cm/1";
    public const string UnitSchema = "apolloiq.unit/1";
    public const string UnitsDirectory = "units";
    public const string LayoutSchema = "apolloiq.layout/1";
    public const string LayoutFileName = "layout.json";
    public const string TopologySchema = "apolloiq.topology/1";
    public const string TopologyFileName = "topology.json";
    public const string ProjectFileName = "project.json";
    public const string FoldersDirectory = "folders";
    public const string ControlModulesDirectory = "control-modules";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(string directory, Guid projectId, string projectName, Project project)
    {
        Directory.CreateDirectory(directory);
        WriteIfChanged(Path.Combine(directory, ProjectFileName), new ProjectFile
        {
            Id = projectId,
            Name = projectName,
            MaxNameLength = project.Settings.MaxNameLength
        });

        var folders = project.Objects.OfType<Folder>().ToDictionary(f => FileName(f.Id), f => (object)new FolderFile
        {
            Id = f.Id,
            Name = f.Name,
            ParentId = f.ParentId
        });
        var modules = project.Objects.OfType<ControlModule>().ToDictionary(cm => FileName(cm.Id), cm => (object)ToFile(project, cm));
        var topologyPath = Path.Combine(directory, TopologyFileName);
        if (!project.Topology.IsEmpty || File.Exists(topologyPath))
            WriteIfChanged(topologyPath, new TopologyFile
            {
                Devices = project.Topology.Devices.Select(d => new DeviceEntry { Id = d.Id, Name = d.Name, Role = d.Role.ToString(), Description = d.Description }).ToList(),
                Links = project.Topology.Links.Select(l => new LinkEntry { Id = l.Id, From = l.From, To = l.To, Protocol = l.Protocol, Class = l.Class.ToString() }).ToList()
            });

        var units = project.Objects.OfType<UnitInstance>().ToDictionary(u => FileName(u.Id), u => (object)new UnitFile
        {
            Id = u.Id,
            Name = u.Name,
            ParentId = u.ParentId,
            BlueprintId = u.BlueprintId,
            BlueprintVersion = u.BlueprintVersion,
            EquipmentModule = u.IsEquipmentModule,
            Members = new SortedDictionary<string, Guid>(u.RoleMembers.ToDictionary(m => m.Key, m => m.Value), StringComparer.Ordinal),
            Tags = TagEntries(project, u.Id),
            CommandInputs = u.CommandInputs,
            Interlocks = InterlockEntry.From(u.Interlocks)
        });
        if (units.Count > 0 || Directory.Exists(Path.Combine(directory, UnitsDirectory)))
            SyncDirectory(Path.Combine(directory, UnitsDirectory), units);
        var layoutPath = Path.Combine(directory, LayoutFileName);
        if (project.Layout.Count > 0 || File.Exists(layoutPath))
            WriteIfChanged(layoutPath, new LayoutFile
            {
                Positions = new SortedDictionary<Guid, LayoutEntry>(project.Layout.ToDictionary(p => p.Key, p => new LayoutEntry { X = p.Value.X, Y = p.Value.Y }))
            });

        SyncDirectory(Path.Combine(directory, FoldersDirectory), folders);
        SyncDirectory(Path.Combine(directory, ControlModulesDirectory), modules);
    }

    public static LoadedProject Load(string directory)
    {
        var errors = new List<string>();
        var projectPath = Path.Combine(directory, ProjectFileName);
        if (!File.Exists(projectPath))
            throw new ProjectLoadException([$"{projectPath} does not exist."]);

        var header = Read<ProjectFile>(projectPath, errors);
        if (header is null)
            throw new ProjectLoadException(errors);
        if (header.Schema != Schema)
            throw new ProjectLoadException([$"{ProjectFileName}: unsupported schema '{header.Schema}'."]);

        var project = new Project(new ProjectSettings
        {
            MaxNameLength = header.MaxNameLength > 0 ? header.MaxNameLength : ProjectSettings.DefaultMaxNameLength
        });

        var topologyPath = Path.Combine(directory, TopologyFileName);
        if (File.Exists(topologyPath) && Read<TopologyFile>(topologyPath, errors) is { } topology)
        {
            Guard(TopologyFileName, errors, () => project.SetTopology(
                topology.Devices.Select(d => new Device(d.Id, d.Name, ParseAny<DeviceRole>(d.Role, "device role"), d.Description)),
                topology.Links.Select(l => new Link(l.Id, l.From, l.To, l.Protocol, ParseAny<LinkClass>(l.Class, "link class")))));
        }

        var folders = ReadAll<FolderFile>(Path.Combine(directory, FoldersDirectory), FolderSchema, errors);
        var modules = ReadAll<ControlModuleFile>(Path.Combine(directory, ControlModulesDirectory), ControlModuleSchema, errors);
        var units = ReadAll<UnitFile>(Path.Combine(directory, UnitsDirectory), UnitSchema, errors);
        var layoutFile = Path.Combine(directory, LayoutFileName);
        var layout = File.Exists(layoutFile) ? Read<LayoutFile>(layoutFile, errors) : null;
        if (errors.Count > 0)
            throw new ProjectLoadException(errors);

        var pending = folders.ToList();
        while (pending.Count > 0)
        {
            var ready = pending
                .Where(p => p.Data.ParentId is null || project.Find(p.Data.ParentId.Value) is not null)
                .OrderBy(p => p.Data.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ready.Count == 0)
            {
                errors.AddRange(pending.Select(p => $"{p.File}: parent {p.Data.ParentId} does not exist or forms a cycle."));
                break;
            }
            foreach (var (file, data) in ready)
            {
                pending.Remove((file, data));
                Guard(file, errors, () => project.AddFolder(data.Name, data.ParentId, data.Id));
            }
        }

        foreach (var (file, data) in units.OrderBy(u => u.Data.EquipmentModule).ThenBy(u => u.Data.Name, StringComparer.OrdinalIgnoreCase))
        {
            Guard(file, errors, () =>
            {
                var unit = project.AddUnit(data.Name, data.ParentId, data.BlueprintId, data.BlueprintVersion, data.Id, data.EquipmentModule);
                foreach (var tag in data.Tags)
                    AddTag(project, unit.Id, tag, file);
                if (data.CommandInputs is { } inputs)
                    project.SetUnitCommandInputs(unit.Id, inputs);
            });
        }

        foreach (var (file, data) in modules.OrderBy(m => m.Data.Name, StringComparer.OrdinalIgnoreCase))
        {
            Guard(file, errors, () =>
            {
                var cm = project.AddControlModule(data.Name, data.ParentId, data.BlueprintId, data.BlueprintVersion, data.Id);
                if (data.CommandInputs is { } inputs)
                    project.SetCommandInputs(cm.Id, inputs);
                foreach (var (alarm, priority) in data.AlarmPriority ?? [])
                    project.SetAlarmPriority(cm.Id, alarm, priority);
                foreach (var tag in data.Tags)
                    AddTag(project, cm.Id, tag, file);
                if (data.ExecutionDeviceId is { } device)
                    project.SetExecutionDevice(cm.Id, device);
            });
        }

        foreach (var (file, data) in units)
            foreach (var (role, member) in data.Members)
                Guard(file, errors, () => project.SetUnitMember(data.Id, role, member));
        foreach (var (id, position) in layout?.Positions ?? [])
            if (project.Find(id) is not null)
                project.SetPosition(id, position.X, position.Y);

        foreach (var (file, data) in units.Where(u => u.Data.Interlocks is { Count: > 0 }))
            Guard(file, errors, () => project.SetInterlocks(data.Id, data.Interlocks!.Select(i => i.ToRule()), adoptTags: true));

        foreach (var (file, data) in modules)
        {
            if (data.Interlocks is { Count: > 0 } rules)
                Guard(file, errors, () => project.SetInterlocks(data.Id, rules.Select(i => i.ToRule()), adoptTags: true));
            if (data.Wires is { Count: > 0 } wires)
                Guard(file, errors, () => project.SetCommandWires(data.Id, wires.Select(w =>
                    new CommandWire(w.Source,
                        Enum.TryParse<WireMode>(w.Mode, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
                            ? mode
                            : throw new ProjectException(ProjectErrors.InvalidWire, $"Unknown wire mode '{w.Mode}'."),
                        w.Command))));
        }

        if (errors.Count > 0)
            throw new ProjectLoadException(errors);
        return new LoadedProject(header.Id, header.Name, project);
    }

    private static ControlModuleFile ToFile(Project project, ControlModule cm) => new()
    {
        Id = cm.Id,
        Name = cm.Name,
        ParentId = cm.ParentId,
        BlueprintId = cm.BlueprintId,
        BlueprintVersion = cm.BlueprintVersion,
        Interlocks = InterlockEntry.From(cm.Interlocks),
        AlarmPriority = cm.AlarmPriorities.Count == 0
            ? null
            : new SortedDictionary<string, int>(cm.AlarmPriorities.ToDictionary(a => a.Key, a => a.Value), StringComparer.Ordinal),
        Wires = cm.CommandWires.Count == 0
            ? null
            : cm.CommandWires.Select(w => new WireEntry { Source = w.SourceTagId, Mode = w.Mode.ToString(), Command = w.Command }).ToList(),
        ExecutionDeviceId = cm.ExecutionDeviceId,
        CommandInputs = cm.CommandInputs,
        Tags = TagEntries(project, cm.Id)
    };

    private static List<TagEntry> TagEntries(Project project, Guid ownerId) =>
        project.GetChildren(ownerId).OfType<Tag>()
            .OrderBy(t => t.Group)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new TagEntry
            {
                Id = t.Id,
                Group = t.Group.Code(),
                Name = t.Name,
                DataType = t.DataType.ToString(),
                Direction = t.Direction.ToString(),
                Kind = t.TagKind.ToString(),
                SymbolKey = t.SymbolKey,
                Initial = t.InitialValue?.DeepClone(),
                Enum = t.EnumType,
                Unit = t.Unit,
                Description = t.Description,
                Origin = t.Origin is { } o ? new OriginEntry { Device = o.DeviceId, Source = o.Source, Address = o.Address } : null
            })
            .ToList();

    private static TagDefinition ToDefinition(TagEntry tag, string file)
    {
        if (!TagGroupNames.TryParse(tag.Group, out var group))
            throw new ProjectException(ProjectErrors.InvalidName, $"Tag {tag.Id}: unknown group '{tag.Group}'.");
        return new TagDefinition(
            tag.Name,
            group,
            ParseEnum<TagDataType>(tag.DataType, tag, "data type"),
            ParseEnum<TagDirection>(tag.Direction, tag, "direction"),
            ParseEnum<TagKind>(tag.Kind, tag, "kind"),
            tag.Initial?.DeepClone(),
            tag.Enum,
            tag.Description,
            tag.Unit);
    }

    private static void AddTag(Project project, Guid ownerId, TagEntry entry, string file)
    {
        var tag = project.AddTag(ownerId, ToDefinition(entry, file), entry.Id, entry.SymbolKey);
        if (entry.Origin is { } origin)
            project.SetOrigin(tag.Id, new TagOrigin(origin.Device, origin.Source, origin.Address));
    }

    private static T ParseAny<T>(string text, string what) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value
            : throw new ProjectException(ProjectErrors.InvalidTopology, $"Unknown {what} '{text}'.");

    private static T ParseEnum<T>(string text, TagEntry tag, string what) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value
            : throw new ProjectException(ProjectErrors.InvalidName, $"Tag {tag.Id}: unknown {what} '{text}'.");

    private static void Guard(string file, List<string> errors, Action action)
    {
        try
        {
            action();
        }
        catch (ProjectException ex)
        {
            errors.Add($"{file}: {ex.Message}");
        }
    }

    private static string FileName(Guid id) => $"{id:D}.json";

    private static void SyncDirectory(string directory, Dictionary<string, object> files)
    {
        Directory.CreateDirectory(directory);
        foreach (var (name, content) in files)
            WriteIfChanged(Path.Combine(directory, name), content);
        foreach (var existing in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (!files.ContainsKey(Path.GetFileName(existing)))
                File.Delete(existing);
        }
    }

    private static void WriteIfChanged(string path, object content)
    {
        var text = JsonSerializer.Serialize(content, content.GetType(), Options) + "\n";
        if (File.Exists(path) && File.ReadAllText(path) == text)
            return;
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static List<(string File, T Data)> ReadAll<T>(string directory, string schema, List<string> errors) where T : class
    {
        var result = new List<(string, T)>();
        if (!Directory.Exists(directory))
            return result;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var data = Read<T>(path, errors);
            if (data is null)
                continue;
            var actual = data switch
            {
                FolderFile f => f.Schema,
                ControlModuleFile c => c.Schema,
                UnitFile u => u.Schema,
                TopologyFile t => t.Schema,
                _ => schema
            };
            if (actual != schema)
            {
                errors.Add($"{Path.GetFileName(path)}: unsupported schema '{actual}'.");
                continue;
            }
            result.Add((Path.GetFileName(path), data));
        }
        return result;
    }

    private static T? Read<T>(string path, List<string> errors) where T : class
    {
        try
        {
            var data = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
            if (data is null)
                errors.Add($"{Path.GetFileName(path)}: empty file.");
            return data;
        }
        catch (JsonException ex)
        {
            errors.Add($"{Path.GetFileName(path)}({(ex.LineNumber ?? 0) + 1}): {ex.Message}");
            return null;
        }
    }
}
