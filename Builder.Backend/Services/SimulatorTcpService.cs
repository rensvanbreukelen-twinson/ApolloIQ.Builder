using System.Net;
using Builder.Simulator.Tcp;

namespace Builder.Backend.Services;

public sealed class SimulatorTcpService(BuilderOptions options, SimulationHost host, ILogger<SimulatorTcpService> logger) : IHostedService
{
    private JsonLineServer? _server;

    public int? Port => _server?.Port;

    public int ConnectedClients => _server?.ConnectedClients ?? 0;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.SimulatorTcpPort < 0)
        {
            logger.LogInformation("Simulator TCP server disabled");
            return Task.CompletedTask;
        }
        var server = new JsonLineServer(host.Served, IPAddress.Parse(options.SimulatorTcpAddress), options.SimulatorTcpPort);
        server.Error += message => logger.LogWarning("Simulator TCP client error: {Message}", message);
        try
        {
            server.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            logger.LogError("Simulator TCP server could not listen on {Address}:{Port}: {Message}", options.SimulatorTcpAddress,
                options.SimulatorTcpPort, ex.Message);
            return Task.CompletedTask;
        }
        _server = server;
        logger.LogInformation("Simulator TCP server listening on {Address}:{Port}", options.SimulatorTcpAddress, server.Port);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }
}
