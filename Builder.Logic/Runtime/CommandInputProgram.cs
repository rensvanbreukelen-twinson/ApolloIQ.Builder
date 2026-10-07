using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Logic.Expressions;

namespace Builder.Logic.Runtime;

internal sealed class CommandInputProgram
{
    private sealed class Contact(int value, int invert, int debounce)
    {
        public int Value { get; } = value;
        public int Invert { get; } = invert;
        public int Debounce { get; } = debounce;
        public bool Filtered { get; set; }
        public bool Raw { get; set; }
        public double ChangedAt { get; set; } = double.NegativeInfinity;
        public bool Initialised { get; set; }

        public bool Read(TagMemory memory, double time)
        {
            var v = memory.Get(Value);
            var raw = v.Good && (v.IsTrue ^ (Invert >= 0 && memory.Get(Invert).IsTrue));
            if (raw != Raw || !Initialised)
            {
                Raw = raw;
                ChangedAt = time;
            }
            var delay = Debounce >= 0 ? memory.Get(Debounce).Number : 0;
            if (!Initialised || delay <= 0 || time - ChangedAt >= delay - 1e-6)
                Filtered = Raw;
            Initialised = true;
            return Filtered;
        }
    }

    private sealed class Row(CommandInput input, int index)
    {
        public CommandInput Input { get; } = input;
        public int Index { get; } = index;
        public int OnSlot { get; set; } = -1;
        public int OffSlot { get; set; } = -1;
        public Contact? Main { get; set; }
        public Contact? OffContact { get; set; }
        public List<(int Target, int Source)> Singles { get; } = [];
        public int StuckTime { get; set; } = -1;
        public int StuckActive { get; set; } = -1;
        public int StuckEnabled { get; set; } = -1;
        public int StuckCount { get; set; } = -1;
        public double ActiveSince { get; set; } = double.PositiveInfinity;
        public bool Stuck { get; set; }
        public bool Previous { get; set; }
        public double OnSeen { get; set; } = double.NegativeInfinity;
        public double OffSeen { get; set; } = double.NegativeInfinity;
    }

    private sealed class Request(Row row, bool on, int priority, bool holdLike)
    {
        public Row Row { get; } = row;
        public bool On { get; } = on;
        public int Priority { get; } = priority;
        public bool HoldLike { get; } = holdLike;
        public bool Blocked { get; set; }
        public bool Active { get; set; }
    }

    private readonly Row[] _rows;
    private readonly Request[] _requests;
    private readonly bool _level;
    private readonly int _setOn;
    private readonly int _setOff;
    private readonly int _selector;
    private readonly int _auto;
    private readonly int _activeInput;
    private readonly int _override;
    private readonly List<(int Target, List<Row> Rows)> _singles = [];
    private Request? _previousWinner;

    public CommandInputProgram(CommandInputConfig config, Func<string, int> slot, int autoSlot, List<string> problems)
    {
        _level = config.Level;
        _setOn = slot("CMD.set_on");
        _setOff = slot("CMD.set_off");
        _selector = config.Selector is { } selector ? slot(selector) : -1;
        _auto = autoSlot;
        _activeInput = slot("STS.active_input");
        _override = slot("STS.override");
        var rows = new List<Row>();
        var requests = new List<Request>();
        foreach (var (input, index) in config.Rows.Select((r, i) => (r, i)))
        {
            var row = new Row(input, index);
            int Need(string name)
            {
                var s = slot(name);
                if (s < 0)
                    problems.Add($"command input {input.Name}: tag {name} is missing. Save the command inputs again.");
                return s;
            }
            if (input.IsPhysical)
            {
                int Debounce() => input.Debounce is null ? -1 : Need($"PAR.{input.Name}_debounce");
                if (input.TwoContacts)
                {
                    row.Main = new Contact(Need($"FIN.{input.Name}_on"), slot($"SET.invert_{input.Name}_on"), Debounce());
                    row.OffContact = new Contact(Need($"FIN.{input.Name}_off"), slot($"SET.invert_{input.Name}_off"), Debounce());
                }
                else
                    row.Main = new Contact(Need($"FIN.{input.Name}"), slot($"SET.invert_{input.Name}"), Debounce());
                if (input.StuckTime is not null)
                {
                    row.StuckTime = Need($"PAR.{input.Name}_stuck_time");
                    row.StuckActive = Need($"ALM.{input.Name}_stuck.active");
                    row.StuckEnabled = slot($"ALM.{input.Name}_stuck.enabled");
                    row.StuckCount = slot($"ALM.{input.Name}_stuck.raise_count");
                }
            }
            else
            {
                if (input.GivesOn)
                    row.OnSlot = Need($"CMD.{input.Name}_on");
                if (input.GivesOff)
                    row.OffSlot = Need($"CMD.{input.Name}_off");
            }
            foreach (var command in input.SingleCommands)
            {
                var target = Need($"CMD.{command}");
                var source = input.IsPhysical ? -1 : Need($"CMD.{input.Name}_{command}");
                row.Singles.Add((target, source));
                var group = _singles.FirstOrDefault(s => s.Target == target);
                if (group.Rows is null)
                    _singles.Add((target, [row]));
                else
                    group.Rows.Add(row);
            }
            var holdLike = input.Kind is InputKind.Hold or InputKind.Button or InputKind.Switch or InputKind.Sensor && input.Drives != InputDrives.Toggle;
            if (input.GivesOn && input.On is { } on)
                requests.Add(new Request(row, true, on, holdLike));
            if (input.GivesOff && input.Off is { } off)
                requests.Add(new Request(row, false, off, holdLike));
            rows.Add(row);
        }
        if ((requests.Count > 0) && (_setOn < 0 || _setOff < 0))
            problems.Add("command inputs need CMD.set_on and CMD.set_off.");
        _rows = [.. rows];
        _requests = [.. requests];
    }

