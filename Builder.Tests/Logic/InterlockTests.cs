using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Expressions;
using Builder.Logic.Runtime;
using Builder.Persistence;
using Xunit;

namespace Builder.Tests.Logic;

/// <summary>Project-level interlocks (G-172): lists of Switch on / Switch off / Trip conditions that act in the target CM.</summary>
public class InterlockTests
{
    private static readonly CmLibrary Library = CmLibrary.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "cm-types"));

    private sealed class Plant
    {
        public Plant()
        {
            Project = new Project();
            var pms = Project.AddFolder("PMS");
            Gen = InstanceFactory.Create(Project, Library, "GenSet", "GEN1", pms.Id);
            Breaker = InstanceFactory.Create(Project, Library, "CircuitBreaker", "GEN1_CB", pms.Id);
        }

        public Project Project { get; }

        public ControlModule Gen { get; }

        public ControlModule Breaker { get; }

        public LogicProgram Program { get; private set; } = null!;

        public void Rules(ControlModule owner, params InterlockRule[] rules) =>
            Project.SetInterlocks(owner.Id, rules.Select(r =>
            {
                var copy = r.Clone();
                copy.Condition = ExpressionReferences.ToStored(Project, r.Condition);
                return copy;
            }));

        public LogicProgram Build()
        {
            Program = LogicProgram.Build(Project, Library);
            return Program;
        }

        private int Slot(string path) => Program.Memory.Slots[new TagRegistry(Project).FindByPath(path)!.Id];

        public Value Get(string path) => Program.Memory.Get(Slot(path));

        public void Set(string path, bool value) => Program.Memory.Set(Slot(path), Value.Of(value));

        public void Force(string path, bool value) => Program.Memory.Force(Slot(path), Value.Of(value));

        public void Release(string path) => Program.Memory.Unforce(Slot(path));

        public void Run(int cycles)
        {
            for (var i = 0; i < cycles; i++)
                Program.Step();
        }

        public int State(string cm) => (int)Get($"{cm}.STS.state").Number;

        public void CloseBreakerOnRunningGen()
        {
            Run(3);
            Set("PMS.GEN1.CMD.set_on", true);
            Run(180);
            Assert.Equal(401, State("PMS.GEN1"));
            Set("PMS.GEN1_CB.CMD.set_on", true);
            Run(2);
            Assert.Equal(400, State("PMS.GEN1_CB"));
        }
    }

    private static InterlockRule On(string condition, string text = "") => new() { Kind = InterlockKind.SwitchOn, Condition = condition, Text = text };

    private static InterlockRule Off(string condition) => new() { Kind = InterlockKind.SwitchOff, Condition = condition };

    private static InterlockRule Trip(string condition, string? alarm = null) => new() { Kind = InterlockKind.Trip, Condition = condition, Alarm = alarm, Severity = 22 };

    [Fact]
    public void SwitchOnBlocksTheCommandAndReportsABitPerCondition()
    {
        var plant = new Plant();
        plant.Rules(plant.Breaker, On("[PMS.GEN1.STS.state] = ReadyToConnect"), On("TRUE"));
        plant.Build();
        Assert.Empty(plant.Program.Errors);
        plant.Run(4);
        Assert.False(plant.Get("PMS.GEN1_CB.LOK.can_on").Bool);
        Assert.Equal(2, plant.Get("PMS.GEN1_CB.LOK.can_on_status").Number);
        plant.Set("PMS.GEN1_CB.CMD.set_on", true);
        plant.Run(2);
        Assert.Equal(200, plant.State("PMS.GEN1_CB"));

        plant.CloseBreakerOnRunningGen();
        Assert.Equal(3, plant.Get("PMS.GEN1_CB.LOK.can_on_status").Number);
    }

    [Fact]
    public void SwitchOffKeepsTheEngineRunningWhileTheBreakerIsClosed()
    {
        var plant = new Plant();
        plant.Rules(plant.Gen, Off("NOT [PMS.GEN1_CB.is_closed]"));
        plant.Build();
        Assert.Empty(plant.Program.Errors);
        plant.CloseBreakerOnRunningGen();
        Assert.False(plant.Get("PMS.GEN1.LOK.can_off").Bool);
        plant.Set("PMS.GEN1.CMD.set_off", true);
        plant.Run(3);
        Assert.Equal(401, plant.State("PMS.GEN1"));
    }

    [Fact]
    public void TripOpensAtOnceRaisesAnAlarmOnTheOwnerAndLatchesUntilReset()
    {
        var plant = new Plant();
        plant.Rules(plant.Breaker, Trip("NOT [PMS.GEN1.is_running]", "GenStopped"));
        plant.Build();
        Assert.Empty(plant.Program.Errors);
        plant.Run(3);
        Assert.False(plant.Get("PMS.GEN1_CB.LOK.trip").Bool);
        Assert.False(plant.Get("PMS.GEN1_CB.ALM.GenStopped.active").Bool);

        plant.CloseBreakerOnRunningGen();
        plant.Force("PMS.GEN1.FIN.running", false);
        plant.Run(2);
        Assert.True(plant.Get("PMS.GEN1_CB.LOK.trip").Bool);
        Assert.True(plant.Get("PMS.GEN1_CB.ALM.GenStopped.active").Bool);
        Assert.Equal(1, plant.Get("PMS.GEN1_CB.ALM.GenStopped.raise_count").Number);
        Assert.False(plant.Get("PMS.GEN1_CB.LOK.can_on").Bool);
        Assert.NotEqual(400, plant.State("PMS.GEN1_CB"));

        plant.Release("PMS.GEN1.FIN.running");
        plant.Set("PMS.GEN1.CMD.set_on", true);
        plant.Run(180);
        Assert.True(plant.Get("PMS.GEN1_CB.LOK.trip").Bool);
        plant.Set("PMS.GEN1_CB.CMD.reset", true);
        plant.Run(1);
        Assert.False(plant.Get("PMS.GEN1_CB.LOK.trip").Bool);
        Assert.False(plant.Get("PMS.GEN1_CB.ALM.GenStopped.active").Bool);
        Assert.True(plant.Get("PMS.GEN1_CB.LOK.can_on").Bool);
    }

    [Fact]
    public void TripIsOnlyArmedWhileTheTargetHeadsOn()
    {
        var plant = new Plant();
        plant.Rules(plant.Breaker, Trip("NOT [PMS.GEN1.is_running]"));
        plant.Build();
        plant.Run(20);
        Assert.Equal(200, plant.State("PMS.GEN1_CB"));
        Assert.False(plant.Get("PMS.GEN1_CB.LOK.trip").Bool);
        Assert.False(plant.Get("PMS.GEN1_CB.ALM.Trip1.active").Bool);
    }

    [Fact]
    public void BadQualityBlocksAPermissiveButDoesNotTrip()
    {
        var plant = new Plant();
        plant.Rules(plant.Breaker, On("[PMS.GEN1.FIN.ready]"), Trip("NOT [PMS.GEN1.FIN.ready]"));
        plant.Build();
        plant.Run(3);
        Assert.True(plant.Get("PMS.GEN1_CB.LOK.can_on").Bool);
        plant.Program.Memory.SetBadQuality(plant.Program.Memory.Slots[new TagRegistry(plant.Project).FindByPath("PMS.GEN1.FIN.ready")!.Id], true);
        plant.Run(1);
        Assert.False(plant.Get("PMS.GEN1_CB.LOK.can_on").Bool);
        Assert.False(plant.Get("PMS.GEN1_CB.LOK.trip").Bool);
    }

    [Fact]
    public void TripAlarmTagsFollowTheList()
    {
        var plant = new Plant();
        var registry = () => new TagRegistry(plant.Project);
        plant.Rules(plant.Breaker, Trip("FALSE", "A"), Trip("FALSE"));
        Assert.NotNull(registry().FindByPath("PMS.GEN1_CB.ALM.A.active"));
        Assert.NotNull(registry().FindByPath("PMS.GEN1_CB.ALM.Trip2.raise_count"));
        Assert.Equal(["A", "Trip2"], plant.Breaker.Interlocks.Select(r => r.Alarm));
        plant.Rules(plant.Breaker, On("TRUE"));
        Assert.Null(registry().FindByPath("PMS.GEN1_CB.ALM.A.active"));
        Assert.NotNull(registry().FindByPath("PMS.GEN1_CB.ALM.Tripped.active"));
    }

    [Fact]
    public void InvalidListsAreRefused()
    {
        var plant = new Plant();
        Assert.Equal(ProjectErrors.InvalidInterlock,
            Assert.Throws<ProjectException>(() => plant.Rules(plant.Breaker, new InterlockRule { TargetId = plant.Gen.Id, Condition = "TRUE" })).Code);
        Assert.Equal(ProjectErrors.InvalidInterlock, Assert.Throws<ProjectException>(() => plant.Rules(plant.Breaker, On(" "))).Code);
        Assert.Equal(ProjectErrors.DuplicateName, Assert.Throws<ProjectException>(() => plant.Rules(plant.Breaker, Trip("TRUE", "Tripped"))).Code);
        Assert.Equal(ProjectErrors.InvalidSeverity,
            Assert.Throws<ProjectException>(() => plant.Rules(plant.Breaker, new InterlockRule { Kind = InterlockKind.Trip, Condition = "TRUE", Severity = 99 })).Code);
    }

    [Theory]
    [InlineData("time([PMS.GEN1.is_running]) > 5", "not allowed")]
    [InlineData("[PMS.GEN1.FIN.frequency]", "Bool")]
    [InlineData("[PMS.NOPE.FIN.running]", "Unknown")]
    public void InvalidConditionsAreReportedOnTheTarget(string condition, string message)
    {
        var plant = new Plant();
        plant.Rules(plant.Breaker, On("TRUE"), On(condition));
        plant.Build();
        var error = Assert.Single(plant.Program.Errors);
        Assert.StartsWith("PMS.GEN1_CB PMS.GEN1_CB (CircuitBreaker) interlocks[1]", error.Location);
        Assert.Contains(message, error.Message);
        Assert.Contains(message, LogicProgram.CheckInterlockCondition(plant.Project, Library, plant.Breaker.Id, condition));
        Assert.Null(LogicProgram.CheckInterlockCondition(plant.Project, Library, plant.Breaker.Id, "STS.remote_ok AND [PMS.GEN1.is_running]"));
    }

    [Fact]
    public void ConditionsAreStoredWithIdsSurviveRenamesAndRoundTrip()
    {
        var plant = new Plant();
        plant.Rules(plant.Breaker, On("[PMS.GEN1.is_running]", "Engine running"), Trip("NOT [PMS.GEN1.is_running]", "GenStopped"));
        Assert.DoesNotContain("GEN1", plant.Breaker.Interlocks[0].Condition);
        plant.Project.Rename(plant.Gen.Id, "GEN_PORT");
        Assert.Equal("[PMS.GEN_PORT.is_running]", ExpressionReferences.ToDisplay(plant.Project, plant.Breaker.Interlocks[0].Condition));

        var root = Path.Combine(Path.GetTempPath(), $"il-{Guid.NewGuid():N}");
        try
        {
            ProjectStore.Save(root, Guid.NewGuid(), "IL", plant.Project);
            var loaded = ProjectStore.Load(root).Project;
            var rules = loaded.Get<ControlModule>(plant.Breaker.Id).Interlocks;
            Assert.Equal([InterlockKind.SwitchOn, InterlockKind.Trip], rules.Select(r => r.Kind));
            Assert.Equal("GenStopped", rules[1].Alarm);
            Assert.Equal(22, rules[1].Severity);
            Assert.Equal("Engine running", rules[0].Text);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DeletingTheTargetRemovesTheLine()
    {
        var project = new Project();
        var pms = project.AddFolder("PMS");
        var unit = project.AddUnit("U", pms.Id, "X", "1");
        var cb = InstanceFactory.Create(project, Library, "CircuitBreaker", "CB", unit.Id);
        project.SetInterlocks(unit.Id, [new InterlockRule { TargetId = cb.Id, Condition = "TRUE" }]);
        Assert.Single(unit.Interlocks);
        project.Delete(cb.Id);
        Assert.Empty(unit.Interlocks);
    }
}
