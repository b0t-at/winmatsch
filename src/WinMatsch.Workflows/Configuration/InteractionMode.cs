namespace WinMatsch.Workflows.Configuration;

/// <summary>When the tool may prompt the user interactively.</summary>
public enum InteractionMode
{
    /// <summary>Prompt only when attached to an interactive terminal.</summary>
    Auto,

    /// <summary>Always prompt.</summary>
    Always,

    /// <summary>Never prompt.</summary>
    Never,
}
