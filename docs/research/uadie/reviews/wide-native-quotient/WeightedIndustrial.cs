using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
internal static class WeightedIndustrial
{
    internal static void Run(string dir)
    {
        foreach(string name in new[]{"air03","air04","air05","10teams","mod010","assign1-5-8","wachplan","fhnw-binschedule2","comp08-2idx"})
        {
            string path=Path.Combine(dir,name+".mps.gz");
            string sha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            WeightedMpsInput? input=null;string? reason=null;
            try{input=WeightedMps.Read(path);}catch(Exception e) when(e is NotSupportedException or OverflowException or ArgumentException){reason=e.Message;}
            IntPtr h=Native.Highs_create();if(h==IntPtr.Zero)throw new Exception("create");
            void Ok(int s){if(s<0)throw new Exception("HiGHS error");}
            try{
                Ok(Native.Highs_setBoolOptionValue(h,"output_flag",0));Ok(Native.Highs_setIntOptionValue(h,"threads",1));Ok(Native.Highs_setDoubleOptionValue(h,"time_limit",2));Ok(Native.Highs_setDoubleOptionValue(h,"mip_rel_gap",0));Ok(Native.Highs_setDoubleOptionValue(h,"mip_abs_gap",0));
                Ok(Native.Highs_readModel(h,path[..^3]));
                int n=Native.Highs_getNumCol(h),r=Native.Highs_getNumRow(h),nz=Native.Highs_getNumNz(h);
                var c=new double[n];var cl=new double[n];var cu=new double[n];var rl=new double[r];var ru=new double[r];var start=new int[r+1];var idx=new int[nz];var val=new double[nz];var types=new int[n];
                Ok(Native.Highs_getLp(h,2,out int nc,out int nr,out int nnz,out int sense,out double off,c,cl,cu,rl,ru,start,idx,val,types));start[r]=nz;
                if(nc!=n||nr!=r||nnz!=nz)throw new Exception("dimensions");
                var m=input?.Model;int checkedNames=0,checkedCoefficients=0;bool verified=false;
                if(input!=null&&m!=null){
                    if(n!=m.N||r!=m.Rows.Length||sense!=1||off!=0)throw new Exception("parser dimensions/sense/offset");
                    for(int i=0;i<n;i++){
                        var sb=new System.Text.StringBuilder(4096);Ok(Native.Highs_getColName(h,i,sb));
                        if(sb.ToString()!=input.Columns[i]||c[i]!=m.Cost[i]||cl[i]!=0||cu[i]!=1||types[i]!=1)throw new Exception("column identity/domain/obj");checkedNames++;
                    }
                    for(int j=0;j<r;j++){
                        var sb=new System.Text.StringBuilder(4096);Ok(Native.Highs_getRowName(h,j,sb));if(sb.ToString()!=input.Rows[j])throw new Exception("row identity");checkedNames++;
                        var row=m.Rows[j];var source=row.Index.Select((i,k)=>(I:i,V:(double)row.Coeff[k])).OrderBy(x=>x.I).ToArray();
                        var actual=Enumerable.Range(start[j],start[j+1]-start[j]).Select(k=>(I:idx[k],V:val[k])).OrderBy(x=>x.I).ToArray();
                        if(!source.SequenceEqual(actual))throw new Exception("matrix coefficient");
                        double min=source.Where(x=>x.V<0).Sum(x=>x.V),max=source.Where(x=>x.V>0).Sum(x=>x.V);
                        double l=rl[j]< -1e20?Math.Min(min,ru[j]):rl[j],u=ru[j]>1e20?Math.Max(max,rl[j]):ru[j];
                        if(l!=row.L||u!=row.U)throw new Exception("row bound");
                        checkedCoefficients+=actual.Length;
                    }
                    verified=true;
                }
                Ok(Native.Highs_run(h));int status=Native.Highs_getModelStatus(h);Ok(Native.Highs_getIntInfoValue(h,"primal_solution_status",out int primal));
                double? objective=null;bool witness=false;
                if(primal==2){
                    double[] x=new double[n];Ok(Native.Highs_getSolution(h,x,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero));
                    double err=0,objectiveCalc=off;
                    for(int i=0;i<n;i++){if(!double.IsFinite(x[i]))throw new Exception("nonfinite");err=Math.Max(err,Math.Max(cl[i]-x[i],x[i]-cu[i]));if(types[i]==1)err=Math.Max(err,Math.Abs(x[i]-Math.Round(x[i])));objectiveCalc+=c[i]*x[i];}
                    for(int j=0;j<r;j++){double sum=0;for(int k=start[j];k<start[j+1];k++)sum+=val[k]*x[idx[k]];err=Math.Max(err,Math.Max(rl[j]-sum,sum-ru[j]));}
                    Ok(Native.Highs_getDoubleInfoValue(h,"objective_function_value",out double reported));
                    if(err>1e-5||Math.Abs(reported-objectiveCalc)>1e-5*Math.Max(1,Math.Abs(reported)))throw new Exception("native primal");
                    if(m!=null&&Math.Abs(reported-WeightedReduction.VerifyWitness(m,x.Select(v=>(int)Math.Round(v)).ToArray()))>1e-6)throw new Exception("exact source primal");
                    objective=reported;witness=true;
                }
                WeightedDecision? original=null,candidate=null;
                if(m!=null){
                    string d=WeightedReduction.Digest(m);original=WeightedReduction.Decide(m,false);WeightedVerifier.Replay(m,d,original);
                    candidate=WeightedReduction.Decide(m,true);WeightedVerifier.Replay(m,d,candidate);
                    if(original.Result.Status=="Optimal"&&candidate.Result.Status=="Optimal"&&original.Result.Value!=candidate.Result.Value||status==7&&candidate.Result.Status=="Optimal"&&Math.Abs(objective!.Value-candidate.Result.Value!.Value)>1e-6)throw new Exception("objective mismatch");
                    if(candidate.InfeasibleProved&&(original.Result.Bits!=null||witness))throw new Exception("false infeasibility");
                }
                Console.WriteLine(JsonSerializer.Serialize(new{name,sha,n,rows=r,nonzeros=nz,admission=verified?"supported":"unsupported",reason,checkedNames,checkedCoefficients,nativeStatus=status,nativePrimal=primal,objective,witness,originalStatus=original?.Result.Status,originalValue=original?.Result.Value,candidateStatus=candidate?.Result.Status,candidateValue=candidate?.Result.Value,formalInfeasible=candidate?.InfeasibleProved,dropped=candidate?.Proof?.Rows.Count(x=>x.Drop),gcdRows=candidate?.Proof?.Rows.Count(x=>x.Gcd>1)}));
            }finally{Native.Highs_destroy(h);}
        }
    }
    private static class Native
    {
        const string Lib="highs.so";
        [DllImport(Lib)]internal static extern int Highs_getColName(IntPtr h,int i,System.Text.StringBuilder name);
        [DllImport(Lib)]internal static extern int Highs_getRowName(IntPtr h,int i,System.Text.StringBuilder name);
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
