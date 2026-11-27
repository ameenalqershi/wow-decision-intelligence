using System.Diagnostics;

// Trusted executable. It does not run an adapter before the host attaches this
// process to the enforced OS scope and sends the one-time startup gate.
if(args.Length<1||!Path.IsPathFullyQualified(args[0]))return 125;
if(await Console.In.ReadLineAsync()!="WOWDI_START")return 125;
var info=new ProcessStartInfo(args[0]){UseShellExecute=false,CreateNoWindow=true};
foreach(var arg in args.Skip(1))info.ArgumentList.Add(arg);
using var process=Process.Start(info);
if(process is null)return 125;
await process.WaitForExitAsync();
return process.ExitCode;
