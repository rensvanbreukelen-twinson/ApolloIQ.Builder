using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Expressions;
using Builder.Logic.Runtime;
using Xunit;

namespace Builder.Tests.Logic;

public class InterpreterTests
{
    private const string LampTags = """
        {
          "FIN": [ { "name": "feedback", "type": "Bool" } ],
          "CMD": [ { "name": "set_on", "type": "Bool" }, { "name": "set_off", "type": "Bool" } ],
          "OUT": [ { "name": "lamp", "type": "Bool" }, { "name": "horn", "type": "Bool" } ],
          "PAR": [ { "name": "delay", "type": "Real", "initial": 0.1 } ],
          "PMT": [ { "name": "switchings", "type": "Int32" } ],
          "INT": [ { "name": "timeout", "type": "Bool" }, { "name": "ratio", "type": "Real" } ]
        }
        """;

    private const string LampLogic = """
        {
          "plant": [ { "set": "FIN.feedback", "expr": "OUT.lamp" } ],
          "stateMachine": {
            "transitions": [
              { "from": "Stopped", "to": "Starting", "guard": "CMD.set_on" },
              { "from": "Starting", "to": "Running", "guard": "INT.feedback", "name": "lit" },
              { "from": ["Starting", "Running"], "to": "Stopped", "guard": "CMD.set_off", "priority": 1 }
            ],
            "outputs": { "OUT.lamp": [ "Starting", "Running" ] }
          }
        }
        """;

    private static string Type(string name, string tags, string logic, string extra = "") => $$"""
        { "schema": "apolloiq.cmtype/1", "name": "{{name}}", "version": "1.0.0", {{extra}} "tags": {{tags}}, "logic": {{logic}} }
        """;

    private sealed class Harness
    {
        public Harness(IEnumerable<string> types, params (string Type, string Name, string[] Optional)[] instances)
        {
            var library = new CmLibrary();
            foreach (var json in types)
                library.Add(CmTypeLoader.Parse(json, "test.cmtype.json"));
            Project = new Project();
            var pms = Project.AddFolder("PMS");
            foreach (var (type, name, optional) in instances)
                InstanceFactory.Create(Project, library, type, name, pms.Id, optional);
            Program = LogicProgram.Build(Project, library, 0.1);
        }

        public Project Project { get; }

        public LogicProgram Program { get; }

        public int Slot(string path) => Program.Memory.Slots[new TagRegistry(Project).FindByPath(path)!.Id];

        public Value this[string path]
        {
            get => Program.Memory.Get(Slot(path));
            set => Program.Memory.Set(Slot(path), value);
        }

        public double State(string cm) => this[$"{cm}.STS.state"].Number;

        public void Step(int times = 1)
        {
            for (var i = 0; i < times; i++)
                Program.Step();
        }
    }

    private static Harness Lamp(string logic = LampLogic, string tags = LampTags) =>
        new([Type("Lamp", tags, logic)], ("Lamp", "L1", []));

    [Fact]
    public void StartsInStopped()
    {
        var h = Lamp();
        Assert.Empty(h.Program.Errors);
        Assert.Equal(0, h.State("PMS.L1"));
    }

    [Fact]
    public void TakesOneTransitionPerCycle()
    {
        var h = Lamp();
        h.Program.Memory.Force(h.Slot("PMS.L1.FIN.feedback"), Value.True);
        h["PMS.L1.CMD.set_on"] = Value.True;
        h.Step();
        Assert.Equal(300, h.State("PMS.L1"));
        h.Step();
        Assert.Equal(400, h.State("PMS.L1"));
    }

    [Fact]
    public void PlantModelClosesTheLoop()
    {
        var h = Lamp();
        h["PMS.L1.CMD.set_on"] = Value.True;
        h.Step(3);
        Assert.Equal(400, h.State("PMS.L1"));
        Assert.True(h["PMS.L1.OUT.lamp"].Bool);
        Assert.True(h["PMS.L1.INT.feedback"].Bool);
    }

    [Fact]
    public void CommandsAreResetAfterEachCycle()
    {
        var h = Lamp();
        h["PMS.L1.CMD.set_on"] = Value.True;
        h.Step();
        Assert.False(h["PMS.L1.CMD.set_on"].Bool);
    }

