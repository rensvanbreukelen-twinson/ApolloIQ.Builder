using Builder.Core.Tags;
using Builder.Core.Types;
using Xunit;

namespace Builder.Tests.Types;

public class CmTypeLoaderTests
{
    private const string Minimal = """
        {
          "schema": "apolloiq.cmtype/1",
          "name": "Lamp",
          "version": "1.0.0",
          "tags": {
            "FIN": [ { "name": "feedback", "type": "Bool" } ],
            "CMD": [ { "name": "set_on", "type": "Bool" } ]
          }
        }
        """;

    private static CmTypeException Fails(string json) =>
        Assert.Throws<CmTypeException>(() => CmTypeLoader.Parse(json, "test.cmtype.json"));

    private static string WithTags(string tags) => $$"""
        { "schema": "apolloiq.cmtype/1", "name": "Lamp", "version": "1.0.0", "tags": {{tags}} }
        """;

    [Fact]
    public void LoadsAMinimalType()
    {
        var type = CmTypeLoader.Parse(Minimal, "Lamp.cmtype.json");
        Assert.Equal("Lamp", type.Name);
        Assert.Equal("1.0.0", type.Version);
        Assert.Equal(2, type.Tags.Count);
        Assert.Equal(TagDirection.In, type.Tags[0].Direction);
        Assert.Equal(TagKind.External, type.Tags[0].Kind);
        Assert.Equal(TagSource.Hardwired, type.Tags[0].Source);
    }

    [Fact]
    public void ExpandAddsTheSharedBaseAndConditionedInputs()
    {
        var tags = CmTypeLoader.Parse(Minimal, "Lamp.cmtype.json").ExpandTags().Select(t => $"{t.Group.Code()}.{t.Name}");
        Assert.Equal(["STS.enabled", "STS.state", "STS.remote_ok", "FIN.feedback", "INT.feedback", "SET.invert_feedback", "CMD.set_on"], tags);
    }

    [Fact]
    public void SyntaxErrorReportsTheLine()
    {
        var ex = Fails("{\n  \"schema\": \"apolloiq.cmtype/1\",\n  \"name\": \n}");
        var error = Assert.Single(ex.Errors);
        Assert.Equal(4, error.Line);
        Assert.Contains("test.cmtype.json(4)", error.ToString());
    }

    [Fact]
    public void MissingRequiredPropertiesAreReported()
    {
        var ex = Fails("{}");
        Assert.Equal(["schema", "name", "version"], ex.Errors.Select(e => e.Path));
    }

    [Fact]
    public void WrongSchemaIsRejected()
    {
        var ex = Fails("""{ "schema": "apolloiq.cmtype/2", "name": "Lamp", "version": "1.0.0" }""");
        Assert.Contains(ex.Errors, e => e.Path == "schema");
    }

    [Fact]
    public void BadVersionIsRejected()
    {
        var ex = Fails("""{ "schema": "apolloiq.cmtype/1", "name": "Lamp", "version": "1.0" }""");
        Assert.Contains(ex.Errors, e => e.Path == "version");
    }

    [Fact]
    public void UnknownPropertyIsRejected()
    {
        var ex = Fails(WithTags("""{ "FIN": [ { "name": "feedback", "type": "Bool", "inital": true } ] }"""));
        var error = Assert.Single(ex.Errors);
        Assert.Equal("tags.FIN[0].inital", error.Path);
    }

    [Fact]
    public void UnknownGroupAndTypeAreRejected()
    {
        var ex = Fails(WithTags("""{ "FOO": [], "CMD": [ { "name": "x", "type": "Float" } ] }"""));
        Assert.Contains(ex.Errors, e => e.Path == "tags.FOO");
        Assert.Contains(ex.Errors, e => e.Path == "tags.CMD[0].type");
    }

    [Fact]
    public void LowerCaseGroupIsRejected()
    {
        var ex = Fails(WithTags("""{ "cmd": [] }"""));
        Assert.Contains(ex.Errors, e => e.Path == "tags.cmd");
    }

