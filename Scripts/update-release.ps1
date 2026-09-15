<#
.SYNOPSIS
    Updates the Release build of this fork that runs from Output\Release.

.DESCRIPTION
    1. Merges the latest upstream Flow Launcher dev into the current branch (unless -SkipUpstream).
    2. Builds Release from a staged copy of the commit, stamping the built-in plugins and the app with the
       official version number like the upstream release pipeline does, so plugin updates only prompt for real
       new releases. The checkout itself is never modified by the stamp.
    3. Runs the tests (unless -SkipTests).
    4. Closes the running Release Flow Launcher gracefully so it saves settings and history, replaces the
       program files (UserData is left alone) and starts it again.

.PARAMETER SkipUpstream
    Build the current commit without merging upstream first.

.PARAMETER Push
    Push the branch to origin after merging upstream.

.PARAMETER Version
    Version stamp to use. Defaults to the latest official Flow Launcher release.

.PARAMETER SkipTests
    Skip running Flow.Launcher.Test.

.EXAMPLE
    .\Scripts\update-release.ps1 -Push
#>
param(
    [switch]$SkipUpstream,
    [switch]$Push,
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$install = Join-Path $repo 'Output\Release'
$upstreamUrl = 'https://github.com/Flow-Launcher/Flow.Launcher.git'

function Invoke-Native([string]$Description, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed (exit code $LASTEXITCODE)" }
}

Write-Host '== Checking the checkout' -ForegroundColor Cyan
if (git -C $repo status --porcelain --untracked-files=no) {
    throw 'The checkout has uncommitted changes. Commit or stash them first.'
}
$branch = git -C $repo rev-parse --abbrev-ref HEAD

if (-not $SkipUpstream) {
    Write-Host "== Merging upstream Flow Launcher dev into $branch" -ForegroundColor Cyan
    Invoke-Native 'Fetching upstream' { git -C $repo fetch $upstreamUrl dev }
    git -C $repo merge --no-edit FETCH_HEAD
    if ($LASTEXITCODE -ne 0) {
        git -C $repo merge --abort
        throw 'Merging upstream hit conflicts, so it was undone. Merge upstream by hand, then run with -SkipUpstream.'
    }
    if ($Push) {
        Invoke-Native 'Pushing to origin' { git -C $repo push origin $branch }
    }
}

if (-not $Version) {
    $Version = (Invoke-RestMethod 'https://api.github.com/repos/Flow-Launcher/Flow.Launcher/releases/latest').tag_name.TrimStart('v')
}
Write-Host "== Building Release $Version" -ForegroundColor Cyan

$stage = Join-Path ([IO.Path]::GetTempPath()) ("flow-release-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $archive = Join-Path $stage 'source.tar'
    Invoke-Native 'Exporting the commit' { git -C $repo archive --format=tar -o $archive HEAD }
    Invoke-Native 'Extracting the commit' { tar -xf $archive -C $stage }
    Remove-Item $archive

    # Same version stamping as appveyor.yml
    foreach ($pluginJson in Get-ChildItem (Join-Path $stage 'Plugins\*\plugin.json')) {
        $json = [IO.File]::ReadAllText($pluginJson.FullName)
        [IO.File]::WriteAllText($pluginJson.FullName, ($json -replace '"Version"\s*:\s*".*?"', "`"Version`": `"$Version`""))
    }
    $assemblyInfo = Join-Path $stage 'SolutionAssemblyInfo.cs'
    [IO.File]::WriteAllText($assemblyInfo, ([IO.File]::ReadAllText($assemblyInfo) -replace 'Version\("[^"]*"\)', "Version(`"$Version`")"))

    Invoke-Native 'Building' { dotnet build (Join-Path $stage 'Flow.Launcher.sln') -c Release -nologo -v q -clp:ErrorsOnly }

    if (-not $SkipTests) {
        Write-Host '== Running tests' -ForegroundColor Cyan
        Invoke-Native 'Tests' { dotnet test (Join-Path $stage 'Flow.Launcher.Test\Flow.Launcher.Test.csproj') -c Release --no-build -nologo }
    }

    Write-Host '== Closing the running Release Flow Launcher' -ForegroundColor Cyan
    Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class FlowUpdateWindows {
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    public static IntPtr FindMainWindow(uint processId) {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) => {
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (pid != processId) return true;
            var title = new StringBuilder(256); var cls = new StringBuilder(256);
            GetWindowText(hwnd, title, 256); GetClassName(hwnd, cls, 256);
            if (cls.ToString().StartsWith("HwndWrapper[Flow.Launcher") && title.ToString() == "Flow.Launcher") { found = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
    $installPrefix = $install.TrimEnd('\') + '\'
    foreach ($process in Get-Process Flow.Launcher -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase) }) {
        $window = [FlowUpdateWindows]::FindMainWindow([uint32]$process.Id)
        if ($window -eq [IntPtr]::Zero) { throw 'Could not find the Flow Launcher window to close it gracefully.' }
        # WM_CLOSE runs Flow's normal exit, which saves settings and history
        [FlowUpdateWindows]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        if (-not $process.WaitForExit(30000)) { throw 'Flow Launcher did not exit within 30 seconds.' }
    }

    Write-Host "== Installing to $install" -ForegroundColor Cyan
    robocopy (Join-Path $stage 'Output\Release') $install /MIR /XD UserData /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the build failed (robocopy exit code $LASTEXITCODE)" }

    Start-Process (Join-Path $install 'Flow.Launcher.exe') -WorkingDirectory $install
    Write-Host "== Flow Launcher $Version is running from $install" -ForegroundColor Green
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
