using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace WinMatsch.GitHub.Auth;

/// <summary>
/// Stores the token as a generic password in the macOS login keychain via the Security
/// framework. Native password memory is zeroed before release, and errors carry OSStatus
/// codes only — never the secret.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacOSKeychainTokenStore : ITokenStore
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;

    private readonly byte[] _serviceName;
    private readonly byte[] _accountName;

    /// <param name="serviceName">The keychain service name. Overridable so tests can use an isolated entry.</param>
    /// <param name="accountName">The keychain account name.</param>
    public MacOSKeychainTokenStore(
        string serviceName = TokenStores.ServiceName,
        string accountName = TokenStores.AccountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        _serviceName = Encoding.UTF8.GetBytes(serviceName);
        _accountName = Encoding.UTF8.GetBytes(accountName);
    }

    public string ProviderName => "macOS Keychain";

    public bool IsAvailable => OperatingSystem.IsMacOS();

    public Task<GitHubToken?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    public Task SetTokenAsync(GitHubToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        cancellationToken.ThrowIfCancellationRequested();
        Write(token);
        return Task.CompletedTask;
    }

    public Task<bool> RemoveTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int status = SecKeychainFindItem(
            0,
            (uint)_serviceName.Length,
            _serviceName,
            (uint)_accountName.Length,
            _accountName,
            0,
            0,
            out nint itemRef);
        if (status == ErrSecItemNotFound)
        {
            return Task.FromResult(false);
        }

        ThrowOnError(status, "Finding the keychain item");
        try
        {
            ThrowOnError(SecKeychainItemDelete(itemRef), "Deleting the keychain item");
            return Task.FromResult(true);
        }
        finally
        {
            CFRelease(itemRef);
        }
    }

    private unsafe GitHubToken? Read()
    {
        int status = SecKeychainFindGenericPassword(
            0,
            (uint)_serviceName.Length,
            _serviceName,
            (uint)_accountName.Length,
            _accountName,
            out uint passwordLength,
            out nint passwordData,
            out nint itemRef);
        if (status == ErrSecItemNotFound)
        {
            return null;
        }

        ThrowOnError(status, "Reading the keychain item");
        try
        {
            if (passwordData == 0 || passwordLength == 0)
            {
                return null;
            }

            var password = new Span<byte>((void*)passwordData, (int)passwordLength);
            string value = Encoding.UTF8.GetString(password);
            password.Clear();
            return string.IsNullOrWhiteSpace(value) ? null : new GitHubToken(value);
        }
        finally
        {
            if (passwordData != 0)
            {
                _ = SecKeychainItemFreeContent(0, passwordData);
            }

            if (itemRef != 0)
            {
                CFRelease(itemRef);
            }
        }
    }

    private void Write(GitHubToken token)
    {
        byte[] secretBytes = Encoding.UTF8.GetBytes(token.RevealValue());
        try
        {
            int status = SecKeychainAddGenericPassword(
                0,
                (uint)_serviceName.Length,
                _serviceName,
                (uint)_accountName.Length,
                _accountName,
                (uint)secretBytes.Length,
                secretBytes,
                0);
            if (status == ErrSecDuplicateItem)
            {
                Update(secretBytes);
                return;
            }

            ThrowOnError(status, "Adding the keychain item");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private void Update(byte[] secretBytes)
    {
        int status = SecKeychainFindItem(
            0,
            (uint)_serviceName.Length,
            _serviceName,
            (uint)_accountName.Length,
            _accountName,
            0,
            0,
            out nint itemRef);
        ThrowOnError(status, "Finding the keychain item");
        try
        {
            ThrowOnError(
                SecKeychainItemModifyAttributesAndData(itemRef, 0, (uint)secretBytes.Length, secretBytes),
                "Updating the keychain item");
        }
        finally
        {
            CFRelease(itemRef);
        }
    }

    private static void ThrowOnError(int status, string operation)
    {
        if (status != ErrSecSuccess)
        {
            throw new TokenStoreException($"{operation} failed with OSStatus {status}.");
        }
    }

    [LibraryImport(SecurityFramework, EntryPoint = "SecKeychainFindGenericPassword")]
    private static partial int SecKeychainFindGenericPassword(
        nint keychainOrArray,
        uint serviceNameLength,
        ReadOnlySpan<byte> serviceName,
        uint accountNameLength,
        ReadOnlySpan<byte> accountName,
        out uint passwordLength,
        out nint passwordData,
        out nint itemRef);

    [LibraryImport(SecurityFramework, EntryPoint = "SecKeychainFindGenericPassword")]
    private static partial int SecKeychainFindItem(
        nint keychainOrArray,
        uint serviceNameLength,
        ReadOnlySpan<byte> serviceName,
        uint accountNameLength,
        ReadOnlySpan<byte> accountName,
        nint passwordLength,
        nint passwordData,
        out nint itemRef);

    [LibraryImport(SecurityFramework, EntryPoint = "SecKeychainAddGenericPassword")]
    private static partial int SecKeychainAddGenericPassword(
        nint keychain,
        uint serviceNameLength,
        ReadOnlySpan<byte> serviceName,
        uint accountNameLength,
        ReadOnlySpan<byte> accountName,
        uint passwordLength,
        ReadOnlySpan<byte> passwordData,
        nint itemRef);

    [LibraryImport(SecurityFramework, EntryPoint = "SecKeychainItemModifyAttributesAndData")]
    private static partial int SecKeychainItemModifyAttributesAndData(
        nint itemRef,
        nint attrList,
        uint length,
        ReadOnlySpan<byte> data);

    [LibraryImport(SecurityFramework, EntryPoint = "SecKeychainItemDelete")]
    private static partial int SecKeychainItemDelete(nint itemRef);

    [LibraryImport(SecurityFramework, EntryPoint = "SecKeychainItemFreeContent")]
    private static partial int SecKeychainItemFreeContent(nint attrList, nint data);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
    private static partial void CFRelease(nint reference);
}
