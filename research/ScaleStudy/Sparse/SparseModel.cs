namespace WowDi.Research.Sparse;

/// <summary>Explicit structural limits. They are guard rails, not tuning knobs: exceeding one is a diagnostic, never a
/// silent truncation. Defaults admit 10^7 nonzeros and 10^7 columns.</summary>
public sealed record SparseLimits(int MaxColumns=20_000_000,int MaxRows=20_000_000,long MaxNonzeros=100_000_000,int MaxDistinctValues=10_000_000)
{
    public static readonly SparseLimits Default=new();
}

/// <summary>Exact sparse linear model: rows lo &lt;= a·x &lt;= hi in CSR form, column bounds, integrality and a linear objective.
/// Coefficients are indices into a pool of distinct exact rationals (industrial models reuse few distinct values), so the
/// structure costs 8 bytes per nonzero plus the pool. Index -1 means an infinite bound. Canonical: columns of a row are
/// strictly increasing, no explicit zero coefficient, pool entries in first-use order over (objective, rows, bounds).</summary>
public sealed class SparseModel
{
    public required string Name {get;init;}
    public required bool Maximize {get;init;}
    public required Q[] Pool {get;init;}
    public required int[] ColLower {get;init;}
    public required int[] ColUpper {get;init;}
    public required bool[] Integer {get;init;}
    public required int[] Objective {get;init;}     // pool index per column, -1 = zero
    public required int ObjectiveOffset {get;init;} // pool index, -1 = zero
    public required int[] RowLower {get;init;}
    public required int[] RowUpper {get;init;}
    public required int[] RowStart {get;init;}      // length Rows+1
    public required int[] ColIndex {get;init;}
    public required int[] ValueIndex {get;init;}
    public int Columns=>ColLower.Length;
    public int Rows=>RowLower.Length;
    public long Nonzeros=>ColIndex.LongLength;

    /// <summary>Structural validation (O(nnz)). Throws <see cref="SparseException"/> with a stable code.</summary>
    public void Validate(SparseLimits? limits=null)
    {
        var l=limits??SparseLimits.Default;
        int n=Columns,m=Rows;
        Need(n<=l.MaxColumns&&m<=l.MaxRows&&Nonzeros<=l.MaxNonzeros&&Pool.Length<=l.MaxDistinctValues,"sparse.limit");
        Need(ColUpper.Length==n&&Integer.Length==n&&Objective.Length==n&&RowUpper.Length==m&&RowStart.Length==m+1&&
             ValueIndex.Length==ColIndex.Length&&RowStart[0]==0&&RowStart[m]==ColIndex.Length,"sparse.shape");
        bool P(int i)=>i>=-1&&i<Pool.Length;
        Need(P(ObjectiveOffset),"sparse.pool");
        for(int j=0;j<n;j++)
        {
            Need(P(ColLower[j])&&P(ColUpper[j])&&P(Objective[j]),"sparse.pool");
            if(ColLower[j]>=0&&ColUpper[j]>=0)Need(Pool[ColLower[j]]<=Pool[ColUpper[j]],"sparse.columnBounds");
            if(Objective[j]>=0)Need(Pool[Objective[j]].Sign!=0,"sparse.zero");
        }
        for(int i=0;i<m;i++)
        {
            Need(P(RowLower[i])&&P(RowUpper[i]),"sparse.pool");
            if(RowLower[i]>=0&&RowUpper[i]>=0)Need(Pool[RowLower[i]]<=Pool[RowUpper[i]],"sparse.rowBounds");
            int s=RowStart[i],e=RowStart[i+1];Need(s<=e,"sparse.rowStart");
            for(int k=s;k<e;k++)
            {
                Need(ColIndex[k]>=0&&ColIndex[k]<n&&(k==s||ColIndex[k]>ColIndex[k-1]),"sparse.columnOrder");
                Need(ValueIndex[k]>=0&&ValueIndex[k]<Pool.Length&&Pool[ValueIndex[k]].Sign!=0,"sparse.zero");
            }
        }
    }

    /// <summary>Column-major (CSC) view built in O(nnz) by counting sort; used by column-oriented algorithms.</summary>
    public (int[] ColStart,int[] RowIndex,int[] ValueIndex) Transpose()
    {
        var start=new int[Columns+1];
        foreach(var j in ColIndex)start[j+1]++;
        for(int j=0;j<Columns;j++)start[j+1]+=start[j];
        var next=(int[])start.Clone();var rows=new int[ColIndex.Length];var vals=new int[ColIndex.Length];
        for(int i=0;i<Rows;i++)for(int k=RowStart[i];k<RowStart[i+1];k++){var p=next[ColIndex[k]]++;rows[p]=i;vals[p]=ValueIndex[k];}
        return (start,rows,vals);
    }

