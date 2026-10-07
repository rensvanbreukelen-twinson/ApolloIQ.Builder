using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Simulator;
using Builder.Simulator.Tcp;
using Xunit;

namespace Builder.Tests.Simulator;

public sealed class JsonLineServerTests : IAsyncLifetime
{
    private static readonly CmLibrary Library = Fixtures.Library();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Project _project = null!;
    private SimulationSession _session = null!;
    private JsonLineServer _server = null!;
    private SimulationSession? _served;

    public ValueTask InitializeAsync()
    {
        _project = new Project();
        InstanceFactory.Create(_project, Library, Fixtures.CircuitBreaker, "GEN1_CB", _project.AddFolder("PMS").Id);
        _session = new SimulationSession(Guid.NewGuid(), _project, Library);
        _served = _session;
        _server = new JsonLineServer(() => _served, IPAddress.Loopback, 0);
        _server.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _session.DisposeAsync();
    }

    private async Task<(StreamReader reader, StreamWriter writer, TcpClient client)> Connect()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.Port, Ct);
        var stream = client.GetStream();
        return (new StreamReader(stream, Encoding.UTF8), new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true }, client);
    }

    private static async Task<JsonElement> Send(StreamReader reader, StreamWriter writer, object request)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        var line = await reader.ReadLineAsync(Ct);
        return JsonDocument.Parse(line!).RootElement;
    }

    [Fact]
    public async Task ReadsAndWritesLikeTheScadaTcpDriver()
    {
        var (reader, writer, client) = await Connect();
        using var _ = client;
        _session.Step(3);

        var state = await Send(reader, writer, new { Action = "read", Address = "PMS.GEN1_CB.STS.state", Value = (object?)null });
        Assert.True(state.GetProperty("ok").GetBoolean());
        Assert.Equal(200, state.GetProperty("value").GetInt32());

        var written = await Send(reader, writer, new { Action = "write", Address = "PMS.GEN1_CB.CMD.HMI_on", Value = true });
        Assert.True(written.GetProperty("ok").GetBoolean());
        _session.Step(2);

        var closed = await Send(reader, writer, new { Action = "read", Address = "PMS.GEN1_CB.STS.state" });
        Assert.Equal(400, closed.GetProperty("value").GetInt32());
        var command = await Send(reader, writer, new { Action = "read", Address = "PMS.GEN1_CB.CMD.HMI_on" });
        Assert.False(command.GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task AddressesBySymbolKey()
    {
        var (reader, writer, client) = await Connect();
        using var _ = client;
        var key = _project.Tags.Single(t => _project.GetPath(t.Id) == "PMS.GEN1_CB.PAR.pulse_time").SymbolKey;
        var response = await Send(reader, writer, new { Action = "read", Address = key });
        Assert.Equal(0.5, response.GetProperty("value").GetDouble());
    }

    [Theory]
    [InlineData("""{"Action":"read","Address":"PMS.NOPE"}""", "Unknown tag")]
    [InlineData("""{"Action":"write","Address":"PMS.GEN1_CB.OUT.coil_on","Value":true}""", "Force")]
    [InlineData("""{"Action":"delete","Address":"PMS.GEN1_CB.OUT.coil_on"}""", "Unknown action")]
    [InlineData("""not json""", "Invalid JSON")]
    [InlineData("""{"Action":"read"}""", "address")]
    public void ErrorsAreReportedAsNotOk(string request, string message)
    {
        var response = JsonDocument.Parse(_server.Handle(request)).RootElement;
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Contains(message, response.GetProperty("error").GetString());
    }

    [Fact]
    public void BadQualityIsNotOk()
    {
        _session.SetBadQuality("PMS.GEN1_CB.FIN.feedback", true);
        var response = JsonDocument.Parse(_server.Handle("""{"action":"read","address":"PMS.GEN1_CB.FIN.feedback"}""")).RootElement;
        Assert.False(response.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void NoActiveSimulationIsNotOk()
    {
        _served = null;
        var response = JsonDocument.Parse(_server.Handle("""{"Action":"read","Address":"PMS.GEN1_CB.STS.state"}""")).RootElement;
        Assert.Contains("No simulation", response.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ServesSeveralClients()
    {
        var (r1, w1, c1) = await Connect();
        var (r2, w2, c2) = await Connect();
        using var a = c1;
        using var b = c2;
        var first = await Send(r1, w1, new { Action = "read", Address = "PMS.GEN1_CB.SET.bistable" });
        var second = await Send(r2, w2, new { Action = "read", Address = "PMS.GEN1_CB.SET.bistable" });
        Assert.True(first.GetProperty("ok").GetBoolean());
        Assert.True(second.GetProperty("ok").GetBoolean());
        Assert.Equal(2, _server.ConnectedClients);
    }
}
