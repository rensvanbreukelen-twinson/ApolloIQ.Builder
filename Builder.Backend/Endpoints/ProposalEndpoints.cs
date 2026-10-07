using System.Text.Json.Nodes;
using Builder.Backend.Contracts;
using Builder.Backend.Services;
using Builder.Design;

namespace Builder.Backend.Endpoints;

public sealed record CreateProposalRequest(string? Title, string? Description, string? Author, JsonNode? Design, Guid? Supersedes);

public sealed record UpdateProposalRequest(JsonNode? Design, string? Note, string? Author, string? Title, string? Description);

public sealed record SelectionRequest(IReadOnlyList<string>? ItemIds, string? Author);

public sealed record CommentRequest(string? Text, string? Author, string? ItemId);

public sealed record DesignRequest(JsonNode? Design);

/// <summary>
/// Designs and proposals. <c>GET …/design</c> reads the project as a design; <c>POST …/design/validate</c> checks a fragment without
/// opening a proposal; <c>…/proposals</c> is the review: create, revise, validate a selection, accept (one undoable step), reject, comment.
/// </summary>
public static class ProposalEndpoints
{
    public static void MapProposalApi(this WebApplication app)
    {
        app.MapGet("/api/blueprints/design", (string? name, BlueprintStore store) => Guarded(() => Results.Json(DesignReader.ReadBlueprints(store.All(), name), DesignDocument.Json)));

        var project = app.MapGroup("/api/projects/{projectId:guid}");

        project.MapGet("/design", (Guid projectId, string? path, ProjectWorkspace workspace, BlueprintStore store) => Guarded(() =>
        {
            var session = workspace.Get(projectId);
            var blueprints = store.All();
            return Results.Json(session.Read(p => DesignReader.Read(p, session.Name, blueprints, path)), DesignDocument.Json);
        }));

        project.MapPost("/design/validate", (Guid projectId, DesignRequest request, ProposalService proposals) =>
            Guarded(() => Results.Ok(proposals.Check(projectId, request.Design))));

        var group = project.MapGroup("/proposals");

        group.MapGet("", (Guid projectId, ProposalService proposals) => proposals.List(projectId));

        group.MapPost("", (Guid projectId, CreateProposalRequest request, ProposalService proposals) => Guarded(() =>
        {
            var result = proposals.Create(projectId, request.Title, request.Description, request.Author, request.Design, request.Supersedes);
            return Results.Created($"/api/projects/{projectId}/proposals/{result.Proposal.Id}", result);
        }));

        group.MapPost("/undo", (Guid projectId, ProposalService proposals) => Guarded(() => Results.Ok(proposals.UndoLastAccept(projectId))));

        group.MapGet("/{proposalId:guid}", (Guid projectId, Guid proposalId, ProposalService proposals) =>
            Guarded(() => Results.Ok(proposals.Get(projectId, proposalId))));

        group.MapPut("/{proposalId:guid}", (Guid projectId, Guid proposalId, UpdateProposalRequest request, ProposalService proposals) =>
            Guarded(() => Results.Ok(proposals.Update(projectId, proposalId, request.Design, request.Note, request.Author, request.Title, request.Description))));

        group.MapPost("/{proposalId:guid}/validate", (Guid projectId, Guid proposalId, SelectionRequest request, ProposalService proposals) =>
            Guarded(() => Results.Ok(proposals.ValidateSelection(projectId, proposalId, request.ItemIds ?? []))));

        group.MapPost("/{proposalId:guid}/accept", (Guid projectId, Guid proposalId, SelectionRequest request, ProposalService proposals) =>
            Guarded(() => Results.Ok(proposals.Accept(projectId, proposalId, request.ItemIds ?? [], request.Author))));

        group.MapPost("/{proposalId:guid}/reject", (Guid projectId, Guid proposalId, SelectionRequest request, ProposalService proposals) =>
            Guarded(() => Results.Ok(proposals.Reject(projectId, proposalId, request.ItemIds ?? [], request.Author, wholeProposal: false))));

        group.MapPost("/{proposalId:guid}/reopen", (Guid projectId, Guid proposalId, SelectionRequest request, ProposalService proposals) =>
            Guarded(() => Results.Ok(proposals.Reopen(projectId, proposalId, request.ItemIds ?? []))));

        group.MapPost("/{proposalId:guid}/reject-proposal", (Guid projectId, Guid proposalId, SelectionRequest request, ProposalService proposals) =>
            Guarded(() => Results.Ok(proposals.Reject(projectId, proposalId, null, request.Author, wholeProposal: true))));

        group.MapGet("/{proposalId:guid}/comments", (Guid projectId, Guid proposalId, ProposalService proposals) =>
            Guarded(() => Results.Ok(proposals.Comments(projectId, proposalId))));

        group.MapPost("/{proposalId:guid}/comments", (Guid projectId, Guid proposalId, CommentRequest request, ProposalService proposals) =>
            Guarded(() => Results.Ok(proposals.Comment(projectId, proposalId, request.Text, request.Author, request.ItemId))));
    }

    private static IResult Guarded(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (DesignException ex)
        {
            return Results.Json(new ApiError("invalid_design", ex.Message), statusCode: 400);
        }
        catch (ProposalException ex)
        {
            return Results.Json(new ApiError(ex.Code, ex.Message), statusCode: ex.Status);
        }
        catch (ValidationFailedException ex)
        {
            return Results.Json(new { code = "validation_failed", message = $"The selection does not validate: {ex.Validation.Errors.FirstOrDefault()}", validation = ex.Validation },
                statusCode: 422);
        }
    }
}
