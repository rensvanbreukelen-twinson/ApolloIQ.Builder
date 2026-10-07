using System.Text.Json.Nodes;

namespace Builder.Core.Tags;

public sealed record TagDefinition(
    string Name,
    TagGroup Group,
    TagDataType DataType,
    TagDirection Direction,
    TagKind Kind = TagKind.Internal,
    JsonNode? InitialValue = null,
    string? EnumType = null,
    string Description = "",
    string? Unit = null);
