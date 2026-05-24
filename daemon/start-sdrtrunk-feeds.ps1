param(
    [string]$Config = "sdrtrunk-feeds.yml",
    [string]$DaemonPath,
    [string]$Rc2ConfigPath,
    [switch]$GenerateOnly,
    [switch]$NoRc2ConfigUpdate,
    [switch]$NoJobObject
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

# The launcher intentionally uses a tiny YAML reader so Windows installs do not need an
# extra PowerShell module before they can generate per-feed daemon configs.
function Remove-Rc2YamlComment {
    param([string]$Line)

    $inSingle = $false
    $inDouble = $false
    for ($i = 0; $i -lt $Line.Length; $i++) {
        $ch = $Line[$i]
        if ($ch -eq "'" -and -not $inDouble) {
            $inSingle = -not $inSingle
        }
        elseif ($ch -eq '"' -and -not $inSingle) {
            $escaped = $i -gt 0 -and $Line[$i - 1] -eq '\'
            if (-not $escaped) {
                $inDouble = -not $inDouble
            }
        }
        elseif ($ch -eq "#" -and -not $inSingle -and -not $inDouble) {
            return $Line.Substring(0, $i)
        }
    }

    return $Line
}

function ConvertFrom-Rc2YamlScalar {
    param([string]$Value)

    $value = $Value.Trim()
    if ($value.Length -eq 0) {
        return ""
    }

    if ($value.StartsWith("[") -and $value.EndsWith("]")) {
        $inner = $value.Substring(1, $value.Length - 2).Trim()
        if ($inner.Length -eq 0) {
            return @()
        }

        return @($inner -split "," | ForEach-Object { ConvertFrom-Rc2YamlScalar $_ })
    }

    if (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'"))) {
        $unquoted = $value.Substring(1, $value.Length - 2)
        return $unquoted.Replace('\"', '"').Replace("\\", "\")
    }

    switch -Regex ($value) {
        "^(?i:true)$" { return $true }
        "^(?i:false)$" { return $false }
        "^(?i:null|~)$" { return $null }
        "^-?\d+$" { return [int]$value }
        "^-?\d+\.\d+$" { return [double]$value }
        default { return $value }
    }
}

function ConvertFrom-Rc2FeedYaml {
    param([string]$Path)

    # The feed file only uses a small subset of YAML: top-level defaults and a list of
    # feeds. Parse just that shape and fail loudly if an unsupported line is added.
    $root = [ordered]@{
        defaults = [ordered]@{}
        feeds = @()
    }
    $section = $null
    $currentFeed = $null

    foreach ($rawLine in Get-Content -LiteralPath $Path) {
        $line = Remove-Rc2YamlComment $rawLine
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line -match "^(?<key>[A-Za-z0-9_]+):\s*(?<value>.*)$") {
            $key = $Matches.key
            $value = $Matches.value
            if ($key -eq "defaults" -or $key -eq "feeds") {
                $section = $key
                $currentFeed = $null
                continue
            }

            $root[$key] = ConvertFrom-Rc2YamlScalar $value
            $section = $null
            $currentFeed = $null
            continue
        }

        if ($section -eq "defaults" -and $line -match "^\s+(?<key>[A-Za-z0-9_]+):\s*(?<value>.*)$") {
            $root.defaults[$Matches.key] = ConvertFrom-Rc2YamlScalar $Matches.value
            continue
        }

        if ($section -eq "feeds" -and $line -match "^\s*-\s+(?<rest>.+)$") {
            $currentFeed = [ordered]@{}
            $root.feeds += $currentFeed
            if ($Matches.rest -match "^(?<key>[A-Za-z0-9_]+):\s*(?<value>.*)$") {
                $currentFeed[$Matches.key] = ConvertFrom-Rc2YamlScalar $Matches.value
            }
            continue
        }

        if ($section -eq "feeds" -and $null -ne $currentFeed -and $line -match "^\s+(?<key>[A-Za-z0-9_]+):\s*(?<value>.*)$") {
            $currentFeed[$Matches.key] = ConvertFrom-Rc2YamlScalar $Matches.value
            continue
        }

        throw "Unsupported YAML line in ${Path}: $rawLine"
    }

    return $root
}

function Resolve-Rc2Path {
    param(
        [string]$Path,
        [string]$BaseDirectory
    )

    # Paths in the feed file are relative to that file, not the caller's current
    # directory, so the launcher behaves the same from a shell or a shortcut.
    $expandedPath = [Environment]::ExpandEnvironmentVariables($Path)
    if ([System.IO.Path]::IsPathRooted($expandedPath)) {
        return [System.IO.Path]::GetFullPath($expandedPath)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BaseDirectory $expandedPath))
}

