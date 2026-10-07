using Builder.Core.Tags;

namespace Builder.Core.Model;

public enum AccessKind
{
    Local,
    Direct,
    Relayed,
    Unreachable,
    Unbound
}

public sealed record TagAccess(
    Guid TagId,
    string Tag,
    Guid? OwnerDeviceId,
    Guid ConsumerDeviceId,
    string UsedBy,
    AccessKind Kind,
    IReadOnlyList<Guid> Path,
    IReadOnlyList<Guid> Links,
    bool Critical,
    bool SafetyViolation,
    string Address);

public sealed record BindingIssue(string Severity, string Code, string Subject, string Message);

public sealed record BindingReport(IReadOnlyList<TagAccess> Accesses, IReadOnlyList<BindingIssue> Issues)
{
    public int Errors => Issues.Count(i => i.Severity == "Error");

    public int Warnings => Issues.Count(i => i.Severity == "Warning");
}

public static class BindingResolver
{
    public static Guid? Owner(Project project, Tag tag)
    {
        if (tag.Group == TagGroup.Fin && tag.Origin is { } origin)
            return origin.DeviceId;
        return project.Find(tag.ParentId!.Value) switch
        {
            ControlModule cm => cm.ExecutionDeviceId,
            _ => null
        };
    }

    public static BindingReport Resolve(Project project)
    {
        var topology = project.Topology;
        var accesses = new List<TagAccess>();
        var issues = new List<BindingIssue>();
        if (topology.IsEmpty)
            return new BindingReport(accesses, [new BindingIssue("Info", "no_topology", "project", "No devices yet. Add the PLCs, SCADA and third-party devices to check the bindings.")]);

        var registry = new TagRegistry(project);
        void Access(Tag tag, Guid consumer, string usedBy, bool critical)
        {
            var owner = Owner(project, tag);
            var path = project.GetPath(tag.Id);
            if (owner is null)
            {
                accesses.Add(new TagAccess(tag.Id, path, null, consumer, usedBy, AccessKind.Unbound, [], [], critical, false, tag.SymbolKey));
                return;
            }
            if (owner == consumer)
            {
                var local = tag.Origin is { } o && o.DeviceId == consumer ? o.Address : tag.SymbolKey;
                accesses.Add(new TagAccess(tag.Id, path, owner, consumer, usedBy, AccessKind.Local, [consumer], [], critical, false, local));
                return;
            }
            var route = Route(topology, consumer, owner.Value, controlOnly: true) ?? Route(topology, consumer, owner.Value, controlOnly: false);
            if (route is null)
            {
                accesses.Add(new TagAccess(tag.Id, path, owner, consumer, usedBy, AccessKind.Unreachable, [], [], critical, false, ""));
                issues.Add(new BindingIssue("Error", "unreachable", path,
                    $"{Name(topology, consumer)} needs {path} ({usedBy}), but there is no link path to {Name(topology, owner.Value)}."));
                return;
            }
            var (devices, links) = route.Value;
            var unsafeRoute = critical && links.Any(l => topology.Links.First(x => x.Id == l).Class == LinkClass.Monitoring);
            var kind = devices.Count == 2 ? AccessKind.Direct : AccessKind.Relayed;
            var served = devices[1];
            var address = kind == AccessKind.Direct && tag.Origin is { } origin && origin.DeviceId == owner
                ? origin.Address
                : $"{Name(topology, served)}:{tag.SymbolKey}";
            accesses.Add(new TagAccess(tag.Id, path, owner, consumer, usedBy, kind, devices, links, critical, unsafeRoute, address));
            if (unsafeRoute)
                issues.Add(new BindingIssue("Error", "safety", path,
                    $"{usedBy} on {Name(topology, consumer)} uses {path} over a monitoring link ({string.Join(" → ", devices.Select(d => Name(topology, d)))}). Add a direct control link or move the logic (G-64)."));
            else if (critical && kind == AccessKind.Relayed)
                issues.Add(new BindingIssue("Warning", "relayed", path,
                    $"{usedBy} on {Name(topology, consumer)} gets {path} relayed via {string.Join(" → ", devices.Select(d => Name(topology, d)))}."));
        }

        foreach (var cm in project.Objects.OfType<ControlModule>().OrderBy(c => project.GetPath(c.Id), StringComparer.Ordinal))
        {
            var cmPath = project.GetPath(cm.Id);
            CheckOppositeCommands(project, cm, cmPath, issues);
            if (cm.ExecutionDeviceId is not { } device)
            {
                issues.Add(new BindingIssue("Warning", "not_deployed", cmPath, $"{cmPath} has no execution device."));
                continue;
            }
            var inputs = project.GetChildren(cm.Id).OfType<Tag>().Where(t => t.Group == TagGroup.Fin).ToList();
            foreach (var tag in inputs)
                Access(tag, device, cmPath, critical: true);
            var local = inputs.Where(t => t.Origin is null).Select(t => t.Name).ToList();
            if (local.Count > 0)
                issues.Add(new BindingIssue("Info", "local_input", cmPath,
                    $"{cmPath}: {local.Count} FIN tag{(local.Count == 1 ? " has" : "s have")} no origin and {(local.Count == 1 ? "is" : "are")} taken as local I/O of {Name(topology, device)} ({string.Join(", ", local)})."));
            foreach (var id in cm.Interlocks.SelectMany(r => ExpressionReferences.References(r.Condition)).Distinct())
            {
                var referenced = project.Find(id) switch
                {
                    Tag t => t,
                    ControlModule c => registry.FindByPath($"{project.GetPath(c.Id)}.STS.state"),
                    _ => null
                };
                if (referenced is not null)
                    Access(referenced, device, $"{cmPath} interlock", critical: true);
            }
            foreach (var wire in cm.CommandWires)
            {
                if (project.Find(wire.SourceTagId) is Tag source)
                    Access(source, device, $"{cmPath} command wire", critical: true);
            }
        }

        foreach (var scada in topology.Devices.Where(d => d.Role == DeviceRole.Scada))
        {
            foreach (var tag in project.Tags.OrderBy(t => project.GetPath(t.Id), StringComparer.Ordinal))
                Access(tag, scada.Id, "HMI", critical: false);
        }

        return new BindingReport(accesses, issues);
    }

