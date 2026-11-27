param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Thumbprint)
$ErrorActionPreference = 'Stop'
if ($Thumbprint -notmatch '^[A-Fa-f0-9]{40}$' -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Invalid signature policy or missing artifact.' }
$signature = Get-AuthenticodeSignature -LiteralPath $Path
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint) { throw "Invalid Authenticode signature: $Path" }
