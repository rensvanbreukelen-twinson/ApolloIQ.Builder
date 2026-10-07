using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Builder.Design;

namespace Builder.Backend.Services;

public static class ProposalStatus
{
    public const string Open = "Open";
    public const string PartlyAccepted = "PartlyAccepted";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
    public const string Superseded = "Superseded";
}

public static class ItemStates
{
    public const string Open = "Open";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
}

/// <summary>
/// A proposal: a pull request against the project. It holds versions (each a design fragment with the change items computed when the
/// version was made), the state of each item and the review comments. Stored as <c>proposals/&lt;id&gt;.json</c> in the project folder.
/// </summary>
public sealed class Proposal
{
    public const string SchemaId = "apolloiq.proposal/1";

    public string Schema { get; set; } = SchemaId;
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "AI";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string Status { get; set; } = ProposalStatus.Open;

    /// <summary>The project revision (<see cref="DesignReader.Revision"/>) the first version was computed against.</summary>
    public string BaseRevision { get; set; } = "";

    /// <summary>The project revision after the last version, accept or undo of this proposal: a later change was made elsewhere.</summary>
    public string KnownRevision { get; set; } = "";

    /// <summary>The whole proposal was rejected or closed: no item can be accepted any more.</summary>
    public bool Closed { get; set; }

    /// <summary>The proposal that replaces this one: a new proposal named this one in <c>supersedes</c>.</summary>
    public Guid? SupersededBy { get; set; }

    public List<ProposalVersion> Versions { get; set; } = [];
    public Dictionary<string, ItemState> ItemStates { get; set; } = [];
    public List<ReviewComment> Comments { get; set; } = [];

    [JsonIgnore]
    public ProposalVersion Latest => Versions[^1];
}

public sealed class ProposalVersion
{
    public int Number { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Author { get; set; } = "";
    public string? Note { get; set; }

    /// <summary>The project revision this version's items were computed against.</summary>
    public string BaseRevision { get; set; } = "";

    public JsonObject Design { get; set; } = [];
    public List<ChangeItem> Items { get; set; } = [];
    public List<string> ChangedItems { get; set; } = [];
    public List<string> NewItems { get; set; } = [];
    public List<string> RemovedItems { get; set; } = [];
}

public sealed class ItemState
{
    public string State { get; set; } = ItemStates.Open;
    public int Version { get; set; }
    public DateTimeOffset At { get; set; }
    public string By { get; set; } = "";

    /// <summary>The item as it was accepted (computed against the project at that moment).</summary>
    public ChangeItem? Item { get; set; }
}

public sealed class ReviewComment
{
    public Guid Id { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public int Version { get; set; }

    /// <summary>The item the comment is about; null for a comment on the whole proposal.</summary>
    public string? ItemId { get; set; }
}

/// <summary>What is needed to undo the last accept: the project files before it and the blueprints it touched.</summary>
public sealed class UndoRecord
{
    public Guid ProposalId { get; set; }
    public string ProposalTitle { get; set; } = "";
    public List<string> ItemIds { get; set; } = [];
    public DateTimeOffset At { get; set; }
    public string By { get; set; } = "";

    /// <summary>The project revision right after the accept; the undo is refused when the project changed since.</summary>
    public string RevisionAfter { get; set; } = "";

    public Dictionary<string, string> ProjectFiles { get; set; } = [];

    /// <summary>Blueprint id → its file content before the accept (null: it did not exist).</summary>
    public Dictionary<Guid, string?> Blueprints { get; set; } = [];
}

/// <summary>The proposals of one project, as files in <c>&lt;project&gt;/proposals</c>, and the undo record in <c>&lt;project&gt;/undo</c>.</summary>
public sealed class ProposalFiles(string projectDirectory)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private string Directory => Path.Combine(projectDirectory, "proposals");

    private string UndoFile => Path.Combine(projectDirectory, "undo", "last-accept.json");

    public IReadOnlyList<Proposal> All()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];
        return System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(Read).OfType<Proposal>()
            .OrderByDescending(p => p.UpdatedAt).ToList();
    }

    public Proposal? Find(Guid id) => Read(Path.Combine(Directory, $"{id:D}.json"));

    public void Save(Proposal proposal)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, $"{proposal.Id:D}.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(proposal, Json) + "\n");
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public UndoRecord? Undo() => File.Exists(UndoFile) ? JsonSerializer.Deserialize<UndoRecord>(File.ReadAllText(UndoFile), Json) : null;

    public void SaveUndo(UndoRecord? record)
    {
        if (record is null)
        {
            if (File.Exists(UndoFile))
                File.Delete(UndoFile);
            return;
        }
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(UndoFile)!);
        File.WriteAllText(UndoFile, JsonSerializer.Serialize(record, Json) + "\n");
    }

    private static Proposal? Read(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<Proposal>(File.ReadAllText(path), Json) is { Schema: Proposal.SchemaId } proposal ? proposal : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
