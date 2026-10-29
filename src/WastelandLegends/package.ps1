# Builds the release zip: GameInternals\dist\WastelandLegends-<version>.zip
#   powershell -ExecutionPolicy Bypass -File package.ps1
# The zip unpacks into the game folder: BepInEx\plugins\WastelandLegends\ with WastelandLegends.dll and the WW2
# bridge WastelandLegends.Ww2.dll, plus README and CHANGELOG at the zip root (Nexus and the Release Helper's layout;
# Apocasetter strips BepInEx/plugins/ and skips the root files, as for the Weapons Pack). The version comes from
# Plugin.cs, which must match the GitHub tag v<version>.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..')

$m = Select-String -Path (Join-Path $here 'Plugin.cs') -Pattern 'const string Version = "([0-9.]+)"'
if (-not $m) { throw 'No Version in Plugin.cs' }
$version = $m.Matches[0].Groups[1].Value

& dotnet build (Join-Path $here 'WastelandLegends.csproj') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& dotnet build (Join-Path $here '..\WastelandLegends.Ww2\WastelandLegends.Ww2.csproj') -c Release -nologo -v q

$dll = Join-Path $root 'build\WastelandLegends\WastelandLegends.dll'
$bridge = Join-Path $root 'build\WastelandLegends\WastelandLegends.Ww2.dll'
if ($LASTEXITCODE -ne 0) {
    # Builds before 0.9.0's load-from-bytes kept the bridge open in a running game, so the copy into build\ fails.
    # That's fine only if the copy there is the same as the fresh build.
    $fresh = Join-Path $here '..\WastelandLegends.Ww2\obj\Release\WastelandLegends.Ww2.dll'
    if (-not (Test-Path $bridge) -or (Get-FileHash $fresh).Hash -ne (Get-FileHash $bridge).Hash) { throw 'Bridge build failed' }
    Write-Output 'Bridge in build\ is in use but identical to the fresh build: using it.'
}
# Next to this script in GameInternals; at the root of the public repo (src\WastelandLegends\..\..).
$readme = Join-Path $here 'README.md'
if (-not (Test-Path $readme)) { $readme = Join-Path $root 'README.md' }
$changelog = Join-Path $here 'CHANGELOG.md'
if (-not (Test-Path $changelog)) { $changelog = Join-Path $root 'CHANGELOG.md' }
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist ("WastelandLegends-" + $version + ".zip")
if (Test-Path $zip) { Remove-Item $zip -Confirm:$false }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    # Forward slashes in entry names: Windows PowerShell's Compress-Archive writes backslashes, which some tools misread.
    $files = @(
        @($dll, 'BepInEx/plugins/WastelandLegends/WastelandLegends.dll'),
        @($bridge, 'BepInEx/plugins/WastelandLegends/WastelandLegends.Ww2.dll'),
        @($readme, 'README.md'),
        @($changelog, 'CHANGELOG.md')
    )
    foreach ($f in $files) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f[0], $f[1], 'Optimal') | Out-Null
    }
} finally { $archive.Dispose() }

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output ("Built " + $zip)
Write-Output ("Version " + $version + ", " + (Get-Item $zip).Length + " bytes, sha256 " + $hash)
