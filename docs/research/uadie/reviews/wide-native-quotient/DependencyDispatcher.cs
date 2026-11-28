using System.Text.Json;

internal record LocalBlock(int[] Members,WideModel Model);
internal record LocalProof(int[] Members,ProofOutcome? Quotient,AssignmentProof? Assignment,FlowProof? Flow,WideResult Result);
internal record UnifiedOutcome(string Route,WideResult Result,LocalProof[] Blocks,int Reused,int Transported,int Rebuilt,bool Formal);
internal record UnifiedEvidence(string Digest,WideModel Model,UnifiedOutcome Outcome);

internal static class DependencyPartition
{
    // Fresh connectivity of ALL constraint scopes, not only parity graph.
    internal static LocalBlock[] Build(WideModel m)
    {
        int[] parent=Enumerable.Range(0,m.N).ToArray(),size=Enumerable.Repeat(1,m.N).ToArray();
        int Root(int i){while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;}
        void Join(int a,int b){a=Root(a);b=Root(b);if(a==b)return;if(size[a]<size[b])(a,b)=(b,a);parent[b]=a;size[a]+=size[b];}
        foreach(var(a,b) in m.Edges)Join(a,b);
        foreach(var r in m.Rows)for(int i=1;i<r.Scope.Length;i++)Join(r.Scope[0],r.Scope[i]);
        var members=Enumerable.Range(0,m.N).GroupBy(Root).Select(g=>g.ToArray()).OrderBy(a=>a[0]).ToArray();
        int[] owner=new int[m.N],local=new int[m.N];
        for(int j=0;j<members.Length;j++)for(int i=0;i<members[j].Length;i++){owner[members[j][i]]=j;local[members[j][i]]=i;}
        var edges=members.Select(_=>new List<(int,int)>()).ToArray();
        var fixes=members.Select(_=>new List<(int,int)>()).ToArray();
        var rows=members.Select(_=>new List<WideRow>()).ToArray();
        foreach(var(a,b) in m.Edges)edges[owner[a]].Add((local[a],local[b]));
        foreach(var(i,v) in m.Fixes)fixes[owner[i]].Add((local[i],v));
        foreach(var r in m.Rows)rows[r.Scope.Length==0?0:owner[r.Scope[0]]].Add(new(r.Scope.Select(i=>local[i]).ToArray(),r.L,r.U));
        return members.Select((a,j)=>new LocalBlock(a,new(a.Length,edges[j].ToArray(),fixes[j].ToArray(),rows[j].ToArray(),a.Select(i=>m.Cost[i]).ToArray()))).ToArray();
    }
    internal static bool SameShape(WideModel a,WideModel b)
    {
        if(a.N!=b.N||!a.Edges.AsSpan().SequenceEqual(b.Edges)||!a.Fixes.AsSpan().SequenceEqual(b.Fixes)||a.Rows.Length!=b.Rows.Length)return false;
        for(int i=0;i<a.Rows.Length;i++)if(a.Rows[i].L!=b.Rows[i].L||a.Rows[i].U!=b.Rows[i].U||!a.Rows[i].Scope.AsSpan().SequenceEqual(b.Rows[i].Scope))return false;
        return true;
    }
}
internal sealed class DependencyDispatcher(bool reuse,bool decompose,bool flow=true)
{
    private sealed record Token(LocalBlock Block,LocalProof Proof);
    private Dictionary<int,Token> tokens=[];
    private readonly ProofReuse monolithic=new(reuse);
    private int busy;
    private UnifiedOutcome Solve(WideModel m,PhaseTrace trace,CancellationToken ct=default)
    {
        if(Interlocked.Exchange(ref busy,1)!=0)throw new InvalidOperationException("single owner");
        try{
            ct.ThrowIfCancellationRequested();
            string digest=CertifiedQuotient.Digest(m);
            var whole=new LocalBlock(Enumerable.Range(0,m.N).ToArray(),m);
            // First specialized assignment/flow, then cheap component gate; no warm hint.
            bool networkCandidate=m.Edges.Length==0&&m.Fixes.Length==0&&m.Rows.Length>=2&&m.Rows.All(r=>r.U==1&&r.Scope.Length>0);
            bool topHit=false,topTransport=false;
            LocalProof? top=networkCandidate?TryLocal(whole,tokens.GetValueOrDefault(0),out topHit,out topTransport,true):null;
            trace.Mark("network-gate");
            if(top!=null){
                tokens=reuse?new(){[0]=new(whole,top)}:new();
                return new(top.Assignment!=null?"assignment":top.Flow!=null?"flow":"direct",top.Result,[top],topHit?1:0,topTransport?1:0,topHit||topTransport?0:1,true);
            }
            if(decompose&&m.N>=16){
                var parts=DependencyPartition.Build(m);trace.Mark("partition");
                // Avoid huge singleton fanout or dense monolithic redundant work.
                if(parts.Length>=2&&parts.Length<=256&&parts.Max(b=>b.Model.N)<=m.N*3/4){
                    var next=new Dictionary<int,Token>();var proofs=new List<LocalProof>();
                    int hits=0,transports=0,rebuilt=0;
                    foreach(var b in parts){
                        ct.ThrowIfCancellationRequested();
                        var p=TryLocal(b,tokens.GetValueOrDefault(b.Members[0]),out bool hit,out bool transported,false);
                        if(p==null){proofs.Clear();break;}
                        proofs.Add(p);if(hit)hits++;else if(transported)transports++;else rebuilt++;
                        if(reuse)next.Add(b.Members[0],new(b,p));
                    }
                    trace.Mark("block-solve");
                    if(proofs.Count==parts.Length){
                        var bits=new int[m.N];long cost=0;
                        foreach(var p in proofs){for(int i=0;i<p.Members.Length;i++)bits[p.Members[i]]=p.Result.Bits![i];cost+=p.Result.Value!.Value;}
                        ct.ThrowIfCancellationRequested();tokens=next;
                        return new("components",new("Optimal",cost,bits,m.N,m.Rows.Length),proofs.ToArray(),hits,transports,rebuilt,true);
                    }
                }
            }
            tokens.Clear();
            var f=CertifiedStudy.Features(m);trace.Mark("fallback-gate");
            if(f.Disjoint||f.ReductionProxy>=.125){
                var o=monolithic.Solve(m,digest,trace,ct:ct);
                return new(o.Route,o.Result,[new(whole.Members,o,null,null,o.Result)],0,0,1,o.OptimalityProved);
            }
            var original=CertifiedStudy.Original(m);ct.ThrowIfCancellationRequested();
            return new("original",original,[],0,0,0,false);
        }finally{Volatile.Write(ref busy,0);}
    }
    private LocalProof? TryLocal(LocalBlock b,Token? old,out bool hit,out bool transported,bool networkOnly)
    {
        hit=false;transported=false;var m=b.Model;string d=CertifiedQuotient.Digest(m);
        bool same=reuse&&old!=null&&old.Block.Members.AsSpan().SequenceEqual(b.Members)&&DependencyPartition.SameShape(old.Block.Model,m);
        if(same&&old!.Block.Model.Cost.AsSpan().SequenceEqual(m.Cost)){hit=true;return old.Proof;}
        if(same&&old!.Proof.Quotient?.Cert is {} oldCert&&!networkOnly){
            var c=ProofReuse.Bind(oldCert,m,d);
            if(!ProofReuse.VerifyObjective(m,c,d))throw new Exception("objective proof");
            int[]? y=old.Proof.Quotient.Reduced;
            if(y!=null&&TransformationVerifier.VerifyDirect(c,y))transported=true;
            else y=CertifiedQuotient.Direct(c);
            if(y!=null){
                if(!TransformationVerifier.VerifyDirect(c,y))throw new Exception("direct proof");
                var bits=Enumerable.Range(0,m.N).Select(i=>c.Base[i]^(c.Map[i]<0?0:y[c.Map[i]])).ToArray();
                long cost=CertifiedStudy.Verify(m,bits);var r=new WideResult("Optimal",cost,bits,c.Free,c.Rows.Length);
                return new(b.Members,new(r,c,y,"direct",true,true),null,null,r);
            }
        }
        if(same&&old!.Proof.Assignment is {} a){
            // Retain v, recompute u from current matched cells; verify all dual inequalities.
            var u=Enumerable.Range(0,a.Shape.K).Select(i=>checked(m.Cost[a.Shape.Matrix[i*a.Shape.K+a.Match[i]]]-a.V[a.Match[i]])).ToArray();
            var p=a with{Digest=d,U=u};var r=CertifiedAssignment.Reconstruct(m,p.Shape,p.Match);
            if(AssignmentVerifier.Verify(m,d,p,r)){transported=true;return new(b.Members,null,p,null,r);}
        }
        var shape=CertifiedAssignment.Discover(m);
        if(shape!=null){
            var p=CertifiedAssignment.Hungarian(m,shape,d);var r=CertifiedAssignment.Reconstruct(m,shape,p.Match);
            if(!AssignmentVerifier.Verify(m,d,p,r))throw new Exception("assignment proof");
            return new(b.Members,null,p,null,r);
        }
        if(flow){
            var f=CertifiedSparseFlow.Solve(m,d);
            if(f!=null)return new(b.Members,null,null,f.Value.Proof,f.Value.Result);
        }
        if(networkOnly)return null;
        var t=CertifiedQuotient.Produce(m,d);if(t==null)return null;
        var reduced=CertifiedQuotient.Direct(t);if(reduced==null)return null;
        if(!TransformationVerifier.Verify(m,d,t)||!TransformationVerifier.VerifyDirect(t,reduced))throw new Exception("local proof");
        var x=Enumerable.Range(0,m.N).Select(i=>t.Base[i]^(t.Map[i]<0?0:reduced[t.Map[i]])).ToArray();
        var result=new WideResult("Optimal",CertifiedStudy.Verify(m,x),x,t.Free,t.Rows.Length);
        return new(b.Members,new(result,t,reduced,"direct",true,true),null,null,result);
    }
    // Caller boundary: never expose arrays owned by tokens; caller must use this API.
    internal UnifiedOutcome Decide(WideModel input,PhaseTrace trace,CancellationToken ct=default)
    {
        // Snapshot must not race with caller mutation.
        var m=IncrementalQuotient.Admit(ProofReuse.Snapshot(input));
        var o=Solve(m,trace,ct);
        return JsonSerializer.Deserialize<UnifiedOutcome>(JsonSerializer.SerializeToUtf8Bytes(o,CertifiedQuotient.Json),CertifiedQuotient.Json)!;
    }
}
internal static class UnifiedVerifier
{
    internal static void Verify(UnifiedEvidence e)
    {
        var m=IncrementalQuotient.Admit(e.Model);var o=e.Outcome;
        if(CertifiedQuotient.Digest(m)!=e.Digest)throw new Exception("digest");
        var r=o.Result;
        if(r.Bits!=null&&CertifiedStudy.Verify(m,r.Bits)!=r.Value)throw new Exception("source witness");
        if((r.Status is "Optimal" or "Feasible")!=(r.Bits!=null&&r.Value.HasValue))throw new Exception("status witness");
        if(o.Blocks.Length==0){if(o.Formal)throw new Exception("missing proof");return;}
        // Independent partition: ownership check against ALL original scopes, not producer DSU.
        int[] owner=Enumerable.Repeat(-1,m.N).ToArray(),local=new int[m.N];
        for(int j=0;j<o.Blocks.Length;j++){
            var b=o.Blocks[j];
            if(b.Members.Length==0)throw new Exception("empty block");
            for(int i=0;i<b.Members.Length;i++){int v=b.Members[i];if(v<0||v>=m.N||owner[v]>=0)throw new Exception("partition");owner[v]=j;local[v]=i;}
        }
        if(owner.Any(j=>j<0))throw new Exception("missing source column");
        var edges=o.Blocks.Select(_=>new List<(int,int)>()).ToArray();
        var fixes=o.Blocks.Select(_=>new List<(int,int)>()).ToArray();
        var rows=o.Blocks.Select(_=>new List<WideRow>()).ToArray();
        foreach(var(a,b) in m.Edges){if(owner[a]!=owner[b])throw new Exception("cross-edge");edges[owner[a]].Add((local[a],local[b]));}
        foreach(var(i,v) in m.Fixes)fixes[owner[i]].Add((local[i],v));
        foreach(var row in m.Rows){
            int j=row.Scope.Length==0?0:owner[row.Scope[0]];if(row.Scope.Any(i=>owner[i]!=j))throw new Exception("cross-row");
            rows[j].Add(new(row.Scope.Select(i=>local[i]).ToArray(),row.L,row.U));
        }
        bool allFormal=true;Int128 total=0;
        for(int j=0;j<o.Blocks.Length;j++){
            var b=o.Blocks[j];var source=new WideModel(b.Members.Length,edges[j].ToArray(),fixes[j].ToArray(),rows[j].ToArray(),b.Members.Select(i=>m.Cost[i]).ToArray());
            string d=CertifiedQuotient.Digest(source);
            if(b.Result.Bits!=null&&CertifiedStudy.Verify(source,b.Result.Bits)!=b.Result.Value)throw new Exception("local witness");
            int kinds=(b.Quotient!=null?1:0)+(b.Assignment!=null?1:0)+(b.Flow!=null?1:0);if(kinds!=1)throw new Exception("proof kind");
            if(b.Quotient!=null){
                // Compare result content rather than record reference identity.
                var q=b.Quotient.Result;
                if(q.Status!=b.Result.Status||q.Value!=b.Result.Value||!(q.Bits??[]).AsSpan().SequenceEqual(b.Result.Bits??[]))throw new Exception("quotient result");
                ProofReadback.Verify(new(d,source.N,"unified",0,0,"unified",source,b.Quotient));allFormal&=b.Quotient.OptimalityProved;
            }else if(b.Assignment!=null){if(!AssignmentVerifier.Verify(source,d,b.Assignment,b.Result))throw new Exception("assignment proof");}
            else if(!CertifiedSparseFlow.Verify(source,d,b.Flow!,b.Result))throw new Exception("flow proof");
            if(r.Bits!=null){
                if(b.Result.Bits==null)throw new Exception("local missing");
                for(int i=0;i<b.Members.Length;i++)if(r.Bits[b.Members[i]]!=b.Result.Bits[i])throw new Exception("compose witness");
                total+=b.Result.Value!.Value;
            }
        }
        if(o.Formal!=allFormal||o.Formal&&r.Status!="Optimal"||r.Bits!=null&&total!=r.Value)throw new Exception("compose status/cost");
        if(o.Blocks.Length>1&&!o.Formal)throw new Exception("uncertified component composition");
        if(o.Blocks.Length==1&&o.Blocks[0].Result.Status!=r.Status)throw new Exception("status binding");
    }
}
