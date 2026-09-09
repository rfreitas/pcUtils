param(
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory)] [string] $ExpectedSha256
)

$sha256 = [System.Security.Cryptography.SHA256]::Create()
$stream = [System.IO.File]::OpenRead($Path)
try {
    $hashBytes = $sha256.ComputeHash($stream)
} finally {
    $stream.Dispose()
    $sha256.Dispose()
}
$actual = ([System.BitConverter]::ToString($hashBytes) -replace '-', '').ToLowerInvariant()
if ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
    Remove-Item -Path $Path -Force
    Write-Error "Hash mismatch for '$Path': expected $ExpectedSha256, got $actual. Deleted the downloaded file."
    exit 1
}
