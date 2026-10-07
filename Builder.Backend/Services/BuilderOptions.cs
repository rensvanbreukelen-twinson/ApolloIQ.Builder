namespace Builder.Backend.Services;

public sealed class BuilderOptions
{
    public string ProjectsRoot { get; set; } = "projects";

    public string LibraryPath { get; set; } = "../Library/cm-types";

    public string ScenarioPath { get; set; } = "../Library/scenarios";

    public string BlueprintPath { get; set; } = "../Library/blueprints";

    public string ExamplePath { get; set; } = "../Library/examples";

    public int SimulatorTcpPort { get; set; } = 5190;

    public string SimulatorTcpAddress { get; set; } = "127.0.0.1";

    public double CycleSeconds { get; set; } = Builder.Logic.Runtime.LogicProgram.DefaultCycleSeconds;

    public int SimulationFlushMs { get; set; } = 100;

    public string[] AllowedOrigins { get; set; } = ["http://localhost:5174"];
}
