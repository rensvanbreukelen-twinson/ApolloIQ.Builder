using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Builder.Core.Model;
using Builder.Core.Tags;

namespace Builder.Core.Types;

public static partial class CmTypeLoader
{
    private const int MaxIdentifierLength = ProjectSettings.TagNameMaxLength;

    private static readonly HashSet<string> RootProperties = ["schema", "name", "version", "description", "enums", "states", "aliases", "tags", "logic", "alarms", "builtin"];
    private static readonly HashSet<string> TagProperties = ["name", "type", "direction", "source", "optional", "initial", "absent", "enum", "unit", "description"];
    private static readonly HashSet<string> StateProperties = ["code", "name", "description"];
    private static readonly HashSet<string> EnumMemberProperties = ["name", "value"];
    private static readonly HashSet<string> AlarmProperties = ["name", "severity", "message", "condition", "limit", "watchdog", "onTransition", "latch", "runsOn", "requires"];
    private static readonly HashSet<string> LimitProperties = ["input", "above", "below", "delay"];
    private static readonly HashSet<string> WatchdogProperties = ["while", "timeout"];

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex SemanticVersion();

    public static CmType LoadFile(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static CmType Parse(string json, string fileName)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            throw new CmTypeException([new CmTypeError(fileName, "", $"Invalid JSON: {ex.Message}", (ex.LineNumber ?? 0) + 1)]);
        }

