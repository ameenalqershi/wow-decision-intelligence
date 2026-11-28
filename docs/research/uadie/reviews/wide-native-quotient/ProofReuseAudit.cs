using System.Text.Json;
internal static class ProofReuseAudit
{
    internal static void Run()
    {
        int cases=0,hits=0,tamper=0,highs=0;
        void Assert(bool b,string msg){if(!b)throw new Exception(msg);}
        long? Exact(WideModel m)
        {
            long? best=null;
            for(int mask=0;mask<(1<<m.N);mask++){
                int X(int i)=>(mask>>i)&1;
                if(m.Edges.Any(e=>X(e.A)+X(e.B)!=1)||m.Fixes.Any(f=>X(f.Bit)!=f.Value)||m.Rows.Any(r=>{int s=r.Scope.Sum(X);return s<r.L||s>r.U;}))continue;
                long v=Enumerable.Range(0,m.N).Sum(i=>m.Cost[i]*X(i));if(best==null||v<best)best=v;
            }return best;
        }
        ProofOutcome RunOne(ProofReuse session,WideModel m)
        {
            IncrementalQuotient.Admit(m);string digest=CertifiedQuotient.Digest(m);
            var o=session.Solve(m,digest,new());
            ProofReadback.Verify(new(digest,m.N,"audit",0,0,"G16",m,o));return o;
        }
        void Reject(string name,ProofEvidence e)
        {
            bool rejected=false;try{ProofReadback.Verify(e);}catch(Exception){rejected=true;}
            Console.WriteLine(JsonSerializer.Serialize(new{kind="tamper",name,rejected}));
            Assert(rejected,name);tamper++;
        }
        for(int seed=0;seed<96;seed++)
        {
            var rng=new Random(2520101+seed);int n=8;
            var edges=new List<(int,int)>();for(int i=0;i<6;i+=2)if(rng.Next(2)==1)edges.Add((i,i+1));
            var rows=new[]{new WideRow([0,2,4],1,2),new WideRow([1,3,5,7],1,3)};
            var m=new WideModel(n,edges.ToArray(),[],rows,Enumerable.Range(0,n).Select(_=>(long)rng.Next(-20,21)).ToArray());
            var session=new ProofReuse(true);TransformCertificate? prior=null;
            for(int rev=0;rev<8;rev++)
            {
                if(rev==1)m.Cost[0]+=17;
                if(rev==2){m=m with{Edges=m.Edges.Append((0,2)).ToArray()};}
                if(rev==3){m.Edges[^1]=(0,0);}
                if(rev==4){m=m with{Edges=m.Edges.SkipLast(1).ToArray()};}
                if(rev==5){m.Rows[0].Scope[0]=1;}
                if(rev==6){m=m with{Fixes=[(0,0),(0,1)]};}
                if(rev==7){m=m with{Fixes=[]};Array.Reverse(m.Rows);}
                var o=RunOne(session,m);long? expected=Exact(m);
                Assert(o.Result.Status==(expected.HasValue?"Optimal":"Infeasible")&&o.Result.Value==expected,"finite");
                bool expectedHit=rev==1;Assert(session.Hit==expectedHit,"cache invalidation");
                cases++;if(session.Hit)hits++;
                var e=new ProofEvidence(CertifiedQuotient.Digest(m),n,"audit",seed,rev,"G16",m,o);
                Console.WriteLine(JsonSerializer.Serialize(new{kind="finite",e,expected,hit=session.Hit},CertifiedQuotient.Json));
                if(rev==0&&o.Cert!=null){
                    prior=o.Cert with{Map=o.Cert.Map.ToArray(),Base=o.Cert.Base.ToArray(),Cost=o.Cert.Cost.ToArray()};
                    // Mutate returned proof arrays. Session must not alias them.
                    o.Cert.Base[0]=2;o.Cert.Map[0]=int.MaxValue;
                    Reject("mutated-export",e);
                }
                if(rev==1&&o.Cert!=null&&prior!=null){
                    Reject("stale-objective",e with{Outcome=o with{Cert=prior}});
                    Reject("forged-rebinding",e with{Outcome=o with{Cert=prior with{Digest=e.Digest}}});
                    Reject("wrong-offset",e with{Outcome=o with{Cert=o.Cert with{Offset=o.Cert.Offset+1}}});
                    Reject("missing-proof",e with{Outcome=o with{Cert=null}});
                    Reject("wrong-source-digest",e with{Digest="bad"});
                    if(o.Reduced!=null){var y=o.Reduced.ToArray();y[0]=2;Reject("reduced-domain",e with{Outcome=o with{Reduced=y}});}
                }
            }
        }
        // Objective boundary, isolated instances, cancellation and recovery.
        var boundary=new WideModel(2,[(0,1)],[],[],[(1L<<49),-(1L<<49)]);
        var local=new ProofReuse(true);Assert(RunOne(local,boundary).Result.Value==-(1L<<49),"boundary");
        boundary.Cost[0]=long.MaxValue;
        bool overflow=false;try{RunOne(local,boundary);}catch(OverflowException){overflow=true;}Assert(overflow,"overflow");
        boundary.Cost[0]=1;Assert(RunOne(local,boundary).Result.Value==-(1L<<49)&&local.Hit,"recover");
        bool cancelled=false;try{local.Solve(boundary,CertifiedQuotient.Digest(boundary),new(),ct:new CancellationToken(true));}catch(OperationCanceledException){cancelled=true;}Assert(cancelled,"cancel");
        Assert(RunOne(local,boundary).Result.Value==-(1L<<49)&&local.Hit,"cancel recovery");
        Assert(Enumerable.Range(0,8).AsParallel().All(i=>{var m=new WideModel(2,[(0,1)],[],[],[i,-i]);return RunOne(new(true),m).Result.Value==-i;}),"isolated sessions");
        foreach(int n in new[]{128,1024})for(int seed=0;seed<12;seed++){
            var session=new ProofReuse(true);
            foreach(var m in ProofReuseStudy.History(n,2550101+seed,"signed",2)){
                var o=RunOne(session,m);var h=IncrementalAudit.Highs(m);
                Assert(o.Result.Status=="Optimal"&&h.Status=="Optimal"&&o.Result.Value==h.Value&&CertifiedStudy.Verify(m,h.Bits!)==h.Value,"HiGHS");
                Console.WriteLine(JsonSerializer.Serialize(new{kind="highs",model=m,result=o,highs=h,hit=session.Hit},CertifiedQuotient.Json));highs++;
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",cases,hits,tamper,highs,boundary=6}));
    }
}
