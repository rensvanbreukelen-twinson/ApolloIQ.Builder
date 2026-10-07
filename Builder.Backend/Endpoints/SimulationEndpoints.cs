using System.Text.Json;
using Builder.Backend.Services;
using Builder.Core.Tags;
using Builder.Simulator;

namespace Builder.Backend.Endpoints;

public sealed record SimulationDto(SimStatus Status, IReadOnlyList<string> Errors, IReadOnlyList<SimControlModule> ControlModules);

public sealed record SimulationTagDto(Guid Id, string Path, string SymbolKey, string Group, string DataType, string? EnumType,
    object? Value, bool Good, bool Forced);

public sealed record StepRequest(int Cycles = 1);

public sealed record SpeedRequest(double Speed);

public sealed record TagValueRequest(string Tag, JsonElement Value);

public sealed record TagRequest(string Tag);

public sealed record QualityRequest(string Tag, bool Bad);

public sealed record LinkStateRequest(bool Down);

public static class SimulationEndpoints
{
    public static void MapSimulationApi(this WebApplication app)
    {
        app.MapGet("/api/simulator/tcp", (SimulatorTcpService tcp, SimulationHost host) =>
            new { port = tcp.Port, clients = tcp.ConnectedClients, projectId = host.ServedProject });

        var sim = app.MapGroup("/api/projects/{projectId:guid}/simulation");

        sim.MapPost("/serve", (Guid projectId, SimulationHost host, SimulatorTcpService tcp) =>
        {
            host.Serve(projectId);
            return new { port = tcp.Port, clients = tcp.ConnectedClients, projectId = host.ServedProject };
        });

        sim.MapGet("", (Guid projectId, SimulationHost host) => Dto(host.Get(projectId)));

        sim.MapPost("/start", (Guid projectId, SimulationHost host) =>
        {
            var session = host.Get(projectId);
            host.Serve(projectId);
            session.Start();
            return Dto(session);
        });

        sim.MapPost("/pause", async (Guid projectId, SimulationHost host) =>
        {
            var session = host.Get(projectId);
            await session.PauseAsync();
            return Dto(session);
        });

        sim.MapPost("/step", (Guid projectId, StepRequest? request, SimulationHost host) =>
        {
            var session = host.Get(projectId);
            session.Step(request?.Cycles ?? 1);
            return Dto(session);
        });

        sim.MapPost("/reset", async (Guid projectId, SimulationHost host) =>
        {
            var session = host.Get(projectId);
            await session.PauseAsync();
            host.Reset(projectId);
            return Dto(session);
        });

        sim.MapPost("/reload", (Guid projectId, SimulationHost host) =>
        {
            host.Reload(projectId);
            return Dto(host.Get(projectId));
        });

        sim.MapPut("/speed", (Guid projectId, SpeedRequest request, SimulationHost host) =>
        {
            var session = host.Get(projectId);
            session.Speed = request.Speed;
            return Dto(session);
        });

        sim.MapGet("/tags", (Guid projectId, SimulationHost host) =>
        {
            var session = host.Get(projectId);
            var values = session.ReadAll().ToDictionary(v => v.Id);
            return session.Tags
                .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                .Select(t => ToDto(t, values[t.Id]))
                .ToList();
        });

        sim.MapGet("/links", (Guid projectId, SimulationHost host) => host.Get(projectId).Links());

        sim.MapPost("/links/{linkId:guid}", (Guid projectId, Guid linkId, LinkStateRequest request, SimulationHost host) =>
        {
            var session = host.Get(projectId);
            session.SetLinkDown(linkId, request.Down);
            return session.Links();
        });

        sim.MapGet("/diagnostics", (Guid projectId, SimulationHost host) => host.Get(projectId).Diagnostics);

        sim.MapGet("/alarms", (Guid projectId, SimulationHost host) => host.Get(projectId).Alarms());

        sim.MapPost("/write", (Guid projectId, TagValueRequest request, SimulationHost host) =>
            host.Get(projectId).Write(request.Tag, request.Value));

        sim.MapPost("/force", (Guid projectId, TagValueRequest request, SimulationHost host) =>
            host.Get(projectId).Force(request.Tag, request.Value));

        sim.MapPost("/unforce", (Guid projectId, TagRequest request, SimulationHost host) =>
            host.Get(projectId).Unforce(request.Tag));

        sim.MapPost("/unforce-all", (Guid projectId, SimulationHost host) =>
        {
            host.Get(projectId).UnforceAll();
            return Results.NoContent();
        });

        sim.MapPost("/quality", (Guid projectId, QualityRequest request, SimulationHost host) =>
            host.Get(projectId).SetBadQuality(request.Tag, request.Bad));
    }

    private static SimulationDto Dto(SimulationSession session) =>
        new(session.Snapshot(), session.Errors.Select(e => e.ToString()).ToList(), session.ControlModules());

    private static SimulationTagDto ToDto(SimTag tag, SimTagValue value) =>
        new(tag.Id, tag.Path, tag.SymbolKey, tag.Group.Code(), tag.DataType.ToString(), tag.EnumType,
            value.Value, value.Good, value.Forced);
}
