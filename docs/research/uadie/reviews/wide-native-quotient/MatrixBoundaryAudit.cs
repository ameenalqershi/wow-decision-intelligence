using System.Text.Json;
internal static class MatrixBoundaryAudit
{
    internal static void Run(){
        int checks=0;
        void Pass(string name,bool ok){if(!ok)throw new Exception(name);checks++;Console.WriteLine(JsonSerializer.Serialize(new{kind="check",name,valid=true}));}
        var m=MatrixAdmission.From(new GeneralNetwork(2,[1,-1],[new(0,1,0,2,1),new(1,0,0,2,-1)]));
        var s=new MatrixDispatcher(preserveNetworkRows:true,reuseStructure:true);
        var a=s.Decide(m,new());MatrixVerifier.Replay(a);Pass("initial-network",a.Assurance=="ProvenOptimal");
        var edited=MatrixAdmission.Snapshot(m);edited.Cost[0]=(1L<<40)+1;
        var b=s.Decide(edited,new());MatrixVerifier.Replay(b);Pass("current-objective-envelope-fallback",b.Route=="original"&&b.Status=="Optimal"&&b.Value==edited.Cost[0]&&s.StructuralHits==0);
        var c=s.Decide(m,new());MatrixVerifier.Replay(c);Pass("recovery",c.Assurance=="ProvenOptimal"&&s.StructuralHits==1);
        var d=MatrixAdmission.Snapshot(m);d.Rows[0]=d.Rows[0] with{L=0,U=0};d.Rows[1]=d.Rows[1] with{L=0,U=0};
        var e=s.Decide(d,new());MatrixVerifier.Replay(e);Pass("supply-edit-invalidates",e.Assurance=="ProvenOptimal"&&s.StructuralHits==1);
        var zero=MatrixAdmission.From(new GeneralNetwork(4,[1,-1,0,0],[new(0,1,1,1,5),new(2,3,0,2,-4),new(3,2,0,2,1)]));
        var z=new MatrixDispatcher().Decide(zero,new());MatrixVerifier.Replay(z);Pass("disconnected-negative-cycle",z.Assurance=="ProvenOptimal"&&z.Value==-1);
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",checks,valid=true}));
    }
}
