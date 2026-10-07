using System.Text.Json;
using System.Text.Json.Nodes;
using ApolloIQ.Core.Expressions;
using Builder.Core.Tags;

namespace Builder.Logic.Runtime;

public static class InitialValues
{
    public static Value From(JsonNode? node, TagDataType type)
    {
        if (node is not JsonValue value)
            return type == TagDataType.Bool ? Value.False : Value.Of(0d);
        return value.GetValueKind() switch
        {
            JsonValueKind.True => Value.True,
            JsonValueKind.False => Value.False,
            JsonValueKind.Number => Value.Of(double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)),
            _ => type == TagDataType.Bool ? Value.False : Value.Of(0d)
        };
    }
}
