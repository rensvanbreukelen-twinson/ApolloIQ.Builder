using Microsoft.AspNetCore.SignalR;

namespace Builder.Backend.Hubs;

public sealed class SimulationHub : Hub
{
    public static string Group(Guid projectId) => $"simulation:{projectId}";

    public Task Join(Guid projectId) => Groups.AddToGroupAsync(Context.ConnectionId, Group(projectId));

    public Task Leave(Guid projectId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(projectId));
}
