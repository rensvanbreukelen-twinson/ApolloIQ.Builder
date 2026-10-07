using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;

namespace Builder.Persistence.Export;

/// <summary>Per object: the interlock lines that act on it and the LOK tags the HMI binds to (G-172).</summary>
public static class HmiInterlocksExporter
{
    public static HmiInterlocks? Build(Project project, CmLibrary library, TagRegistry registry, ProjectObject target)
    {
        var path = project.GetPath(target.Id);
        HmiTagRef? Ref(string name) => registry.FindByPath($"{path}.{name}") is { } tag ? new HmiTagRef(tag.Id, $"{path}.{name}") : null;
        var canOn = Ref("LOK.can_on");
        if (canOn is null)
            return null;
        var on = new List<HmiInterlockLine>();
        var off = new List<HmiInterlockLine>();
        var trips = new List<HmiInterlockLine>();
        foreach (var source in InterlockSources.Targeting(project, library, target.Id))
        {
            var owner = project.GetPath(source.Owner.Id);
            var condition = InterlockDisplay.Condition(project, source);
            var text = InterlockDisplay.Text(project, source);
            var origin = source.FromBlueprint ? "blueprint" : "project";
            switch (source.Rule.Kind)
            {
                case InterlockKind.SwitchOn:
                    on.Add(new HmiInterlockLine(on.Count, text, condition, owner, source.Owner.Id, origin));
                    break;
                case InterlockKind.SwitchOff:
                    off.Add(new HmiInterlockLine(off.Count, text, condition, owner, source.Owner.Id, origin));
                    break;
                default:
                    var alarm = registry.FindByPath($"{owner}.ALM.{source.Rule.Alarm}.active");
                    trips.Add(new HmiInterlockLine(trips.Count, text, condition, owner, source.Owner.Id, origin, source.Rule.Alarm,
                        alarm is null ? null : new HmiTagRef(alarm.Id, $"{owner}.ALM.{source.Rule.Alarm}.active"), source.Rule.Severity,
                        source.Rule.Escalate.ToString()));
                    break;
            }
        }
        return new HmiInterlocks(canOn, Ref("LOK.can_off"), Ref("LOK.trip"), Ref("LOK.can_on_status"), Ref("LOK.can_off_status"), on, off, trips);
    }
}

public sealed record HmiTagRef(Guid Uid, string Name);

public sealed record HmiInterlocks(HmiTagRef CanOn, HmiTagRef? CanOff, HmiTagRef? Trip, HmiTagRef? CanOnStatus, HmiTagRef? CanOffStatus,
    IReadOnlyList<HmiInterlockLine> SwitchOn, IReadOnlyList<HmiInterlockLine> SwitchOff, IReadOnlyList<HmiInterlockLine> Trips);

public sealed record HmiInterlockLine(int Bit, string Text, string Condition, string DefinedBy, Guid DefinedByUid, string Origin,
    string? Alarm = null, HmiTagRef? AlarmActive = null, int? Severity = null, string? Escalate = null);
