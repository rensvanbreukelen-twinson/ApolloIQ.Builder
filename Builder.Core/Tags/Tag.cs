using System.Text.Json.Nodes;
using Builder.Core.Model;

namespace Builder.Core.Tags;

public sealed class Tag : ProjectObject
{
    internal Tag(Guid id, Guid parentId, string symbolKey, TagDefinition definition)
        : base(id, definition.Name, parentId)
    {
        SymbolKey = symbolKey;
        Group = definition.Group;
        DataType = definition.DataType;
        Direction = definition.Direction;
        TagKind = definition.Kind;
        InitialValue = definition.InitialValue?.DeepClone();
        EnumType = definition.EnumType;
        Description = definition.Description;
        Unit = definition.Unit;
    }

    public override ObjectKind Kind => ObjectKind.Tag;

    public override string PathSegment => $"{Group.Code()}.{Name}";

    public string SymbolKey { get; }

    public TagGroup Group { get; }

    public TagDataType DataType { get; }

    public TagDirection Direction { get; }

    public TagKind TagKind { get; }

    public JsonNode? InitialValue { get; internal set; }

    public string? EnumType { get; }

    public string Description { get; }

    public string? Unit { get; }

    public TagOrigin? Origin { get; internal set; }
}
