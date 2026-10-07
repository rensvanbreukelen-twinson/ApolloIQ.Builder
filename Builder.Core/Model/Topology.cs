namespace Builder.Core.Model;

public enum DeviceRole
{
    Plc,
    Scada,
    ThirdParty
}

public enum LinkClass
{
    Control,
    Monitoring
}

public sealed record Device(Guid Id, string Name, DeviceRole Role, string Description = "")
{
    public bool CanExecuteLogic => Role == DeviceRole.Plc;
}

public sealed record Link(Guid Id, Guid From, Guid To, string Protocol, LinkClass Class);

public sealed record TagOrigin(Guid DeviceId, string Source, string Address);

public sealed class Topology
{
    private readonly List<Device> _devices = [];
    private readonly List<Link> _links = [];

    public IReadOnlyList<Device> Devices => _devices;

    public IReadOnlyList<Link> Links => _links;

    public bool IsEmpty => _devices.Count == 0;

    public Device? Device(Guid id) => _devices.FirstOrDefault(d => d.Id == id);

    internal void Replace(IEnumerable<Device> devices, IEnumerable<Link> links)
    {
        var deviceList = devices.ToList();
        var linkList = links.ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < deviceList.Count; i++)
        {
            if (NameRules.Check(deviceList[i].Name, 32) is { } error)
                throw new ProjectException(ProjectErrors.InvalidTopology, $"Device {i}: {error}", $"devices[{i}].name");
            if (!names.Add(deviceList[i].Name))
                throw new ProjectException(ProjectErrors.InvalidTopology, $"Device {i}: duplicate name '{deviceList[i].Name}'.", $"devices[{i}].name");
        }
        if (deviceList.Select(d => d.Id).Distinct().Count() != deviceList.Count)
            throw new ProjectException(ProjectErrors.InvalidTopology, "Device IDs must be unique.", "devices");
        var ids = deviceList.Select(d => d.Id).ToHashSet();
        for (var i = 0; i < linkList.Count; i++)
        {
            var link = linkList[i];
            if (!ids.Contains(link.From) || !ids.Contains(link.To))
                throw new ProjectException(ProjectErrors.InvalidTopology, $"Link {i} connects a device that does not exist.", $"links[{i}]");
            if (link.From == link.To)
                throw new ProjectException(ProjectErrors.InvalidTopology, $"Link {i} connects a device to itself.", $"links[{i}]");
        }
        _devices.Clear();
        _devices.AddRange(deviceList);
        _links.Clear();
        _links.AddRange(linkList);
    }
}