        var context = new Context(fileName);
        var type = context.ReadRoot(root);
        if (context.Errors.Count > 0 || type is null)
            throw new CmTypeException(context.Errors);
        return type;
    }

    private sealed class Context(string fileName)
    {
        public List<CmTypeError> Errors { get; } = [];

        private void Error(string path, string message) => Errors.Add(new CmTypeError(fileName, path, message));

        public CmType? ReadRoot(JsonNode? root)
        {
            if (root is not JsonObject obj)
            {
                Error("", "The file must contain a JSON object.");
                return null;
            }

            CheckProperties(obj, RootProperties, "");

            var schema = String(obj, "schema", "schema", required: true);
            if (schema is not null && schema != CmType.Schema)
                Error("schema", $"Unsupported schema '{schema}'. Expected '{CmType.Schema}'.");

            var name = String(obj, "name", "name", required: true);
            if (name is not null)
                Identifier(name, "name");

            var version = String(obj, "version", "version", required: true);
            if (version is not null && !SemanticVersion().IsMatch(version))
                Error("version", $"Version '{version}' must look like 1.0.0.");

            var enums = ReadEnums(obj["enums"]);
            var subStates = ReadStates(obj["states"]);
            var aliases = ReadAliases(obj["aliases"], subStates);
            var tags = ReadTags(obj["tags"], enums);
            var alarms = ReadAlarms(obj["alarms"], tags);

            if (name is null || version is null)
                return null;

            return new CmType
            {
                Name = name,
                Version = version,
                Description = String(obj, "description", "description") ?? "",
                Enums = enums,
                SubStates = subStates,
                Aliases = aliases,
                Tags = tags,
                Logic = obj["logic"]?.DeepClone(),
                Alarms = alarms,
                Builtin = Builtin(obj)
            };
        }

        private string? Builtin(JsonObject obj)
        {
            var builtin = String(obj, "builtin", "builtin");
            if (builtin is not null && builtin != PicBehaviour.Builtin)
                Error("builtin", $"Unknown built-in behaviour '{builtin}'. Use {PicBehaviour.Builtin}.");
            return builtin;
        }

        private List<AlarmDefinition> ReadAlarms(JsonNode? node, List<TagTemplate> tags)
        {
            var result = new List<AlarmDefinition>();
            if (node is null)
                return result;
            if (node is not JsonArray list)
            {
                Error("alarms", "'alarms' must be a list.");
                return result;
            }
            for (var i = 0; i < list.Count; i++)
            {
                var path = $"alarms[{i}]";
                if (list[i] is not JsonObject obj)
                {
                    Error(path, "An alarm must be an object.");
                    continue;
                }
                CheckProperties(obj, AlarmProperties, path);
                var name = String(obj, "name", $"{path}.name", required: true);
                if (name is null)
                    continue;
                Identifier(name, $"{path}.name");
                if (result.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                    Error($"{path}.name", $"Duplicate alarm '{name}'.");

                var severity = Integer(obj, "severity", $"{path}.severity", required: true) ?? 0;
                if (!SeverityBands.IsValid(severity))
                    Error($"{path}.severity", SeverityBands.RangeText);

                var message = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (obj["message"] is JsonObject messages)
                {
                    foreach (var (language, text) in messages)
                    {
                        if (text is JsonValue v && v.TryGetValue<string>(out var t))
                            message[language] = t;
                        else
                            Error($"{path}.message.{language}", "A message must be a text.");
                    }
                }
                else if (obj["message"] is not null)
                    Error($"{path}.message", "'message' must be an object of language → text, for example { \"en\": \"Fail to start\" }.");

                var parameters = new List<TagTemplate>();
                string? condition = String(obj, "condition", $"{path}.condition");
                var onTransition = String(obj, "onTransition", $"{path}.onTransition");
                var forms = new[] { "condition", "limit", "watchdog" }.Count(k => obj[k] is not null);
                if (onTransition is null ? forms != 1 : forms > 1 || obj["condition"] is null && forms == 1)
                    Error(path, "An alarm needs exactly one of 'condition', 'limit' or 'watchdog', or 'onTransition' with an optional 'condition'.");
                if (obj["limit"] is JsonObject limit)
                    condition = LimitCondition(name, limit, $"{path}.limit", parameters);
                else if (obj["limit"] is not null)
                    Error($"{path}.limit", "'limit' must be an object with input, above or below, and an optional delay.");
                if (obj["watchdog"] is JsonObject watchdog)
                    condition = WatchdogCondition(name, watchdog, $"{path}.watchdog", parameters);
                else if (obj["watchdog"] is not null)
                    Error($"{path}.watchdog", "'watchdog' must be an object with while and timeout.");
                foreach (var parameter in parameters)
                {
                    if (tags.Any(t => t.Group == TagGroup.Par && string.Equals(t.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)))
                        Error(path, $"'PAR.{parameter.Name}' is generated for this alarm. Remove it from the tags or refer to it by name.");
                }

                var latch = obj["latch"] switch
                {
                    null => "FALSE",
                    JsonValue v when v.TryGetValue<bool>(out var b) => b ? "TRUE" : "FALSE",
                    JsonValue v when v.TryGetValue<string>(out var e) => e,
                    _ => null
                };
                if (latch is null)
                    Error($"{path}.latch", "'latch' must be true, false or a condition such as \"SET.trip_latches\".");

                var runsOn = String(obj, "runsOn", $"{path}.runsOn") ?? "PLC";
                if (runsOn is not ("PLC" or "SCADA"))
                    Error($"{path}.runsOn", "'runsOn' must be PLC or SCADA.");

                var requires = String(obj, "requires", $"{path}.requires");
                if (requires is not null && !tags.Any(t => t.Optional && string.Equals($"{t.Group.Code()}.{t.Name}", requires, StringComparison.OrdinalIgnoreCase)))
                    Error($"{path}.requires", $"'{requires}' is not an optional tag of this type.");

                result.Add(new AlarmDefinition(name, severity, message, condition ?? (onTransition is null ? "FALSE" : "TRUE"), latch ?? "FALSE",
                    runsOn == "PLC", requires, parameters, onTransition));
            }
            return result;
        }

        private string? Reference(string alarm, JsonObject obj, string key, string suffix, string unit, string path, List<TagTemplate> parameters)
        {
            switch (obj[key])
            {
                case null:
                    return null;
                case JsonValue v when v.TryGetValue<string>(out var reference):
                    return reference;
                case JsonValue v when v.TryGetValue<double>(out var number):
                    var name = $"{alarm}_{suffix}";
                    parameters.Add(new TagTemplate(name, TagGroup.Par, TagDataType.Real, TagDirection.InOut, TagKind.Internal, TagSource.Internal,
                        false, JsonValue.Create(number), null, unit.Length == 0 ? null : unit, $"{suffix} of alarm {alarm}"));
                    return $"PAR.{name}";
                default:
                    Error($"{path}.{key}", $"'{key}' must be a number (a PAR is generated) or a tag.");
                    return null;
            }
        }

        private string? LimitCondition(string alarm, JsonObject limit, string path, List<TagTemplate> parameters)
        {
            CheckProperties(limit, LimitProperties, path);
            var input = String(limit, "input", $"{path}.input", required: true);
            var above = Reference(alarm, limit, "above", "limit", "", path, parameters);
            var below = Reference(alarm, limit, "below", "limit", "", path, parameters);
            if ((above is null) == (below is null))
                Error(path, "A limit needs exactly one of 'above' or 'below'.");
            var delay = Reference(alarm, limit, "delay", "delay", "s", path, parameters);
            if (input is null || (above ?? below) is null)
                return null;
            var compare = above is not null ? $"{input} > {above}" : $"{input} < {below}";
            return delay is null ? compare : $"time({compare}) > {delay}";
        }

        private string? WatchdogCondition(string alarm, JsonObject watchdog, string path, List<TagTemplate> parameters)
        {
            CheckProperties(watchdog, WatchdogProperties, path);
            var active = String(watchdog, "while", $"{path}.while", required: true);
            var timeout = Reference(alarm, watchdog, "timeout", "timeout", "s", path, parameters);
            if (timeout is null)
                Error($"{path}.timeout", "'timeout' is required.");
            return active is null || timeout is null ? null : $"time({active}) > {timeout}";
        }

        private List<EnumDefinition> ReadEnums(JsonNode? node)
        {
            var result = new List<EnumDefinition>();
            if (node is null)
                return result;
            if (node is not JsonObject obj)
            {
                Error("enums", "'enums' must be an object of enum name → member list.");
                return result;
            }

            foreach (var (enumName, membersNode) in obj)
            {
                var path = $"enums.{enumName}";
                Identifier(enumName, path);
                if (string.Equals(enumName, UniversalStates.EnumName, StringComparison.OrdinalIgnoreCase))
                    Error(path, $"'{UniversalStates.EnumName}' is reserved for the universal state codes.");
                if (membersNode is not JsonArray members || members.Count == 0)
                {
                    Error(path, "An enum needs a non-empty list of members.");
                    continue;
                }

                var list = new List<EnumMember>();
                for (var i = 0; i < members.Count; i++)
                {
                    var memberPath = $"{path}[{i}]";
                    if (members[i] is not JsonObject member)
                    {
                        Error(memberPath, "A member must be an object with 'name' and 'value'.");
                        continue;
                    }
                    CheckProperties(member, EnumMemberProperties, memberPath);
                    var memberName = String(member, "name", $"{memberPath}.name", required: true);
                    var value = Integer(member, "value", $"{memberPath}.value", required: true);
                    if (memberName is null || value is null)
                        continue;
                    Identifier(memberName, $"{memberPath}.name");
                    if (list.Any(m => string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase)))
                        Error($"{memberPath}.name", $"Duplicate member '{memberName}'.");
                    if (list.Any(m => m.Value == value))
                        Error($"{memberPath}.value", $"Duplicate value {value}.");
                    list.Add(new EnumMember(memberName, value.Value));
                }
                result.Add(new EnumDefinition(enumName, list));
            }
            return result;
        }

        private List<StateDefinition> ReadStates(JsonNode? node)
        {
            var result = new List<StateDefinition>();
            if (node is null)
                return result;
            if (node is not JsonArray array)
            {
                Error("states", "'states' must be a list of sub-states.");
                return result;
            }

            for (var i = 0; i < array.Count; i++)
            {
                var path = $"states[{i}]";
                if (array[i] is not JsonObject state)
                {
                    Error(path, "A sub-state must be an object with 'code' and 'name'.");
                    continue;
                }
                CheckProperties(state, StateProperties, path);
                var code = Integer(state, "code", $"{path}.code", required: true);
                var name = String(state, "name", $"{path}.name", required: true);
                if (code is null || name is null)
                    continue;
                Identifier(name, $"{path}.name");
                if (code is < 1 or >= UniversalStates.UnavailableCode || UniversalStates.IsUniversalCode(code.Value))
                    Error($"{path}.code", $"Sub-state code {code} must lie inside a category (for example 101 or 401), not on a universal code.");
                if (UniversalStates.All.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                    Error($"{path}.name", $"'{name}' is a universal state name.");
                if (result.Any(s => s.Code == code))
                    Error($"{path}.code", $"Duplicate sub-state code {code}.");
                if (result.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                    Error($"{path}.name", $"Duplicate sub-state name '{name}'.");
                result.Add(new StateDefinition(code.Value, name, String(state, "description", $"{path}.description") ?? ""));
            }
            return result;
        }

        private Dictionary<string, string> ReadAliases(JsonNode? node, List<StateDefinition> subStates)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (node is null)
                return result;
            if (node is not JsonObject obj)
            {
                Error("aliases", "'aliases' must be an object of alias → standard alias or state name.");
                return result;
            }

            var stateNames = UniversalStates.All.Concat(subStates).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (alias, targetNode) in obj)
            {
                var path = $"aliases.{alias}";
                Identifier(alias, path);
                if (UniversalStates.StandardAliases.ContainsKey(alias))
                    Error(path, $"'{alias}' is a standard alias and cannot be redefined.");
                var target = targetNode is JsonValue v && v.TryGetValue<string>(out var t) ? t : null;
                if (target is null)
                {
                    Error(path, "An alias must refer to a standard alias or a state name.");
                    continue;
                }
                if (!UniversalStates.StandardAliases.ContainsKey(target) && !stateNames.Contains(target))
                    Error(path, $"'{target}' is not a standard alias or a state of this type.");
                result[alias] = target;
            }
            return result;
        }

        private List<TagTemplate> ReadTags(JsonNode? node, List<EnumDefinition> enums)
        {
            var result = new List<TagTemplate>();
            if (node is null)
                return result;
            if (node is not JsonObject groups)
            {
                Error("tags", "'tags' must be an object of group → tag list.");
                return result;
            }

            foreach (var (groupCode, listNode) in groups)
            {
                var groupPath = $"tags.{groupCode}";
                if (!TagGroupNames.TryParse(groupCode, out var group) || groupCode != groupCode.ToUpperInvariant() || group == TagGroup.Alm)
                {
                    Error(groupPath, $"Unknown tag group '{groupCode}'. Use FIN, CMD, OUT, LOK, PAR, SET, PMT, STS or INT.");
                    continue;
                }
                if (listNode is not JsonArray list)
                {
                    Error(groupPath, "A tag group must be a list of tags.");
                    continue;
                }
                for (var i = 0; i < list.Count; i++)
                {
                    var tag = ReadTag(list[i], group, $"{groupPath}[{i}]", enums);
                    if (tag is null)
                        continue;
                    if (result.Any(t => t.Group == group && string.Equals(t.Name, tag.Name, StringComparison.OrdinalIgnoreCase)))
                        Error($"{groupPath}[{i}].name", $"Duplicate tag '{groupCode}.{tag.Name}'.");
                    result.Add(tag);
                }
            }

            foreach (var generated in result.Where(BaseBehaviour.IsConditioned).ToList())
            {
                var copy = BaseBehaviour.ConditionedCopy(generated);
                if (result.Any(t => t.Group == TagGroup.Int && string.Equals(t.Name, copy.Name, StringComparison.OrdinalIgnoreCase)))
                    Error("tags.INT", $"'INT.{copy.Name}' is generated from 'FIN.{generated.Name}' (G-31). Remove it from the file.");
                if (generated.Source != TagSource.Hardwired)
                    continue;
                var invert = BaseBehaviour.InvertSetting(generated);
                if (result.Any(t => t.Group == TagGroup.Set && string.Equals(t.Name, invert.Name, StringComparison.OrdinalIgnoreCase)))
                    Error("tags.SET", $"'SET.{invert.Name}' is generated from 'FIN.{generated.Name}' (G-31). Remove it from the file.");
            }
            return result;
        }

        private TagTemplate? ReadTag(JsonNode? node, TagGroup group, string path, List<EnumDefinition> enums)
        {
            if (node is not JsonObject obj)
            {
                Error(path, "A tag must be an object.");
                return null;
            }
            CheckProperties(obj, TagProperties, path);

            var name = String(obj, "name", $"{path}.name", required: true);
            var typeText = String(obj, "type", $"{path}.type", required: true);
            if (name is null || typeText is null)
                return null;
            Identifier(name, $"{path}.name");
            if (BaseBehaviour.IsReserved(group, name))
                Error($"{path}.name", $"'{group.Code()}.{name}' is part of the shared base and is added automatically.");

            if (!Enum.TryParse<TagDataType>(typeText, ignoreCase: true, out var dataType) || !Enum.IsDefined(dataType))
            {
                Error($"{path}.type", $"Unknown data type '{typeText}'. Use Bool, Int16, Int32, Real, LReal, String, DateTime or Enum.");
                return null;
            }

            var direction = DefaultDirection(group);
            if (String(obj, "direction", $"{path}.direction") is { } directionText)
            {
                if (Enum.TryParse<TagDirection>(directionText, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                    direction = parsed;
                else
                    Error($"{path}.direction", $"Unknown direction '{directionText}'. Use In, Out or InOut.");
            }

            var source = group == TagGroup.Fin ? TagSource.Hardwired : TagSource.Internal;
            if (String(obj, "source", $"{path}.source") is { } sourceText)
            {
                if (group != TagGroup.Fin)
                    Error($"{path}.source", "Only FIN tags have a source.");
                else if (Enum.TryParse<TagSource>(sourceText, ignoreCase: true, out var parsed) && parsed != TagSource.Internal)
                    source = parsed;
                else
                    Error($"{path}.source", $"Unknown source '{sourceText}'. Use Hardwired or Controller.");
            }

            var enumType = String(obj, "enum", $"{path}.enum");
            if (dataType == TagDataType.Enum && enumType is null)
                Error($"{path}.enum", "An Enum tag needs 'enum' with the name of an enum of this type.");
            if (dataType != TagDataType.Enum && enumType is not null)
                Error($"{path}.enum", "'enum' is only allowed for Enum tags.");
            var enumDefinition = enumType is null ? null : enums.FirstOrDefault(e => string.Equals(e.Name, enumType, StringComparison.OrdinalIgnoreCase));
            if (enumType is not null && enumDefinition is null)
                Error($"{path}.enum", $"Unknown enum '{enumType}'.");

            var initial = obj["initial"]?.DeepClone();
            if (initial is not null)
                CheckInitial(initial, dataType, enumDefinition, $"{path}.initial");
            var absent = obj["absent"]?.DeepClone();
            var optional = Boolean(obj, "optional", $"{path}.optional") ?? false;
            if (absent is not null)
            {
                if (!optional)
                    Error($"{path}.absent", "'absent' is only allowed on optional tags: it is the value the logic uses when the tag is not created.");
                CheckInitial(absent, dataType, enumDefinition, $"{path}.absent");
            }

            return new TagTemplate(
                name,
                group,
                dataType,
                direction,
                group == TagGroup.Fin ? TagKind.External : TagKind.Internal,
                source,
                optional,
                initial,
                enumDefinition?.Name,
                String(obj, "unit", $"{path}.unit"),
                String(obj, "description", $"{path}.description") ?? "",
                absent);
        }

        private void CheckInitial(JsonNode initial, TagDataType dataType, EnumDefinition? enumDefinition, string path)
        {
            var value = initial as JsonValue;
            var kind = value?.GetValueKind();
            var ok = dataType switch
            {
                TagDataType.Bool => kind is JsonValueKind.True or JsonValueKind.False,
                TagDataType.Int16 => value is not null && value.TryGetValue<long>(out var i16) && i16 is >= short.MinValue and <= short.MaxValue,
                TagDataType.Int32 => value is not null && value.TryGetValue<long>(out var i32) && i32 is >= int.MinValue and <= int.MaxValue,
                TagDataType.Real or TagDataType.LReal => kind == JsonValueKind.Number,
                TagDataType.String => kind == JsonValueKind.String,
                TagDataType.DateTime => value is not null && value.TryGetValue<string>(out var text)
                                        && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
                TagDataType.Enum => value is not null && value.TryGetValue<string>(out var member)
                                    && (enumDefinition is null || enumDefinition.Members.Any(m => string.Equals(m.Name, member, StringComparison.OrdinalIgnoreCase))),
                _ => false
            };
            if (!ok)
                Error(path, $"Initial value {initial.ToJsonString()} does not fit type {dataType}.");
        }

        private static TagDirection DefaultDirection(TagGroup group) => group switch
        {
            TagGroup.Fin or TagGroup.Cmd or TagGroup.Lok => TagDirection.In,
            TagGroup.Par or TagGroup.Set => TagDirection.InOut,
            _ => TagDirection.Out
        };

        private void Identifier(string name, string path)
        {
            if (NameRules.Check(name, MaxIdentifierLength) is { } error)
                Error(path, $"'{name}': {error}");
        }

        private void CheckProperties(JsonObject obj, HashSet<string> allowed, string path)
        {
            foreach (var (key, _) in obj)
            {
                if (!allowed.Contains(key))
                    Error(path.Length == 0 ? key : $"{path}.{key}", $"Unknown property '{key}'.");
            }
        }

        private string? String(JsonObject obj, string key, string path, bool required = false)
        {
            var node = obj[key];
            if (node is null)
            {
                if (required)
                    Error(path, $"'{key}' is required.");
                return null;
            }
            if (node is JsonValue v && v.TryGetValue<string>(out var s))
                return s;
            Error(path, $"'{key}' must be a string.");
            return null;
        }

        private int? Integer(JsonObject obj, string key, string path, bool required = false)
        {
            var node = obj[key];
            if (node is null)
            {
                if (required)
                    Error(path, $"'{key}' is required.");
                return null;
            }
            if (node is JsonValue v && v.TryGetValue<int>(out var i))
                return i;
            Error(path, $"'{key}' must be a whole number.");
            return null;
        }

        private bool? Boolean(JsonObject obj, string key, string path)
        {
            var node = obj[key];
            if (node is null)
                return null;
            if (node is JsonValue v && v.TryGetValue<bool>(out var b))
                return b;
            Error(path, $"'{key}' must be true or false.");
            return null;
        }
    }
}
