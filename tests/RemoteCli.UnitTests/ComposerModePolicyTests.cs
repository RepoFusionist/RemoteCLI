namespace RemoteCli.UnitTests;

public sealed class ComposerModePolicyTests
{
    [Fact]
    public void ModesAreMutuallyExclusiveWhenNormalized()
    {
        Assert.Equal(RemoteCli.Desktop.ComposerModePolicy.Qa,
            RemoteCli.Desktop.ComposerModePolicy.Normalize("qa", true, "keep working"));
        Assert.Equal(RemoteCli.Desktop.ComposerModePolicy.Plan,
            RemoteCli.Desktop.ComposerModePolicy.Normalize("default", true, "keep working"));
        Assert.Equal(RemoteCli.Desktop.ComposerModePolicy.Goal,
            RemoteCli.Desktop.ComposerModePolicy.Normalize("default", false, "finish the task"));
    }

    [Fact]
    public void PlanAndQaOnlyExposeReadOnlyTools()
    {
        var requested = new[] { "read_file", "write_file", "apply_patch", "exec_command", "web_search", "read_file" };

        var plan = RemoteCli.Desktop.ComposerModePolicy.FilterTools(RemoteCli.Desktop.ComposerModePolicy.Plan, requested);
        var qa = RemoteCli.Desktop.ComposerModePolicy.FilterTools(RemoteCli.Desktop.ComposerModePolicy.Qa, requested);

        Assert.Equal(["read_file", "web_search"], plan);
        Assert.Equal(plan, qa);
    }

    [Fact]
    public void PlanContextRequiresAReviewableProposedPlan()
    {
        var context = RemoteCli.Desktop.ComposerModePolicy.BuildContext(RemoteCli.Desktop.ComposerModePolicy.Plan, "ship the feature");

        Assert.Contains("只允许只读探查", context);
        Assert.Contains("<proposed_plan>", context);
        Assert.Contains("不要开始实施", context);
    }

    [Fact]
    public void GoalRequiresAnExplicitCompletionMarker()
    {
        Assert.False(RemoteCli.Desktop.ComposerModePolicy.HasGoalCompletionMarker("已检查，仍有一步未完成。"));
        Assert.True(RemoteCli.Desktop.ComposerModePolicy.HasGoalCompletionMarker("全部通过。" + RemoteCli.Desktop.ComposerModePolicy.GoalCompletionMarker));
        Assert.Equal("全部通过。", RemoteCli.Desktop.ComposerModePolicy.RemoveGoalCompletionMarker(
            "全部通过。" + RemoteCli.Desktop.ComposerModePolicy.GoalCompletionMarker));
    }
}
