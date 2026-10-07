using System.Globalization;
using System.Text.Json;
using Builder.Core.Tags;
using ApolloIQ.Core.Expressions;

namespace Builder.Simulator;

internal static class ValueConversion
{
    public static object? ToObject(Value value, TagDataType type, string? text) => type switch
    {
        TagDataType.Bool => value.Bool,
        TagDataType.Int16 or TagDataType.Int32 or TagDataType.Enum => (long)value.Number,
        TagDataType.String or TagDataType.DateTime => text,
        TagDataType.Real => double.Parse(((float)value.Number).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
        _ => value.Number
    };

    public static (Value Value, string? Text) FromObject(object? input, SimTag tag)
    {
        if (input is JsonElement element)
            input = element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => element.GetDouble(),
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => throw new SimulationException($"{tag.Path}: a value must be a boolean, a number or a text.")
            };

        if (tag.DataType is TagDataType.String or TagDataType.DateTime)
            return (Value.Of(0d), input switch
            {
                null => null,
                string s => s,
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => input.ToString()
            });

        switch (input)
        {
            case bool b:
                return (tag.DataType == TagDataType.Bool ? Value.Of(b) : Value.Of(b ? 1d : 0d), null);
            case string s when bool.TryParse(s, out var parsed):
                return (tag.DataType == TagDataType.Bool ? Value.Of(parsed) : Value.Of(parsed ? 1d : 0d), null);
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                return (FromNumber(number, tag), null);
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return (FromNumber(Convert.ToDouble(input, CultureInfo.InvariantCulture), tag), null);
            default:
                throw new SimulationException($"'{input}' is not a valid value for {tag.Path} ({tag.DataType}).");
        }
    }

    private static Value FromNumber(double number, SimTag tag) =>
        tag.DataType == TagDataType.Bool ? Value.Of(number != 0) : Value.Of(number);
}
