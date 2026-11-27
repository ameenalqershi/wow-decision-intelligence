using System.Diagnostics;
using System.Collections.Immutable;
using WowDi.Solver.Contracts;

namespace WowDi.Runtime.Governance;

// These values are supplied exclusively by the host's qualified adapter registry.
// Never bind these arguments, executable, directory or environment to an HTTP request or DML expression.
public sealed record WorkerProgram(string Executable,ImmutableArray<string> Arguments,string WorkingDirectory);
public sealed record ScopeCompletion(bool Empty,bool TerminatedResidual,long? CpuMs,long? PeakBytes,string? PeakMetric);
public sealed record BoundaryResult(Termination Termination,int? ExitCode,byte[]? Candidate,Measurement Resources,bool CleanupConfirmed,string? Diagnostic,long? ScopePeakBytes,string? ScopePeakMetric,long ReservedCoreMilliseconds);

// A production scope MUST enforce aggregate memory, CPU and child-process limits before
// the trusted bootstrap is released. CompleteAsync must terminate all descendants and
// confirm the scope is empty. Never substitute an unbounded portable process scope.
public interface IWorkerProcessScope:IAsyncDisposable
{
    ValueTask AttachAsync(Process bootstrap,CancellationToken cancellationToken);
    ValueTask<ScopeCompletion> CompleteAsync(CancellationToken cancellationToken);
}
public interface IWorkerProcessScopeFactory
{
    ValueTask<IWorkerProcessScope> CreateAsync(WorkerBudget budget,CancellationToken cancellationToken);
}

public sealed class WorkerProcessBoundary(IWorkerProcessScopeFactory scopes,WorkerProgram bootstrap,int maximumOutputBytes=1024*1024)
{
    public async Task<BoundaryResult> ExecuteAsync(WorkerLease lease,WorkerProgram program,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateProgram(bootstrap);ValidateProgram(program);
        if(maximumOutputBytes is <1 or >16*1024*1024)throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        var clock=Stopwatch.StartNew();
        IWorkerProcessScope? scope=null;
        Process? process=null;
        Task<byte[]>? stdout=null;
        Task<byte[]>? stderr=null;
        bool started=false,attached=false,clean=false;
        long? cpu=null,peak=null;
        string? peakMetric=null;
        int? exit=null;
        byte[]? candidate=null;
        string? diagnostic=null;
        var termination=Termination.Failed;
        using var deadline=new CancellationTokenSource(lease.Remaining);
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(ct,deadline.Token);
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            scope=await scopes.CreateAsync(lease.Request.Budget,stop.Token);
            var info=new ProcessStartInfo(bootstrap.Executable)
            {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,
                RedirectStandardError=true,WorkingDirectory=program.WorkingDirectory
            };
            // Do not inherit host credentials or environment. Adapter registry must supply absolute paths.
            info.Environment.Clear();
            if(OperatingSystem.IsWindows())
            {
                var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                info.Environment["SystemRoot"]=windows;
                info.Environment["WINDIR"]=windows;
            }
            foreach(var arg in bootstrap.Arguments)info.ArgumentList.Add(arg);
            info.ArgumentList.Add(program.Executable);
            foreach(var arg in program.Arguments)info.ArgumentList.Add(arg);
            process=new Process{StartInfo=info};
            started=process.Start();
            if(!started)throw new InvalidOperationException("Bootstrap did not start.");
            // Bootstrap waits on stdin before starting the adapter; attach first.
            await scope.AttachAsync(process,stop.Token);
            attached=true;
            stdout=ReadBoundedAsync(process.StandardOutput.BaseStream,maximumOutputBytes,stop.Token);
            stderr=ReadBoundedAsync(process.StandardError.BaseStream,64*1024,stop.Token);
            // Faulted output must stop a worker even if it blocks before exit.
            var exited=process.WaitForExitAsync(stop.Token);
            await process.StandardInput.WriteLineAsync("WOWDI_START".AsMemory(),stop.Token);
            process.StandardInput.Close();
            var tasks=new List<Task>{exited,stdout,stderr};
            while(tasks.Count>0)
            {
                var next=await Task.WhenAny(tasks);
                await next;
                tasks.Remove(next);
            }
            exit=process.ExitCode;
            if(exit==0){candidate=await stdout;if(candidate.Length>0)termination=Termination.Completed;else{candidate=null;diagnostic="worker.emptyResult";}}
            else diagnostic="worker.exit";
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested)
        {
            termination=ct.IsCancellationRequested?Termination.Cancelled:Termination.Timeout;
            diagnostic=termination==Termination.Cancelled?"worker.cancelled":"worker.timeout";
        }
        catch(OutputLimitException){diagnostic="worker.outputLimit";}
        catch(Exception ex) when(ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostic="worker.boundary";
        }
        finally
        {
            // Never return a candidate before the OS boundary has confirmed cleanup.
            // Cancellation of the caller cannot cancel cleanup. A failed cleanup quarantines reservation.
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if(started&&!attached&&process is not null)
            {
                // The bootstrap was never released; no adapter could have started.
                try{if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync(cleanup.Token);}
                catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException){}
            }
            if(scope is not null)
            {
                try
                {
                    var summary=await scope.CompleteAsync(cleanup.Token);
                    if(started&&process is not null)await process.WaitForExitAsync(cleanup.Token);
                    clean=summary.Empty&&(!started||process!.HasExited);
                    cpu=summary.CpuMs;peak=summary.PeakBytes;peakMetric=summary.PeakMetric;
                    if(termination==Termination.Completed&&summary.TerminatedResidual)
                    {candidate=null;termination=Termination.Failed;diagnostic="worker.residualProcesses";}
                }
                catch(Exception ex) when(ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or OperationCanceledException)
                {clean=false;}
                try{await scope.DisposeAsync();}
                catch(Exception ex) when(ex is IOException or InvalidOperationException or UnauthorizedAccessException){clean=false;}
            }
            else clean=!started;
            stop.Cancel();
            foreach(var output in new[]{stdout,stderr})
            {
                if(output is null)continue;
                try{await output.WaitAsync(TimeSpan.FromSeconds(1));}
                catch(Exception ex) when(ex is IOException or OperationCanceledException or TimeoutException or OutputLimitException){}
            }
            process?.Dispose();
        }
        if(!clean){candidate=null;termination=Termination.Failed;diagnostic="worker.cleanupUnconfirmed";}
        if(ct.IsCancellationRequested||deadline.IsCancellationRequested)
        {
            candidate=null;
            if(clean){termination=ct.IsCancellationRequested?Termination.Cancelled:Termination.Timeout;}
        }
        var elapsedMs=clock.ElapsedMilliseconds;
        return new(termination,exit,candidate,new(elapsedMs,cpu,null),clean,diagnostic,peak,peakMetric,checked(elapsedMs*lease.Request.Budget.Threads));
    }

    private static void ValidateProgram(WorkerProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if(!Path.IsPathFullyQualified(program.Executable)||!Path.IsPathFullyQualified(program.WorkingDirectory)||
            program.Arguments.IsDefault||program.Arguments.Length>128||program.Arguments.Any(x=>x is null||x.Length>4096||x.Contains('\0')))
            throw new ArgumentException("Invalid trusted worker registration.",nameof(program));
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream,int limit,CancellationToken ct)
    {
        using var buffer=new MemoryStream();
        var chunk=new byte[8192];
        while(true)
        {
            var count=await stream.ReadAsync(chunk,ct);
            if(count==0)return buffer.ToArray();
            if(count>limit-buffer.Length)throw new OutputLimitException();
            buffer.Write(chunk,0,count);
        }
    }
    private sealed class OutputLimitException:Exception;
}

