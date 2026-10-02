<#
.SYNOPSIS
  Creates the self-signed code-signing certificate MoonMovie packages are signed with.

.DESCRIPTION
  Writes build\cert\MoonMovie.pfx (private key, keep it secret; gitignored) and build\cert\MoonMovie.cer (public
  part, shipped next to the .msix so friends can trust it). Nothing is added to any Windows certificate store.
  The subject must match Publisher in src\MoonMovie\Package.appxmanifest.

.EXAMPLE
  .\build\new-cert.ps1 -Password 'something-long'
#>
param(
    [Parameter(Mandatory)] [string] $Password,
    [string] $Subject = 'CN=Ylfmoonn',
    [int] $Years = 10
)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot 'cert'
New-Item -ItemType Directory -Force $dir | Out-Null
$pfx = Join-Path $dir 'MoonMovie.pfx'
$cer = Join-Path $dir 'MoonMovie.cer'
if (Test-Path $pfx) { throw "$pfx already exists. Delete it first to make a new certificate (installed copies will then need the new .cer)." }

$rsa = [System.Security.Cryptography.RSA]::Create(3072)
$request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    $Subject, $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

# Code signing only; not a CA.
$eku = [System.Security.Cryptography.OidCollection]::new()
$eku.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
$request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $false))
$request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
$request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false))

$now = [DateTimeOffset]::Now
$cert = $request.CreateSelfSigned($now.AddDays(-1), $now.AddYears($Years))

[IO.File]::WriteAllBytes($pfx, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $Password))
[IO.File]::WriteAllBytes($cer, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

"Created $pfx"
"Created $cer"
"Thumbprint $($cert.Thumbprint), valid until $($cert.NotAfter.ToString('yyyy-MM-dd'))"
"For GitHub Actions, add these repository secrets:"
"  SIGNING_PFX_BASE64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes('$pfx'))"
"  SIGNING_PFX_PASSWORD = <the password>"
