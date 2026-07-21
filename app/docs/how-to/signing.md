# How to Sign a Windows Release

Codex Local Retrieval currently ships as an unsigned portable ZIP. Windows can run it, but SmartScreen may warn on first launch.

## Trusted Public Signing

Use a trusted Authenticode certificate or Microsoft Trusted Signing. This is the only release path that improves trust for public users.

Expected flow:

1. Publish the Windows x64 build.
2. Sign the top-level `Codex Local Retrieval.exe`, `app/CodexLocalRetrieval.Native.exe`, and any required binaries with the trusted certificate.
3. Verify the signature with `Get-AuthenticodeSignature`.
4. Compress the signed publish directory.
5. Upload the signed ZIP as a release asset.

Verification:

```powershell
Get-AuthenticodeSignature ".\Codex Local Retrieval.exe"
Get-AuthenticodeSignature .\app\CodexLocalRetrieval.Native.exe
```

Expected result: `Status` is `Valid` on a machine that trusts the certificate chain.

## Self-Signed Development Signing

A self-signed certificate is useful only for local testing. It does not remove public SmartScreen warnings unless users explicitly trust the certificate.

```powershell
$cert = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject "CN=Codex Local Retrieval Dev" `
  -CertStoreLocation Cert:\CurrentUser\My

Set-AuthenticodeSignature `
  -FilePath ".\Codex Local Retrieval.exe" `
  -Certificate $cert
```

Do not present a self-signed build as a trusted public release.
