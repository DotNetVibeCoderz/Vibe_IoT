# Builds the Rust native libraries for Windows targets into artifacts/native/{rid}/.
#   ./build/build-native.ps1                                  # x64
#   ./build/build-native.ps1 -Targets x86_64-pc-windows-msvc,aarch64-pc-windows-msvc
param([string[]]$Targets = @('x86_64-pc-windows-msvc'))
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$map = @{ 'x86_64-pc-windows-msvc' = 'win-x64'; 'aarch64-pc-windows-msvc' = 'win-arm64'; 'i686-pc-windows-msvc' = 'win-x86' }
Push-Location (Join-Path $root 'rust')
try {
    foreach ($t in $Targets) {
        $rid = $map[$t]
        Write-Host "==> $t ($rid)"
        rustup target add $t | Out-Null
        cargo build --release -p iotcom-modbus-native --target $t
        if ($LASTEXITCODE -ne 0) { throw "cargo build failed for $t (ARM64 needs the MSVC ARM64 build tools)" }
        $dest = Join-Path $root "artifacts/native/$rid"
        New-Item -ItemType Directory -Force $dest | Out-Null
        Copy-Item "target/$t/release/iotcom_modbus.dll" $dest
    }
}
finally { Pop-Location }
Get-ChildItem -Recurse (Join-Path $root 'artifacts/native')
