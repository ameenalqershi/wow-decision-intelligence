using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WowDi.Research.Sparse;

/// <summary>Canonical little-endian binary encoding. The value pool is written in ascending numeric order (indices remapped),
/// so two builds of the same rows/columns in the same order produce identical bytes and an identical SHA-256 regardless
/// of builder call order. Decoding is strict: every index, order, size and canonical-rational rule is re-validated.
/// Layout: magic "WDSM" | u16 version | name | flags | counts | pool | columns | rows | CSR | objective.</summary>
public static class SparseCodec
{
    private static readonly byte[] Magic="WDSM"u8.ToArray();
    private const ushort Version=1;

    public static (byte[] Bytes,string Sha256) Encode(SparseModel m)
    {
        m.Validate();
        var order=Enumerable.Range(0,m.Pool.Length).OrderBy(i=>m.Pool[i]).ToArray();
        var remap=new int[m.Pool.Length];for(int k=0;k<order.Length;k++)remap[order[k]]=k;
        int R(int i)=>i<0?-1:remap[i];
        using var ms=new MemoryStream(checked((int)Math.Min(int.MaxValue,64+m.Nonzeros*8+(long)m.Columns*13+(long)m.Rows*8+m.Pool.Length*16)));
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var w=new Writer(ms,hash);
        w.Raw(Magic);w.U16(Version);w.Str(m.Name);w.U8((byte)(m.Maximize?1:0));
        w.I32(m.Pool.Length);w.I32(m.Columns);w.I32(m.Rows);w.I64(m.Nonzeros);
        foreach(var i in order){w.Big(m.Pool[i].N);w.Big(m.Pool[i].D);}
        w.I32(R(m.ObjectiveOffset));
        for(int j=0;j<m.Columns;j++){w.I32(R(m.ColLower[j]));w.I32(R(m.ColUpper[j]));w.I32(R(m.Objective[j]));w.U8((byte)(m.Integer[j]?1:0));}
        for(int i=0;i<m.Rows;i++){w.I32(R(m.RowLower[i]));w.I32(R(m.RowUpper[i]));}
        w.Ints(m.RowStart);w.Ints(m.ColIndex);
        var vi=new int[m.ValueIndex.Length];for(int k=0;k<vi.Length;k++)vi[k]=remap[m.ValueIndex[k]];w.Ints(vi);
        w.Flush();
        return (ms.ToArray(),Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public static SparseModel Decode(ReadOnlySpan<byte> bytes,SparseLimits? limits=null)
    {
        var l=limits??SparseLimits.Default;
        var r=new Reader(bytes);
        if(!r.Raw(4).SequenceEqual(Magic)||r.U16()!=Version)throw new SparseException("codec.header");
        var name=r.Str();var flags=r.U8();if(flags>1)throw new SparseException("codec.flags");
        int pool=r.I32(),n=r.I32(),m=r.I32();long nnz=r.I64();
        if(pool<0||n<0||m<0||nnz<0||pool>l.MaxDistinctValues||n>l.MaxColumns||m>l.MaxRows||nnz>l.MaxNonzeros||nnz>int.MaxValue)
            throw new SparseException("codec.limit");
        var values=new Q[pool];
        for(int k=0;k<pool;k++)
        {
            var num=r.Big();var den=r.Big();
            if(den.Sign<=0||!BigInteger.GreatestCommonDivisor(num,den).IsOne&&!(num.IsZero&&den.IsOne))throw new SparseException("codec.rational");
            values[k]=Q.Of(num,den);
            if(k>0&&values[k-1]>=values[k])throw new SparseException("codec.poolOrder");
        }
        int off=r.I32();
        int[] cl=new int[n],cu=new int[n],obj=new int[n];var integer=new bool[n];
        for(int j=0;j<n;j++){cl[j]=r.I32();cu[j]=r.I32();obj[j]=r.I32();var b=r.U8();if(b>1)throw new SparseException("codec.flags");integer[j]=b==1;}
        int[] rl=new int[m],ru=new int[m];
        for(int i=0;i<m;i++){rl[i]=r.I32();ru[i]=r.I32();}
        var rs=r.Ints(m+1);var ci=r.Ints((int)nnz);var vi=r.Ints((int)nnz);
        if(!r.End)throw new SparseException("codec.trailing");
        var model=new SparseModel{Name=name,Maximize=flags==1,Pool=values,ColLower=cl,ColUpper=cu,Integer=integer,Objective=obj,ObjectiveOffset=off,
            RowLower=rl,RowUpper=ru,RowStart=rs,ColIndex=ci,ValueIndex=vi};
        for(int i=0;i<m;i++)if(rs[i+1]<rs[i])throw new SparseException("codec.rowStart");
        model.Validate(l);
        return model;
    }

    private sealed class Writer(Stream s,IncrementalHash h)
    {
        private readonly byte[] _buf=new byte[1<<16];private int _len;
        private void Ensure(int n){if(_len+n>_buf.Length)Flush();}
        public void Flush(){if(_len==0)return;s.Write(_buf,0,_len);h.AppendData(_buf,0,_len);_len=0;}
        public void Raw(ReadOnlySpan<byte> b){Ensure(b.Length);b.CopyTo(_buf.AsSpan(_len));_len+=b.Length;}
        public void U8(byte v){Ensure(1);_buf[_len++]=v;}
        public void U16(ushort v){Ensure(2);BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_len),v);_len+=2;}
        public void I32(int v){Ensure(4);BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(_len),v);_len+=4;}
        public void I64(long v){Ensure(8);BinaryPrimitives.WriteInt64LittleEndian(_buf.AsSpan(_len),v);_len+=8;}
        public void Str(string v){var b=Encoding.UTF8.GetBytes(v);if(b.Length>4096)throw new SparseException("codec.name");I32(b.Length);Raw(b);}
        public void Big(BigInteger v){var b=v.ToByteArray();I32(b.Length);Raw(b);}
        public void Ints(int[] a){foreach(var v in a)I32(v);}
    }
    private ref struct Reader(ReadOnlySpan<byte> b)
    {
        private readonly ReadOnlySpan<byte> _b=b;private int _p;
        public bool End=>_p==_b.Length;
        private ReadOnlySpan<byte> Take(int n){if(n<0||n>_b.Length-_p)throw new SparseException("codec.truncated");var s=_b.Slice(_p,n);_p+=n;return s;}
        public ReadOnlySpan<byte> Raw(int n)=>Take(n);
        public byte U8()=>Take(1)[0];
        public ushort U16()=>BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public int I32()=>BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public long I64()=>BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public string Str(){var n=I32();if(n is <0 or >4096)throw new SparseException("codec.name");return Encoding.UTF8.GetString(Take(n));}
        public BigInteger Big(){var n=I32();if(n is <1 or >Q.MaxBits/8+2)throw new SparseException("codec.rational");return new BigInteger(Take(n));}
        public int[] Ints(int count){var raw=Take(checked(count*4));var a=new int[count];for(int k=0;k<count;k++)a[k]=BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(k*4,4));return a;}
    }
}
