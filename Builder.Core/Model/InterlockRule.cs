using ApolloIQ.Core.Alarms;
using ApolloIQ.Core.Conventions;
using Builder.Core.Types;

namespace Builder.Core.Model;

public enum InterlockKind
{
    SwitchOn,
    SwitchOff,
    Trip
}

public enum TripEscalation
{
    None,
    EM,
    Unit
}

/// <summary>
/// One line in an interlock list (G-172). A blueprint names its target by role path ("" = itself, "BREAKER", "GEN1.BREAKER");
/// a project instance names it by object ID (null = itself). Permissives describe when the target may switch; a trip
/// describes when it must switch off. Conditions use the ApolloIQ.Core syntax: <c>![ENGINE.is_running]</c>.
/// </summary>
public sealed class InterlockRule
{
    public const int MaxPerKind = 32;

    public string Target { get; set; } = "";

    public Guid? TargetId { get; set; }

    public InterlockKind Kind { get; set; } = InterlockKind.SwitchOn;

    public string Condition { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>Trip only: the name of the alarm on the owner (default Trip1, Trip2, …).</summary>
    public string? Alarm { get; set; }

    /// <summary>Trip only: the stable id of that alarm, assigned on save and kept on rename.</summary>
    public Guid? AlarmId { get; set; }

    /// <summary>Trip only: the alarm priority 0–30.</summary>
    public int Priority { get; set; } = AlarmPriority.Default;

    public TripEscalation Escalate { get; set; } = TripEscalation.None;

    public InterlockRule Clone() => (InterlockRule)MemberwiseClone();

    public static string DefaultAlarmName(int tripIndex) => $"Trip{tripIndex + 1}";

    /// <summary>Gives every trip an alarm name: its own, or Trip1, Trip2, ... by position among the trips.</summary>
    public static IReadOnlyList<InterlockRule> WithAlarmNames(IEnumerable<InterlockRule> rules)
    {
        var list = rules.Select(r => r.Clone()).ToList();
        var trip = 0;
        foreach (var rule in list)
        {
            if (rule.Kind != InterlockKind.Trip)
            {
                rule.Alarm = null;
                rule.AlarmId = null;
                rule.Escalate = TripEscalation.None;
                continue;
            }
            rule.Alarm = string.IsNullOrWhiteSpace(rule.Alarm) ? DefaultAlarmName(trip) : rule.Alarm.Trim();
            trip++;
        }
        return list;
    }

    /// <summary>The alarm of a trip line: PLC reactive (the interlock logic raises it), latched until reset.</summary>
    public static CmAlarm TripAlarm(InterlockRule rule, string fallbackText) => new(new AlarmDefinition
    {
        Id = rule.AlarmId ?? Guid.Empty,
        Name = rule.Alarm!,
        Priority = rule.Priority,
        Message = string.IsNullOrWhiteSpace(rule.Text) ? fallbackText : rule.Text,
        Trigger = AlarmTrigger.State,
        PlcReactive = true,
        Condition = rule.Condition
    }, AlarmSource.Trip, Latched: true);
}
