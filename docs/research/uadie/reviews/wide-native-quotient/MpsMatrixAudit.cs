using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
internal static class MpsMatrixAudit
{
    private static string Key(IEnumerable<int> s,long l,long u)=>l+":"+u+":"+string.Join(",",s.Order());
    internal static void Run(string dir,string[]? names=null,bool dispatcher=false)
    {
        foreach(string name in names??new[]{"air03","air04","air05","10teams","mod010","assign1-5-8"})
        {
            string path=Path.Combine(dir,name+".mps.gz");
            string sha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            WideModel? m=null;string? reason=null;
            try{m=IndustrialAdmission.Read(path);}catch(NotSupportedException e){reason=e.Message;}catch(OverflowException e) when(dispatcher){reason=e.Message;}catch(ArgumentException e) when(dispatcher){reason=e.Message;}
            IntPtr h=Native.Highs_create();if(h==IntPtr.Zero)throw new Exception("create");
            void Ok(int s){if(s<0)throw new Exception("HiGHS error");}
            try{
                Ok(Native.Highs_setBoolOptionValue(h,"output_flag",0));
                Ok(Native.Highs_setIntOptionValue(h,"threads",1));
                Ok(Native.Highs_setDoubleOptionValue(h,"time_limit",2));
                Ok(Native.Highs_setDoubleOptionValue(h,"mip_rel_gap",0));
                Ok(Native.Highs_setDoubleOptionValue(h,"mip_abs_gap",0));
                Ok(Native.Highs_readModel(h,path[..^3]));
                int n=Native.Highs_getNumCol(h),r=Native.Highs_getNumRow(h),nz=Native.Highs_getNumNz(h);
                var c=new double[n];var cl=new double[n];var cu=new double[n];var rl=new double[r];var ru=new double[r];var start=new int[r+1];var idx=new int[nz];var val=new double[nz];var types=new int[n];
                Ok(Native.Highs_getLp(h,2,out int nc,out int nr,out int nnz,out int sense,out double off,c,cl,cu,rl,ru,start,idx,val,types));
                if(nc!=n||nr!=r||nnz!=nz)throw new Exception("dimensions");
                start[r]=nz;
                bool checkedMatrix=false;int checkedCoefficients=0;
                if(m!=null)
                {
                    if(n!=m.N||sense!=1||off!=0||r!=m.Edges.Length+m.Fixes.Length+m.Rows.Length)throw new Exception("parser dimensions/sense");
                    for(int j=0;j<n;j++)if(c[j]!=m.Cost[j]||cl[j]!=0||cu[j]!=1||types[j]!=1)throw new Exception("parser objective/domain");
                    var keys=new List<string>();
                    for(int i=0;i<r;i++){
                        int count=start[i+1]-start[i];
                        var cols=idx.AsSpan(start[i],count).ToArray();
                        if(cols.Distinct().Count()!=count||val.AsSpan(start[i],count).ToArray().Any(x=>x!=1))throw new Exception("native coefficients");
                        double l=Math.Max(0,rl[i]),u=Math.Min(count,ru[i]);
                        if(l!=Math.Truncate(l)||u!=Math.Truncate(u)||l>u)throw new Exception("native bounds");
                        keys.Add(Key(cols,(long)l,(long)u));checkedCoefficients+=count;
                    }
                    var ours=m.Rows.Select(x=>Key(x.Scope,x.L,x.U)).Concat(m.Edges.Select(e=>Key([e.A,e.B],1,1))).Concat(m.Fixes.Select(f=>Key([f.Bit],f.Value,f.Value)));
                    if(!keys.Order(StringComparer.Ordinal).SequenceEqual(ours.Order(StringComparer.Ordinal)))throw new Exception("parser coefficient or row mismatch");
                    checkedMatrix=true;
                }
                // Native input is never transformed for unsupported cases.
                Ok(Native.Highs_run(h));int status=Native.Highs_getModelStatus(h);
                Ok(Native.Highs_getIntInfoValue(h,"primal_solution_status",out int primal));
                double? objective=null;bool witness=false;double maxError=0;
                if(primal==2)
                {
                    double[] x=new double[n];Ok(Native.Highs_getSolution(h,x,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero));
                    for(int j=0;j<n;j++){
                        if(!double.IsFinite(x[j]))throw new Exception("native nonfinite");
                        maxError=Math.Max(maxError,Math.Max(cl[j]-x[j],x[j]-cu[j]));
                        if(types[j]==1)maxError=Math.Max(maxError,Math.Abs(x[j]-Math.Round(x[j])));
                    }
                    for(int i=0;i<r;i++){
                        double sum=0;for(int j=start[i];j<start[i+1];j++)sum+=val[j]*x[idx[j]];
                        maxError=Math.Max(maxError,Math.Max(rl[i]-sum,sum-ru[i]));
                    }
                    double cost=off;for(int i=0;i<n;i++)cost+=x[i]*c[i];
                    Ok(Native.Highs_getDoubleInfoValue(h,"objective_function_value",out double reported));
                    if(maxError>1e-5||Math.Abs(cost-reported)>1e-5*Math.Max(1,Math.Abs(cost)))throw new Exception("native primal/objective check");
                    if(m!=null){
                        var bits=x.Select(v=>checked((int)Math.Round(v))).ToArray();long exact=CertifiedStudy.Verify(m,bits);
                        if(Math.Abs(reported-exact)>1e-6)throw new Exception("exact source objective");
                    }
                    objective=reported;witness=true;
                }
                WideResult? original=null;UnifiedOutcome? candidate=null;
                if(dispatcher&&m!=null){
                    original=CertifiedStudy.Original(m);if(original.Bits!=null&&CertifiedStudy.Verify(m,original.Bits)!=original.Value)throw new Exception("original witness");
                    candidate=new DependencyDispatcher(true,true,true,true).Decide(m,new());UnifiedVerifier.Verify(new(CertifiedQuotient.Digest(m),m,candidate));
                    if(original.Status=="Optimal"&&candidate.Result.Status=="Optimal"&&original.Value!=candidate.Result.Value)throw new Exception("dispatcher objective");
                    if(status==7&&candidate.Result.Status=="Optimal"&&Math.Abs(objective!.Value-candidate.Result.Value!.Value)>1e-6)throw new Exception("independent optimum");
                }
                Console.WriteLine(JsonSerializer.Serialize(new{name,sha,n,rows=r,nonzeros=nz,admission=m!=null?"supported":"unsupported",reason,checkedMatrix,checkedCoefficients,checkedCosts=m?.N??0,checkedDomains=m?.N??0,checkedRows=m==null?0:r,status,primal,objective,witnessVerified=witness,maxError,originalStatus=original?.Status,originalValue=original?.Value,candidateStatus=candidate?.Result.Status,candidateValue=candidate?.Result.Value,candidateRoute=candidate?.Route,candidateFormal=candidate?.Formal}));
            }finally{Native.Highs_destroy(h);}
        }
    }
    private static class Native
    {
        const string Lib="highs.so";
        [DllImport(Lib)]internal static extern IntPtr Highs_create();
        [DllImport(Lib)]internal static extern void Highs_destroy(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_readModel(IntPtr h,string path);
        [DllImport(Lib)]internal static extern int Highs_getNumCol(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_getNumRow(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_getNumNz(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_getLp(IntPtr h,int format,out int nc,out int nr,out int nz,out int sense,out double offset,[Out]double[] c,[Out]double[] cl,[Out]double[] cu,[Out]double[] rl,[Out]double[] ru,[Out]int[] start,[Out]int[] idx,[Out]double[] val,[Out]int[] types);
        [DllImport(Lib)]internal static extern int Highs_setBoolOptionValue(IntPtr h,string name,int value);
        [DllImport(Lib)]internal static extern int Highs_setIntOptionValue(IntPtr h,string name,int value);
        [DllImport(Lib)]internal static extern int Highs_setDoubleOptionValue(IntPtr h,string name,double value);
        [DllImport(Lib)]internal static extern int Highs_run(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_getModelStatus(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_getIntInfoValue(IntPtr h,string name,out int value);
        [DllImport(Lib)]internal static extern int Highs_getDoubleInfoValue(IntPtr h,string name,out double value);
        [DllImport(Lib)]internal static extern int Highs_getSolution(IntPtr h,[Out]double[] x,IntPtr cd,IntPtr rv,IntPtr rd);
    }
}
