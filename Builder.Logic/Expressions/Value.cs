using System.Globalization;

namespace Builder.Logic.Expressions;

public enum ValueType
{
    Bool,
    Number
}

public readonly record struct Value(ValueType Type, double Number, bool Good)
{
    public static readonly Value True = new(ValueType.Bool, 1, true);
    public static readonly Value False = new(ValueType.Bool, 0, true);
    public static readonly Value BadBool = new(ValueType.Bool, 0, false);
    public static readonly Value BadNumber = new(ValueType.Number, 0, false);

    public bool Bool => Number != 0;

    public bool IsTrue => Good && Bool;

    public static Value Of(bool value, bool good = true) => new(ValueType.Bool, value ? 1 : 0, good);

    public static Value Of(double value, bool good = true) =>
        double.IsFinite(value) ? new(ValueType.Number, value, good) : BadNumber;

    public Value WithQuality(bool good) => this with { Good = Good && good };

    public override string ToString()
    {
        var text = Type == ValueType.Bool ? (Bool ? "TRUE" : "FALSE") : Number.ToString(CultureInfo.InvariantCulture);
        return Good ? text : $"{text} (bad)";
    }
}
