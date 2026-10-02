# MoonMovie 安装脚本：信任随包附带的签名证书，然后安装 / 更新 MoonMovie。
# 由「安装 MoonMovie.cmd」调用；需要管理员权限（导入证书到「受信任的人」）。

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

try {
    $cer = Get-ChildItem $here -Filter '*.cer' | Select-Object -First 1
    $msix = Get-ChildItem $here -Filter '*.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $cer -or -not $msix) { throw '安装包不完整：需要和 .cer、.msix 文件放在同一个文件夹里。' }

    Write-Host '正在信任 MoonMovie 的签名证书…'
    Import-Certificate -FilePath $cer.FullName -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

    Write-Host '正在安装 MoonMovie…'
    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown

    Write-Host ''
    Write-Host '安装完成：在开始菜单里找「MoonMovie」。' -ForegroundColor Green
}
catch {
    Write-Host ''
    Write-Host "安装失败：$($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ''
Read-Host '按回车键关闭'
