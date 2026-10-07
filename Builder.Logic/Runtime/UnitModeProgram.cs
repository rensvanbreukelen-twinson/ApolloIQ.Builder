using ApolloIQ.Core.Expressions;

namespace Builder.Logic.Runtime;

internal sealed class UnitModeProgram(int autoSlot, int setAuto, int setManual, int[] overrides, int[] remoteOk, int alarmActive, int alarmEnabled, int alarmCount, int previousState)
{
    private bool _previous;

    public int PreviousStateSlot { get; } = previousState;

    public bool Execute(TagMemory memory)
    {
        var auto = autoSlot >= 0 && memory.Get(autoSlot).IsTrue;
        var overridden = overrides.Any(s => memory.Get(s).IsTrue);
        var membersReady = remoteOk.All(s => memory.Get(s) is { IsTrue: true, Good: true });
        if (setAuto >= 0 && memory.Get(setAuto).IsTrue && membersReady && !overridden)
            auto = true;
        if ((setManual >= 0 && memory.Get(setManual).IsTrue) || !membersReady)
            auto = false;
        var byOverride = auto && overridden;
        if (byOverride)
            auto = false;
        if (autoSlot >= 0)
            memory.Set(autoSlot, Value.Of(auto));
        if (alarmActive >= 0)
        {
            var enabled = alarmEnabled < 0 || memory.Get(alarmEnabled).IsTrue;
            var was = memory.Get(alarmActive).IsTrue;
            var now = enabled && (byOverride || (was && !auto));
            if (now && !was && alarmCount >= 0)
                memory.Set(alarmCount, Value.Of(memory.Get(alarmCount).Number + 1));
            memory.Set(alarmActive, Value.Of(now));
        }
        var rising = auto && !_previous;
        _previous = auto;
        return rising;
    }
}
