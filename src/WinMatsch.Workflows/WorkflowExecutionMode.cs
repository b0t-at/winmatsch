namespace WinMatsch.Workflows;

/// <summary>Whether a workflow only plans changes or applies them after validation.</summary>
public enum WorkflowExecutionMode
{
    Plan,
    Apply,
}