// Application orchestration: only current local attempt can return a candidate to the
// independent verifier. A durable fenced commit is still required in S12/E03.
public sealed class WorkerExecutor(WorkerGovernor governor,WorkerProcessBoundary boundary)
{
    public async Task<(WorkerCompletion Completion,BoundaryResult Result)> RunAsync(WorkerLease lease,WorkerProgram program,CancellationToken ct=default)
    {
        lease=governor.TryBeginExecution(lease)??throw new InvalidOperationException("Lease is stale or already executing.");
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor=MonitorAsync();
        BoundaryResult result;
        try { result=await boundary.ExecuteAsync(lease,program,cancellation.Token); }
        catch
        {
            governor.Finish(lease,Termination.Failed,cleanupConfirmed:false);
            throw;
        }
        finally
        {
            cancellation.Cancel();
            await monitor;
        }
        var completion=governor.Finish(lease,result.Termination,result.CleanupConfirmed);
        var finalTermination=completion.State switch
        {RunState.TimedOut=>Termination.Timeout,RunState.Cancelled=>Termination.Cancelled,RunState.Completed=>Termination.Completed,_=>Termination.Failed};
        return (completion,result with{Termination=finalTermination,Candidate=completion.CandidateEligible?result.Candidate:null});
        async Task MonitorAsync()
        {
            try
            {
                while(!cancellation.IsCancellationRequested)
                {
                    if(!governor.IsCurrent(lease)){cancellation.Cancel();return;}
                    await Task.Delay(25,cancellation.Token);
                }
            }
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested){}
        }
    }
}