    private static void Need(bool ok,string code){if(!ok)throw new SparseException(code);}
}

public sealed class SparseException(string code):Exception(code){public string Code{get;}=code;}

/// <summary>Incremental builder: sorts and merges duplicate column terms exactly, drops zeros, deduplicates values.</summary>
public sealed class SparseBuilder(string name,bool maximize,SparseLimits? limits=null)
{
    private readonly SparseLimits _limits=limits??SparseLimits.Default;
    private readonly Dictionary<Q,int> _index=[];
    private readonly List<Q> _pool=[];
    private readonly List<int> _cl=[],_cu=[],_obj=[],_rl=[],_ru=[],_rs=[0],_ci=[],_vi=[];
    private readonly List<bool> _int=[];
    private int _offset=-1;
    private readonly List<(int Col,Q Value)> _scratch=[];

    public int Columns=>_cl.Count;
    public int Rows=>_rl.Count;
    public long Nonzeros=>_ci.Count;

    private int Id(Q? v)
    {
        if(v is not {} q)return -1;
        if(_index.TryGetValue(q,out var i))return i;
        if(_pool.Count>=_limits.MaxDistinctValues)throw new SparseException("sparse.limit");
        _index.Add(q,_pool.Count);_pool.Add(q);return _pool.Count-1;
    }

    public int AddColumn(Q? lower,Q? upper,bool integer,Q cost)
    {
        if(_cl.Count>=_limits.MaxColumns)throw new SparseException("sparse.limit");
        if(lower is {} lo&&upper is {} hi&&lo>hi)throw new SparseException("sparse.columnBounds");
        _cl.Add(Id(lower));_cu.Add(Id(upper));_int.Add(integer);_obj.Add(cost.Sign==0?-1:Id(cost));
        return _cl.Count-1;
    }

    public void SetObjectiveOffset(Q offset)=>_offset=offset.Sign==0?-1:Id(offset);

    public int AddRow(ReadOnlySpan<int> cols,ReadOnlySpan<Q> coefs,Q? lower,Q? upper)
    {
        if(cols.Length!=coefs.Length)throw new ArgumentException("length");
        if(_rl.Count>=_limits.MaxRows||_ci.Count+cols.Length>_limits.MaxNonzeros)throw new SparseException("sparse.limit");
        if(lower is {} lo&&upper is {} hi&&lo>hi)throw new SparseException("sparse.rowBounds");
        _scratch.Clear();
        for(int k=0;k<cols.Length;k++)
        {
            if((uint)cols[k]>=(uint)_cl.Count)throw new SparseException("sparse.columnOrder");
            _scratch.Add((cols[k],coefs[k]));
        }
        _scratch.Sort((a,b)=>a.Col.CompareTo(b.Col));
        for(int k=0;k<_scratch.Count;)
        {
            var c=_scratch[k].Col;var sum=Q.Zero;
            while(k<_scratch.Count&&_scratch[k].Col==c){sum+=_scratch[k].Value;k++;}
            if(sum.Sign!=0){_ci.Add(c);_vi.Add(Id(sum));}
        }
        _rl.Add(Id(lower));_ru.Add(Id(upper));_rs.Add(_ci.Count);
        return _rl.Count-1;
    }

    /// <summary>Integer fast path (no allocation of Q per term for small integers beyond the pool lookup).</summary>
    public int AddRow(ReadOnlySpan<int> cols,ReadOnlySpan<long> coefs,Q? lower,Q? upper)
    {
        var q=new Q[coefs.Length];for(int k=0;k<q.Length;k++)q[k]=coefs[k];
        return AddRow(cols,q,lower,upper);
    }

    public SparseModel Build()
    {
        var m=new SparseModel
        {
            Name=name,Maximize=maximize,Pool=[.._pool],ColLower=[.._cl],ColUpper=[.._cu],Integer=[.._int],Objective=[.._obj],
            ObjectiveOffset=_offset,RowLower=[.._rl],RowUpper=[.._ru],RowStart=[.._rs],ColIndex=[.._ci],ValueIndex=[.._vi]
        };
        m.Validate(_limits);
        return m;
    }
}
