using Builder.Core.Model;
using Builder.Core.Tags;

namespace Builder.Core.Types;

public static class PicBehaviour
{
    public const string Builtin = "PriorityInputControl";
    public const double HmiHoldTimeoutSeconds = 0.5;

    public static void Configure(Project project, Guid controlModuleId, PicConfiguration configuration)
    {
        var cm = project.Get<ControlModule>(controlModuleId);
        Check(configuration);
        var wanted = configuration.Rows
            .SelectMany(r => new[] { (r.On, r.OnCommand, r, true), (r.Off, r.OffCommand, r, false) })
            .Where(x => x.Item1 is not null)
            .ToDictionary(x => x.Item2, x => (Row: x.r, On: x.Item4), StringComparer.Ordinal);
        var existing = project.GetChildren(cm.Id).OfType<Tag>().Where(t => t.Group == TagGroup.Cmd).ToList();
        foreach (var tag in existing.Where(t => IsRowTag(cm, t.Name) && !wanted.ContainsKey(t.Name)))
            project.Delete(tag.Id);
        foreach (var (name, (row, on)) in wanted)
        {
            if (existing.All(t => t.Name != name))
                project.AddTag(cm.Id, new TagDefinition(name, TagGroup.Cmd, TagDataType.Bool, TagDirection.In, TagKind.Internal, null, null,
                    $"{(on ? configuration.OnLabel : configuration.OffLabel)} request from {row.Name} ({row.Source}, {row.Kind})"));
        }
        project.SetPic(cm.Id, configuration);
    }

    private static bool IsRowTag(ControlModule cm, string name) =>
        cm.Pic?.Rows.Any(r => r.OnCommand == name || r.OffCommand == name) == true;

    public static void Check(PicConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.OnLabel) || string.IsNullOrWhiteSpace(configuration.OffLabel))
            throw new ProjectException(ProjectErrors.InvalidPic, "Both labels are required, for example Open and Close.", "labels");
        if (configuration.Rows.Count > PicConfiguration.MaxRows)
            throw new ProjectException(ProjectErrors.InvalidPic, $"A PIC has at most {PicConfiguration.MaxRows} input rows.", "rows");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < configuration.Rows.Count; i++)
        {
            var row = configuration.Rows[i];
            if (NameRules.Check(row.Name, 24) is { } error)
                throw new ProjectException(ProjectErrors.InvalidPic, $"Row {i}: {error}", $"rows[{i}].name");
            if (row.Name.Equals("auto", StringComparison.OrdinalIgnoreCase) && row.Source != PicSource.Auto)
                throw new ProjectException(ProjectErrors.InvalidPic, $"Row {i}: the name 'auto' is reserved for the Auto source.", $"rows[{i}].name");
            if (!names.Add(row.Name))
                throw new ProjectException(ProjectErrors.InvalidPic, $"Row {i}: duplicate row '{row.Name}'.", $"rows[{i}].name");
            if (row.On is null && row.Off is null)
                throw new ProjectException(ProjectErrors.InvalidPic, $"Row {row.Name}: give a priority for at least one direction.", $"rows[{i}]");
            if (!CommandInputLimits.IsPriority(row.On) || !CommandInputLimits.IsPriority(row.Off))
                throw new ProjectException(ProjectErrors.InvalidPic, $"Row {row.Name}: {CommandInputLimits.PriorityText}", $"rows[{i}]");
            if (row.Source == PicSource.Auto && row.InAuto is not (PicInAuto.Only or PicInAuto.Normal))
                throw new ProjectException(ProjectErrors.InvalidPic, $"Row {row.Name}: the Auto row counts only in auto.", $"rows[{i}].inAuto");
        }
    }
}
