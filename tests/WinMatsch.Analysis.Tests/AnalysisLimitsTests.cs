using Xunit;

namespace WinMatsch.Analysis.Tests;

public class AnalysisLimitsTests
{
    [Fact]
    public void Allocation_over_the_limit_is_rejected_before_buffer_creation()
    {
        AnalysisResourceLimitException exception = Assert.Throws<AnalysisResourceLimitException>(
            () => AnalysisLimits.ValidateAllocation(
                AnalysisLimits.MaxEntryBytes + 1,
                "Hostile entry",
                AnalysisLimits.MaxEntryBytes));

        Assert.Contains("allocation limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Expanded_archive_size_over_the_limit_is_rejected()
    {
        AnalysisResourceLimitException exception = Assert.Throws<AnalysisResourceLimitException>(
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

            AnalysisResourceLimitException exception = Assert.Throws<AnalysisResourceLimitException>(
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

    [Fact]
    public void Bounded_read_distinguishes_truncation_from_resource_exhaustion()
    {
        using var truncated = new MemoryStream([1, 2]);
        using var oversized = new MemoryStream([1, 2, 3, 4]);

        InvalidDataException corrupt = Assert.Throws<InvalidDataException>(
            () => AnalysisLimits.ReadBounded(truncated, 4, "Truncated payload", 3));
        AnalysisResourceLimitException exhausted = Assert.Throws<AnalysisResourceLimitException>(
            () => AnalysisLimits.ReadBounded(oversized, 4, "Oversized payload", 3));

        Assert.Contains("ends before its declared size", corrupt.Message, StringComparison.Ordinal);
        Assert.Contains("allocation limit", exhausted.Message, StringComparison.Ordinal);
    }
}
