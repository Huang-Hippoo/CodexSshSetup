param([switch]$SkipTests, [switch]$SkipSmoke)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$localDotnet = Join-Path $projectRoot 'work\dotnet-sdk\dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet).Source }
$solution = Join-Path $projectRoot 'CodexSshSetup.sln'
$appProject = Join-Path $projectRoot 'src\CodexSshSetup.App\CodexSshSetup.App.csproj'
$publishRoot = Join-Path $projectRoot 'artifacts\publish'

if (-not $SkipTests) {
    & $dotnetExe test $solution -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

$resolvedArtifacts = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$resolvedPublish = [System.IO.Path]::GetFullPath($publishRoot)
if (-not $resolvedPublish.StartsWith($resolvedArtifacts, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unsafe publish path.'
}
if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
New-Item -ItemType Directory -Force $publishRoot | Out-Null

& $dotnetExe publish $appProject -c Release -r win-x64 --self-contained false -o (Join-Path $publishRoot 'framework-dependent')
if ($LASTEXITCODE -ne 0) { throw 'Framework-dependent publish failed.' }

& $dotnetExe publish $appProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o (Join-Path $publishRoot 'self-contained')
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }

& $dotnetExe publish $appProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o (Join-Path $publishRoot 'single-file')
if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }

if (-not $SkipSmoke) {
    $smokeExe = Join-Path $publishRoot 'single-file\CodexSshSetup.exe'
    $smokeProcess = Start-Process -FilePath $smokeExe -PassThru
    try {
        $deadline = (Get-Date).AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 250
            $smokeProcess.Refresh()
        } while (-not $smokeProcess.HasExited -and $smokeProcess.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)

        if ($smokeProcess.HasExited) { throw "Published app exited during smoke test with code $($smokeProcess.ExitCode)." }
        if ($smokeProcess.MainWindowHandle -eq 0 -or $smokeProcess.MainWindowTitle -ne 'Codex SSH Setup') {
            throw 'Published app did not create its main window.'
        }

        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $windowElement = [System.Windows.Automation.AutomationElement]::FromHandle($smokeProcess.MainWindowHandle)
        $configureButton = $null
        $descendants = $windowElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $descendants) {
            if ($element.Current.AutomationId -eq 'ConfigureButton') { $configureButton = $element; break }
        }
        if ($null -eq $configureButton -or $configureButton.Current.IsOffscreen) {
            throw 'Configure button is missing or off-screen.'
        }
        $windowBounds = $windowElement.Current.BoundingRectangle
        $buttonBounds = $configureButton.Current.BoundingRectangle
        if ($buttonBounds.Left -lt $windowBounds.Left -or $buttonBounds.Right -gt $windowBounds.Right -or
            $buttonBounds.Top -lt $windowBounds.Top -or $buttonBounds.Bottom -gt $windowBounds.Bottom) {
            throw 'Configure button is outside the main window bounds.'
        }
    }
    finally {
        if (-not $smokeProcess.HasExited) { Stop-Process -Id $smokeProcess.Id -Force }
    }
}

Write-Host "Published to $publishRoot"
