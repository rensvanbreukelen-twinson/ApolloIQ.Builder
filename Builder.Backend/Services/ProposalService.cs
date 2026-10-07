using System.Text.Json;
using System.Text.Json.Nodes;
using Builder.Backend.Endpoints;
using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Design;
using Builder.Logic.Blueprints;
using Builder.Persistence;

namespace Builder.Backend.Services;

/// <summary>A change item as the review shows it: its state, whether it conflicts with the current project and what changed since the previous version.</summary>
public sealed record ReviewItemDto(string Id, string Kind, string Area, string Target, string Group, string? Section, string Summary, JsonNode? Before,
    JsonNode? After, IReadOnlyList<string> DependsOn, IReadOnlyList<string> Problems, string State, string? Conflict, bool ChangedSincePreviousVersion,
    bool NewInVersion, int Version);

public sealed record VersionSummaryDto(int Number, DateTimeOffset CreatedAt, string Author, string? Note, int Items, int ChangedItems, int NewItems, int RemovedItems);

public sealed record ProposalCountsDto(int Open, int Accepted, int Rejected, int Conflicts);

public sealed record ProposalSummaryDto(Guid Id, string Title, string Author, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Version,
    int OpenItems, int Items);

public sealed record UndoSummaryDto(Guid ProposalId, string ProposalTitle, int Items, DateTimeOffset At, string By);

public sealed record ProposalListDto(IReadOnlyList<ProposalSummaryDto> Proposals, UndoSummaryDto? Undo);

