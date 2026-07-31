using WinMatsch.Validation;
using Xunit;

namespace WinMatsch.Workflows.Tests;

public sealed class OperationPlanTests
{
    [Fact]
    public void Valid_nonempty_plan_can_apply()
    {
        var plan = new OperationPlan(
            "update",
            [new PlannedChange(PlannedChangeKind.Update, "manifest.yaml", "Update version")]);

        Assert.True(plan.CanApply);
    }

    [Fact]
    public void Empty_or_invalid_plan_cannot_apply()
    {
        var empty = new OperationPlan("update");
        var invalid = new OperationPlan(
            "update",
            [new PlannedChange(PlannedChangeKind.Update, "manifest.yaml", "Update version")],
            new ValidationReport(
            [
                new ValidationFinding("TEST001", ValidationSeverity.Error, "Invalid"),
            ]));

        Assert.False(empty.CanApply);
        Assert.False(invalid.CanApply);
    }
}
