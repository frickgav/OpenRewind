using System.Runtime.InteropServices;
using System.Text;

namespace OpenRewind;

/// API keys in the Windows Credential Manager ("OpenRewind AI/<provider>"), one per provider.
/// Never written to settings.json or the database. The Mac app uses the Keychain.
static class AiKeyStore
{
    static string Target(string provider) => $"OpenRewind AI/{provider}";

    public static string? Get(string provider)
    {
        if (!CredRead(Target(provider), CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var c = Marshal.PtrToStructure<Credential>(ptr);
            if (c.CredentialBlob == IntPtr.Zero || c.CredentialBlobSize == 0) return null;
            var bytes = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally { CredFree(ptr); }
    }

    /// null or "" removes the key.
    public static void Set(string provider, string? key)
    {
        if (string.IsNullOrEmpty(key)) { CredDelete(Target(provider), CredTypeGeneric, 0); return; }
        var bytes = Encoding.Unicode.GetBytes(key);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new Credential
            {
                Type = CredTypeGeneric, TargetName = Target(provider), CredentialBlobSize = bytes.Length, CredentialBlob = blob,
                Persist = CredPersistLocalMachine, UserName = provider,
            };
            if (!CredWrite(ref c, 0)) Log.Write($"could not save AI key to Credential Manager (error {Marshal.GetLastWin32Error()})");
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    const int CredTypeGeneric = 1;
    const int CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}