public sealed record ValidationDto(bool Ok, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public sealed record ProposalDto(Guid Id, string Title, string Description, string Author, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string BaseRevision, string CurrentRevision, bool ProjectChanged, int Version, IReadOnlyList<VersionSummaryDto> Versions,
    IReadOnlyList<DesignQuestion> Questions, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings, IReadOnlyList<ReviewItemDto> Items,
    IReadOnlyList<ReviewComment> Comments, ProposalCountsDto Counts, JsonObject Design);

/// <summary>A proposal with the result of validating "accept everything that is open".</summary>
public sealed record ProposalResultDto(ProposalDto Proposal, ValidationDto Validation);

public sealed record DesignCheckDto(IReadOnlyList<ChangeItem> Items, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings,
    IReadOnlyList<DesignQuestion> Questions, ValidationDto Validation);

public sealed class ProposalException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// Proposals: create, revise, review, accept (one undoable step) and reject. The AI never changes the project directly; only an accept
/// by the engineer does, through <see cref="DesignApplier"/> on a copy that is validated before it replaces the project.
/// </summary>
public sealed class ProposalService(ProjectWorkspace workspace, BlueprintStore store, CmLibrary library)
{
    private readonly Lock _gate = new();

    public ProposalListDto List(Guid projectId)
    {
        var session = workspace.Get(projectId);
        var files = new ProposalFiles(session.Directory);
        var undo = files.Undo();
        return new ProposalListDto(files.All().Select(Summary).ToList(),
            undo is null ? null : new UndoSummaryDto(undo.ProposalId, undo.ProposalTitle, undo.ItemIds.Count, undo.At, undo.By));
    }

    public ProposalDto Get(Guid projectId, Guid proposalId)
    {
        var session = workspace.Get(projectId);
        return View(session, Load(session, proposalId));
    }

    public DesignCheckDto Check(Guid projectId, JsonNode? designJson)
    {
        var session = workspace.Get(projectId);
        var design = DesignDocument.Parse(designJson);
        var blueprints = store.All();
        return session.Read(project =>
        {
            var plan = DesignPlanner.Plan(design, project, blueprints);
            var validation = Validate(plan, plan.Items.Select(i => i.Id), project, blueprints);
            return new DesignCheckDto(plan.Items, plan.Problems, plan.Warnings, plan.Questions, validation);
        });
    }

    public ProposalResultDto Create(Guid projectId, string? title, string? description, string? author, JsonNode? designJson)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ProposalException(400, "invalid_proposal", "A proposal needs a title.");
        var session = workspace.Get(projectId);
        var design = DesignDocument.Parse(designJson);
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var blueprints = store.All();
            var (plan, revision) = session.Read(p => (DesignPlanner.Plan(design, p, blueprints), DesignReader.Revision(p, blueprints)));
            var proposal = new Proposal
            {
                Id = Guid.NewGuid(),
                Title = title.Trim(),
                Description = description?.Trim() ?? "",
                Author = string.IsNullOrWhiteSpace(author) ? "AI" : author.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
                BaseRevision = revision,
                Versions =
                [
                    new ProposalVersion
                    {
                        Number = 1, CreatedAt = now, Author = string.IsNullOrWhiteSpace(author) ? "AI" : author.Trim(), BaseRevision = revision,
                        Design = design.ToJson(), Items = plan.Items, NewItems = plan.Items.Select(i => i.Id).ToList()
                    }
                ]
            };
            proposal.Status = Status(proposal);
            new ProposalFiles(session.Directory).Save(proposal);
            return Result(session, proposal);
        }
    }

    public ProposalResultDto Update(Guid projectId, Guid proposalId, JsonNode? designJson, string? note, string? author, string? title, string? description)
    {
        var session = workspace.Get(projectId);
        var design = DesignDocument.Parse(designJson);
        lock (_gate)
        {
            var proposal = Load(session, proposalId);
            if (proposal.Closed)
                throw new ProposalException(409, "proposal_closed", "The proposal is rejected or closed; open a new one.");
            var blueprints = store.All();
            var (plan, revision) = session.Read(p => (DesignPlanner.Plan(design, p, blueprints), DesignReader.Revision(p, blueprints)));
            var previous = proposal.Latest;
            var previousItems = previous.Items.ToDictionary(i => i.Id);
            var changed = plan.Items.Where(i => previousItems.TryGetValue(i.Id, out var old) && !Same(old, i)).Select(i => i.Id).ToList();
            var added = plan.Items.Where(i => !previousItems.ContainsKey(i.Id)).Select(i => i.Id).ToList();
            var removed = previous.Items.Where(i => plan.Items.All(n => n.Id != i.Id) && State(proposal, i.Id) != ItemStates.Accepted).Select(i => i.Id).ToList();
            foreach (var id in changed.Where(id => proposal.ItemStates.TryGetValue(id, out var s) && s.State == ItemStates.Rejected))
                proposal.ItemStates.Remove(id);
            var now = DateTimeOffset.UtcNow;
            proposal.Versions.Add(new ProposalVersion
            {
                Number = previous.Number + 1, CreatedAt = now, Author = string.IsNullOrWhiteSpace(author) ? proposal.Author : author.Trim(),
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), BaseRevision = revision, Design = design.ToJson(), Items = plan.Items,
                ChangedItems = changed, NewItems = added, RemovedItems = removed
            });
            if (!string.IsNullOrWhiteSpace(title))
                proposal.Title = title.Trim();
            if (description is not null)
                proposal.Description = description.Trim();
            proposal.UpdatedAt = now;
            proposal.Status = Status(proposal);
            new ProposalFiles(session.Directory).Save(proposal);
            return Result(session, proposal);
        }
    }

    public ValidationDto ValidateSelection(Guid projectId, Guid proposalId, IReadOnlyList<string> itemIds)
    {
        var session = workspace.Get(projectId);
        var proposal = Load(session, proposalId);
        var design = DesignDocument.Parse(proposal.Latest.Design);
        var blueprints = store.All();
        return session.Read(project => Validate(DesignPlanner.Plan(design, project, blueprints), itemIds, project, blueprints));
    }

    /// <summary>Applies the selected items as one step: validated on a copy, then the project and the blueprints are replaced; undoable.</summary>
    public ProposalResultDto Accept(Guid projectId, Guid proposalId, IReadOnlyList<string> itemIds, string? by)
    {
        if (itemIds.Count == 0)
            throw new ProposalException(400, "empty_selection", "Select the items to accept.");
        var session = workspace.Get(projectId);
        lock (_gate)
        {
            var proposal = Load(session, proposalId);
            if (proposal.Closed)
                throw new ProposalException(409, "proposal_closed", "The proposal is rejected or closed.");
            var design = DesignDocument.Parse(proposal.Latest.Design);
            UndoRecord? undo = null;
            var result = session.Replace(project =>
            {
                var blueprints = store.All();
                var plan = DesignPlanner.Plan(design, project, blueprints);
                var applied = DesignApplier.Apply(plan, itemIds, project, blueprints);
                if (!applied.Ok)
                    return ((Project?)null, applied);
                undo = new UndoRecord
                {
                    ProposalId = proposal.Id, ProposalTitle = proposal.Title, ItemIds = [.. itemIds], At = DateTimeOffset.UtcNow,
                    By = string.IsNullOrWhiteSpace(by) ? "engineer" : by.Trim(), ProjectFiles = ProjectStore.Snapshot(session.Directory)
                };
                foreach (var id in applied.ChangedBlueprints.Select(b => b.Id).Concat(applied.DeletedBlueprints))
                    undo.Blueprints[id] = store.Find(id) is { } old ? JsonSerializer.Serialize(old, Blueprint.Json) : null;
                foreach (var blueprint in applied.ChangedBlueprints)
                    store.Save(blueprint);
                foreach (var id in applied.DeletedBlueprints)
                    store.Delete(id);
                BlueprintEndpoints.LoadInto(library, store);
                return (applied.Project, applied);
            });
            if (!result.Ok)
                throw new ValidationFailedException(new ValidationDto(false, result.Errors, result.Warnings));
            undo!.RevisionAfter = session.Read(p => DesignReader.Revision(p, store.All()));
            var files = new ProposalFiles(session.Directory);
            files.SaveUndo(undo);
            foreach (var id in itemIds)
                proposal.ItemStates[id] = new ItemState { State = ItemStates.Accepted, Version = proposal.Latest.Number, At = undo.At, By = undo.By };
            proposal.UpdatedAt = undo.At;
            proposal.Status = Status(proposal);
            files.Save(proposal);
            return new ProposalResultDto(View(session, proposal), new ValidationDto(true, [], result.Warnings));
        }
    }

    public ProposalDto Reject(Guid projectId, Guid proposalId, IReadOnlyList<string>? itemIds, string? by, bool wholeProposal)
    {
        var session = workspace.Get(projectId);
        lock (_gate)
        {
            var proposal = Load(session, proposalId);
            var now = DateTimeOffset.UtcNow;
            var ids = wholeProposal
                ? proposal.Latest.Items.Select(i => i.Id).Where(id => State(proposal, id) == ItemStates.Open).ToList()
                : itemIds ?? [];
            foreach (var id in ids.Where(id => State(proposal, id) != ItemStates.Accepted))
                proposal.ItemStates[id] = new ItemState { State = ItemStates.Rejected, Version = proposal.Latest.Number, At = now, By = by?.Trim() ?? "engineer" };
            if (wholeProposal)
                proposal.Closed = true;
            proposal.UpdatedAt = now;
            proposal.Status = Status(proposal);
            new ProposalFiles(session.Directory).Save(proposal);
            return View(session, proposal);
        }
    }

    /// <summary>Puts rejected items back to open.</summary>
    public ProposalDto Reopen(Guid projectId, Guid proposalId, IReadOnlyList<string> itemIds)
    {
        var session = workspace.Get(projectId);
        lock (_gate)
        {
            var proposal = Load(session, proposalId);
            foreach (var id in itemIds.Where(id => State(proposal, id) == ItemStates.Rejected))
                proposal.ItemStates.Remove(id);
            proposal.Closed = false;
            proposal.UpdatedAt = DateTimeOffset.UtcNow;
            proposal.Status = Status(proposal);
            new ProposalFiles(session.Directory).Save(proposal);
            return View(session, proposal);
        }
    }

    public ReviewComment Comment(Guid projectId, Guid proposalId, string? text, string? author, string? itemId)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ProposalException(400, "empty_comment", "A comment needs text.");
        var session = workspace.Get(projectId);
        lock (_gate)
        {
            var proposal = Load(session, proposalId);
            if (itemId is not null && proposal.Versions.All(v => v.Items.All(i => i.Id != itemId)))
                throw new ProposalException(404, "item_not_found", $"Item {itemId} is not part of this proposal.");
            var comment = new ReviewComment
            {
                Id = Guid.NewGuid(), Author = string.IsNullOrWhiteSpace(author) ? "engineer" : author.Trim(), Text = text.Trim(),
                At = DateTimeOffset.UtcNow, Version = proposal.Latest.Number, ItemId = itemId
            };
            proposal.Comments.Add(comment);
            proposal.UpdatedAt = comment.At;
            new ProposalFiles(session.Directory).Save(proposal);
            return comment;
        }
    }

    public IReadOnlyList<ReviewComment> Comments(Guid projectId, Guid proposalId) => Load(workspace.Get(projectId), proposalId).Comments;

    /// <summary>Restores the project and the blueprints as they were before the last accept.</summary>
    public ProposalListDto UndoLastAccept(Guid projectId)
    {
        var session = workspace.Get(projectId);
        lock (_gate)
        {
            var files = new ProposalFiles(session.Directory);
            var undo = files.Undo() ?? throw new ProposalException(404, "nothing_to_undo", "There is no accept to undo.");
            var current = session.Read(p => DesignReader.Revision(p, store.All()));
            if (current != undo.RevisionAfter)
                throw new ProposalException(409, "project_changed", "The project or the blueprints changed after the accept; the accept can no longer be undone as one step.");
            session.Replace(_ =>
            {
                foreach (var (id, content) in undo.Blueprints)
                {
                    if (content is null)
                        store.Delete(id);
                    else
                        store.Save(JsonSerializer.Deserialize<Blueprint>(content, Blueprint.Json)!);
                }
                BlueprintEndpoints.LoadInto(library, store);
                ProjectStore.Restore(session.Directory, undo.ProjectFiles);
                return (ProjectStore.Load(session.Directory).Project, 0);
            });
            if (files.Find(undo.ProposalId) is { } proposal)
            {
                foreach (var id in undo.ItemIds)
                    proposal.ItemStates.Remove(id);
                proposal.UpdatedAt = DateTimeOffset.UtcNow;
                proposal.Status = Status(proposal);
                files.Save(proposal);
            }
            files.SaveUndo(null);
            return List(projectId);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static Proposal Load(ProjectSession session, Guid proposalId) =>
        new ProposalFiles(session.Directory).Find(proposalId) ?? throw new ProposalException(404, "proposal_not_found", $"Proposal {proposalId} does not exist.");

    private ProposalResultDto Result(ProjectSession session, Proposal proposal)
    {
        var view = View(session, proposal);
        var open = view.Items.Where(i => i.State == ItemStates.Open && i.Conflict is null).Select(i => i.Id).ToList();
        var design = DesignDocument.Parse(proposal.Latest.Design);
        var blueprints = store.All();
        var validation = session.Read(p => Validate(DesignPlanner.Plan(design, p, blueprints), open, p, blueprints));
        return new ProposalResultDto(view, validation);
    }

    private static ValidationDto Validate(DesignPlan plan, IEnumerable<string> itemIds, Project project, IReadOnlyCollection<Blueprint> blueprints)
    {
        var ids = itemIds.ToList();
        if (ids.Count == 0)
            return new ValidationDto(plan.Problems.Count == 0, plan.Problems, plan.Warnings);
        var result = DesignApplier.Apply(plan, ids, project, blueprints);
        var errors = plan.Problems.Concat(result.Errors).ToList();
        return new ValidationDto(errors.Count == 0, errors, plan.Warnings.Concat(result.Warnings).Distinct().ToList());
    }

    private static string State(Proposal proposal, string itemId) =>
        proposal.ItemStates.TryGetValue(itemId, out var state) ? state.State : ItemStates.Open;

    private static bool Same(ChangeItem a, ChangeItem b) => JsonNode.DeepEquals(a.Before, b.Before) && JsonNode.DeepEquals(a.After, b.After);

    private static string Status(Proposal proposal)
    {
        var ids = proposal.Latest.Items.Select(i => i.Id)
            .Concat(proposal.ItemStates.Where(s => s.Value.State == ItemStates.Accepted).Select(s => s.Key)).Distinct().ToList();
        var accepted = ids.Count(id => State(proposal, id) == ItemStates.Accepted);
        var rejected = ids.Count(id => State(proposal, id) == ItemStates.Rejected);
        var open = ids.Count - accepted - rejected;
        if (proposal.Closed && accepted == 0)
            return ProposalStatus.Rejected;
        if (open == 0 || proposal.Closed)
            return accepted == 0 ? ProposalStatus.Rejected : rejected == 0 && open == 0 ? ProposalStatus.Accepted : ProposalStatus.PartlyAccepted;
        return accepted > 0 ? ProposalStatus.PartlyAccepted : ProposalStatus.Open;
    }

    private static ProposalSummaryDto Summary(Proposal proposal) => new(proposal.Id, proposal.Title, proposal.Author, proposal.Status, proposal.CreatedAt,
        proposal.UpdatedAt, proposal.Latest.Number, proposal.Latest.Items.Count(i => State(proposal, i.Id) == ItemStates.Open), proposal.Latest.Items.Count);

    /// <summary>
    /// The review: the latest version's items recomputed against the current project. An open item whose target changed since the
    /// version was made, or that no longer applies, is marked as a conflict. Items accepted in earlier versions stay listed.
    /// </summary>
    private ProposalDto View(ProjectSession session, Proposal proposal)
    {
        var latest = proposal.Latest;
        var design = DesignDocument.Parse(latest.Design);
        var blueprints = store.All();
        var (plan, revision) = session.Read(p => (DesignPlanner.Plan(design, p, blueprints), DesignReader.Revision(p, blueprints)));
        var live = plan.Items.ToDictionary(i => i.Id);
        var items = new List<ReviewItemDto>();
        ReviewItemDto Item(ChangeItem item, string state, string? conflict, int version) => new(item.Id, item.Kind, item.Area, item.Target, item.Group,
            item.Section, item.Summary, item.Before, item.After, item.DependsOn.Where(live.ContainsKey).ToList(), item.Problems, state, conflict,
            latest.ChangedItems.Contains(item.Id), latest.NewItems.Contains(item.Id) && latest.Number > 1, version);
        foreach (var stored in latest.Items)
        {
            var state = State(proposal, stored.Id);
            if (state != ItemStates.Open || proposal.Closed)
            {
                items.Add(Item(stored, proposal.Closed && state == ItemStates.Open ? ItemStates.Rejected : state, null, latest.Number));
                continue;
            }
            if (!live.TryGetValue(stored.Id, out var current))
                items.Add(Item(stored, state, "No longer applies: the project already has this change, or its target is gone.", latest.Number));
            else if (!JsonNode.DeepEquals(current.Before, stored.Before))
                items.Add(Item(current, state, "The target changed since this version was made; check the diff.", latest.Number));
            else
                items.Add(Item(current, state, null, latest.Number));
        }
        foreach (var current in plan.Items.Where(i => latest.Items.All(s => s.Id != i.Id)))
            items.Add(Item(current, ItemStates.Open, "New: needed because the project changed since this version was made.", latest.Number));
        foreach (var version in proposal.Versions.Take(proposal.Versions.Count - 1).Reverse())
            foreach (var old in version.Items.Where(i => State(proposal, i.Id) == ItemStates.Accepted && items.All(x => x.Id != i.Id)))
                items.Add(Item(old, ItemStates.Accepted, null, version.Number));
        var counts = new ProposalCountsDto(items.Count(i => i.State == ItemStates.Open), items.Count(i => i.State == ItemStates.Accepted),
            items.Count(i => i.State == ItemStates.Rejected), items.Count(i => i.State == ItemStates.Open && i.Conflict is not null));
        var versions = proposal.Versions.Select(v => new VersionSummaryDto(v.Number, v.CreatedAt, v.Author, v.Note, v.Items.Count, v.ChangedItems.Count,
            v.NewItems.Count, v.RemovedItems.Count)).ToList();
        return new ProposalDto(proposal.Id, proposal.Title, proposal.Description, proposal.Author, proposal.Status, proposal.CreatedAt, proposal.UpdatedAt,
            proposal.BaseRevision, revision, revision != latest.BaseRevision, latest.Number, versions, plan.Questions, plan.Problems, plan.Warnings, items,
            proposal.Comments, counts, latest.Design);
    }
}

public sealed class ValidationFailedException(ValidationDto validation) : Exception("The selection does not validate.")
{
    public ValidationDto Validation { get; } = validation;
}
