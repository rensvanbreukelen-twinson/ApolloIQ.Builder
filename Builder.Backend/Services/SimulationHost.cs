using System.Collections.Concurrent;
using Builder.Backend.Hubs;
using Builder.Core.Types;
using Builder.Logic.Runtime;
using Builder.Simulator;
using Microsoft.AspNetCore.SignalR;

namespace Builder.Backend.Services;

public sealed class SimulationHost(ProjectWorkspace workspace, CmLibrary library, BuilderOptions options, IHubContext<SimulationHub> hub, ILogger<SimulationHost> logger)
    : IAsyncDisposable
{
    private readonly TimeSpan _flushInterval = TimeSpan.FromMilliseconds(options.SimulationFlushMs);

    private readonly SimulationManager _manager = new();
    private readonly ConcurrentDictionary<Guid, Outbox> _outboxes = new();

    private Guid? _served;

    public SimulationManager Manager => _manager;

    public Guid? ServedProject => _served;

    public SimulationSession Get(Guid projectId) => _manager.GetOrCreate(projectId, () => Create(projectId));

    public void Serve(Guid projectId)
    {
        Get(projectId);
        _served = projectId;
    }

    public SimulationSession? Served()
    {
        if (_served is { } id && _manager.Find(id) is { } session)
            return session;
        var projects = workspace.List();
        if (projects.Count != 1)
            return null;
        Serve(projects[0].Id);
        return _manager.Find(projects[0].Id);
    }

    public SimulationSession? Find(Guid projectId) => _manager.Find(projectId);

    public void Reload(Guid projectId)
    {
        var project = workspace.Get(projectId);
        var session = Get(projectId);
        project.Read(p =>
        {
            session.Reload(p);
            return 0;
        });
    }

    public void Reset(Guid projectId)
    {
        var project = workspace.Get(projectId);
        var session = Get(projectId);
        project.Read(p =>
        {
            session.Reset(p);
            return 0;
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var outbox in _outboxes.Values)
            await outbox.DisposeAsync();
        await _manager.DisposeAsync();
    }

    private SimulationSession Create(Guid projectId)
    {
        var projectSession = workspace.Get(projectId);
        var session = projectSession.Read(p => new SimulationSession(projectId, p, library, options.CycleSeconds));
        var group = hub.Clients.Group(SimulationHub.Group(projectId));
        var outbox = _outboxes[projectId] = new Outbox(group, logger, _flushInterval);
        _served ??= projectId;

        session.ValuesChanged += outbox.Add;
        session.StatusChanged += status => outbox.Send("status", status);
        session.StateChanged += change => outbox.Send("stateChanged", ToDto(session, change));
        session.DiagnosticAdded += diagnostic => outbox.Send("diagnostic", diagnostic);
        projectSession.Changed += changed =>
        {
            try
            {
                changed.Read(p =>
                {
                    session.Reload(p);
                    return 0;
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reloading the simulation of project {Project} failed", projectId);
            }
        };
        logger.LogInformation("Simulation created for project {Project} with {Tags} tags and {Errors} logic errors",
            projectId, session.Tags.Count, session.Errors.Count);
        return session;
    }

    private static object ToDto(SimulationSession session, StateChange change) => new
    {
        change.Cycle,
        change.ControlModule,
        change.From,
        change.To,
        change.Transition,
        ToName = session.ControlModules().FirstOrDefault(c => c.Path == change.ControlModule)?.StateName
    };

    private sealed class Outbox : IAsyncDisposable
    {
        private readonly IClientProxy _clients;
        private readonly ILogger _logger;
        private readonly TimeSpan _interval;
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, SimTagValue> _pending = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;
        private long _cycle;
        private double _time;

        public Outbox(IClientProxy clients, ILogger logger, TimeSpan interval)
        {
            _clients = clients;
            _logger = logger;
            _interval = interval;
            _task = Flush(_stop.Token);
        }

        public void Add(SimChangeBatch batch)
        {
            lock (_gate)
            {
                foreach (var value in batch.Values)
                    _pending[value.Id] = value;
                _cycle = batch.Cycle;
                _time = batch.TimeSeconds;
            }
        }

        public void Send(string method, object payload) => _ = SendSafe(method, payload);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _task;
            }
            catch (OperationCanceledException)
            {
            }
            _stop.Dispose();
        }

        private async Task Flush(CancellationToken token)
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(token))
            {
                SimChangeBatch? batch = null;
                lock (_gate)
                {
                    if (_pending.Count > 0)
                    {
                        batch = new SimChangeBatch(_cycle, _time, _pending.Values.ToList());
                        _pending.Clear();
                    }
                }
                if (batch is not null)
                    await SendSafe("values", batch);
            }
        }

        private async Task SendSafe(string method, object payload)
        {
            try
            {
                await _clients.SendAsync(method, payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sending {Method} to simulation clients failed", method);
            }
        }
    }
}
