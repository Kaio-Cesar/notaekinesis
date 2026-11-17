using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Notaekinesis.App.Services;

/// <summary>
/// Keeps the session token in Windows Credential Manager (encrypted with the user's
/// login, visible under "Windows Credentials" as notaekinesis:&lt;server&gt;).
/// </summary>
internal static unsafe partial class TokenStore
{
    private const uint CredTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;

    private static string Target(string serverUrl) => "notaekinesis:" + serverUrl;

    public static string? Read(string serverUrl)
    {
        if (!CredReadW(Target(serverUrl), CredTypeGeneric, 0, out var cred)) return null;
        try
        {
            return cred->CredentialBlobSize == 0
                ? null
                : new string((char*)cred->CredentialBlob, 0, (int)cred->CredentialBlobSize / sizeof(char));
        }
        finally
        {
            CredFree(cred);
        }
    }

    public static void Write(string serverUrl, string userName, string token)
    {
        fixed (char* target = Target(serverUrl))
        fixed (char* user = userName)
        fixed (char* blob = token)
        {
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = user,
                CredentialBlob = (byte*)blob,
                CredentialBlobSize = (uint)(token.Length * sizeof(char)),
                Persist = PersistLocalMachine,
            };
            if (!CredWriteW(&cred, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public static void Delete(string serverUrl) => CredDeleteW(Target(serverUrl), CredTypeGeneric, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, uint type, uint flags, out Credential* credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(Credential* credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(void* buffer);
}
