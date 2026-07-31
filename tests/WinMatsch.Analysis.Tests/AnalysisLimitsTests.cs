using Xunit;

namespace WinMatsch.Analysis.Tests;

public class AnalysisLimitsTests
{
    [Fact]
    public void Allocation_over_the_limit_is_rejected_before_buffer_creation()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => AnalysisLimits.ValidateAllocation(
                AnalysisLimits.MaxEntryBytes + 1,
                "Hostile entry",
                AnalysisLimits.MaxEntryBytes));

        Assert.Contains("allocation limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Expanded_archive_size_over_the_limit_is_rejected()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => AnalysisLimits.ValidateExpandedSize(AnalysisLimits.MaxExpandedArchiveBytes + 1, "Hostile archive"));

        Assert.Contains("expands to", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Excessive_archive_nesting_requires_manual_analysis()
    {
        List<IDisposable> scopes = [];
        try
        {
            for (int i = 0; i < AnalysisLimits.MaxNestedArchives; i++)
            {
                scopes.Add(AnalysisLimits.EnterArchive("Nested archive"));
            }

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => AnalysisLimits.EnterArchive("Nested archive"));

            Assert.Contains("nesting limit", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Manual analysis is required", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            for (int i = scopes.Count - 1; i >= 0; i--)
            {
                scopes[i].Dispose();
            }
        }
    }
}
