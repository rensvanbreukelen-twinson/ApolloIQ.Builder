namespace Builder.Core.Types;

public static class CommandInputLimits
{
    public const int PriorityMin = 1;
    public const int PriorityMax = 99;
    public const double MaxDebounceSeconds = 10;
    public const double DefaultDebounceSeconds = 0.05;

    public static bool IsPriority(int? priority) => priority is null or (>= PriorityMin and <= PriorityMax);

    public static string PriorityText => $"priorities are {PriorityMin} (highest) to {PriorityMax}.";
}
