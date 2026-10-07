using Builder.Core.Tags;
using Builder.Core.Types;
using Xunit;

namespace Builder.Tests.Types;

public class CmLibraryTests
{
    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "cm-types");

    private static CmLibrary Library()
    {
        var library = CmLibrary.LoadDirectory(Directory);
        Assert.Empty(library.Errors);
        return library;
    }

    private static List<string> Names(IEnumerable<TagTemplate> tags) =>
        tags.Where(t => t.Group != TagGroup.Alm).Select(t => $"{t.Group.Code()}.{t.Name}").ToList();

    private static readonly string[] GenSetTags =
    [
        "STS.enabled", "STS.state", "STS.remote_ok", "STS.load_pct", "STS.cooldown_remaining",
        "FIN.running", "FIN.ready", "FIN.in_auto", "FIN.shutdown_active", "FIN.common_alarm", "FIN.common_warning",
        "FIN.speed", "FIN.frequency", "FIN.voltage", "FIN.current", "FIN.active_power", "FIN.reactive_power",
        "FIN.power_factor", "FIN.coolant_temp", "FIN.lube_oil_pressure", "FIN.lube_oil_temp", "FIN.exhaust_temp",
        "FIN.battery_voltage", "FIN.controller_hours",
        "CMD.set_on", "CMD.set_off", "CMD.reset", "CMD.set_auto", "CMD.set_manual",
        "OUT.start_request", "OUT.stop_request", "OUT.reset_request", "OUT.auto_request", "OUT.manual_request",
        "LOK.can_on", "LOK.trip", "LOK.can_off", "LOK.can_on_status", "LOK.can_off_status",
        "PAR.max_start_time", "PAR.max_stop_time", "PAR.cooldown_time", "PAR.stable_time",
        "PAR.coolant_temp_H", "PAR.coolant_temp_HH", "PAR.lube_oil_pressure_L", "PAR.lube_oil_pressure_LL",
        "PAR.oil_pressure_delay", "PAR.lube_oil_temp_H", "PAR.exhaust_temp_H", "PAR.battery_voltage_L",
        "PAR.overload_pct", "PAR.overload_delay", "PAR.voltage_tol_pct", "PAR.frequency_tol_pct",
        "PAR.service_interval", "PAR.mean_time_to_failure", "PAR.mean_switch_count_to_failure",
        "SET.rated_power", "SET.rated_voltage", "SET.rated_frequency", "SET.standby", "SET.priority", "SET.cooldown_enabled",
        "PMT.running_hours", "PMT.start_count", "PMT.failed_start_count", "PMT.hours_since_service", "PMT.energy", "PMT.avg_load_pct",
        "INT.running", "INT.ready", "INT.in_auto", "INT.shutdown_active", "INT.common_alarm", "INT.common_warning",
        "INT.state_timer", "INT.running_timer", "INT.stable_timer", "INT.shutdown_latched",
        "INT.reporting", "INT.v_ok", "INT.f_ok", "INT.unloaded", "INT.fast_stop"
    ];

    private static readonly string[] BreakerTags =
    [
        "STS.enabled", "STS.state", "STS.remote_ok",
        "FIN.feedback", "FIN.power_ok", "FIN.tripped", "FIN.spring_charged", "FIN.remote", "FIN.current",
        "CMD.set_on", "CMD.set_off", "CMD.reset",
        "OUT.coil_on", "OUT.coil_off",
        "LOK.can_on", "LOK.trip", "LOK.can_off", "LOK.can_on_status", "LOK.can_off_status",
        "PAR.mean_switch_count_to_failure", "PAR.max_close_time", "PAR.max_open_time", "PAR.pulse_time",
        "PAR.spring_charge_time", "PAR.current_H", "PAR.current_H_delay",
        "SET.invert_output", "SET.invert_feedback", "SET.invert_power_ok", "SET.bistable",
        "SET.invert_tripped", "SET.invert_spring_charged", "SET.invert_remote", "SET.trip_latches",
        "PMT.switch_count", "PMT.trip_count", "PMT.closed_hours", "PMT.avg_close_time",
        "INT.feedback", "INT.power_ok", "INT.tripped", "INT.spring_charged", "INT.remote",
        "INT.state_timer", "INT.pulse_timer", "INT.trip_latched"
    ];

    [Fact]
    public void LibraryContainsBothTypes()
    {
        var library = Library();
        Assert.NotNull(library.Find("GenSet"));
        Assert.NotNull(library.Find("CircuitBreaker"));
    }

    [Fact]
    public void GenSetTagsMatchTheDesign()
    {
        var type = Library().Find("GenSet")!;
        var all = Names(type.ExpandTags(type.OptionalTags.Select(t => $"{t.Group.Code()}.{t.Name}")));
        Assert.Equal(GenSetTags.Order(), all.Order());
    }

    [Fact]
    public void BreakerTagsMatchTheDesign()
    {
        var type = Library().Find("CircuitBreaker")!;
        var all = Names(type.ExpandTags(type.OptionalTags.Select(t => $"{t.Group.Code()}.{t.Name}")));
        Assert.Equal(BreakerTags.Order(), all.Order());
    }

    [Fact]
    public void OptionalTagsAreLeftOutByDefault()
    {
        var names = Names(Library().Find("GenSet")!.ExpandTags());
        Assert.DoesNotContain("FIN.exhaust_temp", names);
        Assert.DoesNotContain("CMD.set_auto", names);
        Assert.Contains("FIN.coolant_temp", names);
    }

    [Fact]
    public void GenSetHasItsSubStates()
    {
        var type = Library().Find("GenSet")!;
        Assert.Equal([0, 100, 101, 200, 300, 400, 401, 500, 999], type.States.Select(s => s.Code));
        Assert.Equal("ReadyToConnect", type.States.Single(s => s.Code == 401).Name);
    }

    [Fact]
    public void BreakerHasItsAliases()
    {
        var type = Library().Find("CircuitBreaker")!;
        Assert.Equal("is_running", type.Aliases["is_closed"]);
        Assert.Equal("is_off", type.Aliases["is_open"]);
    }

    [Fact]
    public void DefaultsComeFromTheTypeFile()
    {
        var type = Library().Find("GenSet")!;
        var cooldown = type.Tags.Single(t => t.Name == "cooldown_time");
        Assert.Equal(180, cooldown.InitialValue!.GetValue<int>());
        Assert.Equal("s", cooldown.Unit);
        Assert.Equal("Frequency", type.Tags.Single(t => t.Name == "rated_frequency").EnumType);
    }

    [Fact]
    public void BrokenFileIsReportedWithoutStoppingTheOthers()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cmtypes-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            File.Copy(Path.Combine(Directory, "GenSet.cmtype.json"), Path.Combine(directory, "GenSet.cmtype.json"));
            File.WriteAllText(Path.Combine(directory, "Broken.cmtype.json"), "{ \"schema\": 1 }");
            var library = CmLibrary.LoadDirectory(directory);
            Assert.NotNull(library.Find("GenSet"));
            Assert.All(library.Errors, e => Assert.Equal("Broken.cmtype.json", e.File));
            Assert.NotEmpty(library.Errors);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("GenSet", 20, 18)]
    [InlineData("CircuitBreaker", 10, 9)]
    public void AlarmsMatchTheDesign(string typeName, int total, int plc)
    {
        var type = Library().Find(typeName)!;
        Assert.Equal(total, type.Alarms.Count);
        Assert.Equal(plc, type.Alarms.Count(a => a.Plc));
        var all = type.OptionalTags.Select(t => $"{t.Group.Code()}.{t.Name}").ToList();
        var alarmTags = type.ExpandTags(all).Where(t => t.Group == TagGroup.Alm).ToList();
        Assert.Equal(plc * 3, alarmTags.Count);
        Assert.All(type.Alarms, a => Assert.InRange(a.Severity, 0, 30));
    }

    [Fact]
    public void GenSetAlarmSeveritiesFollowTheDesign()
    {
        var alarms = Library().Find("GenSet")!.Alarms.ToDictionary(a => a.Name);
        Assert.Equal(30, alarms["EngineShutdown"].Severity);
        Assert.Equal("TRUE", alarms["EngineShutdown"].Latch);
        Assert.Equal("fail to start", alarms["FailToStart"].OnTransition);
        Assert.Equal("time(STS.load_pct > PAR.overload_pct) > PAR.overload_delay", alarms["Overload"].Condition);
        Assert.False(alarms["ServiceDue"].Plc);
    }
}
