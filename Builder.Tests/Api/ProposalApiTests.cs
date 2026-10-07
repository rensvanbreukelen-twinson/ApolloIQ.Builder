using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ApolloIQ.Core.Exchange;
using Builder.Backend.Contracts;
using Builder.Backend.Endpoints;
using Builder.Backend.Services;
using Builder.Tests.Design;
using Xunit;

namespace Builder.Tests.Api;

public sealed class ProposalApiTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiServer _server = null!;
    private Guid _project;

    public async ValueTask InitializeAsync()
    {
        _server = await ApiServer.StartAsync(fixtures: false);
        _project = (await _server.Post<ProjectDto>("/api/projects", new CreateProjectRequest("Dirty water demo"))).Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _server.DeleteData();
    }

    private string Url => $"/api/projects/{_project}/proposals";

    private async Task<ProposalResultDto> Create() =>
        await _server.Post<ProposalResultDto>(Url, new CreateProposalRequest("Dirty water tank", "Open questions below.", "AI", DesignTests.DirtyWaterJson()));

    [Fact]
    public async Task CheckingADesignDoesNotChangeTheProject()
    {
        var check = await _server.Post<DesignCheckDto>($"/api/projects/{_project}/design/validate", new DesignRequest(DesignTests.DirtyWaterJson()));
        Assert.True(check.Validation.Ok, string.Join("\n", check.Validation.Errors));
        Assert.Equal(7, check.Items.Count);
        Assert.Equal(4, check.Questions.Count);
        var design = await _server.Get<JsonObject>($"/api/projects/{_project}/design");
        Assert.Null(design["objects"]);
        Assert.Empty(await _server.Get<List<BlueprintSummaryDto>>("/api/blueprints"));
    }

    [Fact]
    public async Task APartialAcceptThenTheRestThenExport()
    {
        var created = await Create();
        Assert.True(created.Validation.Ok, string.Join("\n", created.Validation.Errors));
        var proposal = created.Proposal;
        Assert.Equal(ProposalStatus.Open, proposal.Status);
        Assert.Equal(7, proposal.Counts.Open);

        var refused = await _server.Post<ValidationDto>($"{Url}/{proposal.Id}/validate", new SelectionRequest(["object:Bilge.DirtyWaterTank"], null));
        Assert.False(refused.Ok);
        var response = await _server.Client.PostAsJsonAsync($"{Url}/{proposal.Id}/accept", new SelectionRequest(["blueprint:DirtyWaterTank"], null), ApiServer.Json, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var pump = await _server.Post<ProposalResultDto>($"{Url}/{proposal.Id}/accept", new SelectionRequest(["blueprint:Pump"], "Rens"));
        Assert.Equal(ProposalStatus.PartlyAccepted, pump.Proposal.Status);
        Assert.Equal(["Pump"], (await _server.Get<List<BlueprintSummaryDto>>("/api/blueprints")).Select(b => b.Name));

        await _server.Post<ReviewComment>($"{Url}/{proposal.Id}/comments", new CommentRequest("Is the overfull float really normally closed?", "Rens", "object:Bilge.DirtyWaterTank:values"));
        await _server.Post<ReviewComment>($"{Url}/{proposal.Id}/comments", new CommentRequest("Looks good otherwise.", "Rens", null));

        var rest = pump.Proposal.Items.Where(i => i.State == ItemStates.Open).Select(i => i.Id).ToList();
        Assert.Equal(6, rest.Count);
        Assert.True((await _server.Post<ValidationDto>($"{Url}/{proposal.Id}/validate", new SelectionRequest(rest, null))).Ok);
        var all = await _server.Post<ProposalResultDto>($"{Url}/{proposal.Id}/accept", new SelectionRequest(rest, "Rens"));
        Assert.Equal(ProposalStatus.Accepted, all.Proposal.Status);
        Assert.Equal(2, all.Proposal.Comments.Count);

        var tree = await _server.Get<List<TreeNodeDto>>($"/api/projects/{_project}/tree");
        Assert.Equal("Bilge.DirtyWaterTank.TransferPump", tree[0].Children[0].Children[0].Path);

        var export = await _server.Client.PostAsync($"/api/projects/{_project}/export/scada", null, Ct);
        export.EnsureSuccessStatusCode();
        var file = ExchangeJson.Read(await export.Content.ReadAsStringAsync(Ct));
        Assert.Equal(["DirtyWaterTank", "Pump"], file.Blueprints.Select(b => b.Name).Order());
        Assert.Equal(["DirtyWaterTank", "TransferPump"], file.Instances.Select(i => i.Name));

        var again = await _server.Post<ProposalResultDto>(Url, new CreateProposalRequest("Same again", null, "AI", DesignTests.DirtyWaterJson()));
        Assert.Empty(again.Proposal.Items);
    }

    [Fact]
    public async Task UndoRestoresTheProjectAndTheBlueprints()
    {
        var proposal = (await Create()).Proposal;
        var all = proposal.Items.Select(i => i.Id).ToList();
        await _server.Post<ProposalResultDto>($"{Url}/{proposal.Id}/accept", new SelectionRequest(all, null));
        Assert.NotEmpty(await _server.Get<List<TreeNodeDto>>($"/api/projects/{_project}/tree"));
        var list = await _server.Get<ProposalListDto>(Url);
        Assert.Equal(7, list.Undo!.Items);

        var undone = await _server.Post<ProposalListDto>($"{Url}/undo", new { });
        Assert.Null(undone.Undo);
        Assert.Empty(await _server.Get<List<TreeNodeDto>>($"/api/projects/{_project}/tree"));
        Assert.Empty(await _server.Get<List<BlueprintSummaryDto>>("/api/blueprints"));
        var reopened = await _server.Get<ProposalDto>($"{Url}/{proposal.Id}");
        Assert.Equal(ProposalStatus.Open, reopened.Status);
        Assert.Equal(7, reopened.Counts.Open);
    }

    [Fact]
    public async Task ANewVersionMarksChangedItemsAndAConflictIsShown()
    {
        var proposal = (await Create()).Proposal;
        await _server.Post<ProposalDto>($"{Url}/{proposal.Id}/reject", new SelectionRequest(["object:Bilge.DirtyWaterTank:values"], null));

        var revised = DesignTests.DirtyWaterJson();
        var tank = revised["objects"]![0]!["children"]![0]!.AsObject();
        tank["values"] = new JsonObject { ["SET.invert_overfull_level"] = "TRUE", ["PAR.maximum_empty_time"] = "900" };
        tank["description"] = "Dirty water collecting tank";
        var version2 = (await _server.Client.PutAsJsonAsync($"{Url}/{proposal.Id}", new UpdateProposalRequest(revised, "Longer empty time", null, null, null), ApiServer.Json, Ct));
        version2.EnsureSuccessStatusCode();
        var result = (await version2.Content.ReadFromJsonAsync<ProposalResultDto>(ApiServer.Json, Ct))!.Proposal;
        Assert.Equal(2, result.Version);
        Assert.Equal(["object:Bilge.DirtyWaterTank", "object:Bilge.DirtyWaterTank:values"], result.Items.Where(i => i.ChangedSincePreviousVersion).Select(i => i.Id).Order());
        Assert.Equal(ItemStates.Open, result.Items.Single(i => i.Id == "object:Bilge.DirtyWaterTank:values").State);

        // The engineer makes the folder by hand: the proposal's "create folder" no longer applies.
        await _server.Post<TreeNodeDto>($"/api/projects/{_project}/folders", new CreateFolderRequest("Bilge", null));
        var view = await _server.Get<ProposalDto>($"{Url}/{proposal.Id}");
        Assert.True(view.ProjectChanged);
        Assert.NotNull(view.Items.Single(i => i.Id == "object:Bilge").Conflict);
        Assert.Equal(1, view.Counts.Conflicts);

        var closed = await _server.Post<ProposalDto>($"{Url}/{proposal.Id}/reject-proposal", new SelectionRequest(null, null));
        Assert.Equal(ProposalStatus.Rejected, closed.Status);
    }
}
