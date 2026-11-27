using System.Runtime.InteropServices;
namespace WowDi.Enterprise.Isolation;

// Linux statx stable ABI, not libc architecture-specific struct stat. Reject special files and
// hard links before opening a registered bundle entry. Store immutability still required.
internal static class LinuxFileIdentity
{
    [StructLayout(LayoutKind.Explicit,Size=256)]
    private struct Statx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint Links;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(40)] internal ulong Size;
    }
    [DllImport("libc",EntryPoint="statx",SetLastError=true)]
    private static extern int Stat(int directory,[MarshalAs(UnmanagedType.LPUTF8Str)] string path,int flags,uint mask,out Statx value);
    internal static long RequireRegularFile(string path)
    {
        if(!OperatingSystem.IsLinux())throw new ExtensionRejectedException("extension.platform");
        const uint fields=0x0001|0x0004|0x0200;
        try
        {
            if(Stat(-100,path,0x100|0x800,fields,out var stat)!=0||(stat.Mask&fields)!=fields||
                (stat.Mode&0xf000)!=0x8000||stat.Links!=1||stat.Size>long.MaxValue)
                throw new ExtensionRejectedException("extension.packagePath");
            return (long)stat.Size;
        }
        catch(EntryPointNotFoundException){throw new ExtensionRejectedException("extension.platform");}
    }
}
