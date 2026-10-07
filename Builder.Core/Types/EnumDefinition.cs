namespace Builder.Core.Types;

public sealed record EnumMember(string Name, int Value);

public sealed record EnumDefinition(string Name, IReadOnlyList<EnumMember> Members);
