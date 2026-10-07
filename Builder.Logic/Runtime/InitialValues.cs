using System.Text.Json;
using System.Text.Json.Nodes;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Expressions;

namespace Builder.Logic.Runtime;

public static class InitialValues
{
    public static Value From(JsonNode? node, TagDataType type, string? enumType, CmType? cmType)
    {
        if (node is not JsonValue value)
            return type == TagDataType.Bool ? Value.False : Value.Of(0d);
        switch (value.GetValueKind())
        {
            case JsonValueKind.True:
                return Value.True;
            case JsonValueKind.False:
                return Value.False;
            case JsonValueKind.Number:
                return Value.Of(double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture));
            case JsonValueKind.String when type == TagDataType.Enum:
            {
                var name = value.GetValue<string>();
                var member = cmType?.Enums.FirstOrDefault(e => e.Name == enumType)?.Members
                    .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                return member is null ? Value.BadNumber : Value.Of(member.Value);
            }
            default:
                return type == TagDataType.Bool ? Value.False : Value.Of(0d);
        }
    }
}