    [Fact]
    public void OutputsFollowTheState()
    {
        var h = Lamp();
        h["PMS.L1.CMD.set_on"] = Value.True;
        h.Step(3);
        h["PMS.L1.CMD.set_off"] = Value.True;
        h.Step();
        Assert.Equal(0, h.State("PMS.L1"));
        Assert.False(h["PMS.L1.OUT.lamp"].Bool);
    }

    [Fact]
    public void LowestPriorityNumberWins()
    {
        var h = Lamp();
        h["PMS.L1.CMD.set_on"] = Value.True;
        h.Step();
        h.Program.Memory.Force(h.Slot("PMS.L1.FIN.feedback"), Value.True);
        h["PMS.L1.CMD.set_off"] = Value.True;
        h.Step();
        Assert.Equal(0, h.State("PMS.L1"));
    }

    [Fact]
    public void StateChangesAreReportedWithTheTransitionName()
    {
        var h = Lamp();
        var changes = new List<StateChange>();
        h.Program.StateChanged += changes.Add;
        h["PMS.L1.CMD.set_on"] = Value.True;
        h.Step(3);
        Assert.Equal([(0, 300), (300, 400)], changes.Select(c => (c.From, c.To)));
        Assert.Equal("lit", changes[1].Transition);
        Assert.Equal("PMS.L1", changes[1].ControlModule);
    }

    [Fact]
    public void InvertSettingConditionsTheInput()
    {
        var h = Lamp();
        h["PMS.L1.SET.invert_feedback"] = Value.True;
        h.Program.RunLogic();
        Assert.True(h["PMS.L1.INT.feedback"].Bool);
    }

    [Fact]
    public void BadQualityInputMakesTheGuardFalse()
    {
        var h = Lamp();
        h["PMS.L1.CMD.set_on"] = Value.True;
        h.Step();
        h.Program.Memory.Force(h.Slot("PMS.L1.FIN.feedback"), Value.True);
        h.Program.Memory.SetBadQuality(h.Slot("PMS.L1.FIN.feedback"), true);
        h.Step(3);
        Assert.Equal(300, h.State("PMS.L1"));
        Assert.False(h["PMS.L1.INT.feedback"].Good);
    }

    [Fact]
    public void DecadeMatchIncludesSubStates()
    {
        var logic = """
            {
              "stateMachine": {
                "transitions": [
                  { "from": "Stopped", "to": "Draining", "guard": "CMD.set_on" },
                  { "from": "Stopping", "to": "Available", "guard": "CMD.set_off" }
                ]
              }
            }
            """;
        var h = new Harness([Type("Lamp", LampTags, logic, """ "states": [ { "code": 101, "name": "Draining" } ], """)], ("Lamp", "L1", []));
        Assert.Empty(h.Program.Errors);
        h["PMS.L1.CMD.set_on"] = Value.True;
        h.Step();
        Assert.Equal(101, h.State("PMS.L1"));
        h["PMS.L1.CMD.set_off"] = Value.True;
        h.Step();
        Assert.Equal(200, h.State("PMS.L1"));
    }

    [Fact]
    public void WildcardTransitionDoesNotRetriggerInItsTargetState()
    {
        var logic = """
            { "stateMachine": { "transitions": [ { "from": "*", "to": "Shutdown", "guard": "CMD.set_off" } ] } }
            """;
        var h = Lamp(logic);
        var changes = new List<StateChange>();
        h.Program.StateChanged += changes.Add;
        h["PMS.L1.CMD.set_off"] = Value.True;
        h.Step();
        h["PMS.L1.CMD.set_off"] = Value.True;
        h.Step();
        Assert.Equal(500, h.State("PMS.L1"));
        Assert.Single(changes);
        Assert.Equal(1, h.Program.Programs[0].StateCycles);
    }

