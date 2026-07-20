using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// A named set of get/set delegates plus deep-equality and deep-clone functions for one
/// <see cref="InstallerFieldsBase"/> property. Rules iterate the hand-written table in
/// <see cref="InstallerFieldAccessors"/> instead of using reflection, which keeps hoisting
/// and push-down generic while staying deterministic and AOT-compatible.
/// </summary>
internal sealed class InstallerFieldAccessor
{
    /// <summary>The property name, e.g. <c>InstallerType</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Reads the property; null when unset.</summary>
    public required Func<InstallerFieldsBase, object?> Get { get; init; }

    /// <summary>Writes the property; null clears it.</summary>
    public required Action<InstallerFieldsBase, object?> Set { get; init; }

    /// <summary>Deep value equality. Both arguments are non-null values previously returned by <see cref="Get"/>.</summary>
    public required Func<object, object, bool> ValueEquals { get; init; }

    /// <summary>Deep clone, so pushed-down values are never aliased between installers. The argument is non-null.</summary>
    public required Func<object, object> Clone { get; init; }
}
