using System.Text.Json;
internal static class UnifiedAdversarial
{
    internal static void Run()
    {
        int checks=0,finite=0;
        void Pass(string name,bool ok){if(!ok)throw new Exception(name);Console.WriteLine(JsonSerializer.Serialize(new{kind="check",name,valid=ok}));checks++;}
        var m=new WideModel(4,[],[],[new([0,1],1,1),new([2,3],1,1),new([0,2],1,1),new([1,3],1,1)],[0,10,10,0]);
        string d=CertifiedQuotient.Digest(m);var f=CertifiedSparseFlow.Solve(m,d)??throw new Exception("flow");
        Pass("exact-flow",f.Result.Value==0&&CertifiedSparseFlow.Verify(m,d,f.Proof,f.Result));
        Pass("false-optimal-feasible",!CertifiedSparseFlow.Verify(m,d,f.Proof,f.Result with{Bits=[0,1,1,0],Value=20}));
        long[] potential=f.Proof.Potential.ToArray();potential[f.Proof.From[0]]=long.MaxValue;
        Pass("forged-potential",!CertifiedSparseFlow.Verify(m,d,f.Proof with{Potential=potential},f.Result));
        var cheaper=m with{Cost=[0,-100,10,0]};string dc=CertifiedQuotient.Digest(cheaper);
        Pass("stale-dual-rebound",!CertifiedSparseFlow.Verify(cheaper,dc,f.Proof with{Digest=dc},f.Result));
        var hall=new WideModel(4,[],[],[new([0],1,1),new([1],1,1),new([2,3],1,1),new([0,1],1,1),new([2],1,1),new([3],1,1)],[1,2,3,4]);
        Pass("hall-infeasible-flow-abstain",CertifiedSparseFlow.Solve(hall,CertifiedQuotient.Digest(hall))==null);
        var hr=new DependencyDispatcher(true,true).Decide(hall,new());
        Pass("hall-native-infeasibility",hr.Result.Status=="Infeasible"&&!hr.Formal);
        var side=m with{Rows=m.Rows.Append(new WideRow([0],0,0)).ToArray()};
        // Invalid row upper0 is a valid source constraint; flow must abstain.
        Pass("side-abstain",CertifiedSparseFlow.Solve(side,CertifiedQuotient.Digest(side))==null);
        var sr=new DependencyDispatcher(true,true).Decide(side,new());
        UnifiedVerifier.Verify(new(CertifiedQuotient.Digest(side),side,sr));
        Pass("side-fallback-optimum",sr.Result.Value==20);
        var transport=new DependencyDispatcher(true,true,true,true,true);
        var first=transport.Decide(m,new());var stable=m with{Cost=[0,11,11,0]};
        var second=transport.Decide(stable,new());
        Pass("residual-proof-transport",first.Formal&&second.Formal&&second.Transported==1&&second.Result.Value==0);
        var third=transport.Decide(cheaper,new());
        Pass("invalidated-residual-proof",third.Formal&&third.Transported==0&&third.Result.Value==-90);
        bool pre=false;try{_=transport.Decide(m,new(),new CancellationToken(true));}catch(OperationCanceledException){pre=true;}
        Pass("pre-cancel",pre);
        Pass("cancel-recovery",transport.Decide(m,new()).Result.Value==0);
        bool rejected=false;try{UnifiedVerifier.Verify(new(d,m,new("original",new("Unknown",1,null,m.N,m.Rows.Length),[],0,0,0,false)));}catch{rejected=true;}
        Pass("unknown-with-value-rejected",rejected);
        for(int h=0;h<48;h++){
            var input=UnifiedStudy.Sparse(3,2840101+h,h%2==0);long? best=null;
            if(input.N>20)throw new Exception("audit scope");
            for(int mask=0;mask<(1<<input.N);mask++){
                bool ok=true;foreach(var row in input.Rows){int n=0;foreach(int i in row.Scope)n+=(mask>>i)&1;if(n<row.L||n>row.U){ok=false;break;}}
                if(!ok)continue;long val=0;for(int i=0;i<input.N;i++)if(((mask>>i)&1)!=0)val+=input.Cost[i];if(best==null||val<best)best=val;
            }
            var o=new DependencyDispatcher(true,true,true,true,true).Decide(input,new());
            var e=new UnifiedEvidence(CertifiedQuotient.Digest(input),input,o);UnifiedVerifier.Verify(e);
            if(!o.Formal||o.Result.Value!=best)throw new Exception("independent sparse enumeration");
            Console.WriteLine(JsonSerializer.Serialize(new{kind="finite",h,e,expected=best},CertifiedQuotient.Json));finite++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",checks,finite,valid=true}));
    }
}