    [Fact]
    public void OutputConditionsCombineWithStates()
    {
        var logic = """
            {
              "stateMachine": {
                "transitions": [ { "from": "Stopped", "to": "Running", "guard": "CMD.set_on" } ],
                "outputs": {
                  "OUT.lamp": { "states": [ "Running" ], "when": "NOT INT.timeout" },
                  "OUT.horn": "INT.timeout"
                }
              }
            }
            """;
        var h = Lamp(logic);
        h["PMS.L1.CMD.set_on"] = Value.True;
        h.Step();
        Assert.True(h["PMS.L1.OUT.lamp"].Bool);
        Assert.False(h["PMS.L1.OUT.horn"].Bool);
        h.Program.Memory.Force(h.Slot("PMS.L1.INT.timeout"), Value.True);
        h.Step();
        Assert.False(h["PMS.L1.OUT.lamp"].Bool);
        Assert.True(h["PMS.L1.OUT.horn"].Bool);
    }

    [Fact]
    public void BlocksRunInsideTheCycle()
    {
        var logic = """
            {
              "before": [ { "block": "TON", "in": { "IN": "is_starting", "PT": "PAR.delay * 2" }, "out": { "Q": "INT.timeout" } } ],
              "stateMachine": {
                "transitions": [
                  { "from": "Stopped", "to": "Starting", "guard": "CMD.set_on" },
                  { "from": "Starting", "to": "Shutdown", "guard": "INT.timeout" }
                ]
              },
              "after": [ { "block": "CTU", "in": { "CU": "is_starting" }, "out": { "CV": "PMT.switchings" } } ]
            }
            """;
        var h = Lamp(logic);
        Assert.Empty(h.Program.Errors);
        h["PMS.L1.PMT.switchings"] = Value.Of(7);
        h["PMS.L1.CMD.set_on"] = Value.True;
        h.Step(3);
        Assert.Equal(300, h.State("PMS.L1"));
        h.Step();
        Assert.Equal(500, h.State("PMS.L1"));
        Assert.Equal(8, h["PMS.L1.PMT.switchings"].Number);
    }

    [Fact]
    public void AssignmentsCoerceToTheTagType()
    {
        var logic = """{ "before": [ { "set": "PMT.switchings", "expr": "2.6" }, { "set": "INT.ratio", "expr": "PAR.delay / 3" } ] }""";
        var h = Lamp(logic);
        h.Step();
        Assert.Equal(3, h["PMS.L1.PMT.switchings"].Number);
        Assert.Equal((double)(float)(0.1f / 3.0), h["PMS.L1.INT.ratio"].Number, 6);
    }

    [Fact]
    public void DivisionByZeroIsReportedAsADiagnostic()
    {
        var logic = """{ "before": [ { "set": "INT.ratio", "expr": "1 / (PAR.delay - PAR.delay)" } ] }""";
        var h = Lamp(logic);
        h.Step(3);
        Assert.False(h["PMS.L1.INT.ratio"].Good);
        var entry = Assert.Single(h.Program.Diagnostics.Entries);
        Assert.Equal("PMS.L1", entry.ControlModule);
        Assert.Contains("Division by zero", entry.Message);
    }