function Resolve-Rc2LauncherConfigPath {
    param([string]$Path)

    $expandedPath = [Environment]::ExpandEnvironmentVariables($Path)
    if ([System.IO.Path]::IsPathRooted($expandedPath)) {
        return [System.IO.Path]::GetFullPath($expandedPath)
    }

    $currentDirectoryPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $expandedPath))
    if (Test-Path -LiteralPath $currentDirectoryPath) {
        return $currentDirectoryPath
    }

    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $expandedPath))
}

function Get-Rc2Value {
    param(
        [System.Collections.IDictionary]$Feed,
        [System.Collections.IDictionary]$Defaults,
        [string]$Name,
        $Fallback = $null
    )

    if ($Feed.Contains($Name) -and $null -ne $Feed[$Name]) {
        return $Feed[$Name]
    }

    if ($Defaults.Contains($Name) -and $null -ne $Defaults[$Name]) {
        return $Defaults[$Name]
    }

    return $Fallback
}

function ConvertTo-Rc2YamlString {
    param($Value)

    if ($null -eq $Value) {
        return '""'
    }

    $escaped = ([string]$Value).Replace("\", "\\").Replace('"', '\"')
    return '"' + $escaped + '"'
}

function Write-Rc2DaemonConfig {
    param(
        [System.Collections.IDictionary]$Feed,
        [System.Collections.IDictionary]$Defaults,
        [string]$Path
    )

    $name = Get-Rc2Value $Feed $Defaults "name" $Feed.id
    $desc = Get-Rc2Value $Feed $Defaults "desc" "SDRTrunk direct stream"
    $rc2ListenAddress = Get-Rc2Value $Feed $Defaults "rc2ListenAddress" "0.0.0.0"
    $rc2Port = Get-Rc2Value $Feed $Defaults "rc2Port" $null
    $sourceListenAddress = Get-Rc2Value $Feed $Defaults "sourceListenAddress" "0.0.0.0"
    $sourcePort = Get-Rc2Value $Feed $Defaults "sourcePort" $null
    $mount = Get-Rc2Value $Feed $Defaults "mount" ("/" + $Feed.id)
    $sourcePassword = Get-Rc2Value $Feed $Defaults "sourcePassword" ""
    $zoneName = Get-Rc2Value $Feed $Defaults "zoneName" "SDRTrunk"
    $channelName = Get-Rc2Value $Feed $Defaults "channelName" $Feed.id
    $rxThresholdDb = Get-Rc2Value $Feed $Defaults "rxThresholdDb" -45
    $attackMs = Get-Rc2Value $Feed $Defaults "attackMs" 0
    $hangMs = Get-Rc2Value $Feed $Defaults "hangMs" 3000
    $outputSampleRate = Get-Rc2Value $Feed $Defaults "outputSampleRate" 16000
    $emitSourceNoAudioRestartMs = [bool](Get-Rc2Value $Feed $Defaults "emitSourceNoAudioRestartMs" $false)
    $sourceNoAudioRestartMs = Get-Rc2Value $Feed $Defaults "sourceNoAudioRestartMs" 10000
    $softkeys = @(Get-Rc2Value $Feed $Defaults "softkeys" @("MON", "HOME", "SEL"))

    if ($null -eq $rc2Port) {
        throw "Feed '$($Feed.id)' is missing rc2Port"
    }
    if ($null -eq $sourcePort) {
        throw "Feed '$($Feed.id)' is missing sourcePort"
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Generated by start-sdrtrunk-feeds.ps1. Edit the master feed YAML instead.")
    $lines.Add("")
    $lines.Add("daemon:")
    $lines.Add("    name: $(ConvertTo-Rc2YamlString $name)")
    $lines.Add("    desc: $(ConvertTo-Rc2YamlString $desc)")
    $lines.Add("    listenAddress: $rc2ListenAddress")
    $lines.Add("    listenPort: $rc2Port")
    $lines.Add("")
    $lines.Add("audio:")
    $lines.Add("    txDevice: `"`"")
    $lines.Add("    rxDevice: `"`"")
    $lines.Add("")
    $lines.Add("control:")
    $lines.Add("    controlMode: 5")
    $lines.Add("    rxOnly: true")
    $lines.Add("")
    $lines.Add("    sdrTrunk:")
    $lines.Add("        listenAddress: $sourceListenAddress")
    $lines.Add("        listenPort: $sourcePort")
    $lines.Add("        mount: $(ConvertTo-Rc2YamlString $mount)")
    $lines.Add("        sourcePassword: $(ConvertTo-Rc2YamlString $sourcePassword)")
    $lines.Add("        zoneName: $(ConvertTo-Rc2YamlString $zoneName)")
    $lines.Add("        channelName: $(ConvertTo-Rc2YamlString $channelName)")
    $lines.Add("        rxThresholdDb: $rxThresholdDb")
    $lines.Add("        attackMs: $attackMs")
    $lines.Add("        hangMs: $hangMs")
    $lines.Add("        outputSampleRate: $outputSampleRate")
    if ($emitSourceNoAudioRestartMs) {
        $lines.Add("        sourceNoAudioRestartMs: $sourceNoAudioRestartMs")
    }
    $lines.Add("")
    $lines.Add("softkeys:")
    foreach ($softkey in $softkeys) {
        $lines.Add("    - $softkey")
    }

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    Set-Content -LiteralPath $Path -Value $lines -Encoding UTF8
}

function Assert-Rc2UniqueFeedPorts {
    param(
        [array]$Feeds,
        [System.Collections.IDictionary]$Defaults
    )

    $rc2Ports = @{}
    $sourcePorts = @{}

    foreach ($feed in $Feeds) {
        $id = [string]$feed.id
        $rc2Port = Get-Rc2Value $feed $Defaults "rc2Port" $null
        $sourcePort = Get-Rc2Value $feed $Defaults "sourcePort" $null

        if ($null -eq $rc2Port) {
            throw "Feed '$id' is missing rc2Port"
        }

        if ($null -eq $sourcePort) {
            throw "Feed '$id' is missing sourcePort"
        }

        $rc2Key = [string]$rc2Port
        if ($rc2Ports.ContainsKey($rc2Key)) {
            throw "Duplicate rc2Port ${rc2Port}: feeds '$($rc2Ports[$rc2Key])' and '$id' cannot both use the same RC2 websocket port"
        }

        $sourceKey = [string]$sourcePort
        if ($sourcePorts.ContainsKey($sourceKey)) {
            throw "Duplicate sourcePort ${sourcePort}: feeds '$($sourcePorts[$sourceKey])' and '$id' cannot both use the same SDRTrunk source port"
        }

        $rc2Ports[$rc2Key] = $id
        $sourcePorts[$sourceKey] = $id
    }
}

function Test-Rc2Property {
    param($Object, [string]$Name)
    return $null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name
}

function Get-Rc2ObjectProperty {
    param($Object, [string]$Name, $Fallback = $null)
    if (Test-Rc2Property $Object $Name) {
        return $Object.$Name
    }

    return $Fallback
}

function Set-Rc2ObjectProperty {
    param($Object, [string]$Name, $Value)
    if (Test-Rc2Property $Object $Name) {
        $Object.$Name = $Value
    }
    else {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
    }
}

function Write-Rc2Utf8NoBom {
    param(
        [string]$Path,
        [string]$Text
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $encoding)
}

function Get-Rc2ConsoleConfigPath {
    param(
        [System.Collections.IDictionary]$LauncherConfig,
        [System.Collections.IDictionary]$Defaults,
        [string]$ConfigDirectory,
        [string]$OverridePath
    )

    if (-not [string]::IsNullOrWhiteSpace($OverridePath)) {
        return Resolve-Rc2Path $OverridePath (Get-Location).Path
    }

    $configuredPath = Get-Rc2Value $LauncherConfig $Defaults "rc2ConfigPath" $null
    if (-not [string]::IsNullOrWhiteSpace([string]$configuredPath)) {
        return Resolve-Rc2Path $configuredPath $ConfigDirectory
    }

    $roamingRoot = [Environment]::GetFolderPath("ApplicationData")
    $defaultPath = Join-Path $roamingRoot "rc2-console\config.json"
    $alternatePath = Join-Path $roamingRoot "RadioConsole2 GUI\config.json"
    if (Test-Path -LiteralPath $defaultPath) {
        return $defaultPath
    }
    if (Test-Path -LiteralPath $alternatePath) {
        return $alternatePath
    }

    return $defaultPath
}

function New-Rc2DefaultConsoleConfig {
    return [pscustomobject]@{
        Radios = @()
        Autoconnect = $false
        ClockFormat = "UTC"
        Audio = [pscustomobject]@{
            ButtonSounds = $true
            UnselectedVol = -9.0
            ToneVolume = -9.0
            UseAGC = $true
        }
        Extension = [pscustomobject]@{
            address = "127.0.0.1"
            port = 5555
        }
        Peripherals = [pscustomobject]@{
            serialPort = ""
            useCtsForPtt = $false
        }
        Midi = [pscustomobject]@{
            port = -1
            enabled = $false
            ccs = [pscustomobject]@{
                masterPtt = [pscustomobject]@{ chan = $null; num = $null }
                masterVol = [pscustomobject]@{ chan = $null; num = $null }
            }
        }
    }
}

function New-Rc2ConsoleRadio {
    param(
        [System.Collections.IDictionary]$Feed,
        [System.Collections.IDictionary]$Defaults
    )

    $name = Get-Rc2Value $Feed $Defaults "consoleName" (Get-Rc2Value $Feed $Defaults "name" $Feed.id)
    $address = Get-Rc2Value $Feed $Defaults "rc2Address" "127.0.0.1"
    $port = Get-Rc2Value $Feed $Defaults "rc2Port" $null
    $color = Get-Rc2Value $Feed $Defaults "color" "blue"
    $pan = Get-Rc2Value $Feed $Defaults "pan" 0
    if ($null -eq $port) {
        throw "Feed '$($Feed.id)' is missing rc2Port"
    }

    return [pscustomobject][ordered]@{
        name = [string]$name
        address = [string]$address
        port = [int]$port
        color = [string]$color
        pan = [double]$pan
        managedBy = "sdrtrunk-feeds"
        sdrTrunkFeedId = [string]$Feed.id
    }
}

function New-Rc2PersistedRadio {
    param($Radio)

    $persisted = [ordered]@{
        name = [string](Get-Rc2ObjectProperty $Radio "name" "")
        address = [string](Get-Rc2ObjectProperty $Radio "address" "127.0.0.1")
        port = Get-Rc2ObjectProperty $Radio "port" 0
        color = [string](Get-Rc2ObjectProperty $Radio "color" "blue")
        pan = Get-Rc2ObjectProperty $Radio "pan" 0
    }

    if ((Get-Rc2ObjectProperty $Radio "managedBy" "") -eq "sdrtrunk-feeds") {
        $persisted.managedBy = "sdrtrunk-feeds"
        $persisted.sdrTrunkFeedId = [string](Get-Rc2ObjectProperty $Radio "sdrTrunkFeedId" "")
    }

    return [pscustomobject]$persisted
}

function New-Rc2CleanConsoleConfig {
    param($ConsoleConfig, [array]$Radios)

    $defaults = New-Rc2DefaultConsoleConfig

    return [pscustomobject][ordered]@{
        Radios = @($Radios | ForEach-Object { New-Rc2PersistedRadio $_ })
        Autoconnect = Get-Rc2ObjectProperty $ConsoleConfig "Autoconnect" $defaults.Autoconnect
        ClockFormat = Get-Rc2ObjectProperty $ConsoleConfig "ClockFormat" $defaults.ClockFormat
        Audio = Get-Rc2ObjectProperty $ConsoleConfig "Audio" $defaults.Audio
        Extension = Get-Rc2ObjectProperty $ConsoleConfig "Extension" $defaults.Extension
        Peripherals = Get-Rc2ObjectProperty $ConsoleConfig "Peripherals" $defaults.Peripherals
        Midi = Get-Rc2ObjectProperty $ConsoleConfig "Midi" $defaults.Midi
    }
}

function Update-Rc2ConsoleConfig {
    param(
        [string]$Path,
        [array]$Feeds,
        [System.Collections.IDictionary]$Defaults
    )

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    if (Test-Path -LiteralPath $Path) {
        $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $backupPath = "$Path.bak-$timestamp"
        Copy-Item -LiteralPath $Path -Destination $backupPath -Force
        $consoleConfig = (Get-Content -LiteralPath $Path -Raw) | ConvertFrom-Json
        Write-Host "Backed up RC2 console config to $backupPath"
    }
    else {
        $consoleConfig = New-Rc2DefaultConsoleConfig
        Write-Host "Creating RC2 console config at $Path"
    }

    if (-not (Test-Rc2Property $consoleConfig "Radios") -or $null -eq $consoleConfig.Radios) {
        Set-Rc2ObjectProperty $consoleConfig "Radios" @()
    }

    $desiredRadios = @{}
    foreach ($feed in $Feeds) {
        $radio = New-Rc2ConsoleRadio $feed $Defaults
        $desiredRadios[[string]$feed.id] = $radio
    }

    $updatedRadios = New-Object System.Collections.Generic.List[object]
    foreach ($radio in @($consoleConfig.Radios)) {
        $managedBy = Get-Rc2ObjectProperty $radio "managedBy" ""
        $feedId = [string](Get-Rc2ObjectProperty $radio "sdrTrunkFeedId" "")

        if ($managedBy -eq "sdrtrunk-feeds") {
            if ($desiredRadios.ContainsKey($feedId)) {
                continue
            }
            continue
        }

        $updatedRadios.Add($radio)
    }

    foreach ($radio in $desiredRadios.Values) {
        $updatedRadios.Add($radio)
    }

    $cleanConfig = New-Rc2CleanConsoleConfig $consoleConfig @($updatedRadios.ToArray())
    $json = $cleanConfig | ConvertTo-Json -Depth 100
    Write-Rc2Utf8NoBom $Path $json
    Write-Host "Updated RC2 console config with $($desiredRadios.Count) SDRTrunk radio(s): $Path"
}

function New-Rc2KillOnCloseJob {
    if ($env:OS -ne "Windows_NT") {
        return [IntPtr]::Zero
    }

    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class Rc2JobObject
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    public static IntPtr CreateKillOnCloseJob(string name)
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, name);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, pointer, (uint)length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return job;
    }
}
"@

    return [Rc2JobObject]::CreateKillOnCloseJob("RadioConsole2-SDRTrunk-$PID")
}

