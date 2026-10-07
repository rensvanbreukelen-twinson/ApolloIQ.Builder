using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Builder.Simulator.Tcp;

public sealed class JsonLineServer(Func<SimulationSession?> resolve, IPAddress address, int port) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _clients = [];
    private readonly Lock _gate = new();
    private TcpListener? _listener;
    private Task _accept = Task.CompletedTask;

    public int Port { get; private set; }

    public int ConnectedClients
    {
        get
        {
            lock (_gate)
                return _clients.Count(t => !t.IsCompleted);
        }
    }

    public event Action<string>? Error;

    public void Start()
    {
        _listener = new TcpListener(address, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoop(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener?.Stop();
        Task[] clients;
        lock (_gate)
            clients = [.. _clients];
        try
        {
            await Task.WhenAll([_accept, .. clients]);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
        {
        }
        _stop.Dispose();
    }

    public string Handle(string line)
    {
        Request? request;
        try
        {
            request = JsonSerializer.Deserialize<Request>(line, Json);
        }
        catch (JsonException ex)
        {
            return Fail($"Invalid JSON: {ex.Message}");
        }
        if (request is null || string.IsNullOrWhiteSpace(request.Action) || string.IsNullOrWhiteSpace(request.Address))
            return Fail("A request needs an action and an address.");

        var session = resolve();
        if (session is null)
            return Fail("No simulation is active.");
        try
        {
            switch (request.Action.ToLowerInvariant())
            {
                case "read":
                {
                    var value = session.Read(request.Address);
                    return value.Good ? Ok(value.Value) : Fail("Bad quality.");
                }
                case "write":
                {
                    var value = session.Write(request.Address, request.Value);
                    return Ok(value.Value);
                }
                default:
                    return Fail($"Unknown action '{request.Action}'. Use read or write.");
            }
        }
        catch (SimulationException ex)
        {
            return Fail(ex.Message);
        }
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            lock (_gate)
            {
                _clients.RemoveAll(t => t.IsCompleted);
                _clients.Add(Serve(client, token));
            }
        }
    }

    private async Task Serve(TcpClient client, CancellationToken token)
    {
        using var _ = client;
        client.NoDelay = true;
        try
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is null)
                    return;
                if (line.Length == 0)
                    continue;
                await writer.WriteLineAsync(Handle(line).AsMemory(), token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
    }

    private static string Ok(object? value) => JsonSerializer.Serialize(new Response(true, value, null), Json);

    private static string Fail(string error) => JsonSerializer.Serialize(new Response(false, null, error), Json);

    private sealed record Request(string? Action, string? Address, JsonElement? Value);

    private sealed record Response(bool Ok, object? Value, string? Error);
}