    [Theory]
    [InlineData("""{ "before": [ { "set": "FIN.feedback", "expr": "TRUE" } ] }""", "logic.before[0].set", "INT, OUT, STS")]
    [InlineData("""{ "before": [ { "set": "STS.state", "expr": "400" } ] }""", "logic.before[0].set", "not state or enabled")]
    [InlineData("""{ "before": [ { "set": "CMD.set_on", "expr": "TRUE" } ] }""", "logic.before[0].set", "cannot be written")]
    [InlineData("""{ "plant": [ { "set": "OUT.lamp", "expr": "TRUE" } ] }""", "logic.plant[0].set", "only write FIN")]
    [InlineData("""{ "stateMachine": { "outputs": { "INT.timeout": [ "Running" ] } } }""", "logic.stateMachine.outputs.INT.timeout", "OUT tags")]
    [InlineData("""{ "stateMachine": { "transitions": [ { "from": "Idle", "to": "Running", "guard": "TRUE" } ] } }""", "transitions[0].from", "Unknown state 'Idle'")]
    [InlineData("""{ "before": [ { "set": "INT.timeout", "expr": "PAR.delay" } ] }""", "logic.before[0].expr", "")]
    [InlineData("""{ "before": [ { "set": "INT.timeout", "expr": "FIN.missing" } ] }""", "logic.before[0].expr", "")]
    [InlineData("""{ "before": [ { "block": "TON", "in": { "IN": "TRUE" }, "out": { "Q": "INT.timeout" } } ] }""", "in.PT", "required")]
    [InlineData("""{ "before": [ { "block": "TON", "in": { "IN": "TRUE", "PT": 1 }, "out": { "ET": "INT.timeout" } } ] }""", "out.ET", "Number")]
    public void InvalidLogicIsReportedWithItsLocation(string logic, string location, string message)
    {
        var h = Lamp(logic);
        var error = Assert.Single(h.Program.Errors);
        Assert.StartsWith("PMS.L1 (Lamp) ", error.Location);
        Assert.Contains(location, error.Location);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void AbsentOptionalTagUsesItsAbsentValue()
    {
        var tags = """
            {
              "FIN": [ { "name": "remote", "type": "Bool", "optional": true, "absent": true } ],
              "CMD": [ { "name": "set_on", "type": "Bool" } ]
            }
            """;
        var logic = """{ "stateMachine": { "transitions": [ { "from": "Stopped", "to": "Running", "guard": "CMD.set_on AND FIN.remote" } ] } }""";
        var type = Type("Remote", tags, logic);
        var h = new Harness([type], ("Remote", "WITHOUT", []), ("Remote", "WITH", ["FIN.remote"]));
        Assert.Empty(h.Program.Errors);
        h["PMS.WITHOUT.CMD.set_on"] = Value.True;
        h["PMS.WITH.CMD.set_on"] = Value.True;
        h.Step();
        Assert.Equal(400, h.State("PMS.WITHOUT"));
        Assert.Equal(0, h.State("PMS.WITH"));
    }

    [Fact]
    public void ReferencesToOtherCmsRunTheSourceFirst()
    {
        var followerTags = """{ "OUT": [ { "name": "lamp", "type": "Bool" }, { "name": "running", "type": "Bool" } ] }""";
        var followerLogic = """{ "after": [ { "set": "OUT.lamp", "expr": "[PMS.B_SOURCE.OUT.lamp]" }, { "set": "OUT.running", "expr": "[PMS.B_SOURCE.is_running]" } ] }""";
        var h = new Harness(
            [Type("Lamp", LampTags, LampLogic), Type("Follower", followerTags, followerLogic)],
            ("Follower", "A_FOLLOWER", []), ("Lamp", "B_SOURCE", []));
        Assert.Empty(h.Program.Errors);
        Assert.Equal(["PMS.B_SOURCE", "PMS.A_FOLLOWER"], h.Program.Programs.Select(p => p.Path));

        h["PMS.B_SOURCE.CMD.set_on"] = Value.True;
        h.Step();
        Assert.True(h["PMS.A_FOLLOWER.OUT.lamp"].Bool);
        Assert.False(h["PMS.A_FOLLOWER.OUT.running"].Bool);
        h.Step();
        Assert.True(h["PMS.A_FOLLOWER.OUT.running"].Bool);
    }

    [Fact]
    public void UnknownCmTypeIsAnError()
    {
        var library = new CmLibrary();
        library.Add(CmTypeLoader.Parse(Type("Lamp", LampTags, LampLogic), "Lamp.cmtype.json"));
        var project = new Project();
        InstanceFactory.Create(project, library, "Lamp", "L1", project.AddFolder("PMS").Id);
        var program = LogicProgram.Build(project, new CmLibrary());
        Assert.Contains("not in the library", Assert.Single(program.Errors).Message);
    }

    [Fact]
    public void TimeAdvancesPerLogicCycle()
    {
        var h = Lamp();
        h.Step(5);
        Assert.Equal(5, h.Program.Cycle);
        Assert.Equal(0.5, h.Program.TimeSeconds, 9);
    }

    [Fact]
    public void ForcedValuesOverrideLogicWrites()
    {
        var h = Lamp();
        h.Program.Memory.Force(h.Slot("PMS.L1.OUT.lamp"), Value.True);
        h.Step();
        Assert.True(h["PMS.L1.OUT.lamp"].Bool);
        Assert.False(h.Program.Memory.GetUnforced(h.Slot("PMS.L1.OUT.lamp")).Bool);
        Assert.Equal(TagDataType.Bool, h.Program.Memory.TypeOf(h.Slot("PMS.L1.OUT.lamp")));
    }
}
