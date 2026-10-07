using Builder.Logic.Blocks;
using Builder.Logic.Expressions;
using Xunit;

namespace Builder.Tests.Logic;

public class BlockTests
{
    private const double Dt = 0.1;

    private sealed class Runner(string name)
    {
        private readonly BlockDefinition _definition = BlockLibrary.Get(name);
        private readonly IBlock _block = BlockLibrary.Get(name).Create();

        public Value[] Outputs { get; } = Enumerable.Repeat(Value.BadNumber, BlockLibrary.Get(name).Outputs.Count).ToArray();

        public Value[] Run(params Value[] inputs)
        {
            Assert.Equal(_definition.Inputs.Count, inputs.Length);
            _block.Execute(inputs, Outputs, Dt);
            return Outputs;
        }
    }

    private static Value B(bool v) => Value.Of(v);

    private static Value N(double v) => Value.Of(v);

    [Fact]
    public void TonDelaysTheRisingEdge()
    {
        var ton = new Runner("TON");
        var q = new List<bool>();
        foreach (var input in new[] { true, true, true, true, false, true })
            q.Add(ton.Run(B(input), N(0.3))[0].Bool);
        Assert.Equal([false, false, false, true, false, false], q);
    }

    [Fact]
    public void TonReportsElapsedTimeCappedAtPreset()
    {
        var ton = new Runner("TON");
        for (var i = 0; i < 10; i++)
            ton.Run(B(true), N(0.3));
        Assert.Equal(0.3, ton.Outputs[1].Number, 6);
    }

    [Fact]
    public void TofHoldsAfterTheFallingEdge()
    {
        var tof = new Runner("TOF");
        var q = new List<bool>();
        foreach (var input in new[] { true, false, false, false, false, false })
            q.Add(tof.Run(B(input), N(0.3))[0].Bool);
        Assert.Equal([true, true, true, true, false, false], q);
    }

    [Fact]
    public void TpGivesAFixedPulseAndIgnoresRetriggers()
    {
        var tp = new Runner("TP");
        var q = new List<bool>();
        foreach (var input in new[] { true, false, true, false, false, true })
            q.Add(tp.Run(B(input), N(0.3))[0].Bool);
        Assert.Equal([true, true, true, false, false, true], q);
    }

    [Fact]
    public void EdgesFireForOneCycle()
    {
        var rising = new Runner("R_TRIG");
        var falling = new Runner("F_TRIG");
        var r = new List<bool>();
        var f = new List<bool>();
        foreach (var input in new[] { false, true, true, false, false })
        {
            r.Add(rising.Run(B(input))[0].Bool);
            f.Add(falling.Run(B(input))[0].Bool);
        }
        Assert.Equal([false, true, false, false, false], r);
        Assert.Equal([false, false, false, true, false], f);
    }

    [Fact]
    public void SrIsSetDominantAndRsIsResetDominant()
    {
        Assert.True(new Runner("SR").Run(B(true), B(true))[0].Bool);
        Assert.False(new Runner("RS").Run(B(true), B(true))[0].Bool);

        var sr = new Runner("SR");
        sr.Run(B(true), B(false));
        Assert.True(sr.Run(B(false), B(false))[0].Bool);
        Assert.False(sr.Run(B(false), B(true))[0].Bool);
    }

    [Fact]
    public void CtuCountsRisingEdgesAndResets()
    {
        var ctu = new Runner("CTU");
        foreach (var input in new[] { true, false, true, true, false, true })
            ctu.Run(B(input), B(false), N(3));
        Assert.Equal(3, ctu.Outputs[1].Number);
        Assert.True(ctu.Outputs[0].Bool);
        ctu.Run(B(false), B(true), N(3));
        Assert.Equal(0, ctu.Outputs[1].Number);
    }

    [Fact]
    public void CtuStartsFromTheRetainedValue()
    {
        var ctu = new Runner("CTU");
        ctu.Outputs[1] = N(41);
        ctu.Run(B(true), B(false), N(0));
        Assert.Equal(42, ctu.Outputs[1].Number);
    }

    [Fact]
    public void HysteresisSwitchesAtTheLimits()
    {
        var hyst = new Runner("HYST");
        var q = new List<bool>();
        foreach (var input in new[] { 50.0, 81, 70, 59, 70 })
            q.Add(hyst.Run(N(input), N(80), N(60))[0].Bool);
        Assert.Equal([false, true, true, false, false], q);
    }

    [Fact]
    public void HysteresisReportsBadQuality()
    {
        var hyst = new Runner("HYST");
        Assert.False(hyst.Run(Value.Of(90, good: false), N(80), N(60))[0].Good);
    }

    [Fact]
    public void ScaleMapsLinearlyAndRejectsAnEmptyRange()
    {
        Assert.Equal(50, new Runner("SCALE").Run(N(12), N(4), N(20), N(0), N(100))[0].Number, 6);
        Assert.False(new Runner("SCALE").Run(N(12), N(4), N(4), N(0), N(100))[0].Good);
    }

    [Fact]
    public void AverageUsesAMovingWindowAndSkipsBadSamples()
    {
        var avg = new Runner("AVG");
        avg.Run(N(10), N(2));
        avg.Run(N(20), N(2));
        Assert.Equal(25, avg.Run(N(30), N(2))[0].Number);
        Assert.Equal(25, avg.Run(Value.Of(1000, good: false), N(2))[0].Number);
    }

    [Fact]
    public void RuntimeAccumulatesHoursFromTheRetainedValue()
    {
        var runtime = new Runner("RUNTIME");
        runtime.Outputs[0] = N(100);
        for (var i = 0; i < 36; i++)
            runtime.Run(B(true), B(false));
        runtime.Run(B(false), B(false));
        Assert.Equal(100.001, runtime.Outputs[0].Number, 9);
        Assert.Equal(0, runtime.Run(B(true), B(true))[0].Number);
    }

    [Fact]
    public void EveryBlockIsListedWithItsPins()
    {
        Assert.Equal(["TON", "TOF", "TP", "R_TRIG", "F_TRIG", "SR", "RS", "CTU", "HYST", "SCALE", "AVG", "RUNTIME"], BlockLibrary.Names);
        Assert.All(BlockLibrary.All, d => Assert.NotEmpty(d.Outputs));
    }
}
