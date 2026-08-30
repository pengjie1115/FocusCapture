# FocusCapture 一键打包脚本
# 双击运行（经桌面 .bat 调用）。自动：同步源码 -> 构建单文件 exe -> 拷贝到桌面。
# 原理：在项目外固定副本目录构建，避开工作区 obj 缓存被 IDE 占用的问题。
$ErrorActionPreference = 'Stop'

$src = 'D:/桌面文件存放位置260518/项目代码/专注力工具 - 上传git - 260726\FocusCapture'
$dst = 'D:/项目代码/FocusCapture-release-build'
$desktop = [Environment]::GetFolderPath('Desktop')
$exeName = 'FocusCapture.exe'

Write-Host '[1/4] 同步最新源码到发布副本 ...'
if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst | Out-Null }
robocopy $src $dst /MIR /XD obj bin asr_venv .git .workbuddy /XF *.user /NFL /NDL /NJH /NJS /NP | Out-Null
if (-not (Test-Path (Join-Path $dst 'FocusCapture.csproj'))) {
    Write-Host '错误: 源码同步失败（缺少 csproj）' -ForegroundColor Red
    exit 1
}

Write-Host '[2/4] 清理副本旧缓存并开始构建（约 5-7 分钟，请勿关闭窗口）...'
Remove-Item -Recurse -Force (Join-Path $dst 'obj'), (Join-Path $dst 'bin'), (Join-Path $dst 'publish') -ErrorAction SilentlyContinue
Push-Location $dst
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
$code = $LASTEXITCODE
Pop-Location
if ($code -ne 0) { Write-Host '构建失败，详见上方错误' -ForegroundColor Red; exit 1 }

Write-Host '[3/4] 拷贝成品到桌面 ...'
$exe = Join-Path $dst ('publish\' + $exeName)
if (-not (Test-Path $exe)) { Write-Host '错误: 未找到构建产物' -ForegroundColor Red; exit 1 }
Copy-Item $exe (Join-Path $desktop $exeName) -Force

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ('[4/4] 完成！桌面已生成 ' + $exeName + '（' + $size + ' MB，单文件，双击即用）') -ForegroundColor Green
Write-Host ''
Write-Host '注意: 语音模型约 740MB，对方首次使用语音功能时自动联网下载。' -ForegroundColor Yellow
