# 当前框架全量测试（2026-10-18 用户口径：旧框架用例不修，从当前测试记录里剔除）
#
# 背景：docs/全量红名单分类-2026-10-16.md 的「桶 A」是一批钉着**已废机制**的旧框架用例
#（旧轮次续解模型 / 源包→其余物 / 单层路 / 跨盘回收站档 / 冲突询问档 …）。
# 用户 2026-10-18 明确：这些「不用管」，从当前测试记录里消掉；不删除用例源码（留作历史证据），
# 只在本脚本的运行记录里排除。新框架用例（含 30 份真机批）仍按 ①–⑤ 验收。
#
# 用法：pwsh -NoProfile -File scripts/test-current.ps1 [-NoBuild]
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'

# 桶 A：旧框架用例（类名，按 docs/全量红名单分类-2026-10-16.md）
$legacyClasses = @(
    'InnerLayerContinuationTests',            # 旧轮次续解模型（一轮=一层）
    'ChainManifestCompletenessTests',          # 旧「源包按设置被搬走/删除」
    'SpecialExtractionPipelineTests',          # 已废的单层解压路
    'ChainLayerReclaimTests',                  # 旧轮数/旧回收文案
    'SiblingFolderVolumeGatherTests',          # 旧「源包进其余物」
    'ChainSpaceReclaimTests',                  # 旧轮次模型（Rounds==layers）
    'ChainRestHandlingSettingTests',           # 旧「源包+过程物一起进其余物」
    'OneClickOptionsPanelTests',               # 已删的「放入其余物+彻底删除」档
    'WinRarFallbackSingleLayerEndToEndTests',  # 已废的单层路
    'DisguiseCoverageTests',                   # 整组进其余物 / 询问档
    'SplitStreamEchoTests',                    # 完整包后源包移入其余物
    'ItemSept27FixTests',                      # 其余物范围含源包
    'SpaceTightModeTests',                     # 源包与其余物两行按旧范围
    'ConflictAskTests'                         # 已废的同名冲突询问档
)

$filter = ($legacyClasses | ForEach-Object { "FullyQualifiedName!~$_" }) -join '&'

$arguments = @(
    'test',
    'tests/ArchiveFixer.Tests/ArchiveFixer.Tests.csproj',
    '--nologo',
    '--filter',
    $filter
)

if ($NoBuild) {
    $arguments += '--no-build'
}

Write-Host "当前框架全量测试：排除 $($legacyClasses.Count) 个旧框架类（桶 A）"
Write-Host ("排除：" + ($legacyClasses -join '、'))
Write-Host ''

& dotnet @arguments
exit $LASTEXITCODE
