using Xunit;

namespace WinMatsch.Validation.Tests;

public sealed class ValidationReportTests
{
    [Fact]
    public void Report_is_valid_without_errors()
    {
        var report = new ValidationReport(
        [
            new ValidationFinding("TEST001", ValidationSeverity.Info, "Info"),
            new ValidationFinding("TEST002", ValidationSeverity.Warning, "Warning"),
        ]);

        Assert.True(report.IsValid);
        Assert.Equal(2, report.Findings.Count);
    }

    [Fact]
    public void Report_is_invalid_with_an_error()
    {
        var report = new ValidationReport(
        [
            new ValidationFinding("TEST003", ValidationSeverity.Error, "Error", "Installer.yaml"),
        ]);

        Assert.False(report.IsValid);
    }
}
