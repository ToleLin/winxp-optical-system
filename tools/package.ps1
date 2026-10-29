# 在 Windows 上產生免安裝版：dist\XPOptical\（整個資料夾即可在 Win10/11 使用）與 dist\XPOptical-<版本>-win-x64.zip
# 與 tools/package.sh 相同的步驟；需要：.NET 10 SDK、Python 3.14 以上（或另有 zstd 指令）
# 用法：powershell -ExecutionPolicy Bypass -File tools\package.ps1
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Version = ([xml](Get-Content -Raw -Encoding UTF8 "$Root\Directory.Build.props")).Project.PropertyGroup.Version
$Dist = Join-Path $Root 'dist'
$Out = Join-Path $Dist 'XPOptical'
$Cache = if ($env:XPOPTICAL_CACHE) { $env:XPOPTICAL_CACHE } else { Join-Path $env:LOCALAPPDATA 'xpoptical-cache' }
$UsbDkUrl = 'https://github.com/daynix/UsbDk/releases/download/v1.00-22/UsbDk_1.0.22_x64.msi'
$UsbDkSha256 = '91f6f695e1e13c656024e6d3b55620bf08d8835ef05ee0496935ba6bb62466a5'
$UsbDkLicenseUrl = 'https://raw.githubusercontent.com/daynix/UsbDk/master/LICENSE'

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe 失敗（結束代碼 $LASTEXITCODE）" }
}

$Python = @('python', 'py', 'python3') | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $Python) { throw '找不到 Python 3' }

if (Test-Path $Dist) { Remove-Item -Recurse -Force $Dist }
New-Item -ItemType Directory -Force "$Out\drivers", "$Out\profiles", $Cache | Out-Null

Write-Host '== 1/4 編譯主程式（自帶執行環境的單一 exe）=='
Invoke-Checked 'dotnet' @('publish', "$Root\src\XPOptical.App", '-c', 'Release', '-o', "$Dist\publish", '-p:DebugType=None', "-p:Version=$Version")
Copy-Item "$Dist\publish\XPOptical.exe" $Out

Write-Host '== 2/4 組裝執行引擎 =='
Invoke-Checked $Python @("$Root\tools\build-engine.py", '--out', "$Out\engine", '--cache', $Cache)

Write-Host '== 3/4 USB 驅動 =='
$Msi = Join-Path $Cache (Split-Path -Leaf $UsbDkUrl)
if (-not (Test-Path $Msi) -or (Get-FileHash -Algorithm SHA256 $Msi).Hash -ne $UsbDkSha256) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $UsbDkUrl -OutFile $Msi
    if ((Get-FileHash -Algorithm SHA256 $Msi).Hash -ne $UsbDkSha256) { throw 'UsbDk 安裝檔的 SHA256 不符' }
}
Copy-Item $Msi "$Out\drivers\"
Invoke-WebRequest -UseBasicParsing -Uri $UsbDkLicenseUrl -OutFile "$Out\drivers\UsbDk-LICENSE.txt"

Write-Host '== 4/4 設定檔、說明與壓縮 =='
Copy-Item "$Root\profiles\ihr320.json" "$Out\profiles\"
Copy-Item "$Root\packaging\使用說明.txt", "$Root\packaging\THIRD-PARTY-NOTICES.txt" $Out
Copy-Item "$Root\LICENSE" "$Out\LICENSE.txt"
$Zip = Join-Path $Dist "XPOptical-$Version-win-x64.zip"
Invoke-Checked $Python @('-m', 'zipfile', '-c', $Zip, $Out)
Remove-Item -Recurse -Force "$Dist\publish"

Write-Host "完成：$Out"
'{0:N0} MB  {1}' -f ((Get-ChildItem -Recurse -File $Out | Measure-Object Length -Sum).Sum / 1MB), $Out
'{0:N0} MB  {1}' -f ((Get-Item $Zip).Length / 1MB), $Zip
