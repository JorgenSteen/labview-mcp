# =================================================================================================
#  Update-LocalPlugin.ps1 - build the user's own installable labview-mcp plugin folder from main,
#  the same way .github/workflows/release.yml assembles its staging tree, plus a local marketplace.
#  It exists so the user's version (upstream + the fork's features) can replace the store plugin.
#  This folder (local/) lives on main only and is never part of a pull request.
# =================================================================================================
<#
.SYNOPSIS
    Build the installable plugin folder and a local marketplace for it. Installs nothing.

.DESCRIPTION
    Mirrors release.yml step by step, minus the parts that only make sense on a runner (tag
    validation, zip, upload, re-download):

      1. dotnet build -c Release with the version stamp, then build.ps1 -VerifyOnly
         (the embedded-documentation gate).
      2. dotnet publish: win-x64, self-contained, single file, native libraries for
         self-extract, NOT trimmed, -p:Version=<stamp>. Asserts the stamp landed.
      3. The pylabview bundle (tools\pylabview\runtime). The release provisions it on every run
         from a pinned Python 3.12 plus a pip-installed Pillow; here the existing runtime is
         reused when it is complete and patched, and provision.ps1 is run only when it is missing
         or -Reprovision is given (provision.ps1 never reaches for the network; Pillow must already
         be in the source Python).
      4. The staging tree: plugin\.claude-plugin, plugin\.mcp.json, plugin\agents, plugin\hooks,
         the exe at bin\LabVIEWMCP.exe, scripts\ at bin\scripts, docs\ at bin\docs, the bundle at
         bin\pylabview, .claude\agents + .claude\settings.json + CLAUDE.md at bin\claude, and
         README.md at bin\README.md.
      5. VERSION.txt at the root and the version injected into .claude-plugin\plugin.json (same
         targeted replacement of the 0.0.0 placeholder, same parse and byte round-trip checks).
      6. The release's asserts: manifest at the root, the run-time .tsv files, the bundle locatable
         and patched, the staged interpreter imports pylabview, both agent flavours carry the right
         tool names, and `LabVIEWMCP.exe --version` reports the stamp and the commit. (--version
         needs no LabVIEW. --help is not run.)
      7. The finished tree replaces -Destination in one move, and the marketplace manifest is
         written beside it at <parent of -Destination>\.claude-plugin\marketplace.json.

    VERSION. `<upstream version>-local.<yyyyMMdd-HHmm>+<short sha>`, e.g.
    1.9.5-local.20261010-1532+9a20a76. The date and time are joined by a HYPHEN, not a dot:
    NuGet's version parser (which `dotnet build -p:Version=` goes through) refuses a numeric
    pre-release identifier with a leading zero, so `20261010.0930` fails the build for every hour
    before 10:00 - measured. `20261010-0930` is one alphanumeric identifier, valid SemVer, and
    still sorts in time order. The exe is stamped WITHOUT the `+sha` part, because the SDK appends
    `+<full sha>` itself and a second `+` would land in the commit field --version prints; plugin.json
    and VERSION.txt carry the full string. A build with -AllowDirty gets `.dirty` after the sha.

.PARAMETER Destination
    The plugin folder to (re)build. Its parent becomes the local marketplace root.

.PARAMETER Check
    Dry run: print what would be built and where, and the state of every input. Writes nothing.

.PARAMETER AllowDirty
    Build even when tracked files have uncommitted changes. The version then says `.dirty`.

.PARAMETER Reprovision
    Rebuild tools\pylabview\runtime with provision.ps1 even when the existing one is fine.

.PARAMETER PythonSource
    The CPython folder provision.ps1 assembles from (it needs Pillow installed). Default: its own
    discovery (py -0p, then PATH). The release pins 3.12.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File local\Update-LocalPlugin.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File local\Update-LocalPlugin.ps1 -Check
