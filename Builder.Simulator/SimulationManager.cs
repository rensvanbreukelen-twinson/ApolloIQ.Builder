using System.Collections.Concurrent;

namespace Builder.Simulator;

public sealed class SimulationManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SimulationSession> _sessions = new();
    private readonly Lock _gate = new();

    public event Action<SimulationSession>? SessionCreated;

    public IReadOnlyCollection<SimulationSession> Sessions => _sessions.Values.ToList();

    public SimulationSession? Find(Guid projectId) => _sessions.GetValueOrDefault(projectId);

    public SimulationSession GetOrCreate(Guid projectId, Func<SimulationSession> create)
    {
        SimulationSession session;
        lock (_gate)
        {
            if (_sessions.TryGetValue(projectId, out var existing))
                return existing;
            session = create();
            _sessions[projectId] = session;
        }
        SessionCreated?.Invoke(session);
        return session;
    }

    public async Task RemoveAsync(Guid projectId)
    {
        if (_sessions.TryRemove(projectId, out var session))
            await session.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
            await RemoveAsync(id);
    }
}
