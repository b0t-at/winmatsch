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

    [Fact]
    public void Report_has_stable_text_and_json_formats()
    {
        var report = new ValidationReport(
        [
            new ValidationFinding("VLD0001", ValidationSeverity.Warning, "Review this.", "manifest.yaml"),
        ]);

        Assert.Equal(
            "warning VLD0001 [manifest.yaml]: Review this.\n",
            report.ToText());
        Assert.Equal(
            """{"isValid":true,"findings":[{"code":"VLD0001","severity":"warning","message":"Review this.","path":"manifest.yaml"}]}""",
            report.ToJson());
        Assert.True(report.CanProceed());
        Assert.False(report.CanProceed(WarningPolicy.TreatAsErrors));
    }
}
