if(args.Length<1||args.Length>2)return 2;
switch(args[0])
{
    case "success": Console.Write("candidate");return 0;
    case "empty":return 0;
    case "crash": return 17;
    case "slow":
        if(args.Length==2)File.WriteAllText(args[1],Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Task.Delay(TimeSpan.FromMinutes(1));return 0;
    case "overflow":
        for(var i=0;i<1024;i++)Console.Write(new string('a',4096));
        return 0;
    case "memory":
        try
        {
            var blocks=new List<byte[]>();
            for(var i=0;i<384;i++)
            {
                var block=new byte[1024*1024];
                for(var j=0;j<block.Length;j+=4096)block[j]=1;
                blocks.Add(block);
            }
            GC.KeepAlive(blocks);
            Console.Write("over-budget");
            return 0;
        }
        catch(OutOfMemoryException){return 42;}
    case "cpu":
        var until=System.Diagnostics.Stopwatch.GetTimestamp()+2*System.Diagnostics.Stopwatch.Frequency;
        var threads=Enumerable.Range(0,4).Select(_=>new Thread(()=>
        {
            long x=0;
            while(System.Diagnostics.Stopwatch.GetTimestamp()<until)x=unchecked(x*31+1);
            GC.KeepAlive(x);
        })).ToArray();
        foreach(var thread in threads)thread.Start();
        foreach(var thread in threads)thread.Join();
        Console.Write("candidate");
        return 0;
    case "secret":
        Console.Write(Environment.GetEnvironmentVariable("WOWDI_HOST_SECRET")==null?"absent":"present");
        return 0;
    default:return 2;
}
