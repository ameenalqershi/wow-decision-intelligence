namespace WowDi.Solver.Portfolio;

/// <summary>Thread-safe accounting of core-milliseconds, concurrent native threads and memory.
/// Invariants (checked on every mutation):
///   charged + outstanding reservations &lt;= budget  (admission never over-commits),
///   reserved threads &lt;= cap, reserved memory &lt;= cap.
/// Physical overspend (measured CPU above a member's reservation, e.g. cancellation latency) cannot be prevented by
/// admission; it is charged in full and reported separately, never netted against other members.</summary>
public sealed class CoreLedger
{
    private readonly object _gate=new();
    private long _outstanding,_charged,_overspend,_reservedTotal,_peakMemory,_memory,_quarantinedThreads;
    private int _threads,_peakThreads;
    public long Budget {get;}
    public int ThreadCap {get;}
    public long MemoryCap {get;}

    public CoreLedger(long budgetCoreMs,int threadCap,long memoryCap)
    {
        if(budgetCoreMs<1||threadCap<1||memoryCap<1)throw new ArgumentOutOfRangeException(nameof(budgetCoreMs));
        Budget=budgetCoreMs;ThreadCap=threadCap;MemoryCap=memoryCap;
    }

    /// <summary>Core-milliseconds that may still be reserved.</summary>
    public long Available{get{lock(_gate)return Math.Max(0,Budget-_charged-_outstanding);}}
    public int FreeThreads{get{lock(_gate)return ThreadCap-_threads;}}
    public long FreeMemory{get{lock(_gate)return MemoryCap-_memory;}}

    public sealed class Reservation
    {
        internal Reservation(int threads,long memory,long coreMs){Threads=threads;Memory=memory;CoreMs=coreMs;}
        public int Threads {get;}
        public long Memory {get;}
        public long CoreMs {get;}
        internal bool Open=true;
    }

    public Reservation? TryReserve(int threads,long memory,TimeSpan deadline)
    {
        if(threads<1||memory<1||deadline<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(threads));
        var core=checked(threads*(long)Math.Ceiling(deadline.TotalMilliseconds));
        lock(_gate)
        {
            if(_threads+threads>ThreadCap||_memory+memory>MemoryCap||_charged+_outstanding+core>Budget)return null;
            _threads+=threads;_memory+=memory;_outstanding+=core;_reservedTotal+=core;
            _peakThreads=Math.Max(_peakThreads,_threads);_peakMemory=Math.Max(_peakMemory,_memory);
            Check();
            return new(threads,memory,core);
        }
    }

    /// <summary>Settle a finished member. <paramref name="actualCoreMs"/> is the boundary measurement (or the conservative
    /// fallback). When cleanup is unconfirmed the threads and memory stay reserved for the rest of the run (quarantine).</summary>
    public (long Charged,long Overspend) Settle(Reservation r,long actualCoreMs,bool cleanupConfirmed)
    {
        ArgumentNullException.ThrowIfNull(r);
        if(actualCoreMs<0)throw new ArgumentOutOfRangeException(nameof(actualCoreMs));
        lock(_gate)
        {
            if(!r.Open)throw new InvalidOperationException("Reservation already settled.");
            r.Open=false;
            _outstanding-=r.CoreMs;
            var over=Math.Max(0,actualCoreMs-r.CoreMs);
            _charged+=actualCoreMs;_overspend+=over;
            if(cleanupConfirmed){_threads-=r.Threads;_memory-=r.Memory;}
            else _quarantinedThreads+=r.Threads;
            if(_outstanding<0||_threads<0||_memory<0)throw new InvalidOperationException("Ledger underflow.");
            return (actualCoreMs,over);
        }
    }

    private void Check()
    {
        if(_threads>ThreadCap||_memory>MemoryCap||_outstanding<0)throw new InvalidOperationException("Ledger invariant violated.");
    }

    public LedgerTotals Totals()
    {
        lock(_gate)return new(Budget,_reservedTotal,_charged,_overspend,_peakThreads,_peakMemory,_quarantinedThreads);
    }
}
