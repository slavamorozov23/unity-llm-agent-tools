param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [object[]] $BridgeArguments
)

# PowerShell reads an unquoted A,B as an array; the bridge takes it as the list it was typed as, with 0.9 kept as 0.9.
$text = { param($value) [Convert]::ToString($value, [Globalization.CultureInfo]::InvariantCulture) }
$BridgeArguments = @(foreach ($argument in @($BridgeArguments | Where-Object { $null -ne $_ })) {
    if ($argument -is [array]) { (@($argument) | ForEach-Object { & $text $_ }) -join ',' } else { & $text $argument }
})

$projectArgument = $null
$forwarded = [Collections.Generic.List[string]]::new()
for ($index = 0; $index -lt $BridgeArguments.Count; $index++) {
    if ($BridgeArguments[$index] -eq '--project') {
        if ($index + 1 -ge $BridgeArguments.Count) {
            throw '--project requires a Unity project path.'
        }
        $projectArgument = $BridgeArguments[++$index]
        continue
    }
    $forwarded.Add($BridgeArguments[$index])
}

if (-not [string]::IsNullOrWhiteSpace($projectArgument)) {
    $project = (Resolve-Path -LiteralPath $projectArgument).Path
    if (-not (Test-Path -LiteralPath (Join-Path $project 'Assets')) -or
        -not (Test-Path -LiteralPath (Join-Path $project 'ProjectSettings'))) {
        throw '--project must point to a Unity project.'
    }
}

$directory = Get-Item -LiteralPath (Get-Location).Path
while ($null -ne $directory) {
    if (-not [string]::IsNullOrEmpty($project)) { break }
    if ((Test-Path -LiteralPath (Join-Path $directory.FullName 'Assets')) -and
        (Test-Path -LiteralPath (Join-Path $directory.FullName 'ProjectSettings'))) {
        $project = $directory.FullName
        break
    }
    $directory = $directory.Parent
}

if ([string]::IsNullOrEmpty($project)) {
    throw 'The current directory is not inside a Unity project.'
}

$python = Join-Path $project 'Library\UnityAgentBridge\Runtime\venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) {
    throw 'Unity Agent Bridge is not installed. Start its server in Unity.'
}

$projectClient = Join-Path $project 'Assets\UnityAgentBridge\CodexPlugin~\unity-agent-bridge\skills\unity-agent-bridge\scripts\bridge_client.py'
if (-not (Test-Path -LiteralPath $projectClient)) {
    throw "Unity Agent Bridge client is missing: $projectClient"
}

Remove-Item Env:UNITY_AGENT_BRIDGE_CLIENT -ErrorAction SilentlyContinue
$previousArguments = $env:UNITY_AGENT_BRIDGE_ARGUMENTS
try {
    $argumentJson = ConvertTo-Json -Compress -InputObject @($forwarded)
    $env:UNITY_AGENT_BRIDGE_ARGUMENTS = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($argumentJson))
    & $python $projectClient
    exit $LASTEXITCODE
} finally {
    if ($null -eq $previousArguments) {
        Remove-Item Env:UNITY_AGENT_BRIDGE_ARGUMENTS -ErrorAction SilentlyContinue
    } else {
        $env:UNITY_AGENT_BRIDGE_ARGUMENTS = $previousArguments
    }
}