#>
[CmdletBinding()]
param(
    [string] $Destination = 'C:\project\claude\labview-mcp-local-plugin\labview-mcp',
    [switch] $Check,
    [switch] $AllowDirty,
    [switch] $Reprovision,
    [string] $PythonSource
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\LabVIEWMCP\LabVIEWMCP.csproj'
$Destination = [IO.Path]::GetFullPath($Destination)
$marketRoot = Split-Path -Parent $Destination
$pluginDirName = Split-Path -Leaf $Destination
$work = Join-Path $marketRoot '.build'
$utf8 = New-Object Text.UTF8Encoding($false)

function Fail([string] $message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    exit 1
}
function Step([string] $message) { Write-Host ''; Write-Host "== $message" -ForegroundColor Cyan }
function Invoke-Git { & git.exe -C $repo @args; if ($LASTEXITCODE -ne 0) { Fail "git $args exited $LASTEXITCODE" } }

if (-not (Test-Path -LiteralPath $project)) { Fail "no $project - this script must sit in <repo>\local\" }

# ------------------------------------------------------------------ inputs
$dirty = @(Invoke-Git status --porcelain --untracked-files=no)
$branch = (Invoke-Git rev-parse --abbrev-ref HEAD) | Select-Object -First 1
$sha = (Invoke-Git rev-parse HEAD) | Select-Object -First 1
$shortSha = (Invoke-Git rev-parse --short=7 HEAD) | Select-Object -First 1

# The upstream version is the newest vX.Y.Z tag reachable from HEAD. Only exact three-part tags
# count: the repository also carries v10.4, which sorts above every real release.
$tags = @(Invoke-Git tag --merged HEAD) | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' }
if (-not $tags) { Fail 'no vX.Y.Z tag is reachable from HEAD (git fetch upstream --tags?)' }
$baseTag = $tags | Sort-Object { [version]($_.Substring(1)) } -Descending | Select-Object -First 1
$baseVersion = $baseTag.Substring(1)

$stamp = (Get-Date).ToString('yyyyMMdd-HHmm')
$buildVersion = "$baseVersion-local.$stamp"
$pluginVersion = "$buildVersion+$shortSha" + $(if ($dirty) { '.dirty' } else { '' })

# dotnet: the first one that actually has an SDK. On this PC the dotnet on PATH (Program Files)
# carries runtimes only and the SDK lives in %USERPROFILE%\.dotnet, so "first on PATH" is wrong.
$dotnet = $null
$candidates = @((Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')) +
    @(Get-Command dotnet -All -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
foreach ($candidate in $candidates) {
    if (-not (Test-Path -LiteralPath $candidate)) { continue }
    if (@(& $candidate --list-sdks) | Where-Object { $_ -match '^8\.' }) {
        $dotnet = $candidate
        $env:DOTNET_ROOT = Split-Path -Parent $candidate
        break
    }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$runtime = Join-Path $repo 'tools\pylabview\runtime'
$patchFile = Join-Path $repo 'tools\pylabview\patches\patches.json'
function Test-Bundle([string] $root, [string] $appRoot) {
    # What PyLabview.Locate() needs, plus every assembly-time patch present - the release's 5c.
    $problems = @()
    foreach ($rel in 'python.exe', 'app\pylabview\readRSRC.py') {
        if (-not (Test-Path -LiteralPath (Join-Path $root $rel))) { $problems += "$rel missing" }
    }
    if (-not $problems -and (Test-Path -LiteralPath $patchFile)) {
        foreach ($patch in ([IO.File]::ReadAllText($patchFile, $utf8) | ConvertFrom-Json).patches) {
            $target = Join-Path $appRoot $patch.file
            if (-not (Test-Path -LiteralPath $target)) { $problems += "patch $($patch.id): $($patch.file) missing"; continue }
            if (-not ([IO.File]::ReadAllText($target)).Contains($patch.replace)) { $problems += "patch $($patch.id) not applied" }
        }
    }
    return , $problems
}
$bundleProblems = Test-Bundle $runtime (Join-Path $runtime 'app')
$provision = $Reprovision -or ($bundleProblems.Count -gt 0)

$pluginJsonSource = Join-Path $repo 'plugin\.claude-plugin\plugin.json'
$pluginName = ([IO.File]::ReadAllText($pluginJsonSource, $utf8) | ConvertFrom-Json).name
$marketFile = Join-Path $marketRoot '.claude-plugin\marketplace.json'
$installed = Join-Path $Destination '.claude-plugin\plugin.json'
$installedVersion = if (Test-Path -LiteralPath $installed) {
    ([IO.File]::ReadAllText($installed, $utf8) | ConvertFrom-Json).version } else { '(none)' }

Write-Host "repo            $repo  (branch $branch, $shortSha)"
Write-Host "tracked changes $(if ($dirty) { "$($dirty.Count) file(s)" } else { 'none' })"
Write-Host "upstream base   $baseTag"
Write-Host "version         $pluginVersion   (exe stamped $buildVersion, SDK appends +$sha)"
Write-Host "dotnet          $(if ($dotnet) { $dotnet } else { 'NOT FOUND' })"
Write-Host "pylabview       $(if ($bundleProblems.Count -eq 0) { "reuse $runtime" } else { 'NOT USABLE: ' + ($bundleProblems -join '; ') })$(if ($provision) { ' -> provision.ps1' + $(if ($PythonSource) { " -Source $PythonSource" } else { '' }) })"
Write-Host "plugin folder   $Destination  (currently $installedVersion)"
Write-Host "marketplace     $marketFile  (labview-local, plugin $pluginName, source ./$pluginDirName)"
Write-Host "work folder     $work  (removed at the end)"

$refuse = @()
if ($dirty -and -not $AllowDirty) {
    $refuse += "tracked files have uncommitted changes (commit them, or pass -AllowDirty):"
    $refuse += ($dirty | ForEach-Object { "    $_" })
}
if (-not $dotnet) { $refuse += 'no .NET 8 SDK, neither in %USERPROFILE%\.dotnet nor in a dotnet on PATH' }
# Never replace the folder under a running server: the exe would be locked, and pulling files out
# from under a live session is the kind of surprise this script must not cause. It is not killed.
$live = @(Get-Process -Name 'LabVIEWMCP' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($Destination, [StringComparison]::OrdinalIgnoreCase) })
if ($live) { $refuse += "a LabVIEWMCP.exe is running from $Destination (pid $($live.Id -join ', ')); close that Claude Code session first" }

if ($Check) {
    Write-Host ''
    if ($refuse) { $refuse | ForEach-Object { Write-Host "would refuse: $_" -ForegroundColor Yellow } }
    Write-Host 'Check only: would build Release + verify embedded docs, publish the single-file exe,'
    Write-Host "$(if ($provision) { 'provision' } else { 'reuse' }) the pylabview bundle, stage the tree as release.yml does, stamp"
    Write-Host "VERSION.txt and plugin.json, run the release asserts, replace $Destination"
    Write-Host "and write $marketFile. Nothing was written."
    exit $(if ($refuse) { 1 } else { 0 })
}
if ($refuse) { $refuse | ForEach-Object { Write-Host $_ -ForegroundColor Red }; exit 1 }

# ------------------------------------------------------------------ 1. Release build + docs gate
Step "dotnet build -c Release -p:Version=$buildVersion"
& $dotnet build $project -c Release --nologo -v quiet "-p:Version=$buildVersion"
if ($LASTEXITCODE -ne 0) { Fail 'Release build failed' }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'build.ps1') -VerifyOnly -Configuration Release
if ($LASTEXITCODE -ne 0) { Fail 'embedded-documentation verification failed' }

# ------------------------------------------------------------------ 2. publish
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null
$publish = Join-Path $work 'publish'
Step 'dotnet publish (win-x64, self-contained, single file, NOT trimmed)'
& $dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
    "-p:Version=$buildVersion" -o $publish
if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed' }
$exe = Join-Path $publish 'LabVIEWMCP.exe'
if (-not (Test-Path -LiteralPath $exe)) { Fail "$exe missing after publish" }
$stamped = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
Write-Host "stamped ProductVersion: $stamped"
if (-not $stamped.StartsWith($buildVersion)) { Fail "the exe reports '$stamped', expected $buildVersion - the stamp did not take" }

# ------------------------------------------------------------------ 3. pylabview bundle
if ($provision) {
    Step 'provision the pylabview bundle'
    $provArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'tools\pylabview\provision.ps1'))
    if ($PythonSource) { $provArgs += @('-Source', $PythonSource) }
    & powershell @provArgs
    if ($LASTEXITCODE -ne 0) { Fail 'provision.ps1 failed' }
    $bundleProblems = Test-Bundle $runtime (Join-Path $runtime 'app')
    if ($bundleProblems.Count -gt 0) { Fail ('the provisioned bundle is not usable: ' + ($bundleProblems -join '; ')) }
} else {
    Step "reuse the pylabview bundle at $runtime"
}

# ------------------------------------------------------------------ 4. staging tree
Step 'assemble the staging tree'
$staging = Join-Path $work 'staging'
New-Item -ItemType Directory -Path $staging | Out-Null
Push-Location $repo
try {
    # Explicit entries, so the dot-prefixed .claude-plugin and .mcp.json are never dropped.
    Copy-Item -Path 'plugin\.claude-plugin' -Destination $staging -Recurse -Force
    Copy-Item -Path 'plugin\.mcp.json'      -Destination $staging -Force
    Copy-Item -Path 'plugin\agents'         -Destination $staging -Recurse -Force
    Copy-Item -Path 'plugin\hooks'          -Destination $staging -Recurse -Force

    $bin = Join-Path $staging 'bin'
    New-Item -ItemType Directory -Path $bin | Out-Null
    Copy-Item -LiteralPath $exe -Destination (Join-Path $bin 'LabVIEWMCP.exe') -Force

    # scripts\ and docs\ next to the exe: the server finds its helpers as <exe dir>\scripts, and
    # pylv-conpane.py opens <script dir>\..\docs\*.tsv at run time.
    New-Item -ItemType Directory -Path (Join-Path $bin 'scripts') | Out-Null
    Copy-Item -Path 'scripts\*' -Destination (Join-Path $bin 'scripts') -Recurse -Force
    New-Item -ItemType Directory -Path (Join-Path $bin 'docs') | Out-Null
    Copy-Item -Path 'docs\*' -Destination (Join-Path $bin 'docs') -Recurse -Force

    # PyLabview.Locate() looks for <exe dir>\pylabview\python.exe.
    New-Item -ItemType Directory -Path (Join-Path $bin 'pylabview') | Out-Null
    Copy-Item -Path (Join-Path $runtime '*') -Destination (Join-Path $bin 'pylabview') -Recurse -Force

    # The non-plugin agent flavour and settings, for scripts\Install-ClaudeAssets.ps1.
    New-Item -ItemType Directory -Path (Join-Path $bin 'claude') | Out-Null
    Copy-Item -Path '.claude\agents'        -Destination (Join-Path $bin 'claude') -Recurse -Force
    Copy-Item -Path '.claude\settings.json' -Destination (Join-Path $bin 'claude') -Force
    Copy-Item -Path 'CLAUDE.md'             -Destination (Join-Path $bin 'claude') -Force
    Copy-Item -Path 'README.md'             -Destination (Join-Path $bin 'README.md') -Force
} finally {
    Pop-Location
}

# ------------------------------------------------------------------ 5. stamp
Step 'stamp VERSION.txt and plugin.json'
$lines = @(
    "tag: none (local build of main; upstream base $baseTag)",
    "version: $pluginVersion",
    "commit: $sha",
    "built: $([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))",
    "branch: $branch"
)
[IO.File]::WriteAllText((Join-Path $staging 'VERSION.txt'), ($lines -join "`n") + "`n", $utf8)
$lines | ForEach-Object { Write-Host "  $_" }

# Targeted replacement, as in the release: the rest of plugin.json (with its non-ASCII author name)
# stays byte for byte, and every read and write names its encoding.
$manifestPath = Join-Path $staging '.claude-plugin\plugin.json'
$before = [IO.File]::ReadAllText($manifestPath, $utf8)
$after = $before -replace '"version"\s*:\s*"0\.0\.0"', ('"version": "' + $pluginVersion + '"')
if ($after -eq $before) { Fail 'plugin\.claude-plugin\plugin.json has no "version": "0.0.0" placeholder' }
[IO.File]::WriteAllText($manifestPath, $after, $utf8)
$parsed = [IO.File]::ReadAllText($manifestPath, $utf8) | ConvertFrom-Json
if ($parsed.version -ne $pluginVersion) { Fail "plugin.json reports '$($parsed.version)' after stamping" }
if ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($manifestPath)) -ne $after) {
    Fail 'plugin.json on disk is not byte-identical to the stamped text'
}
Write-Host "OK: plugin.json version = $($parsed.version)"

