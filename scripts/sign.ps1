[CmdletBinding(DefaultParameterSetName = 'Thumbprint')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Thumbprint')]
    [string]$CertificateThumbprint,

    [Parameter(Mandatory = $true, ParameterSetName = 'Pfx')]
    [string]$PfxPath,

    [Parameter(ParameterSetName = 'Pfx')]
    [string]$PfxPassword = $env:ONECLICKCLOSE_PFX_PASSWORD,

    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    [string[]]$Files
)

$ErrorActionPreference = 'Stop'

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $roots = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $roots.Add((Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $roots.Add((Join-Path $env:ProgramFiles 'Windows Kits\10\bin'))
    }
    foreach ($root in $roots) {
        if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path -LiteralPath $root)) {
            continue
        }
        $candidate = Get-ChildItem -LiteralPath $root -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }
    return $null
}

$signTool = Find-SignTool
if ([string]::IsNullOrWhiteSpace($signTool)) {
    throw '找不到 signtool.exe。请安装 Windows SDK，或把 signtool 加入 PATH。'
}

if (-not $Files -or $Files.Count -eq 0) {
    $defaultFiles = New-Object System.Collections.Generic.List[string]
    $appExe = Join-Path $PSScriptRoot '..\dist\app\OneClickClose.exe'
    if (Test-Path -LiteralPath $appExe) {
        $defaultFiles.Add((Resolve-Path -LiteralPath $appExe).Path)
    }
    $installerDir = Join-Path $PSScriptRoot '..\dist\installer'
    if (Test-Path -LiteralPath $installerDir) {
        Get-ChildItem -LiteralPath $installerDir -Filter '*.exe' -File |
            Sort-Object Name |
            ForEach-Object { $defaultFiles.Add($_.FullName) }
    }
    $Files = $defaultFiles.ToArray()
}

if ($Files.Count -eq 0) {
    throw '没有找到要签名的文件。请先 publish 并编译安装包，或用 -Files 指定路径。'
}

$signArgs = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
if ($PSCmdlet.ParameterSetName -eq 'Thumbprint') {
    $signArgs += @('/sha1', $CertificateThumbprint, '/s', 'My')
} else {
    $resolvedPfx = (Resolve-Path -LiteralPath $PfxPath).Path
    $signArgs += @('/f', $resolvedPfx)
    if (-not [string]::IsNullOrWhiteSpace($PfxPassword)) {
        $signArgs += @('/p', $PfxPassword)
    }
}

foreach ($file in $Files) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "文件不存在：$file"
    }
    Write-Host "签名：$file"
    & $signTool @signArgs $file
    if ($LASTEXITCODE -ne 0) {
        throw "签名失败：$file（退出码 $LASTEXITCODE）"
    }
}

foreach ($file in $Files) {
    Write-Host "验证：$file"
    & $signTool verify /pa /v $file
    if ($LASTEXITCODE -ne 0) {
        throw "签名验证失败：$file（退出码 $LASTEXITCODE）"
    }
}

Write-Host '签名与验证完成。'
