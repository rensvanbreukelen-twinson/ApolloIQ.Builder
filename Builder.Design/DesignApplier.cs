using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Logic.Blueprints;
using Builder.Logic.Runtime;
using Builder.Persistence;

namespace Builder.Design;

/// <summary>The outcome of applying a selection of change items to a copy of the project and the blueprints.</summary>
public sealed class AppliedDesign
{
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>The changed copy of the project (null when the selection was refused before applying).</summary>
    public Project? Project { get; init; }

    /// <summary>The blueprints after the change, by id (only those that were created or changed).</summary>
    public List<Blueprint> ChangedBlueprints { get; } = [];

    public List<Guid> DeletedBlueprints { get; } = [];

    public bool Ok => Errors.Count == 0;
}

public static class DesignApplier
{
    /// <summary>
    /// Applies the selected items (their dependencies must be selected too) to a copy of <paramref name="project"/> and of the
    /// blueprints, then validates the result: the blueprints that changed, the instances' blueprints, and the project's logic
    /// (every expression and interlock compiles). The original project and blueprints are not touched.
    /// </summary>
    public static AppliedDesign Apply(DesignPlan plan, IEnumerable<string> selection, Project project, IReadOnlyCollection<Blueprint> blueprints)
    {
        var selected = selection.Distinct().ToList();
        var refused = new AppliedDesign();
        foreach (var id in selected.Where(id => plan.Find(id) is null))
            refused.Errors.Add($"Item {id} is not part of this proposal (any more).");
        foreach (var item in selected.Select(plan.Find).OfType<ChangeItem>())
            foreach (var dependency in item.DependsOn.Where(d => !selected.Contains(d) && plan.Find(d) is not null))
                refused.Errors.Add($"\"{item.Summary}\" needs \"{plan.Find(dependency)!.Summary}\"; select it too.");
        if (!refused.Ok)
            return refused;

        var copy = ProjectStore.Copy(project);
        var context = new ApplyContext(copy, blueprints, selected);
        var result = new AppliedDesign { Project = copy };
        var items = plan.Items.Where(i => selected.Contains(i.Id)).OrderBy(i => i.Phase).ThenBy(i => i.Order).ToList();
        var finished = false;
        foreach (var item in items)
        {
            if (!finished && item.Phase > DesignPlanner.InterlockPhase)
            {
                Finish(context, result);
                finished = true;
            }
            if (item.Problems.Count > 0)
            {
                result.Errors.AddRange(item.Problems);
                continue;
            }
            try
            {
                item.Apply!(context);
            }
            catch (Exception ex) when (ex is ProjectException or DesignException or ArgumentException or InvalidOperationException)
            {
                result.Errors.Add($"{item.Summary}: {ex.Message}");
            }
        }
        if (!finished)
            Finish(context, result);

        var lookup = (Func<Guid, Blueprint?>)(id => context.Blueprints.GetValueOrDefault(id));
        foreach (var id in context.ChangedBlueprints)
        {
            var blueprint = context.Blueprints[id];
            BlueprintRules.AssignIds(blueprint);
            BlueprintRules.AssignCodes(blueprint);
            foreach (var issue in BlueprintValidator.Validate(blueprint.Clone(), lookup))
                (issue.Severity == "Error" ? result.Errors : result.Warnings).Add($"Blueprint {blueprint.Name} › {issue.Where}: {issue.Message}");
            result.ChangedBlueprints.Add(blueprint);
        }
        result.DeletedBlueprints.AddRange(context.DeletedBlueprints);
        foreach (var instance in copy.Objects.Where(o => o is ControlModule or UnitInstance))
        {
            var blueprintId = InstanceFactory.BlueprintIdOf(instance);
            if (!context.Blueprints.ContainsKey(blueprintId))
                result.Errors.Add($"{copy.GetPath(instance.Id)}: its blueprint {blueprintId} is deleted.");
        }
        if (result.Ok)
            foreach (var error in LogicProgram.Build(copy, context.Library).Errors)
            {
                // A role that is not filled yet is a state the Builder allows (an EM accepted before its members): a warning.
                if (error.Message.Contains("is not filled", StringComparison.Ordinal) || error.Message.Contains("'unfilled_", StringComparison.Ordinal))
                    result.Warnings.Add($"{error.Location.Split(' ')[0]}: a role is not filled yet; its logic does not run until the member is there.");
                else
                    result.Errors.Add($"{error.Location}: {error.Message}");
            }
        var distinct = result.Errors.Distinct().ToList();
        result.Errors.Clear();
        result.Errors.AddRange(distinct);
        var warnings = result.Warnings.Distinct().ToList();
        result.Warnings.Clear();
        result.Warnings.AddRange(warnings);
        return result;
    }

    private static void Finish(ApplyContext context, AppliedDesign result)
    {
        foreach (var action in context.TakeFinalizers())
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is ProjectException or DesignException or ArgumentException)
            {
                result.Errors.Add(ex.Message);
            }
        }
    }
}
