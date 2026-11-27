# Qualification of the extracted portable distribution, not signing or MSI.
param(
    [Parameter(Mandatory=$true)][string]$Published,
    [Parameter(Mandatory=$true)][string]$Installed,
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][string]$Evidence
)
$ErrorActionPreference = 'Stop'
function Snapshot([string]$root) {
    $root = (Resolve-Path -LiteralPath $root).Path
    $entries = [System.Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
        if ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "Unexpected linked file in portable distribution: $($file.Name)"
        }
        $relative = [System.IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
        $entries.Add($relative, [pscustomobject]@{
            path=$relative; length=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    return $entries
}
$source = Snapshot $Published
$destination = Snapshot $Installed
if ($source.Count -eq 0 -or $source.Count -ne $destination.Count) { throw 'File count mismatch after extraction.' }
foreach ($entry in $source.GetEnumerator()) {
    if (-not $destination.ContainsKey($entry.Key)) { throw "Missing installed file: $($entry.Key)" }
    $actual = $destination[$entry.Key]
    if ($actual.length -ne $entry.Value.length -or $actual.sha256 -cne $entry.Value.sha256) {
        throw "Installed file differs: $($entry.Key)"
    }
}
[pscustomobject]@{
    format='portable-extraction-v1'
    packageSha256=(Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant()
    files=$source.Count
    totalBytes=($source.Values | Measure-Object -Property length -Sum).Sum
    verifiedFiles=@($destination.Values)
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Evidence -Encoding utf8
Write-Host ("Portable extraction verified: " + $source.Count + " files")
