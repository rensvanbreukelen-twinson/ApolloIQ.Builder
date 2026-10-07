namespace Builder.Core.Model;

public sealed class ProjectException(string code, string message, string? field = null) : Exception(message)
{
    public string Code { get; } = code;

    public string? Field { get; } = field;
}

public static class ProjectErrors
{
    public const string InvalidName = "invalid_name";
    public const string DuplicateName = "duplicate_name";
    public const string NotFound = "not_found";
    public const string InvalidParent = "invalid_parent";
    public const string Cycle = "cycle";
    public const string InvalidProfile = "invalid_profile";
    public const string InvalidFilter = "invalid_filter";
    public const string InvalidInterlock = "invalid_interlock";
    public const string InvalidCondition = "invalid_condition";
    public const string InvalidSeverity = "invalid_severity";
    public const string InvalidWire = "invalid_wire";
    public const string InvalidPic = "invalid_pic";
    public const string InvalidTopology = "invalid_topology";
    public const string InvalidUnit = "invalid_unit";
    public const string InvalidInputs = "invalid_inputs";
}