# ------------------------------------------------------------------ 6. the release's asserts
Step 'assert the staged tree'
foreach ($rel in '.claude-plugin\plugin.json', 'bin\docs\connector-pane-patterns.tsv', 'bin\docs\connector-pane-typecodes.tsv') {
    if (-not (Test-Path -LiteralPath (Join-Path $staging $rel))) { Fail "$rel is missing from the staged tree" }
}
$stagedBundle = Join-Path $staging 'bin\pylabview'
$stagedProblems = Test-Bundle $stagedBundle (Join-Path $stagedBundle 'app')
if ($stagedProblems.Count -gt 0) { Fail ('staged pylabview bundle: ' + ($stagedProblems -join '; ')) }
& (Join-Path $stagedBundle 'python.exe') -c "import sys, PIL; from pylabview import LVblock; print('python', sys.version.split()[0], '| Pillow', PIL.__version__, '| pylabview imported')"
if ($LASTEXITCODE -ne 0) { Fail "the staged pylabview bundle does not import (exit $LASTEXITCODE)" }

$sourceAgents = @(Get-ChildItem (Join-Path $repo '.claude\agents') -Filter '*.md')
$flavours = @(
    @{ Path = Join-Path $staging 'agents';            Prefix = "mcp__plugin_$($pluginName)_labview__"; Wrong = 'mcp__labview__lvai' },
    @{ Path = Join-Path $staging 'bin\claude\agents'; Prefix = 'mcp__labview__';                       Wrong = 'mcp__plugin_' }
)
foreach ($flavour in $flavours) {
    $staged = @(Get-ChildItem $flavour.Path -Filter '*.md' -ErrorAction SilentlyContinue)
    if ($staged.Count -ne $sourceAgents.Count) { Fail "$($flavour.Path) holds $($staged.Count) agent(s); .claude\agents holds $($sourceAgents.Count)" }
    foreach ($agent in $staged) {
        $tools = @(Get-Content -LiteralPath $agent.FullName | Where-Object { $_ -like 'tools:*' })
        if ($tools.Count -ne 1) { Fail "$($agent.FullName) has $($tools.Count) tools: lines" }
        if ($tools[0] -notmatch [regex]::Escape($flavour.Prefix)) { Fail "$($agent.Name) in $($flavour.Path) names no $($flavour.Prefix)* tool" }
        if ($tools[0] -match [regex]::Escape($flavour.Wrong)) { Fail "$($agent.Name) in $($flavour.Path) names $($flavour.Wrong)* tools" }
    }
    Write-Host "OK: $($staged.Count) agent(s) at $($flavour.Path)"
}

