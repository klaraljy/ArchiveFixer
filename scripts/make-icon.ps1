<#
.SYNOPSIS
    重新生成 ArchiveFixer 的应用图标（src/ArchiveFixer/Assets/ArchiveFixer.ico）。

.DESCRIPTION
    画法与编码都在 scripts/icon-gen/Program.cs 里（那个小工具**不在 ArchiveFixer.slnx 里**，
    主程序的构建 / 测试 / format 都不会碰它）。改图标 = 改那段绘制代码 → 跑这个脚本。

    生成的 .ico 有 7 档尺寸（16/24/32/48/64/128/256）：≤128 用经典 DIB、256 用 PNG。

.EXAMPLE
    pwsh scripts/make-icon.ps1
    pwsh scripts/make-icon.ps1 -Preview E:\tmp\icon-preview.png
#>
[CmdletBinding()]
param(
    # 输出 .ico 的路径（默认 src\ArchiveFixer\Assets\ArchiveFixer.ico）
    [string]$Output,

    # 顺带画一张预览图（上排真实像素、下排 16/32 放大）——只为生成完自己看一眼
    [string]$Preview
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $Output) {
    $Output = Join-Path $repoRoot 'src\ArchiveFixer\Assets\ArchiveFixer.ico'
}

$project = Join-Path $PSScriptRoot 'icon-gen\icon-gen.csproj'

$arguments = @('run', '--project', $project, '-c', 'Release', '--', $Output)

if ($Preview) {
    $arguments += $Preview
}

& dotnet @arguments

if ($LASTEXITCODE -ne 0) {
    throw "图标生成失败（dotnet run 退出码 $LASTEXITCODE）"
}
