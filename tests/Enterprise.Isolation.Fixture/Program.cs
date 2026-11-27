using System.Net.Sockets;
using System.Text;

if(args.Length!=0)return 2;
var input=File.Exists("/input/model.bin")?File.ReadAllText("/input/model.bin"):"";
if(input=="delay"){await Task.Delay(TimeSpan.FromMinutes(1));return 0;}
if(input=="memory")
{
    try
    {
        var blocks=new List<byte[]>();
        for(int i=0;i<384;i++)
        {
            var block=new byte[1024*1024];
            for(int j=0;j<block.Length;j+=4096)block[j]=1;
            blocks.Add(block);
        }
        GC.KeepAlive(blocks);
        Console.Write("over-budget");return 0;
    }
    catch(OutOfMemoryException){return 42;}
}
if(!input.StartsWith("probe:",StringComparison.Ordinal))return 3;
var fields=input.Split(':',3);
if(fields.Length!=3||!int.TryParse(fields[1],out var port))return 4;
var canReadHost=File.Exists(fields[2]);
var canWriteInput=TryWrite("/input/model.bin");
var canWritePlugin=TryWrite("/plugin/fixture-write");
var canWriteScratch=TryWrite("/scratch/fixture-write");
var inherited=Environment.GetEnvironmentVariable("WOWDI_TEST_HOST_MARKER")!=null;
var connected=false;
using(var client=new TcpClient())
using(var timeout=new CancellationTokenSource(TimeSpan.FromMilliseconds(750)))
{
    try{await client.ConnectAsync("127.0.0.1",port,timeout.Token);connected=true;}
    catch(SocketException){}
    catch(OperationCanceledException){}
}
Console.Write(System.Text.Json.JsonSerializer.Serialize(new{canReadHost,canWriteInput,canWritePlugin,canWriteScratch,inherited,connected}));
return 0;
static bool TryWrite(string path)
{
    try{File.WriteAllText(path,"synthetic");return true;}
    catch(IOException){return false;}
    catch(UnauthorizedAccessException){return false;}
}
