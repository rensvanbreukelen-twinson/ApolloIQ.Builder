using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Expressions;
using Builder.Logic.Runtime;
using Xunit;

namespace Builder.Tests.Logic;

public class LibraryLogicTests
{
    private static readonly CmLibrary Library = CmLibrary.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "cm-types"));

    private const int Stopped = 0, Stopping = 100, CoolingDown = 101, Available = 200, Starting = 300, Running = 400,
        ReadyToConnect = 401, Shutdown = 500, Unavailable = 999;

    private sealed class Plant
    {
        private readonly TagRegistry _registry;

        public Plant(string[]? genOptional = null, string[]? breakerOptional = null)
        {
            Project = new Project();
            var pms = Project.AddFolder("PMS");
            InstanceFactory.Create(Project, Library, "GenSet", "GEN1", pms.Id, genOptional);
            InstanceFactory.Create(Project, Library, "CircuitBreaker", "GEN1_CB", pms.Id, breakerOptional);
            _registry = new TagRegistry(Project);
            Program = LogicProgram.Build(Project, Library);
            Program.StateChanged += c => Changes.Add(c);
        }

        public Project Project { get; }

        public LogicProgram Program { get; }

        public List<StateChange> Changes { get; } = [];

        private int Slot(string tag) => Program.Memory.Slots[_registry.FindByPath($"PMS.{tag}")!.Id];

        public Value Get(string tag) => Program.Memory.Get(Slot(tag));

        public bool Bool(string tag) => Get(tag).Bool;

        public double Number(string tag) => Get(tag).Number;

        public void Set(string tag, bool value) => Program.Memory.Set(Slot(tag), Value.Of(value));

        public void Set(string tag, double value) => Program.Memory.Set(Slot(tag), Value.Of(value));

        public void Force(string tag, bool value) => Program.Memory.Force(Slot(tag), Value.Of(value));

        public void Force(string tag, double value) => Program.Memory.Force(Slot(tag), Value.Of(value));

        public void Unforce(string tag) => Program.Memory.Unforce(Slot(tag));

        public void Bad(string tag, bool bad = true) => Program.Memory.SetBadQuality(Slot(tag), bad);

        public int Gen => (int)Number("GEN1.STS.state");

        public int Cb => (int)Number("GEN1_CB.STS.state");

        public void Run(double seconds)
        {
            var cycles = (int)Math.Round(seconds / Program.CycleSeconds);
            for (var i = 0; i < cycles; i++)
                Program.Step();
        }

        public double RunUntil(Func<bool> condition, double maxSeconds)
        {
            var start = Program.TimeSeconds;
            while (!condition())
            {
                Assert.True(Program.TimeSeconds - start < maxSeconds, $"Condition not reached within {maxSeconds} s");
                Program.Step();
            }
            return Program.TimeSeconds - start;
        }

        public void Command(string tag)
        {
            Set(tag, true);
            Program.Step();
        }

        public IEnumerable<int> StatesOf(string cm) => Changes.Where(c => c.ControlModule == $"PMS.{cm}").Select(c => c.To);
    }

    private static Plant RunningGenSet()
    {
        var plant = new Plant();
        plant.Set("GEN1.PAR.cooldown_time", 2);
        plant.Run(0.2);
        plant.Command("GEN1.CMD.set_on");
        plant.RunUntil(() => plant.Gen == ReadyToConnect, 15);
        return plant;
    }

    [Fact]
    public void LibraryTypesCompileWithAndWithoutOptionalTags()
    {
        Assert.Empty(new Plant().Program.Errors);
        var gen = Library.Find("GenSet")!.OptionalTags.Select(t => $"{t.Group.Code()}.{t.Name}").ToArray();
        var cb = Library.Find("CircuitBreaker")!.OptionalTags.Select(t => $"{t.Group.Code()}.{t.Name}").ToArray();
        Assert.Empty(new Plant(gen, cb).Program.Errors);
    }

    [Fact]
    public void GenSetBecomesAvailableAndRemoteControllable()
    {
        var plant = new Plant();
        plant.Run(0.2);
        Assert.Equal(Available, plant.Gen);
        Assert.True(plant.Bool("GEN1.STS.remote_ok"));
    }

    [Fact]
    public void GenSetStartsAndBecomesReadyToConnect()
    {
        var plant = new Plant();
        plant.Run(0.2);
        plant.Command("GEN1.CMD.set_on");
        Assert.Equal(Starting, plant.Gen);
        Assert.True(plant.Bool("GEN1.OUT.start_request"));

        var toRunning = plant.RunUntil(() => plant.Gen == Running, 10);
        Assert.InRange(toRunning, 2.9, 3.2);
        Assert.False(plant.Bool("GEN1.OUT.start_request"));

        var toReady = plant.RunUntil(() => plant.Gen == ReadyToConnect, 10);
        Assert.InRange(toReady, 5.0, 5.2);
        Assert.Equal(1, plant.Number("GEN1.PMT.start_count"));
        Assert.Equal(50, plant.Number("GEN1.FIN.frequency"));
        Assert.Equal([Available, Starting, Running, ReadyToConnect], plant.StatesOf("GEN1"));
    }

    [Fact]
    public void NormalStopCoolsDownBeforeTheStopRequest()
    {
        var plant = RunningGenSet();
        plant.Command("GEN1.CMD.set_off");
        Assert.Equal(Stopping, plant.Gen);
        plant.Program.Step();
        Assert.Equal(CoolingDown, plant.Gen);
        Assert.False(plant.Bool("GEN1.OUT.stop_request"));
        plant.Run(1);
        Assert.InRange(plant.Number("GEN1.STS.cooldown_remaining"), 0.9, 1.0);

        plant.RunUntil(() => plant.Bool("GEN1.OUT.stop_request"), 2);
        plant.RunUntil(() => plant.Gen == Available, 1);
        Assert.False(plant.Bool("GEN1.FIN.running"));
        Assert.Equal(0, plant.Number("GEN1.STS.cooldown_remaining"));
    }

    [Fact]
    public void CooldownWaitsUntilTheGeneratorIsUnloaded()
    {
        var plant = RunningGenSet();
        plant.Force("GEN1.FIN.active_power", 300);
        plant.Command("GEN1.CMD.set_off");
        plant.Run(2);
        Assert.Equal(Stopping, plant.Gen);
        Assert.Equal(60, plant.Number("GEN1.STS.load_pct"), 3);
        plant.Unforce("GEN1.FIN.active_power");
        plant.Run(0.1);
        Assert.Equal(CoolingDown, plant.Gen);
    }

    [Fact]
    public void LosingRunningConditionsStopsWithoutCooldown()
    {
        var plant = RunningGenSet();
        plant.Force("GEN1.LOK.trip", true);
        plant.Program.Step();
        Assert.Equal(Stopping, plant.Gen);
        Assert.True(plant.Bool("GEN1.OUT.stop_request"));
        plant.RunUntil(() => plant.Gen == Stopped, 1);
        Assert.DoesNotContain(CoolingDown, plant.StatesOf("GEN1"));
    }

    [Fact]
    public void StopIsIgnoredWhenStoppingIsNotAllowed()
    {
        var plant = RunningGenSet();
        plant.Force("GEN1.LOK.can_off", false);
        plant.Command("GEN1.CMD.set_off");
        Assert.Equal(ReadyToConnect, plant.Gen);
    }

    [Fact]
    public void FailToStartReturnsToStoppedAndCounts()
    {
        var plant = new Plant();
        plant.Set("GEN1.PAR.max_start_time", 1);
        plant.Force("GEN1.FIN.running", false);
        plant.Run(0.2);
        plant.Command("GEN1.CMD.set_on");
        var elapsed = plant.RunUntil(() => plant.Gen != Starting, 5);
        Assert.InRange(elapsed, 0.95, 1.1);
        Assert.Equal(Stopped, plant.Gen);
        Assert.Equal(1, plant.Number("GEN1.PMT.failed_start_count"));
        plant.Program.Step();
        Assert.Equal(Available, plant.Gen);
    }

    [Fact]
    public void ShutdownLatchesUntilReset()
    {
        var plant = RunningGenSet();
        plant.Set("GEN1.FIN.shutdown_active", true);
        plant.Program.Step();
        Assert.Equal(Shutdown, plant.Gen);
        plant.Run(0.2);
        Assert.False(plant.Bool("GEN1.FIN.running"));

        plant.Command("GEN1.CMD.reset");
        Assert.Equal(Shutdown, plant.Gen);

        plant.Set("GEN1.FIN.shutdown_active", false);
        plant.Run(0.2);
        Assert.Equal(Shutdown, plant.Gen);
        plant.Set("GEN1.CMD.reset", true);
        plant.Program.RunPlant();
        plant.Program.RunLogic();
        Assert.True(plant.Bool("GEN1.OUT.reset_request"));
        Assert.Equal(Stopped, plant.Gen);
        plant.Program.Step();
        Assert.Equal(Available, plant.Gen);
    }

    [Fact]
    public void ManualModeIgnoresCommandsAndTracksTheEngine()
    {
        var plant = new Plant();
        plant.Force("GEN1.FIN.in_auto", false);
        plant.Run(0.2);
        Assert.False(plant.Bool("GEN1.STS.remote_ok"));
        plant.Command("GEN1.CMD.set_on");
        Assert.Equal(Available, plant.Gen);
        Assert.False(plant.Bool("GEN1.OUT.start_request"));

        plant.Force("GEN1.FIN.running", true);
        plant.Program.Step();
        Assert.Equal(Running, plant.Gen);
        plant.Force("GEN1.FIN.running", false);
        plant.Run(0.1);
        Assert.Equal(Available, plant.Gen);
    }

    [Fact]
    public void LostCommunicationGivesUnavailableUntilReportingAgain()
    {
        var plant = new Plant();
        plant.Run(0.2);
        plant.Bad("GEN1.FIN.running");
        plant.Program.Step();
        Assert.Equal(Unavailable, plant.Gen);
        Assert.False(plant.Bool("GEN1.STS.remote_ok"));
        Assert.True(plant.Get("GEN1.STS.remote_ok").Good);
        plant.Bad("GEN1.FIN.running", false);
        plant.Run(0.1);
        Assert.Equal(Available, plant.Gen);
    }

    [Fact]
    public void DisabledGenSetIsUnavailable()
    {
        var plant = new Plant();
        plant.Run(0.2);
        plant.Set("GEN1.STS.enabled", false);
        plant.Program.Step();
        Assert.Equal(Unavailable, plant.Gen);
    }

    [Fact]
    public void BreakerClosesAndOpensOnCommand()
    {
        var plant = new Plant();
        plant.Run(0.1);
        Assert.Equal(Available, plant.Cb);
        Assert.True(plant.Bool("GEN1_CB.STS.remote_ok"));

        plant.Command("GEN1_CB.CMD.set_on");
        Assert.Equal(Starting, plant.Cb);
        Assert.True(plant.Bool("GEN1_CB.OUT.coil_on"));
        plant.Program.Step();
        Assert.Equal(Running, plant.Cb);
        Assert.True(plant.Bool("GEN1_CB.OUT.coil_on"));
        Assert.Equal(1, plant.Number("GEN1_CB.PMT.switch_count"));

        plant.Command("GEN1_CB.CMD.set_off");
        Assert.Equal(Stopping, plant.Cb);
        Assert.False(plant.Bool("GEN1_CB.OUT.coil_on"));
        plant.Run(0.1);
        Assert.Equal(Available, plant.Cb);
        Assert.Equal([Available, Starting, Running, Stopping, Stopped, Available], plant.StatesOf("GEN1_CB"));
    }

    [Fact]
    public void BreakerOpensOnATrip()
    {
        var plant = new Plant();
        plant.Run(0.1);
        plant.Command("GEN1_CB.CMD.set_on");
        plant.Run(0.1);
        plant.Force("GEN1_CB.LOK.trip", true);
        plant.Program.Step();
        Assert.Equal(Stopping, plant.Cb);
        plant.Run(0.1);
        Assert.Equal(Available, plant.Cb);
        Assert.False(plant.Bool("GEN1_CB.FIN.feedback"));
    }

    [Fact]
    public void LatchedTripNeedsAReset()
    {
        var plant = new Plant();
        plant.Run(0.1);
        plant.Command("GEN1_CB.CMD.set_on");
        plant.Run(0.1);
        plant.Force("GEN1_CB.FIN.tripped", true);
        plant.Program.Step();
        Assert.Equal(Shutdown, plant.Cb);
        plant.Run(0.1);
        Assert.False(plant.Bool("GEN1_CB.FIN.feedback"));
        Assert.Equal(1, plant.Number("GEN1_CB.PMT.trip_count"));

        plant.Unforce("GEN1_CB.FIN.tripped");
        plant.Run(0.5);
        Assert.Equal(Shutdown, plant.Cb);
        plant.Command("GEN1_CB.CMD.reset");
        plant.Run(0.1);
        Assert.Equal(Available, plant.Cb);
    }

    [Fact]
    public void UnlatchedTripClearsByItself()
    {
        var plant = new Plant();
        plant.Set("GEN1_CB.SET.trip_latches", false);
        plant.Run(0.1);
        plant.Command("GEN1_CB.CMD.set_on");
        plant.Run(0.1);
        plant.Force("GEN1_CB.FIN.tripped", true);
        plant.Run(0.1);
        Assert.Equal(Shutdown, plant.Cb);
        plant.Unforce("GEN1_CB.FIN.tripped");
        plant.Run(0.15);
        Assert.Equal(Available, plant.Cb);
    }

    [Fact]
    public void BistableBreakerIsDrivenWithPulses()
    {
        var plant = new Plant();
        plant.Set("GEN1_CB.SET.bistable", true);
        plant.Set("GEN1_CB.PAR.max_close_time", 2);
        plant.Run(0.1);
        plant.Command("GEN1_CB.CMD.set_on");
        plant.Program.Step();
        Assert.Equal(Running, plant.Cb);
        Assert.False(plant.Bool("GEN1_CB.OUT.coil_on"));
        plant.Run(1);
        Assert.True(plant.Bool("GEN1_CB.FIN.feedback"));

        plant.Command("GEN1_CB.CMD.set_off");
        Assert.True(plant.Bool("GEN1_CB.OUT.coil_off"));
        plant.Program.Step();
        Assert.Equal(Stopped, plant.Cb);
        Assert.False(plant.Bool("GEN1_CB.OUT.coil_off"));
    }

    [Fact]
    public void BistableCloseCoilPulsesForThePulseTime()
    {
        var plant = new Plant();
        plant.Set("GEN1_CB.SET.bistable", true);
        plant.Force("GEN1_CB.FIN.feedback", false);
        plant.Run(0.1);
        plant.Command("GEN1_CB.CMD.set_on");
        var pulse = plant.RunUntil(() => !plant.Bool("GEN1_CB.OUT.coil_on"), 1);
        Assert.InRange(pulse, 0.4, 0.55);
        var notClosing = plant.RunUntil(() => plant.Cb != Starting, 1);
        Assert.Equal(Stopped, plant.Cb);
        Assert.InRange(pulse + notClosing, 0.95, 1.15);
    }

    [Fact]
    public void LocalModeIgnoresCommandsAndTracksThePosition()
    {
        var plant = new Plant();
        plant.Run(0.1);
        plant.Command("GEN1_CB.CMD.set_on");
        plant.Run(0.1);
        plant.Force("GEN1_CB.FIN.remote", false);
        plant.Command("GEN1_CB.CMD.set_off");
        Assert.Equal(Running, plant.Cb);

        plant.Force("GEN1_CB.FIN.feedback", false);
        plant.Program.Step();
        Assert.Equal(Stopped, plant.Cb);
        Assert.DoesNotContain(Shutdown, plant.StatesOf("GEN1_CB"));
    }

    [Fact]
    public void UnknownPositionHoldsTheCoil()
    {
        var plant = new Plant();
        plant.Run(0.1);
        plant.Command("GEN1_CB.CMD.set_on");
        plant.Run(0.1);
        plant.Bad("GEN1_CB.FIN.feedback");
        plant.Program.Step();
        Assert.Equal(Unavailable, plant.Cb);
        Assert.True(plant.Bool("GEN1_CB.OUT.coil_on"));
        plant.Bad("GEN1_CB.FIN.feedback", false);
        plant.Run(0.1);
        Assert.Equal(Running, plant.Cb);
        Assert.Equal([Available, Starting, Running, Unavailable, Running], plant.StatesOf("GEN1_CB"));
    }

    [Fact]
    public void InvertedOutputsAreActiveLow()
    {
        var plant = new Plant();
        plant.Set("GEN1_CB.SET.invert_output", true);
        plant.Program.RunLogic();
        Assert.True(plant.Bool("GEN1_CB.OUT.coil_on"));
        plant.Run(0.1);
        Assert.Equal(Available, plant.Cb);
        Assert.False(plant.Bool("GEN1_CB.FIN.feedback"));
        plant.Command("GEN1_CB.CMD.set_on");
        plant.Program.Step();
        Assert.Equal(Running, plant.Cb);
        Assert.False(plant.Bool("GEN1_CB.OUT.coil_on"));
    }
}