    [Fact]
    public void DuplicateTagIsRejected()
    {
        var ex = Fails(WithTags("""{ "CMD": [ { "name": "set_on", "type": "Bool" }, { "name": "SET_ON", "type": "Bool" } ] }"""));
        Assert.Contains(ex.Errors, e => e.Path == "tags.CMD[1].name");
    }

    [Fact]
    public void BaseTagsCannotBeDeclared()
    {
        var ex = Fails(WithTags("""{ "STS": [ { "name": "state", "type": "Int16" } ] }"""));
        Assert.Contains(ex.Errors, e => e.Path == "tags.STS[0].name");
    }

    [Fact]
    public void GeneratedTagsCannotBeDeclared()
    {
        var ex = Fails(WithTags("""
            { "FIN": [ { "name": "feedback", "type": "Bool" } ],
              "INT": [ { "name": "feedback", "type": "Bool" } ],
              "SET": [ { "name": "invert_feedback", "type": "Bool" } ] }
            """));
        Assert.Contains(ex.Errors, e => e.Path == "tags.INT");
        Assert.Contains(ex.Errors, e => e.Path == "tags.SET");
    }

    [Fact]
    public void ControllerInputsGetNoInvertSetting()
    {
        var type = CmTypeLoader.Parse(WithTags("""{ "FIN": [ { "name": "running", "type": "Bool", "source": "controller" } ] }"""), "t");
        var names = type.ExpandTags().Select(t => $"{t.Group.Code()}.{t.Name}").ToList();
        Assert.Contains("INT.running", names);
        Assert.DoesNotContain("SET.invert_running", names);
    }

    [Fact]
    public void SourceIsOnlyAllowedOnFinTags()
    {
        var ex = Fails(WithTags("""{ "CMD": [ { "name": "x", "type": "Bool", "source": "hardwired" } ] }"""));
        Assert.Contains(ex.Errors, e => e.Path == "tags.CMD[0].source");
    }

    [Fact]
    public void InitialValueMustFitTheType()
    {
        var ex = Fails(WithTags("""
            { "PAR": [ { "name": "a", "type": "Real", "initial": "x" },
                       { "name": "b", "type": "Int16", "initial": 40000 },
                       { "name": "c", "type": "Bool", "initial": 1 } ] }
            """));
        Assert.Equal(["tags.PAR[0].initial", "tags.PAR[1].initial", "tags.PAR[2].initial"], ex.Errors.Select(e => e.Path));
    }

    [Fact]
    public void EnumTagsNeedAKnownEnum()
    {
        var ex = Fails(WithTags("""{ "SET": [ { "name": "a", "type": "Enum" }, { "name": "b", "type": "Enum", "enum": "Nope" } ] }"""));
        Assert.Contains(ex.Errors, e => e.Path == "tags.SET[0].enum");
        Assert.Contains(ex.Errors, e => e.Path == "tags.SET[1].enum");
    }

    [Fact]
    public void SubStatesMustLieInsideADecade()
    {
        var ex = Fails("""
            { "schema": "apolloiq.cmtype/1", "name": "X", "version": "1.0.0",
              "states": [ { "code": 400, "name": "A" }, { "code": 401, "name": "Running" }, { "code": 402, "name": "B" }, { "code": 402, "name": "C" } ] }
            """);
        Assert.Equal(["states[0].code", "states[1].name", "states[3].code"], ex.Errors.Select(e => e.Path));
    }

    [Fact]
    public void AliasesMustReferToAKnownTarget()
    {
        var ex = Fails("""
            { "schema": "apolloiq.cmtype/1", "name": "X", "version": "1.0.0",
              "aliases": { "is_closed": "is_running", "is_on": "Nowhere", "is_off": "is_stopped" } }
            """);
        Assert.Equal(["aliases.is_on", "aliases.is_off"], ex.Errors.Select(e => e.Path));
    }

    [Fact]
    public void AllErrorsAreCollected()
    {
        var ex = Fails(WithTags("""{ "CMD": [ { "name": "a b", "type": "Bool" }, { "name": "c", "type": "Nope" } ] }"""));
        Assert.Equal(2, ex.Errors.Count);
    }
}