$versionOut = @(& (Join-Path $staging 'bin\LabVIEWMCP.exe') --version)
if ($LASTEXITCODE -ne 0) { Fail "LabVIEWMCP.exe --version exited $LASTEXITCODE" }
$versionOut | ForEach-Object { Write-Host "  $_" }
$text = $versionOut -join "`n"
foreach ($want in @($buildVersion, $sha, $pluginVersion)) {
    if ($text -notlike "*$want*") { Fail "--version does not mention '$want'" }
}

# ------------------------------------------------------------------ 7. swap in, marketplace
Step "replace $Destination"
$previous = Join-Path $work 'previous'
if (Test-Path -LiteralPath $Destination) { Move-Item -LiteralPath $Destination -Destination $previous }
Move-Item -LiteralPath $staging -Destination $Destination

$market = [ordered]@{
    name        = 'labview-local'
    owner       = [ordered]@{ name = 'local build' }
    description = 'Local labview-mcp build: upstream plus the fork features, from local\Update-LocalPlugin.ps1 on main.'
    plugins     = @(
        [ordered]@{
            name        = $pluginName
            source      = "./$pluginDirName"
            description = "labview-mcp $pluginVersion - local build of main at $shortSha."
        }
    )
}
New-Item -ItemType Directory -Path (Split-Path -Parent $marketFile) -Force | Out-Null
[IO.File]::WriteAllText($marketFile, ($market | ConvertTo-Json -Depth 5), $utf8)
$null = [IO.File]::ReadAllText($marketFile, $utf8) | ConvertFrom-Json

Remove-Item -LiteralPath $work -Recurse -Force

$files = @(Get-ChildItem -LiteralPath $Destination -Recurse -File -Force)
$mb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host ''
Write-Host "Built labview-mcp $pluginVersion" -ForegroundColor Green
Write-Host "  $Destination  ($($files.Count) files, $mb MB)"
Write-Host "  $marketFile"
Write-Host 'Nothing was installed or enabled. First time: see local\README.md (one-time swap).'
Write-Host "Later: claude plugin marketplace update labview-local; claude plugin update $pluginName@labview-local; restart Claude Code."