function Stop-Rc2FeedProcesses {
    param($Processes)

    foreach ($entry in $Processes) {
        $process = $entry.Process
        if ($null -eq $process -or $process.HasExited) {
            continue
        }

        Write-Host "Stopping $($entry.Id) (PID $($process.Id))"
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "Unable to stop $($entry.Id): $($_.Exception.Message)"
        }
    }
}

$configPath = Resolve-Rc2LauncherConfigPath $Config
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "Config file not found: $configPath"
}

$configDirectory = Split-Path -Parent $configPath
$launcherConfig = ConvertFrom-Rc2FeedYaml $configPath
$defaults = $launcherConfig.defaults

if ($DaemonPath) {
    $daemonExe = Resolve-Rc2Path $DaemonPath (Get-Location).Path
}
else {
    $daemonExe = Resolve-Rc2Path (Get-Rc2Value $launcherConfig $defaults "daemonPath" ".\daemon.exe") $configDirectory
}

if (-not (Test-Path -LiteralPath $daemonExe) -and -not $GenerateOnly) {
    throw "Daemon executable not found: $daemonExe"
}

$generatedDirectory = Resolve-Rc2Path (Get-Rc2Value $launcherConfig $defaults "generatedConfigDirectory" ".\generated-sdrtrunk") $configDirectory
$logDirectory = Resolve-Rc2Path (Get-Rc2Value $launcherConfig $defaults "logDirectory" ".\logs") $configDirectory
$debugDaemons = [bool](Get-Rc2Value $launcherConfig $defaults "debug" $false)
$enabledFeeds = @($launcherConfig.feeds | Where-Object { -not $_.Contains("enabled") -or [bool]$_.enabled })

