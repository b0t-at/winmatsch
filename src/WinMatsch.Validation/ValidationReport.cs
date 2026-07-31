namespace WinMatsch.Validation;

/// <summary>The ordered findings produced by a complete validation run.</summary>
public sealed class ValidationReport
{
    private readonly IReadOnlyList<ValidationFinding> _findings;

    public ValidationReport(IEnumerable<ValidationFinding>? findings = null)
    {
        _findings = findings is null ? [] : [.. findings];
    }

    public IReadOnlyList<ValidationFinding> Findings => _findings;

    public bool IsValid
    {
        get
        {
            foreach (ValidationFinding finding in _findings)
            {
                if (finding.Severity == ValidationSeverity.Error)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
