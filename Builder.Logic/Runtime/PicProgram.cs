using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Logic.Expressions;

namespace Builder.Logic.Runtime;

internal sealed class PicProgram
{
    private sealed class Request(PicRow row, int index, bool on, int slot, int? priority)
    {
        public PicRow Row { get; } = row;
        public int Index { get; } = index;
        public bool On { get; } = on;
        public int Slot { get; } = slot;
        public int? Priority { get; } = priority;
        public double LastSeen { get; set; } = double.NegativeInfinity;
        public bool Blocked { get; set; }
    }

    private readonly Request[] _requests;
    private readonly int _auto;
    private readonly int _outOn;
    private readonly int _outOff;
    private readonly int _kind;
    private readonly int _activeInput;
    private readonly int _override;
    private readonly int _enabled;
    private Request? _previousWinner;

    public PicProgram(PicConfiguration configuration, Func<string, int> slot)
    {
        _requests = configuration.Rows
            .SelectMany((row, index) => new[]
            {
                new Request(row, index, true, row.On is null ? -1 : slot($"CMD.{row.OnCommand}"), row.On),
                new Request(row, index, false, row.Off is null ? -1 : slot($"CMD.{row.OffCommand}"), row.Off)
            })
            .Where(r => r.Priority is not null && r.Slot >= 0)
            .ToArray();
        _auto = slot("CMD.auto_active");
        _outOn = slot("OUT.set_on");
        _outOff = slot("OUT.set_off");
        _kind = slot("SET.output_kind");
        _activeInput = slot("STS.active_input");
        _override = slot("STS.override");
        _enabled = slot("STS.enabled");
    }

    public bool Complete => _outOn >= 0 && _outOff >= 0 && _enabled >= 0;

    public int Execute(TagMemory memory, double time)
    {
        if (!memory.Get(_enabled).IsTrue)
        {
            memory.Set(_outOn, Value.False);
            memory.Set(_outOff, Value.False);
            _previousWinner = null;
            return UniversalStates.UnavailableCode;
        }

        var auto = _auto >= 0 && memory.Get(_auto).IsTrue;
        var eligible = new List<Request>();
        foreach (var request in _requests)
        {
            var raw = memory.Get(request.Slot).IsTrue;
            if (raw)
                request.LastSeen = time;
            var active = request.Row.Kind == PicInputKind.Hold && request.Row.Source == PicSource.Hmi
                ? time - request.LastSeen <= PicBehaviour.HmiHoldTimeoutSeconds + 1e-9
                : raw;
            if (!active)
            {
                request.Blocked = false;
                continue;
            }
            var counts = request.Row.InAuto switch
            {
                PicInAuto.Only => auto,
                PicInAuto.Ignore => !auto,
                _ => true
            };
            if (counts && !request.Blocked)
                eligible.Add(request);
        }

        Request? winner = null;
        if (eligible.Count > 0)
        {
            var best = eligible.Min(r => r.Priority!.Value);
            var top = eligible.Where(r => r.Priority == best).ToList();
            if (top.All(r => r.On) || top.All(r => !r.On))
                winner = top.Contains(_previousWinner!) ? _previousWinner : top[0];
        }
        foreach (var request in eligible.Where(r => r != winner && r.Row.Kind == PicInputKind.Hold))
            request.Blocked = true;

        var level = _kind >= 0 && memory.Get(_kind).Number == 1;
        var fresh = winner is not null && (winner != _previousWinner || winner.Row.Kind == PicInputKind.Pulse);
        var fire = level ? winner is not null : fresh;
        memory.Set(_outOn, Value.Of(fire && winner!.On));
        memory.Set(_outOff, Value.Of(fire && !winner!.On));
        if (winner is not null && _activeInput >= 0)
            memory.Set(_activeInput, Value.Of(winner.Index + 1d));
        if (_override >= 0)
            memory.Set(_override, Value.Of(winner is not null && auto && winner.Row.InAuto == PicInAuto.Override));
        _previousWinner = winner;
        return 200;
    }
}