if ($enabledFeeds.Count -eq 0) {
    throw "No enabled feeds found in $configPath"
}

Assert-Rc2UniqueFeedPorts $enabledFeeds $defaults

New-Item -ItemType Directory -Path $generatedDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

$feedConfigs = @()
foreach ($feed in $enabledFeeds) {
    if (-not $feed.Contains("id") -or [string]::IsNullOrWhiteSpace([string]$feed.id)) {
        throw "Every feed must have an id"
    }

    $feedConfigPath = Join-Path $generatedDirectory "$($feed.id).yml"
    Write-Rc2DaemonConfig $feed $defaults $feedConfigPath
    $feedConfigs += [pscustomobject]@{
        Id = $feed.id
        ConfigPath = $feedConfigPath
        Rc2Port = Get-Rc2Value $feed $defaults "rc2Port"
        SourcePort = Get-Rc2Value $feed $defaults "sourcePort"
    }
}

Write-Host "Generated $($feedConfigs.Count) daemon config(s) in $generatedDirectory"

if ($GenerateOnly) {
    return
}

$updateRc2Config = [bool](Get-Rc2Value $launcherConfig $defaults "updateRc2Config" $true)
if ($updateRc2Config -and -not $NoRc2ConfigUpdate) {
    $consoleConfigPath = Get-Rc2ConsoleConfigPath $launcherConfig $defaults $configDirectory $Rc2ConfigPath
    Update-Rc2ConsoleConfig $consoleConfigPath $enabledFeeds $defaults
}

