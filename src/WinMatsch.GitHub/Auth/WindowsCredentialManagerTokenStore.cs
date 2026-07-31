using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace WinMatsch.GitHub.Auth;

/// <summary>
/// Stores the token as a generic credential in the Windows Credential Manager. Secret bytes
/// are zeroed after use and never appear in error messages, which carry Win32 error codes only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialManagerTokenStore : ITokenStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    private readonly string _targetName;

    /// <param name="targetName">
    /// The credential target name. Overridable so tests can use an isolated entry.
    /// </param>
    public WindowsCredentialManagerTokenStore(string targetName = "winmatsch:github")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        _targetName = targetName;
    }

    public string ProviderName => "Windows Credential Manager";

    public bool IsAvailable => OperatingSystem.IsWindows();

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
        if (CredDeleteW(_targetName, CredTypeGeneric, 0))
        {
            return Task.FromResult(true);
        }

        int error = Marshal.GetLastPInvokeError();
        if (error == ErrorNotFound)
        {
            return Task.FromResult(false);
        }

        throw new TokenStoreException($"Deleting the credential failed with Win32 error {error}.");
    }

    private unsafe GitHubToken? Read()
    {
        if (!CredReadW(_targetName, CredTypeGeneric, 0, out nint credentialPtr))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new TokenStoreException($"Reading the credential failed with Win32 error {error}.");
        }

        try
        {
            NativeCredential credential = *(NativeCredential*)credentialPtr;
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var blob = new Span<byte>((void*)credential.CredentialBlob, (int)credential.CredentialBlobSize);
            string value = Encoding.UTF8.GetString(blob);
            return string.IsNullOrWhiteSpace(value) ? null : new GitHubToken(value);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    private unsafe void Write(GitHubToken token)
    {
        byte[] secretBytes = Encoding.UTF8.GetBytes(token.RevealValue());
        nint targetNamePtr = 0;
        nint userNamePtr = 0;
        nint blobPtr = 0;
        try
        {
            targetNamePtr = Marshal.StringToHGlobalUni(_targetName);
            userNamePtr = Marshal.StringToHGlobalUni(TokenStores.AccountName);
            blobPtr = Marshal.AllocHGlobal(secretBytes.Length);
            secretBytes.CopyTo(new Span<byte>((void*)blobPtr, secretBytes.Length));

            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = targetNamePtr,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = userNamePtr,
            };

            if (!CredWriteW(in credential, 0))
            {
                int error = Marshal.GetLastPInvokeError();
                throw new TokenStoreException($"Writing the credential failed with Win32 error {error}.");
            }
        }
        finally
        {
            if (blobPtr != 0)
            {
                new Span<byte>((void*)blobPtr, secretBytes.Length).Clear();
                Marshal.FreeHGlobal(blobPtr);
            }

            CryptographicOperations.ZeroMemory(secretBytes);
            if (targetNamePtr != 0)
            {
                Marshal.FreeHGlobal(targetNamePtr);
            }

            if (userNamePtr != 0)
            {
                Marshal.FreeHGlobal(userNamePtr);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string targetName, uint type, uint flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(in NativeCredential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string targetName, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(nint buffer);
}
