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
/// describes when it must switch off.
/// </summary>
public sealed class InterlockRule
{
    public const int MaxPerKind = 32;

    public string Target { get; set; } = "";

    public Guid? TargetId { get; set; }

    public InterlockKind Kind { get; set; } = InterlockKind.SwitchOn;

    public string Condition { get; set; } = "";

    public string Text { get; set; } = "";

    public string? Alarm { get; set; }

    public int Severity { get; set; } = SeverityBands.DefaultSeverity;

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
                rule.Escalate = TripEscalation.None;
                continue;
            }
            rule.Alarm = string.IsNullOrWhiteSpace(rule.Alarm) ? DefaultAlarmName(trip) : rule.Alarm.Trim();
            trip++;
        }
        return list;
    }

    public static AlarmDefinition TripAlarm(InterlockRule rule, string fallbackText) =>
        new(rule.Alarm!, rule.Severity, new Dictionary<string, string> { ["en"] = string.IsNullOrWhiteSpace(rule.Text) ? fallbackText : rule.Text },
            "FALSE", "TRUE", true, null, [], Trip: true);
}
