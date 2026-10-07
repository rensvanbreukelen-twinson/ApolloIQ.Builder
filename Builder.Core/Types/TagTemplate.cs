using System.Text.Json.Nodes;
using Builder.Core.Tags;

namespace Builder.Core.Types;

public sealed record TagTemplate(
    string Name,
    TagGroup Group,
    TagDataType DataType,
    TagDirection Direction,
    TagKind Kind,
    TagSource Source,
    bool Optional,
    JsonNode? InitialValue,
    string? EnumType,
    string? Unit,
    string Description,
    JsonNode? AbsentValue = null)
{
    public TagDefinition ToDefinition() =>
        new(Name, Group, DataType, Direction, Kind, InitialValue?.DeepClone(), EnumType, Description, Unit);
}
