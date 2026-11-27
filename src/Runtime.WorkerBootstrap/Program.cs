using System.Diagnostics;

// Trusted executable. It does not run an adapter before the host attaches this
// process to the enforced OS scope and sends the one-time startup gate.
if(args.Length<1||!Path.IsPathFullyQualified(args[0]))return 125;
if(await Console.In.ReadLineAsync()!="WOWDI_START")return 125;
var info=new ProcessStartInfo(args[0])
{
    UseShellExecute=false,CreateNoWindow=true,
    RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true
};
foreach(var arg in args.Skip(1))info.ArgumentList.Add(arg);
using var process=Process.Start(info);
if(process is null)return 125;
process.StandardInput.Close();
// Explicit bounded-buffer streaming works even where the OS does not inherit
// standard handles (Windows CreateNoWindow). The supervising parent enforces limits.
using var output=Console.OpenStandardOutput();
using var error=Console.OpenStandardError();
await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(output,8192),
    process.StandardError.BaseStream.CopyToAsync(error,8192),process.WaitForExitAsync());
await output.FlushAsync();
await error.FlushAsync();
return process.ExitCode;
