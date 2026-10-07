using ApolloIQ.Core.Alarms;

namespace Builder.Core.Types;

/// <summary>Where an alarm of a CM, EM or Unit comes from.</summary>
public enum AlarmSource
{
    /// <summary>Defined in the blueprint's alarm list; evaluated by the alarm logic.</summary>
    Blueprint,

    /// <summary>Generated from a state timeout; evaluated by the alarm logic.</summary>
    StateTimeout,

    /// <summary>The alarm of a Trip interlock line; written by the interlock logic.</summary>
    Trip,

    /// <summary>The Unit / EM alarm "switched to manual by an override"; written by the auto/manual logic.</summary>
    UnitOverride,

    /// <summary>The stuck alarm of a digital command input; written by the command input logic.</summary>
    StuckInput
}

/// <summary>
/// An alarm of a CM, EM or Unit: the shared <see cref="AlarmDefinition"/> plus what only the Builder's logic needs.
/// <para><see cref="AlarmDefinition.PlcReactive"/>: the PLC logic evaluates it and writes <c>ALM.&lt;name&gt;.*</c>; transition
/// guards may read <c>[ALM.&lt;name&gt;.active]</c>. Otherwise SCADA evaluates it (the simulator does too) and it has no tags.</para>
/// <para><see cref="Latched"/>: stays active until <c>CMD.reset</c> (PLC reactive only). <see cref="OnTransition"/>: raised only in
/// the cycle the named transition is taken (PLC reactive only).</para>
/// </summary>
public sealed record CmAlarm(AlarmDefinition Definition, AlarmSource Source, bool Latched = false, string? OnTransition = null)
{
    public string Name => Definition.Name;

    public bool PlcReactive => Definition.PlcReactive;

    /// <summary>Evaluated by the alarm logic (the others are written by the interlock, auto/manual or command input logic).</summary>
    public bool Evaluated => Source is AlarmSource.Blueprint or AlarmSource.StateTimeout;
}
