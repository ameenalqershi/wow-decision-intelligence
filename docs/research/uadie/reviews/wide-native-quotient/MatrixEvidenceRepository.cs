using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
internal record MatrixProofObject(MatrixNetworkProof? Network,NetworkDecision? Flow,UnifiedEvidence? Boolean,WeightedDecision? Weighted);
internal record MatrixRevision(string Tenant,int Sequence,string Previous,string ShapeKey,string ProofKey,string Digest,long[] Cost,long Offset,string Sense,string Route,string Status,long? Value,long[]? X,string Assurance);
internal record MatrixRevisionRecord(MatrixRevision Body,string Hash);
internal record MatrixAppend(string Status,JournalHead Head);
internal static class MatrixEvidenceRepository
{
    private static readonly JsonSerializerOptions Json=CertifiedQuotient.Json;
    private const int MaxBytes=8*1024*1024,MaxRecords=512;
    internal static byte[] Bytes<T>(T o)=>JsonSerializer.SerializeToUtf8Bytes(o,Json);
    private static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b));
    private static string Key(string s){if(s.Length!=64||s.Any(c=>!(c is >= '0' and <= '9' or >= 'A' and <= 'F')))throw new InvalidDataException("key");return s;}
    private static void Tenant(string t){if(t.Length is <1 or >64||t.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-'))throw new ArgumentException("tenant");}
    private static string ObjectPath(string dir,string key)=>Path.Combine(dir,"objects",Key(key)+".obj");
    private static byte[] Load(string file){
        using var f=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(f.Length>MaxBytes)throw new InvalidDataException("size");
        byte[] b=new byte[checked((int)f.Length)];f.ReadExactly(b);if(f.ReadByte()!=-1)throw new InvalidDataException("length changed");return b;
    }
    private static T Canonical<T>(byte[] b){
        var o=JsonSerializer.Deserialize<T>(b,Json);
        if(o==null||!Bytes(o).AsSpan().SequenceEqual(b))throw new InvalidDataException("canonical record");return o;
    }
    private static void Sync(string dir){
        if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException("Linux only");
        int fd=Native.open(dir,0x10000);if(fd<0)throw new IOException("open dir");
        try{if(Native.fsync(fd)!=0)throw new IOException("fsync dir");}finally{_=Native.close(fd);}
    }
    private static class Native{
        [DllImport("libc",SetLastError=true)]internal static extern int open(string path,int flags);
        [DllImport("libc",SetLastError=true)]internal static extern int fsync(int fd);
        [DllImport("libc",SetLastError=true)]internal static extern int close(int fd);
    }
    private static void Checkpoint(string stage,string at){if(stage==at)Process.GetCurrentProcess().Kill();}
    private static void Publish(string path,byte[] bytes,string stage,string partial,string synced){
        if(bytes.Length>MaxBytes)throw new InvalidDataException("size");
        string dir=Path.GetDirectoryName(path)!,tmp=Path.Combine(dir,Guid.NewGuid().ToString("N")+".tmp");
        using(var f=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
            int half=bytes.Length/2;f.Write(bytes.AsSpan(0,half));f.Flush(true);Checkpoint(stage,partial);
            f.Write(bytes.AsSpan(half));f.Flush(true);
        }
        File.Move(tmp,path,false);Sync(dir);Checkpoint(stage,synced);
    }
    private static string Store<T>(string dir,T o,string stage){
        byte[] b=Bytes(o);string key=Hash(b),path=ObjectPath(dir,key);
        if(File.Exists(path)){if(!Load(path).AsSpan().SequenceEqual(b))throw new InvalidDataException("object collision");}
        else Publish(path,b,stage,"object-partial","object-synced");
        return key;
    }
    private static T ReadObject<T>(string dir,string key){
        byte[] b=Load(ObjectPath(dir,key));if(Hash(b)!=key)throw new InvalidDataException("object digest");return Canonical<T>(b);
    }
    internal static (JournalHead Head,MatrixEvidence[] Records) Read(string dir,string tenant,JournalHead trusted){
        Tenant(tenant);var head=UnifiedJournal.Empty;bool seen=head==trusted;var result=new List<MatrixEvidence>();
        var paths=Directory.GetFiles(dir,"*.json").Order(StringComparer.Ordinal).ToArray();if(paths.Length>MaxRecords)throw new InvalidDataException("record count");
        foreach(string path in paths){
            var record=Canonical<MatrixRevisionRecord>(Load(path));var b=record.Body;
            if(b.Tenant!=tenant||b.Sequence!=head.Sequence+1||b.Previous!=head.Hash||Path.GetFileName(path)!=$"{b.Sequence:D8}.json"||record.Hash!=Hash(Bytes(b)))throw new InvalidDataException("revision chain");
            var shape=ReadObject<IntegralMatrix>(dir,b.ShapeKey);
            if(shape.Cost.Any(x=>x!=0)||shape.Offset!=0||shape.Sense!="min")throw new InvalidDataException("structural object");
            var m=shape with{Cost=b.Cost,Offset=b.Offset,Sense=b.Sense};
            var p=ReadObject<MatrixProofObject>(dir,b.ProofKey);
            var e=new MatrixEvidence(b.Digest,m,b.Route,b.Status,b.Value,b.X,b.Assurance,p.Network,p.Flow,p.Boolean,p.Weighted);
            MatrixVerifier.Replay(e);result.Add(e);
            head=new(b.Sequence,record.Hash);if(head==trusted)seen=true;
        }
        if(!seen)throw new InvalidDataException("trusted receipt missing");
        return(head,result.ToArray());
    }
    internal static MatrixAppend Append(string dir,string tenant,JournalHead expected,MatrixEvidence e,string stage="none"){
        Tenant(tenant);
        FileStream lease;
        try{lease=new FileStream(Path.Combine(dir,"writer.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){return new("Busy",expected);}
        using(lease){
            var current=Read(dir,tenant,expected);if(current.Head!=expected)return new("Conflict",current.Head);
            if(current.Records.Length>=MaxRecords)throw new InvalidDataException("record count");
            MatrixVerifier.Replay(e);
            Directory.CreateDirectory(Path.Combine(dir,"objects"));Sync(dir);
            Checkpoint(stage,"before-object");
            var shape=e.Source with{Cost=new long[e.Source.N],Offset=0,Sense="min"};
            string sk=Store(dir,shape,stage),pk=Store(dir,new MatrixProofObject(e.Network,e.Flow,e.Boolean,e.Weighted),stage);
            Checkpoint(stage,"before-revision");
            var body=new MatrixRevision(tenant,checked(expected.Sequence+1),expected.Hash,sk,pk,e.Digest,e.Source.Cost,e.Source.Offset,e.Source.Sense,e.Route,e.Status,e.Value,e.X,e.Assurance);
            var record=new MatrixRevisionRecord(body,Hash(Bytes(body)));
            Publish(Path.Combine(dir,$"{body.Sequence:D8}.json"),Bytes(record),stage,"revision-partial","revision-synced");
            return new("Committed",new(body.Sequence,record.Hash));
        }
    }
}
