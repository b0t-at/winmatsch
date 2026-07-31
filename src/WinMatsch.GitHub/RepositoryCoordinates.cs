namespace WinMatsch.GitHub;

/// <summary>The owner and name of a GitHub repository.</summary>
public sealed record RepositoryCoordinates
{
    public RepositoryCoordinates(string owner, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (owner.Contains('/') || name.Contains('/'))
        {
            throw new ArgumentException("Repository owners and names must not contain '/'.");
        }

        Owner = owner;
        Name = name;
    }

    public string Owner { get; }

    public string Name { get; }

    /// <summary>
    /// Parses user-supplied <c>owner/name</c> input. Every invalid syntax — null, empty,
    /// whitespace, a missing or extra separator, or an empty owner or name part — throws
    /// <see cref="FormatException"/>, so configuration binding maps it to a configuration
    /// error instead of leaking an argument exception.
    /// </summary>
    public static RepositoryCoordinates Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("A repository must use the 'owner/name' format.");
        }

        string[] parts = value.Split('/');
        if (parts.Length != 2)
        {
            throw new FormatException(
                $"'{value}' is not a valid repository. Use the 'owner/name' format.");
        }

        try
        {
            return new RepositoryCoordinates(parts[0], parts[1]);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException(
                $"'{value}' is not a valid repository. Use the 'owner/name' format.",
                exception);
        }
    }

    public override string ToString() => $"{Owner}/{Name}";
}
