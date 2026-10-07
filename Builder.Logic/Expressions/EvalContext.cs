namespace Builder.Logic.Expressions;

public interface IEvalContext
{
    Value Read(int slot);

    double CycleSeconds { get; }

    double StateTimeSeconds { get; }

    double[] Timers { get; }

    void Report(string message);
}