    public void Execute(TagMemory memory, double time, int state)
    {
        var auto = _auto >= 0 && memory.Get(_auto).IsTrue;
        var remote = _selector >= 0 && memory.Get(_selector).IsTrue;
        var physicalEdge = new Dictionary<Row, bool>();

        foreach (var row in _rows)
        {
            var input = row.Input;
            bool onLevel = false, offLevel = false;
            if (input.IsPhysical)
            {
                var main = row.Main!.Read(memory, time);
                var second = row.OffContact?.Read(memory, time) ?? false;
                var any = main || second;
                if (row.StuckTime >= 0)
                {
                    if (!any)
                    {
                        row.ActiveSince = double.PositiveInfinity;
                        row.Stuck = false;
                    }
                    else if (double.IsPositiveInfinity(row.ActiveSince))
                        row.ActiveSince = time;
                    if (any && time - row.ActiveSince > memory.Get(row.StuckTime).Number + 1e-6)
                        row.Stuck = true;
                    var alarm = row.Stuck && (row.StuckEnabled < 0 || memory.Get(row.StuckEnabled).IsTrue);
                    if (alarm && !memory.Get(row.StuckActive).IsTrue && row.StuckCount >= 0)
                        memory.Set(row.StuckCount, Value.Of(memory.Get(row.StuckCount).Number + 1));
                    memory.Set(row.StuckActive, Value.Of(alarm));
                    if (row.Stuck)
                    {
                        main = false;
                        second = false;
                    }
                }
                var rising = main && !row.Previous;
                row.Previous = main;
                physicalEdge[row] = rising;
                switch (input.Drives)
                {
                    case InputDrives.Toggle:
                        onLevel = rising && !UniversalStates.HeadsOn(state);
                        offLevel = rising && UniversalStates.HeadsOn(state);
                        break;
                    case InputDrives.On:
                        onLevel = main;
                        break;
                    case InputDrives.Off:
                        offLevel = main;
                        break;
                    default:
                        if (input.TwoContacts)
                        {
                            onLevel = main;
                            offLevel = second;
                        }
                        else
                        {
                            onLevel = main;
                            offLevel = !main;
                        }
                        break;
                }
            }
            else
            {
                var onRaw = row.OnSlot >= 0 && memory.Get(row.OnSlot).IsTrue;
                var offRaw = row.OffSlot >= 0 && memory.Get(row.OffSlot).IsTrue;
                if (input.Kind == InputKind.Hold && input.Source == CommandSource.Hmi)
                {
                    if (onRaw)
                        row.OnSeen = time;
                    if (offRaw)
                        row.OffSeen = time;
                    onLevel = time - row.OnSeen <= CommandInputBehaviour.HmiHoldTimeoutSeconds + 1e-9;
                    offLevel = time - row.OffSeen <= CommandInputBehaviour.HmiHoldTimeoutSeconds + 1e-9;
                }
                else
                {
                    onLevel = onRaw;
                    offLevel = offRaw;
                }
            }
            foreach (var request in _requests.Where(r => r.Row == row))
                request.Active = request.On ? onLevel : offLevel;
        }

        bool Counts(Row row)
        {
            var located = row.Input.Location == InputLocation.Any || _selector < 0 || (row.Input.Location == InputLocation.Remote) == remote;
            return located && row.Input.InAuto switch
            {
                InputInAuto.Only => auto,
                InputInAuto.Ignore => !auto,
                _ => true
            };
        }

        var eligible = new List<Request>();
        foreach (var request in _requests)
        {
            if (!request.Active)
            {
                request.Blocked = false;
                continue;
            }
            if (Counts(request.Row) && !request.Blocked)
                eligible.Add(request);
        }
        Request? winner = null;
        if (eligible.Count > 0)
        {
            var best = eligible.Min(r => r.Priority);
            var top = eligible.Where(r => r.Priority == best).ToList();
            if (top.All(r => r.On) || top.All(r => !r.On))
                winner = _previousWinner is not null && top.Contains(_previousWinner) ? _previousWinner : top[0];
        }
        foreach (var request in eligible.Where(r => r != winner && r.HoldLike))
            request.Blocked = true;

        if (_setOn >= 0 && _setOff >= 0 && _requests.Length > 0)
        {
            var fresh = winner is not null && (winner != _previousWinner || !winner.HoldLike);
            var fire = _level ? winner is not null : fresh;
            memory.Set(_setOn, Value.Of(fire && winner!.On));
            memory.Set(_setOff, Value.Of(fire && !winner!.On));
        }
        if (winner is not null && _activeInput >= 0)
            memory.Set(_activeInput, Value.Of(winner.Row.Index + 1d));
        if (_override >= 0)
            memory.Set(_override, Value.Of(winner is not null && auto && winner.Row.Input.InAuto == InputInAuto.Override));
        _previousWinner = winner;

        foreach (var (target, rows) in _singles)
        {
            var requested = rows.Any(row => Counts(row) && row.Singles.Where(s => s.Target == target).Any(s =>
                s.Source >= 0 ? memory.Get(s.Source).IsTrue : physicalEdge.GetValueOrDefault(row)));
            memory.Set(target, Value.Of(requested));
        }
    }
}
