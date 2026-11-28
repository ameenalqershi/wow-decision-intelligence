using System.Text.Json;
internal static class CertifiedAudit
{
    internal static void Run()
    {
        int finite=0,direct=0,native=0,fallback=0,highs=0,tamper=0;
        void Assert(bool b,string label){if(!b)throw new Exception(label);}
        long? Exact(WideModel m)
        {
            long? best=null;for(int mask=0;mask<(1<<m.N);mask++){
                int X(int i)=>(mask>>i)&1;
                if(m.Edges.Any(e=>X(e.A)+X(e.B)!=1)||m.Fixes.Any(f=>X(f.Bit)!=f.Value)||m.Rows.Any(r=>{int n=r.Scope.Sum(X);return n<r.L||n>r.U;}))continue;
                long v=Enumerable.Range(0,m.N).Sum(i=>m.Cost[i]*X(i));if(best==null||v<best)best=v;
            }return best;
        }
        WideResult RunOne(WideModel m,out TransformCertificate? cert,out int[]? y,out string route)
        {
            IncrementalQuotient.Admit(m);string digest=CertifiedQuotient.Digest(m);
            cert=CertifiedQuotient.Produce(m,digest);y=null;
            if(cert==null){route="fallback";return CertifiedStudy.Original(m);}
            Assert(TransformationVerifier.Verify(m,digest,cert),"valid certificate rejected");
            var r=CertifiedQuotient.SolveReduced(cert,true,out route,out y);
            if(route=="direct")Assert(TransformationVerifier.VerifyDirect(cert,y!),"valid direct proof rejected");
            return r;
        }
        void Reject(WideModel m,TransformCertificate bad,string kind)
        {
            bool accepted=TransformationVerifier.Verify(m,CertifiedQuotient.Digest(m),bad);
            Console.WriteLine(JsonSerializer.Serialize(new{kind="tamper",test=kind,accepted},CertifiedQuotient.Json));
            Assert(!accepted,"tamper accepted "+kind);tamper++;
        }
        for(int s=0;s<128;s++)
        {
            var rng=new Random(2340101+s);int n=8;
            var edges=new List<(int,int)>();for(int i=0;i<6;i+=2)if(rng.Next(2)==1)edges.Add((i,i+1));
            var rows=new List<WideRow>{new([0,2,4],1,2),new([1,3,5,7],1,3)};
            if(s%4==0)rows=[new([0,2,4],0,2),new([7],0,1)];
            if(s%4==1)rows=[new([0,1,2,3],0,4),new([4,5,6,7],1,3)];
            var fixes=new List<(int,int)>();long[] costs=Enumerable.Range(0,n).Select(_=>(long)rng.Next(-10,11)).ToArray();
            for(int rev=0;rev<6;rev++)
            {
                if(rev==1)costs[0]+=7;
                if(rev==2){edges.Add((0,1));edges.Add((1,2));edges.Add((2,0));}
                if(rev==3)edges.RemoveAt(edges.Count-1);
                if(rev==4)fixes.Add((0,0));
                if(rev==5){fixes.Add((0,1));rows.Reverse();}
                var m=new WideModel(n,edges.ToArray(),fixes.ToArray(),rows.ToArray(),costs.ToArray());
                var r=RunOne(m,out var c,out var y,out string route);
                long? expected=Exact(m);
                Console.WriteLine(JsonSerializer.Serialize(new{kind="finite",s,rev,model=m,result=r,expected,route,cert=c,reduced=y},CertifiedQuotient.Json));
                Assert(r.Status==(expected.HasValue?"Optimal":"Infeasible")&&r.Value==expected&&(!expected.HasValue||r.Bits!=null&&CertifiedStudy.Verify(m,r.Bits)==expected),"finite mismatch");
                finite++;if(route=="direct")direct++;else if(route=="native-quotient")native++;else fallback++;
                if(c!=null&&rev==0)
                {
                    Reject(m,c with{Digest="forged"},"digest");
                    Reject(m,c with{Offset=c.Offset+1},"offset");
                    long[] cc=c.Cost.ToArray();cc[0]++;Reject(m,c with{Cost=cc},"objective");
                    int[] b=c.Base.ToArray();b[0]=2;Reject(m,c with{Base=b},"base-domain");
                    var rr=c.Rows.ToArray();rr[0]=rr[0] with{L=rr[0].L-1};Reject(m,c with{Rows=rr},"row-bound");
                    Reject(m,c with{Rows=c.Rows.Skip(1).ToArray()},"row-deletion");
                    int[] map=c.Map.ToArray();map[0]=-1;Reject(m,c with{Map=map},"unanchored-fix");
                    var changed=m with{Cost=m.Cost.Select((v,i)=>v+(i==0?1:0)).ToArray()};
                    Reject(changed,c,"stale-objective");
                    Reject(changed,c with{Digest=CertifiedQuotient.Digest(changed)},"forged-rebinding");
                    if(route=="direct"){
                        int[] corrupt=y!.ToArray();Array.Fill(corrupt,2);
                        bool accepted=TransformationVerifier.VerifyDirect(c,corrupt);
                        Console.WriteLine(JsonSerializer.Serialize(new{kind="tamper",test="direct-witness-domain",accepted},CertifiedQuotient.Json));
                        Assert(!accepted,"direct tamper");tamper++;
                    }
                }
            }
        }
        // A superficially feasible but nonoptimal reduced witness must fail the exchange proof.
        var small=new WideModel(3,[],[],[new([0,1,2],1,1)],[1,4,8]);
        var sr=RunOne(small,out var sc,out var sy,out _);
        var nonoptimal=sy!.ToArray();for(int i=0;i<3;i++)nonoptimal[sc!.Map[i]]=sc.Base[i]^(i==2?1:0);
        bool falseOptimal=TransformationVerifier.VerifyDirect(sc!,nonoptimal);
        Console.WriteLine(JsonSerializer.Serialize(new{kind="tamper",test="feasible-nonoptimal",accepted=falseOptimal}));
        Assert(!falseOptimal,"nonoptimal accepted");tamper++;
        Assert(sr.Value==1,"small exact");
        // Forged merge of disconnected vertices, with internally consistent reduced costs.
        var fm=new WideModel(2,[],[],[],[1,2]);
        Reject(fm,new(CertifiedQuotient.Digest(fm),1,[0,0],[0,0],[],0,[3]),"disconnected-merge");
        foreach(int n in new[]{64,512,2048})
        for(int seed=0;seed<12;seed++)
        {
            var rng=new Random(2370101+seed+n);
            var edges=Enumerable.Range(0,n/2).Select(i=>(2*i,2*i+1)).ToArray();
            var rows=new List<WideRow>();
            for(int i=0;i<n;i+=16){int[] scope=Enumerable.Range(0,Math.Min(8,(n-i)/2)).Select(j=>i+2*j+rng.Next(2)).ToArray();rows.Add(new(scope,scope.Length/2,scope.Length/2));}
            var m=new WideModel(n,edges,[],rows.ToArray(),Enumerable.Range(0,n).Select(_=>(long)rng.Next(-50,51)).ToArray());
            var r=RunOne(m,out var cert,out var y,out string route);
            var h=IncrementalAudit.Highs(m);
            Console.WriteLine(JsonSerializer.Serialize(new{kind="highs",n,seed,model=m,result=r,highs=h,route,cert,reduced=y},CertifiedQuotient.Json));
            Assert(r.Status=="Optimal"&&h.Status=="Optimal"&&r.Value==h.Value&&CertifiedStudy.Verify(m,r.Bits!)==r.Value&&CertifiedStudy.Verify(m,h.Bits!)==h.Value,"HiGHS mismatch");highs++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",finite,direct,native,fallback,highs,tamper}));
    }
}
