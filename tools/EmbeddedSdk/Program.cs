using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

const string version = "1.0.0"; // Local validation version, not a public release.
string[] projects = ["Contracts", "Udir", "Dml.Syntax", "Dml.Binding", "Dml.Semantics", "ModelImport", "ModelingSdk"];

if (args.Length != 3 || args[0] is not ("prepare" or "verify"))
{
    Console.Error.WriteLine("Usage: pack-tool prepare|verify <repository> <new-output-directory>");
    return 2;
}
var root = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
var feed = Path.Combine(output, "feed");
var consumer = Path.Combine(output, "consumer");
var cache = Path.Combine(output, "packages");
var sdk = Path.Combine(root, "src", "ModelingSdk");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
var ct = timeout.Token;

try
{
if (args[0] == "prepare")
{
    if (Directory.Exists(output) || File.Exists(output))
        throw new InvalidOperationException("Use a new output directory; existing artifacts are never overwritten.");
    Directory.CreateDirectory(feed);
    Directory.CreateDirectory(consumer);
    var project = Path.Combine(sdk, "WowDi.ModelingSdk.csproj");
    await Run(root, "restore", project, "--locked-mode", "--nologo");
    foreach (var name in projects)
    {
        await Run(root, "pack", Path.Combine(root, "src", name, $"WowDi.{name}.csproj"),
            "-c", "Release", "--no-restore", "-o", feed, "-p:PackageVersion=" + version, "--nologo");
        if (!File.Exists(Path.Combine(feed, $"WowDi.{name}.{version}.nupkg")))
            throw new InvalidOperationException("Missing internal package: " + name);
    }
    // Copy only the actual locked external packages, and verify the committed SHA-512.
    using var lockDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sdk, "packages.lock.json")));
    using var assets = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sdk, "obj", "project.assets.json")));
    var folders = assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(p => p.Name).ToArray();
    var locked = lockDoc.RootElement.GetProperty("dependencies").GetProperty("net10.0");
    var externalHashes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in locked.EnumerateObject())
    {
        if (p.Value.GetProperty("type").GetString() == "Project") continue;
        var name = p.Name.ToLowerInvariant();
        var ver = p.Value.GetProperty("resolved").GetString()!;
        var filename = name + "." + ver + ".nupkg";
        var source = folders.Select(f => Path.Combine(f, name, ver, filename)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Locked package missing: " + p.Name);
        // NuGet signed content hash excludes signing data; raw archive SHA-256 is tracked separately.
        var verification = await Run(root,"nuget","verify",source,"--all");
        var expected = p.Value.GetProperty("contentHash").GetString()!;
        if(!verification.Split('\n').Any(line=>line.Trim()=="Content hash: "+expected))
            throw new InvalidOperationException("Locked package content hash mismatch: "+p.Name);
        externalHashes.Add(p.Name,expected);
        File.Copy(source, Path.Combine(feed, filename));
    }
    foreach (var name in new[] { "WowDi.EmbeddedSdk.Consumer.csproj", "Program.cs" })
        File.Copy(Path.Combine(root, "samples", "EmbeddedSdk", name), Path.Combine(consumer, name));
    new XDocument(new XElement("configuration",
        new XElement("packageSources", new XElement("clear"), new XElement("add", new XAttribute("key", "local"), new XAttribute("value", feed))),
        new XElement("fallbackPackageFolders", new XElement("clear"))))
        .Save(Path.Combine(consumer, "NuGet.Config"));
    var manifest = Directory.GetFiles(feed, "*.nupkg").Order(StringComparer.Ordinal)
        .ToDictionary(p => Path.GetFileName(p)!, p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))), StringComparer.Ordinal);
    File.WriteAllText(Path.Combine(output, "package-sha256.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    File.WriteAllText(Path.Combine(output,"external-content-lock.json"),JsonSerializer.Serialize(externalHashes));
    Console.WriteLine($"Prepared {manifest.Count} local packages; no public publication.");
}
else
{
    if (Directory.Exists(cache)) throw new InvalidOperationException("Verification requires a fresh package cache.");
    var manifest = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllBytes(Path.Combine(output,"package-sha256.json")))!;
    var files = Directory.GetFiles(feed,"*.nupkg");
    if (files.Length != manifest.Count || files.Any(p => !manifest.TryGetValue(Path.GetFileName(p),out var sha) ||
        sha != Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))))
        throw new InvalidOperationException("Local feed content differs from the prepared manifest.");
    var project = Path.Combine(consumer,"WowDi.EmbeddedSdk.Consumer.csproj");
    var config = Path.Combine(consumer,"NuGet.Config");
    await Run(consumer,"restore",project,"--configfile",config,"--source",feed,"--packages",cache,"--no-http-cache","--nologo");
    await Run(consumer,"restore",project,"--configfile",config,"--source",feed,"--packages",cache,"--locked-mode","--no-http-cache","--nologo");
    using var resolved = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(consumer,"packages.lock.json")));
    var entries = resolved.RootElement.GetProperty("dependencies").GetProperty("net10.0");
    if(entries.EnumerateObject().Any(p=>p.Value.GetProperty("type").GetString()=="Project") ||
       entries.EnumerateObject().Count()!=manifest.Count)
        throw new InvalidOperationException("Consumer has unexpected project/package dependencies.");
    var externalHashes = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllBytes(Path.Combine(output,"external-content-lock.json")))!;
    foreach(var p in entries.EnumerateObject())
    {
        var ver=p.Value.GetProperty("resolved").GetString()!;
        var name=p.Name.ToLowerInvariant();
        var filename=files.Single(f=>Path.GetFileName(f).Equals(name+"."+ver+".nupkg",StringComparison.OrdinalIgnoreCase));
        var expected=externalHashes.TryGetValue(p.Name,out var signedHash)?signedHash:Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(filename)));
        if(expected!=p.Value.GetProperty("contentHash").GetString())
            throw new InvalidOperationException("Consumer resolved different package bytes.");
    }
    await Run(consumer,"build",project,"-c","Release","--no-restore","--nologo");
    var dll=Path.Combine(consumer,"bin","Release","net10.0","WowDi.EmbeddedSdk.Consumer.dll");
    var first=await Run(consumer,dll);
    var second=await Run(consumer,dll);
    if(first!=second||!first.Contains("MANAGED_ADMISSION_ONLY",StringComparison.Ordinal))
        throw new InvalidOperationException("Consumer outputs were not deterministic.");
    Console.WriteLine("Verified package-only restore, build, deterministic managed admission, version diagnostic and cancellation.");
}
return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Package validation timed out or was cancelled.");
    return 130;
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

async Task<string> Run(string directory,params string[] arguments)
{
    ct.ThrowIfCancellationRequested();
    var start=new ProcessStartInfo
    {
        FileName=Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet",
        WorkingDirectory=directory,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true
    };
    start.Environment["DOTNET_CLI_UI_LANGUAGE"]="en-US";
    foreach(var a in arguments)start.ArgumentList.Add(a);
    using var process=Process.Start(start)??throw new InvalidOperationException("Could not start dotnet.");
    var stdout=process.StandardOutput.ReadToEndAsync(ct);
    var stderr=process.StandardError.ReadToEndAsync(ct);
    try
    {
        await process.WaitForExitAsync(ct);
        var result=await stdout;var errors=await stderr;
        Console.Write(result);Console.Error.Write(errors);
        if(process.ExitCode!=0)throw new InvalidOperationException("dotnet command failed: "+arguments[0]);
        return result;
    }
    finally
    {
        if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}
    }
}
