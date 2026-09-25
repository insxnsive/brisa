export const helperSource = String.raw`
// Brisa storage migration. This helper runs in one process so Windows handles
// remain open from validation through durable copy and source disposition.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

public static class BrisaStorageTransaction
{
    static readonly string[] Names = { "state.json", "proton-session.json", "native-wiresock.conf", "imported.conf", "wireguard.conf" };
    const uint Read = 0x80000000, Write = 0x40000000, Delete = 0x00010000, ShareRead = 1, ShareWrite = 2;
    const uint OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    const uint ReparsePoint = 0x00000400, Directory = 0x00000010;

    [StructLayout(LayoutKind.Sequential)] struct FileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] struct FileInfo {
        public uint Attributes; public FileTime Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref Disposition data, uint size);

    static SafeFileHandle Open(string path, bool directory, bool delete, bool writable = false) {
        var handle = CreateFile(path, Read | (writable ? Write : 0) | (delete ? Delete : 0), ShareRead | (directory ? ShareWrite : 0),
            IntPtr.Zero, OpenExisting, OpenReparsePoint | (directory ? BackupSemantics : 0), IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Unable to lock storage object: " + Marshal.GetLastWin32Error()); }
        FileInfo info;
        if (!GetFileInformationByHandle(handle, out info) || (info.Attributes & ReparsePoint) != 0 ||
            ((info.Attributes & Directory) != 0) != directory) {
            handle.Dispose(); throw new IOException("Storage object is not a regular file or directory.");
        }
        return handle;
    }

    static readonly SecurityIdentifier SystemSid = new SecurityIdentifier("S-1-5-18");
    static readonly SecurityIdentifier AdminSid = new SecurityIdentifier("S-1-5-32-544");
    static SecurityIdentifier UserSid { get { return WindowsIdentity.GetCurrent().User; } }
    static bool Allowed(IdentityReference identity) {
        var sid = (SecurityIdentifier)identity.Translate(typeof(SecurityIdentifier));
        return sid.Equals(UserSid) || sid.Equals(SystemSid) || sid.Equals(AdminSid);
    }
    static void CheckAcl(string path, bool directory, bool privateAcl) {
        FileSystemSecurity acl = directory ? (FileSystemSecurity)System.IO.Directory.GetAccessControl(path) : System.IO.File.GetAccessControl(path);
        if (!Allowed(acl.GetOwner(typeof(SecurityIdentifier)))) throw new IOException("Foreign storage owner.");
        var raw = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0);
        if (raw.DiscretionaryAcl == null) throw new IOException("Unprotected storage object.");
        if (privateAcl) {
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !Allowed(rule.IdentityReference))
                    throw new IOException("Broad private-data grant.");
        }
    }
    static DirectorySecurity PrivateDirectoryAcl() {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { UserSid, SystemSid, AdminSid })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }
    static FileSecurity PrivateFileAcl() {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { UserSid, SystemSid, AdminSid })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return acl;
    }
    static List<SafeFileHandle> LockAncestors(string path) {
        var parts = new Stack<string>();
        for (var item = Path.GetFullPath(path); item != null; item = Path.GetDirectoryName(item)) {
            parts.Push(item);
            if (Path.GetDirectoryName(item) == null) break;
        }
        var held = new List<SafeFileHandle>();
        try {
            while (parts.Count != 0) {
                var item = parts.Pop();
                held.Add(Open(item, true, false));
                if (String.Equals(item, path, StringComparison.OrdinalIgnoreCase)) CheckAcl(item, true, false);
            }
            return held;
        } catch { DisposeAll(held); throw; }
    }
    static void DisposeAll(IEnumerable<IDisposable> held) { foreach (var item in held) item.Dispose(); }
    static bool Same(FileStream a, FileStream b) {
        a.Position = b.Position = 0;
        if (a.Length != b.Length) return false;
        var x = new byte[65536]; var y = new byte[65536];
        while (true) {
            int count = a.Read(x, 0, x.Length);
            if (count == 0) return true;
            int offset = 0;
            while (offset < count) { int n = b.Read(y, offset, count - offset); if (n == 0) return false; offset += n; }
            for (int i = 0; i < count; i++) if (x[i] != y[i]) return false;
        }
    }
    sealed class Entry : IDisposable {
        public string Name, SourcePath, DestinationPath;
        public FileStream Source, Destination;
        public bool Created;
        public void Dispose() { if (Destination != null) Destination.Dispose(); if (Source != null) Source.Dispose(); }
    }
    static FileStream OpenFile(string path, bool delete, bool writable = false) {
        return new FileStream(Open(path, false, delete, writable), writable ? FileAccess.ReadWrite : FileAccess.Read, 4096, false);
    }
    static void ValidatePrivateTree(string root, List<SafeFileHandle> locks) {
        var stack = new Stack<string>(); stack.Push(root);
        int count = 0;
        while (stack.Count > 0) {
            if (++count > 10000) throw new IOException("Private tree too large.");
            string item = stack.Pop();
            bool directory = (System.IO.File.GetAttributes(item) & FileAttributes.Directory) != 0;
            locks.Add(Open(item, directory, false));
            CheckAcl(item, directory, true);
            if (directory) foreach (var child in System.IO.Directory.GetFileSystemEntries(item)) stack.Push(child);
        }
    }
    static void RemoveHeld(SafeFileHandle handle) {
        var disposition = new Disposition { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf(typeof(Disposition))))
            throw new IOException("Failed to remove original storage link: " + Marshal.GetLastWin32Error());
    }

    public static void HoldLocksForTest(string basePath, string marker) {
        var dirs = LockAncestors(basePath);
        try {
            dirs.Add(Open(Path.Combine(basePath, "native-data"), true, false));
            using (var source = OpenFile(Path.Combine(basePath, "state.json"), true)) {
                System.IO.File.WriteAllText(marker, "locked");
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (System.IO.File.Exists(marker) && DateTime.UtcNow < deadline)
                    System.Threading.Thread.Sleep(20);
            }
        } finally { DisposeAll(dirs); }
    }

    public static string Run(string basePath, bool migrationAllowed, int failAfter, int failAfterDelete) {
        string baseDir = Path.GetFullPath(basePath);
        string dataDir = Path.Combine(baseDir, "native-data");
        var dirs = LockAncestors(baseDir);
        var entries = new List<Entry>();
        var privateLocks = new List<SafeFileHandle>();
        bool deletingSources = false;
        try {
            foreach (string name in Names) {
                string source = Path.Combine(baseDir, name);
                bool present = false;
                foreach (var candidate in System.IO.Directory.GetFileSystemEntries(baseDir, name))
                    if (String.Equals(Path.GetFileName(candidate), name, StringComparison.Ordinal)) present = true;
                if (!present) continue;
                var entry = new Entry { Name = name, SourcePath = source, DestinationPath = Path.Combine(dataDir, name) };
                entry.Source = OpenFile(source, true);
                CheckAcl(source, false, false);
                entries.Add(entry);
            }
            if (entries.Count != 0 && !migrationAllowed) throw new IOException("WireSock migration inspection is unavailable or stale.");
            if (!System.IO.Directory.Exists(dataDir) && !System.IO.File.Exists(dataDir))
                System.IO.Directory.CreateDirectory(dataDir, PrivateDirectoryAcl());
            dirs.Add(Open(dataDir, true, false));
            CheckAcl(dataDir, true, true);
            ValidatePrivateTree(dataDir, privateLocks);
            DisposeAll(privateLocks);
            privateLocks.Clear();
            foreach (var entry in entries) {
                if (!System.IO.File.Exists(entry.DestinationPath) && !System.IO.Directory.Exists(entry.DestinationPath)) continue;
                entry.Destination = OpenFile(entry.DestinationPath, false, true);
                CheckAcl(entry.DestinationPath, false, true);
                if (!Same(entry.Source, entry.Destination)) throw new IOException("Brisa private data migration conflict.");
            }
            int published = 0;
            foreach (var entry in entries) {
                if (entry.Destination != null) continue;
                entry.Destination = new FileStream(entry.DestinationPath, FileMode.CreateNew,
                    FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete,
                    FileShare.Read, 4096, FileOptions.WriteThrough, PrivateFileAcl());
                entry.Created = true;
                CheckAcl(entry.DestinationPath, false, true);
                entry.Source.Position = 0;
                entry.Source.CopyTo(entry.Destination);
                entry.Destination.Flush(true);
                if (!Same(entry.Source, entry.Destination)) throw new IOException("Destination verification failed.");
                if (++published == failAfter) throw new IOException("Fixture publication fault.");
            }
            // All links and bytes are verified before the first original is removed.
            foreach (var entry in entries) {
                if (!Same(entry.Source, entry.Destination)) throw new IOException("Destination changed.");
                entry.Destination.Flush(true);
            }
            deletingSources = true;
            int removed = 0;
            foreach (var entry in entries) {
                RemoveHeld(entry.Source.SafeFileHandle);
                if (++removed == failAfterDelete) throw new IOException("Fixture source disposition fault.");
            }
            return dataDir;
        } catch {
            if (!deletingSources) foreach (var entry in entries) if (entry.Created && entry.Destination != null) {
                try { RemoveHeld(entry.Destination.SafeFileHandle); } catch { /* Original remains authoritative. */ }
            }
            throw;
        } finally {
            DisposeAll(entries);
            DisposeAll(privateLocks);
            DisposeAll(dirs);
        }
    }
}

`;
