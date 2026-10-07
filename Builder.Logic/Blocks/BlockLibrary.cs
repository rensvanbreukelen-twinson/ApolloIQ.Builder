using Builder.Logic.Expressions;
using ValueType = Builder.Logic.Expressions.ValueType;

namespace Builder.Logic.Blocks;

public sealed record Pin(string Name, ValueType Type, string? Default = null);

public sealed record BlockDefinition(string Name, string Description, IReadOnlyList<Pin> Inputs, IReadOnlyList<Pin> Outputs, Func<IBlock> Create);

public interface IBlock
{
    void Execute(ReadOnlySpan<Value> inputs, Span<Value> outputs, double cycleSeconds);
}

public static class BlockLibrary
{
    private static readonly ValueType B = ValueType.Bool;
    private static readonly ValueType N = ValueType.Number;

    private static readonly Dictionary<string, BlockDefinition> Definitions = new BlockDefinition[]
    {
        new("TON", "On-delay: Q goes TRUE when IN has been TRUE for PT seconds", [new("IN", B), new("PT", N)], [new("Q", B), new("ET", N)], () => new Ton()),
        new("TOF", "Off-delay: Q stays TRUE for PT seconds after IN goes FALSE", [new("IN", B), new("PT", N)], [new("Q", B), new("ET", N)], () => new Tof()),
        new("TP", "Pulse: Q is TRUE for PT seconds after a rising edge of IN", [new("IN", B), new("PT", N)], [new("Q", B), new("ET", N)], () => new Tp()),
        new("R_TRIG", "Rising edge of CLK", [new("CLK", B)], [new("Q", B)], () => new RTrig()),
        new("F_TRIG", "Falling edge of CLK", [new("CLK", B)], [new("Q", B)], () => new FTrig()),
        new("SR", "Set-dominant latch", [new("S1", B), new("R", B, "FALSE")], [new("Q1", B)], () => new Sr()),
        new("RS", "Reset-dominant latch", [new("S", B), new("R1", B, "FALSE")], [new("Q1", B)], () => new Rs()),
        new("CTU", "Up counter: counts rising edges of CU; R resets", [new("CU", B), new("R", B, "FALSE"), new("PV", N, "0")], [new("Q", B), new("CV", N)], () => new Ctu()),
        new("HYST", "Limit with hysteresis: Q TRUE above HIGH, FALSE below LOW", [new("IN", N), new("HIGH", N), new("LOW", N)], [new("Q", B)], () => new Hyst()),
        new("SCALE", "Linear scaling from IN_MIN..IN_MAX to OUT_MIN..OUT_MAX", [new("IN", N), new("IN_MIN", N), new("IN_MAX", N), new("OUT_MIN", N), new("OUT_MAX", N)], [new("OUT", N)], () => new Scale()),
        new("AVG", "Moving average over the last N cycles", [new("IN", N), new("N", N, "10")], [new("OUT", N)], () => new Avg()),
        new("RUNTIME", "Accumulates the time IN is TRUE, in hours; R resets. Starts from the retained value of HOURS", [new("IN", B), new("R", B, "FALSE")], [new("HOURS", N)], () => new Runtime())
    }.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> Names => Definitions.Keys;

    public static IEnumerable<BlockDefinition> All => Definitions.Values;

    public static bool Exists(string name) => Definitions.ContainsKey(name);

    public static BlockDefinition Get(string name) => Definitions[name];

    private const double TimeTolerance = 1e-6;

    private static bool Reached(double elapsed, double preset) => elapsed >= preset - TimeTolerance;

    private sealed class Ton : IBlock
    {
        private long _cycles = -1;

        public void Execute(ReadOnlySpan<Value> i, Span<Value> o, double dt)
        {
            _cycles = i[0].IsTrue ? _cycles < 0 ? 0 : _cycles + 1 : -1;
            var et = Math.Max(0, _cycles) * dt;
            var pt = i[1].Number;
            o[0] = Value.Of(_cycles >= 0 && Reached(et, pt), i[0].Good && i[1].Good);
            o[1] = Value.Of(Math.Min(et, Math.Max(pt, 0)), i[1].Good);
        }
    }

    private sealed class Tof : IBlock
    {
        private long _cycles = -1;
        private bool _q;

        public void Execute(ReadOnlySpan<Value> i, Span<Value> o, double dt)
        {
            var pt = i[1].Number;
            if (i[0].IsTrue)
            {
                _q = true;
                _cycles = -1;
            }
            else if (_q)
            {
                _cycles = _cycles < 0 ? 0 : _cycles + 1;
                if (Reached(_cycles * dt, pt))
                    _q = false;
            }
            o[0] = Value.Of(_q, i[0].Good && i[1].Good);
            o[1] = Value.Of(Math.Min(Math.Max(0, _cycles) * dt, Math.Max(pt, 0)), i[1].Good);
        }
    }

