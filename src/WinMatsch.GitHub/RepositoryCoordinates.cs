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

    public static RepositoryCoordinates Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string[] parts = value.Split('/');
        if (parts.Length != 2)
        {
            throw new FormatException("A repository must use the 'owner/name' format.");
        }

        return new RepositoryCoordinates(parts[0], parts[1]);
    }

    public override string ToString() => $"{Owner}/{Name}";
}
