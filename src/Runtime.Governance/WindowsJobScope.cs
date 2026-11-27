using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WowDi.Runtime.Governance;

/// <summary>Windows Job Object containment. The trusted bootstrap waits until assigned.
/// Job commit memory is not RSS; CPU rate is a machine-wide hard cap in core-equivalents.</summary>
public sealed class WindowsJobScopeFactory(int maximumProcesses=64):IWorkerProcessScopeFactory
{
    public ValueTask<IWorkerProcessScope> CreateAsync(WorkerBudget budget,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows Job Objects are required.");
        if(maximumProcesses is <2 or >4096||budget.Threads<=0||budget.MemoryBytes<=0)
            throw new ArgumentException("Invalid job object configuration.");
        var cores=Native.GetActiveProcessorCount(0xffff);
        if(cores==0||budget.Threads>cores)throw new ArgumentException("Invalid CPU budget.");
        var rate=checked((uint)((long)budget.Threads*10000/cores));
        if(rate==0)throw new ArgumentException("CPU budget is below supported rate resolution.");
        var job=Native.CreateJobObject(IntPtr.Zero,null);
        if(job.IsInvalid){job.Dispose();throw Native.Error();}
        try
        {
            var limits=new Native.ExtendedLimits
            {
                Basic=new()
                {
                    Flags=0x2000|0x0200|0x0008, // kill on close, job memory, active process limit
                    ActiveProcessLimit=(uint)maximumProcesses
                },
                JobMemoryLimit=checked((nuint)budget.MemoryBytes)
            };
            Native.Set(job,9,limits);
            Native.Set(job,15,new Native.CpuRate{Flags=0x1|0x4,Rate=rate});
            return ValueTask.FromResult<IWorkerProcessScope>(new Scope(job));
        }
        catch{job.Dispose();throw;}
    }

    private sealed class Scope(SafeFileHandle job):IWorkerProcessScope
    {
        private bool _attached;
        public ValueTask AttachAsync(Process bootstrap,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(_attached||bootstrap.HasExited)throw new InvalidOperationException("Invalid job object attachment.");
            if(!Native.AssignProcessToJobObject(job,bootstrap.SafeHandle))throw Native.Error();
            if(!Native.IsProcessInJob(bootstrap.SafeHandle,job,out var assigned)||!assigned)
                throw new InvalidOperationException("Job object membership not confirmed.");
            _attached=true;
            return ValueTask.CompletedTask;
        }
        public async ValueTask<ScopeCompletion> CompleteAsync(CancellationToken ct)
        {
            var accounting=Native.Get<Native.Accounting>(job,1);
            // Job accounting can lag the signaled process handle on Windows. Give natural
            // termination a bounded grace period; any remaining member is still killed.
            var grace=Stopwatch.StartNew();
            while(accounting.ActiveProcesses!=0&&grace.Elapsed<TimeSpan.FromMilliseconds(250))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(25,ct);
                accounting=Native.Get<Native.Accounting>(job,1);
            }
            var residual=accounting.ActiveProcesses!=0;
            if(residual&&!Native.TerminateJobObject(job,125))throw Native.Error();
            while(accounting.ActiveProcesses!=0)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(25,ct);
                accounting=Native.Get<Native.Accounting>(job,1);
            }
            var limits=Native.Get<Native.ExtendedLimits>(job,9);
            var cpu=checked(accounting.TotalUserTime+accounting.TotalKernelTime)/10000;
            return new(true,residual,cpu,checked((long)limits.PeakJobMemoryUsed),"windows-job-commit");
        }
        public ValueTask DisposeAsync(){job.Dispose();return ValueTask.CompletedTask;}
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct BasicLimits
        {
            internal long PerProcessUserTime,PerJobUserTime;
            internal uint Flags;
            internal nuint MinimumWorkingSet,MaximumWorkingSet;
            internal uint ActiveProcessLimit;
            internal nuint Affinity;
            internal uint PriorityClass,SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            internal ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ExtendedLimits
        {
            internal BasicLimits Basic;
            internal IoCounters Io;
            internal nuint ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct CpuRate {internal uint Flags,Rate;}
        [StructLayout(LayoutKind.Sequential)]
        internal struct Accounting
        {
            internal long TotalUserTime,TotalKernelTime,ThisPeriodUserTime,ThisPeriodKernelTime;
            internal uint TotalPageFaultCount,TotalProcesses,ActiveProcesses,TotalTerminatedProcesses;
        }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        internal static extern SafeFileHandle CreateJobObject(IntPtr attributes,string? name);
        [DllImport("kernel32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeFileHandle job,SafeProcessHandle process);
        [DllImport("kernel32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(SafeProcessHandle process,SafeFileHandle job,[MarshalAs(UnmanagedType.Bool)]out bool result);
        [DllImport("kernel32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeFileHandle job,uint exitCode);
        [DllImport("kernel32.dll")]
        internal static extern uint GetActiveProcessorCount(ushort group);
        [DllImport("kernel32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job,int infoClass,IntPtr buffer,uint length);
        [DllImport("kernel32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(SafeFileHandle job,int infoClass,IntPtr buffer,uint length,IntPtr returnedLength);
        internal static Win32Exception Error()=>new(Marshal.GetLastWin32Error(),"Worker Job Object operation failed.");
        internal static void Set<T>(SafeFileHandle job,int kind,T value) where T:struct
        {
            var length=Marshal.SizeOf<T>();
            var pointer=Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(value,pointer,false);
                if(!SetInformationJobObject(job,kind,pointer,(uint)length))throw Error();
            }
            finally{Marshal.FreeHGlobal(pointer);}
        }
        internal static T Get<T>(SafeFileHandle job,int kind) where T:struct
        {
            var length=Marshal.SizeOf<T>();
            var pointer=Marshal.AllocHGlobal(length);
            try
            {
                if(!QueryInformationJobObject(job,kind,pointer,(uint)length,IntPtr.Zero))throw Error();
                return Marshal.PtrToStructure<T>(pointer);
            }
            finally{Marshal.FreeHGlobal(pointer);}
        }
    }
}