    private sealed class Tp : IBlock
    {
        private long _cycles = -1;
        private bool _previous;

        public void Execute(ReadOnlySpan<Value> i, Span<Value> o, double dt)
        {
            var input = i[0].IsTrue;
            var pt = i[1].Number;
            if (_cycles < 0 && input && !_previous)
                _cycles = 0;
            else if (_cycles >= 0)
                _cycles++;
            var running = _cycles >= 0 && !Reached(_cycles * dt, pt);
            if (_cycles >= 0 && !running && !input)
                _cycles = -1;
            _previous = input;
            o[0] = Value.Of(running, i[0].Good && i[1].Good);
            o[1] = Value.Of(Math.Min(Math.Max(0, _cycles) * dt, Math.Max(pt, 0)), i[1].Good);
        }
    }

    private sealed class RTrig : IBlock
    {
        private bool _previous;

        public void Execute(ReadOnlySpan<Value> i, Span<Value> o, double dt)
        {
            var input = i[0].IsTrue;
            o[0] = Value.Of(input && !_previous, i[0].Good);
            _previous = input;
        }
    }

    private sealed class FTrig : IBlock
    {
        private bool _previous;

        public void Execute(ReadOnlySpan<Value> i, Span<Value> o, double dt)
        {
            var input = i[0].IsTrue;
            o[0] = Value.Of(!input && _previous, i[0].Good);
            _previous = input;
        }
    }

    private sealed class Sr : IBlock
    {
        private bool _q;

        public void Execute(ReadOnlySpan<Value> i, Span<Value> o, double dt)
        {
            _q = i[0].IsTrue || (_q && !i[1].IsTrue);
            o[0] = Value.Of(_q);
        }
    }

    private sealed class Rs : IBlock
    {
        private bool _q;

        public void Execute(ReadOnlySpan<Value> i, Span<Value> o, double dt)
        {
            _q = !i[1].IsTrue && (i[0].IsTrue || _q);
            o[0] = Value.Of(_q);
        }
    }

    private sealed class Ctu : IBlock
    {
        private bool _previous;
        private double _count = double.NaN;

        public void Execute(ReadOnlySpan<Value> i, Span<Value> o, double dt)
        {
            if (double.IsNaN(_count))
                _count = o[1].Good ? o[1].Number : 0;
            var cu = i[0].IsTrue;
            if (i[1].IsTrue)
                _count = 0;
            else if (cu && !_previous)
                _count = Math.Min(_count + 1, int.MaxValue);
            _previous = cu;
            o[0] = Value.Of(i[2].Number > 0 && _count >= i[2].Number, i[2].Good);
            o[1] = Value.Of(_count);
        }
    }

    private sealed class Hyst : IBlock
    {
        private bool _q;

        public void Execute(ReadOnlySpan<Value> i, Span<Value> o, double dt)
        {
            var good = i[0].Good && i[1].Good && i[2].Good;
            if (good)
            {
                if (i[0].Number > i[1].Number)
                    _q = true;
                else if (i[0].Number < i[2].Number)
                    _q = false;
            }
            o[0] = Value.Of(_q, good);
        }
    }

    private sealed class Scale : IBlock
    {
        public void Execute(ReadOnlySpan<Value> i, Span<Value> o, double dt)
        {
            var span = i[2].Number - i[1].Number;
            var good = i[0].Good && i[1].Good && i[2].Good && i[3].Good && i[4].Good && span != 0;
            var result = span == 0 ? 0 : i[3].Number + (i[0].Number - i[1].Number) * (i[4].Number - i[3].Number) / span;
            o[0] = Value.Of(result, good);
        }
    }

    private sealed class Avg : IBlock
    {
        private readonly Queue<double> _window = new();
        private double _sum;

        public void Execute(ReadOnlySpan<Value> i, Span<Value> o, double dt)
        {
            var size = Math.Clamp((int)Math.Round(i[1].Number), 1, 10_000);
            if (i[0].Good)
            {
                _window.Enqueue(i[0].Number);
                _sum += i[0].Number;
            }
            while (_window.Count > size)
                _sum -= _window.Dequeue();
            o[0] = _window.Count == 0 ? Value.BadNumber : Value.Of(_sum / _window.Count);
        }
    }

    private sealed class Runtime : IBlock
    {
        private double _hours = double.NaN;

        public void Execute(ReadOnlySpan<Value> i, Span<Value> o, double dt)
        {
            if (double.IsNaN(_hours))
                _hours = o[0].Good ? o[0].Number : 0;
            if (i[1].IsTrue)
                _hours = 0;
            else if (i[0].IsTrue)
                _hours += dt / 3600;
            o[0] = Value.Of(_hours);
        }
    }
}
