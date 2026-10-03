// Read-only macOS proof artifact reader. No native control/process/launchd operations.
// macOS libSystem fstat struct stat ABI: st_mode offset4, st_nlink offset6,
// st_uid offset16, st_size offset96. Verified offline against disposable regular files.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;

public static class AinurRouteProofFd
{
    private const int O_RDONLY=0, O_NONBLOCK=0x0004, O_NOFOLLOW=0x0100, O_CLOEXEC=0x01000000;
    private const int S_IFMT=0xF000, S_IFREG=0x8000;
    [StructLayout(LayoutKind.Explicit,Size=144)]
    private struct DarwinStat
    {
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort LinkCount;
        [FieldOffset(16)] public uint Uid;
        [FieldOffset(96)] public long Length;
    }
    [DllImport("libSystem.B.dylib",SetLastError=true,EntryPoint="open")]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path,int flags,int mode);
    [DllImport("libSystem.B.dylib",SetLastError=true,EntryPoint="fstat")]
    private static extern int Fstat(int fd,out DarwinStat stat);
    [DllImport("libSystem.B.dylib",EntryPoint="getuid")]
    private static extern uint GetUid();
    // Test seam only, never set by controller. Called after fstat while the FD remains open.
    public static Action<string> AfterOpenForOfflineTest;
    public static byte[] Read(string path,string expectedSha)
    {
        if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("macOS fstat ABI required");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || expectedSha.Length!=64 || !System.Text.RegularExpressions.Regex.IsMatch(expectedSha,"\\A[0-9A-Fa-f]{64}\\z"))
            throw new InvalidOperationException("proof path/digest invalid");
        int raw=Open(path,O_RDONLY|O_NONBLOCK|O_NOFOLLOW|O_CLOEXEC,0);
        if (raw<0) throw new IOException("proof open failed, errno="+Marshal.GetLastPInvokeError());
        using var handle=new SafeFileHandle((IntPtr)raw,true);
        if (Fstat(raw,out var stat)!=0) throw new IOException("proof fstat failed, errno="+Marshal.GetLastPInvokeError());
        if ((stat.Mode&S_IFMT)!=S_IFREG || (stat.Mode&0x1FF)!=0x180
            || stat.Uid!=GetUid() || stat.LinkCount!=1 || stat.Length<3 || stat.Length>4096)
            throw new IOException("proof fd must be owned 0600, single-link, regular, bounded");
        AfterOpenForOfflineTest?.Invoke(path);
        using var stream=new FileStream(handle,FileAccess.Read);
        var buffer=new byte[(int)stat.Length];
        stream.ReadExactly(buffer);
        if (stream.ReadByte()!=-1) throw new IOException("proof grew during read");
        string digest=Convert.ToHexString(SHA256.HashData(buffer));
        if (!digest.Equals(expectedSha,StringComparison.Ordinal)) throw new IOException("proof artifact SHA mismatch");
        return buffer;
    }
}