$jobHandle = [IntPtr]::Zero
if (-not $NoJobObject) {
    try {
        $jobHandle = New-Rc2KillOnCloseJob
        if ($jobHandle -ne [IntPtr]::Zero) {
            Write-Host "Started Windows job group. Closing this launcher will stop all child daemons."
        }
    }
    catch {
        Write-Warning "Could not create Windows job group: $($_.Exception.Message)"
        Write-Warning "Ctrl+C will still stop children while this launcher is running."
    }
}

$startedProcesses = @()
$script:Stopping = $false
$cancelHandler = [System.ConsoleCancelEventHandler]{
    param($sender, $eventArgs)
    $eventArgs.Cancel = $true
    $script:Stopping = $true
    Write-Host ""
    Write-Host "Stopping SDRTrunk daemon group..."
}

[System.Console]::add_CancelKeyPress($cancelHandler)

try {
    foreach ($feedConfig in $feedConfigs) {
        $stdoutPath = Join-Path $logDirectory "$($feedConfig.Id).out.log"
        $stderrPath = Join-Path $logDirectory "$($feedConfig.Id).err.log"
        $arguments = @("-c", $feedConfig.ConfigPath)
        if ($debugDaemons) {
            $arguments += "-d"
        }

        Write-Host "Starting $($feedConfig.Id): RC2 $($feedConfig.Rc2Port), SDRTrunk $($feedConfig.SourcePort)"
        $process = Start-Process `
            -FilePath $daemonExe `
            -ArgumentList $arguments `
            -WorkingDirectory (Split-Path -Parent $daemonExe) `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -WindowStyle Hidden `
            -PassThru

        if ($jobHandle -ne [IntPtr]::Zero) {
            if (-not [Rc2JobObject]::AssignProcessToJobObject($jobHandle, $process.Handle)) {
                Write-Warning "Could not add $($feedConfig.Id) to job group. It may need manual cleanup if the launcher exits unexpectedly."
            }
        }

        $startedProcesses += [pscustomobject]@{
            Id = $feedConfig.Id
            Process = $process
            StdOut = $stdoutPath
            StdErr = $stderrPath
        }
    }

    Write-Host ""
    Write-Host "All feeds are running. Press Ctrl+C to stop them."
    Write-Host "Logs are in $logDirectory"

    while (-not $script:Stopping) {
        $running = @($startedProcesses | Where-Object { -not $_.Process.HasExited })
        if ($running.Count -eq 0) {
            Write-Host "All daemon processes exited."
            break
        }

        Start-Sleep -Seconds 1
    }
}
finally {
    Stop-Rc2FeedProcesses $startedProcesses

    if ($jobHandle -ne [IntPtr]::Zero) {
        [Rc2JobObject]::CloseHandle($jobHandle) | Out-Null
    }

    [System.Console]::remove_CancelKeyPress($cancelHandler)
}