    private static void CheckOppositeCommands(Project project, ControlModule cm, string cmPath, List<BindingIssue> issues)
    {
        var on = cm.CommandWires.Where(w => w.Mode is WireMode.On or WireMode.Toggle or WireMode.Maintained || w.Mode == WireMode.Direct && w.Command == CommandWire.SetOn)
            .Select(w => w.SourceTagId).ToHashSet();
        var off = cm.CommandWires.Where(w => w.Mode is WireMode.Off or WireMode.Toggle or WireMode.Maintained || w.Mode == WireMode.Direct && w.Command == CommandWire.SetOff)
            .Select(w => w.SourceTagId).ToHashSet();
        var owners = on.Concat(off).Select(id => project.Find(id)?.ParentId).Distinct().ToList();
        if (on.Count == 0 || off.Count == 0 || owners.Count < 2 || (on.Count == 1 && off.Count == 1 && on.SetEquals(off)))
            return;
        issues.Add(new BindingIssue("Warning", "opposite_commands", cmPath,
            $"{cmPath} has several sources that can give opposite commands at the same time. Put a PriorityInputControl in front of it (G-107)."));
    }

    private static string Name(Topology topology, Guid id) => topology.Device(id)?.Name ?? id.ToString();

    private static (List<Guid> Devices, List<Guid> Links)? Route(Topology topology, Guid from, Guid to, bool controlOnly)
    {
        var previous = new Dictionary<Guid, (Guid Device, Guid Link)>();
        var queue = new Queue<Guid>([from]);
        var seen = new HashSet<Guid> { from };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == to)
                break;
            foreach (var link in topology.Links.Where(l => (l.From == current || l.To == current) && (!controlOnly || l.Class == LinkClass.Control)))
            {
                var next = link.From == current ? link.To : link.From;
                if (seen.Add(next))
                {
                    previous[next] = (current, link.Id);
                    queue.Enqueue(next);
                }
            }
        }
        if (!seen.Contains(to))
            return null;
        var devices = new List<Guid> { to };
        var links = new List<Guid>();
        for (var at = to; at != from; at = previous[at].Device)
        {
            links.Insert(0, previous[at].Link);
            devices.Insert(0, previous[at].Device);
        }
        return (devices, links);
    }
}
