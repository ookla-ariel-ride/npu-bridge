<#
.SYNOPSIS
  End-to-end smoke test of npu-bridge against a real backend on this machine.

.DESCRIPTION
  Starts NpuBridge.exe (or, with -NoStart, tests a server already listening), waits for /healthz to
  report ready (the first Phi Silica / Aion load can take minutes), then exercises /v1/models,
  /debug/generate (raw model access, cancellation, prompt-length preflight), /v1/chat/completions
  non-streaming and streaming (the SSE wire contract and the client-side cut), the context cache
  (a continuation hits, a control misses, both timed) and overflow handling (the preflight refusal,
  then --truncate-history on a second server) and a tool-call compliance probe.

  It also takes the measurements docs/DECISIONS.md cites: the token estimate against the progress
  callbacks, which system-prompt placement this model obeys, whether cancelling a generation really
  stops the accelerator, and how an over-length prompt is refused -- how long it takes, and whether
  the verdict lands as an HTTP status or as an in-stream error frame (D52, D55). Those report numbers
  and do not fail the run over a surprising number, which is a finding rather than a broken bridge.
  A measurement that contradicts something the bridge guarantees (a placement run that cannot answer
  200, /healthz not carrying the keep-alive timings the D52 step reads) is a failure, though. A status
  line arriving after the first keep-alive was due is not one of those: the keep-alive timer starts
  only once a generation is being waited on, so that number measures the pre-generation phase.

  For -Backend phi-silica the exe is started by path; it relaunches itself through package activation so
  the process has identity and supervises that instance (scripts/identity.ps1 -Install must have been run
  for this build folder). Stopping the started process stops the activated one too.

.PARAMETER Backend
  phi-silica | aion | fake

.PARAMETER Port
  Listen port (default 5273). Refuses to start a server if something already listens there.

.PARAMETER NoStart
  Do not start the exe; test whatever is already listening on the port.

.PARAMETER ToolProbeRuns
  How many times to run the tool-call compliance probe (0 = skip).

.EXAMPLE
  .\scripts\smoke.ps1 -Backend phi-silica
  .\scripts\smoke.ps1 -Backend fake -Port 5299
  .\scripts\smoke.ps1 -Backend fake -JsonOut $env:TEMP\smoke.json

.PARAMETER JsonOut
  Optional path for a UTF-8 (without BOM) JSON summary of the run.
#>
[CmdletBinding()]
param(
    [ValidateSet('phi-silica', 'aion', 'fake')] [string] $Backend = 'phi-silica',
    [int] $Port = 5273,
    [switch] $NoStart,
    [int] $ToolProbeRuns = 5,
    [int] $ReadyTimeoutSeconds = 600,
    [string] $JsonOut
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$base = "http://127.0.0.1:$Port"
$results = [System.Collections.Generic.List[object]]::new()
$firstGenerationRpcRetries = 0
$startedAt = (Get-Date).ToUniversalTime().ToString('o')

# Thrown by a Step body to report SKIP instead of FAIL/PASS, without affecting the exit code.
class SkipStepException : System.Exception {
    SkipStepException([string] $message) : base($message) {}
}

function Skip([string] $reason) {
    throw [SkipStepException]::new($reason)
}

# Thrown by an InfoStep body when the measurement contradicts something the bridge guarantees: the
# numbers stay informational, but a guarantee that did not hold is a failure, not a finding.
class FailStepException : System.Exception {
    FailStepException([string] $message) : base($message) {}
}

function Fail([string] $reason) {
    throw [FailStepException]::new($reason)
}

function Step([string] $name, [scriptblock] $body, [string[]] $Pins = @('-')) {
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "==> $name" -ForegroundColor Cyan
    try {
        $detail = & $body
        $stopwatch.Stop()
        $results.Add([pscustomobject]@{ Step = $name; Result = 'PASS'; Pins = @($Pins); Detail = "$detail"; DurationMs = $stopwatch.ElapsedMilliseconds })
        Write-Host "    PASS $detail" -ForegroundColor Green
    } catch [SkipStepException] {
        $stopwatch.Stop()
        $results.Add([pscustomobject]@{ Step = $name; Result = 'SKIP'; Pins = @($Pins); Detail = $_.Exception.Message; DurationMs = $stopwatch.ElapsedMilliseconds })
        Write-Host "    SKIP $($_.Exception.Message)" -ForegroundColor Yellow
    } catch {
        $stopwatch.Stop()
        $results.Add([pscustomobject]@{ Step = $name; Result = 'FAIL'; Pins = @($Pins); Detail = $_.Exception.Message; DurationMs = $stopwatch.ElapsedMilliseconds })
        Write-Host "    FAIL $($_.Exception.Message)" -ForegroundColor Red
    }
}

# For the real-model measurements: prints numbers for docs/DECISIONS.md. A model that misbehaves or a
# step that cannot get a clean read is a finding, not a bridge failure, so a surprising number never
# adds to the FAIL count or the exit code; only a body that calls Fail, for a contradiction of
# something the bridge guarantees, does (D79).
function InfoStep([string] $name, [scriptblock] $body, [string[]] $Pins = @('-')) {
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "==> $name" -ForegroundColor Cyan
    try {
        $detail = & $body
    } catch [FailStepException] {
        $stopwatch.Stop()
        $results.Add([pscustomobject]@{ Step = $name; Result = 'FAIL'; Pins = @($Pins); Detail = $_.Exception.Message; DurationMs = $stopwatch.ElapsedMilliseconds })
        Write-Host '    FAIL' -ForegroundColor Red
        ("$($_.Exception.Message)" -split "`n") | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
        return
    } catch {
        $detail = "could not measure: $($_.Exception.Message)"
    }
    $stopwatch.Stop()
    $results.Add([pscustomobject]@{ Step = $name; Result = 'INFO'; Pins = @($Pins); Detail = "$detail"; DurationMs = $stopwatch.ElapsedMilliseconds })
    Write-Host '    INFO' -ForegroundColor Yellow
    ("$detail" -split "`n") | ForEach-Object { Write-Host "      $_" -ForegroundColor Yellow }
}

function Get-Json([string] $path, [string] $method = 'GET', [string] $body = $null, [int[]] $expect = @(200), [int] $timeoutSec = 300) {
    $request = @{ Uri = "$base$path"; Method = $method; SkipHttpErrorCheck = $true; TimeoutSec = $timeoutSec }
    if ($body) { $request.Body = $body; $request.ContentType = 'application/json' }
    $r = Invoke-WebRequest @request
    if ($path -eq '/healthz' -and [int]$r.StatusCode -eq 503 -and $expect -notcontains 503) {
        $health = $r.Content | ConvertFrom-Json -Depth 20
        if ($health.status -eq 'degraded') { throw "backend is degraded: $($health.last_generation.error)" }
    }
    if ($expect -notcontains [int]$r.StatusCode) { throw "HTTP $($r.StatusCode) for $method $path : $($r.Content)" }
    return ($r.Content | ConvertFrom-Json -Depth 20)
}

# The Phi Silica runtime can report one known RPC flake on its first generation. Capture the response
# here, rather than through Get-Json, so only that exact 502 is retried and both errors remain visible if
# the one retry also fails.
function Get-FirstDebugGeneration([string] $body) {
    $request = @{ Uri = "$base/debug/generate"; Method = 'POST'; Body = $body; ContentType = 'application/json'; SkipHttpErrorCheck = $true; TimeoutSec = 300 }
    $first = Invoke-WebRequest @request
    $firstStatus = [int]$first.StatusCode
    $firstJson = try { $first.Content | ConvertFrom-Json -Depth 20 } catch { $null }
    $isKnownFault = $Backend -eq 'phi-silica' -and $firstStatus -eq 502 -and
        $null -ne $firstJson.error -and $firstJson.error.message -like '*remote procedure call failed*'
    if (-not $isKnownFault) {
        if ($firstStatus -ne 200) { throw "HTTP $firstStatus for POST /debug/generate : $($first.Content)" }
        return [pscustomobject]@{ Json = $firstJson; Retried = $false }
    }

    $firstError = $first.Content
    Write-Host '    known first-generation RPC fault; waiting 5 seconds before one retry' -ForegroundColor Yellow
    Start-Sleep -Seconds 5
    $script:firstGenerationRpcRetries++
    $retry = Invoke-WebRequest @request
    $retryStatus = [int]$retry.StatusCode
    $retryJson = try { $retry.Content | ConvertFrom-Json -Depth 20 } catch { $null }
    if ($retryStatus -ne 200 -or $null -eq $retryJson) {
        throw "first-generation RPC fault: first error=$firstError; retry error=HTTP ${retryStatus}: $($retry.Content)"
    }
    [pscustomobject]@{ Json = $retryJson; Retried = $true; FirstError = $firstError; RetryBody = $retry.Content }
}

# Reads a server-sent-event response frame by frame rather than buffering it, because three of the
# things this script has to report are only visible while the stream is open: when the response headers
# arrive (the window in which a failure is still an ordinary HTTP status, D52), when the first chunk
# does, and whether the stream ended in a `data: {"error":...}` frame -- a failure that arrived after
# the headers were spent looks like HTTP 200 from outside and is invisible to a caller that only reads
# finish reasons. A reply that is not a stream at all is returned whole in Body, since that is a JSON
# error rather than a stream.
function Invoke-Sse([string] $path, [string] $body, [int] $timeoutSec = 300) {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds($timeoutSec)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$base$path")
    $request.Content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, 'application/json')

    $chunks = [System.Collections.Generic.List[object]]::new()
    $frames = [System.Collections.Generic.List[string]]::new()
    $errors = [System.Collections.Generic.List[object]]::new()
    $keepAlives = 0
    $done = $false
    $firstChunkMs = $null
    $errorMs = $null
    $text = ''
    $reader = $null
    $response = $null
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $headerMs = $sw.Elapsed.TotalMilliseconds
        $status = [int]$response.StatusCode
        $contentType = $response.Content.Headers.ContentType.MediaType

        if ($status -ne 200 -or $contentType -ne 'text/event-stream') {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
        else {
            $reader = [System.IO.StreamReader]::new($response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
            while ($null -ne ($line = $reader.ReadLine())) {
                if ($line.Length -eq 0) { continue }                       # the blank line between frames
                if ($line.StartsWith(':')) { $keepAlives++; continue }     # ': keep-alive', a comment
                if (-not $line.StartsWith('data: ')) { throw "unexpected SSE line: '$line'" }
                $payload = $line.Substring(6)
                if ($payload -eq '[DONE]') { $done = $true; continue }
                if ($null -eq $firstChunkMs) { $firstChunkMs = $sw.Elapsed.TotalMilliseconds }
                $frames.Add($payload)
                $frame = $payload | ConvertFrom-Json -Depth 20
                # A mid-stream failure: the status line was already spent, so the error travels as a
                # frame. Stamped separately because that is the moment the verdict arrived.
                if ($null -ne $frame.error) {
                    if ($null -eq $errorMs) { $errorMs = $sw.Elapsed.TotalMilliseconds }
                    $errors.Add($frame)
                }
                $chunks.Add($frame)
            }
        }
        $sw.Stop()

        $parsed = $chunks.ToArray()
        # The reply as the client sees it: every content delta, in order. The usage chunk contributes
        # nothing, having no choices at all.
        $content = -join ($parsed | ForEach-Object { $_.choices } | ForEach-Object { $_.delta.content })
        $ttft = if ($null -ne $firstChunkMs) { [Math]::Round($firstChunkMs, 1) } else { $null }
        $errAt = if ($null -ne $errorMs) { [Math]::Round($errorMs, 1) } else { $null }

        [pscustomobject]@{
            StatusCode   = $status
            ContentType  = $contentType
            Body         = $text
            Chunks       = $parsed
            Frames       = $frames.ToArray()
            ErrorFrames  = $errors.ToArray()
            Content      = $content
            KeepAlives   = $keepAlives
            Done         = $done
            HeaderMs     = [Math]::Round($headerMs, 1)
            FirstChunkMs = $ttft
            ErrorMs      = $errAt
            TotalMs      = [Math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        }
    }
    finally {
        if ($reader) { $reader.Dispose() }
        if ($response) { $response.Dispose() }
        $client.Dispose()
        $handler.Dispose()
    }
}

# Fires one POST and returns immediately with the in-flight task, so a caller can start several requests
# before awaiting any of them -- the shape chunk 8's concurrency step and its queue-full step both need,
# and the one thing Invoke-Sse deliberately does not offer (it blocks until its own request is done,
# which is fine for every step that only ever needs one call in flight at a time). Same handler/client
# setup as Invoke-Sse's and for the same reason (UseProxy off, so a machine-wide proxy cannot intercept a
# loopback call). $baseUrl defaults to the main server but takes an aux server's own base for the
# queue-full step, which needs its own --queue-capacity.
function Start-JsonRequest([string] $path, [string] $body, [string] $baseUrl = $base, [int] $timeoutSec = 300) {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds($timeoutSec)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$baseUrl$path")
    $request.Content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, 'application/json')
    [pscustomobject]@{ Client = $client; Handler = $handler; Task = $client.SendAsync($request) }
}

# Awaits a Start-JsonRequest task to completion and reads its body, disposing the client/handler
# afterwards -- the two are always used as a pair, never the task alone.
function Complete-JsonRequest($pending) {
    try {
        $response = $pending.Task.GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $json = try { $text | ConvertFrom-Json -Depth 20 } catch { $null }
        $retryAfter = if ($response.Headers.Contains('Retry-After')) { $response.Headers.GetValues('Retry-After') | Select-Object -First 1 } else { $null }
        [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Body = $text; Json = $json; RetryAfter = $retryAfter }
    }
    finally {
        $pending.Client.Dispose()
        $pending.Handler.Dispose()
    }
}

# The finish reasons carried by a stream's chunks, in order. Not simply choices[0] on every chunk: the
# usage chunk carries an empty choices array by design. The leading comma keeps the result an array
# when there is exactly one -- PowerShell would otherwise unroll it to a bare string, whose .Count is
# also 1 and whose [0] is its first character, so the callers' checks would read 's' for 'stop'.
function Get-FinishReasons($chunks) {
    $reasons = @($chunks |
        Where-Object { $_.choices.Count -gt 0 -and $_.choices[0].finish_reason } |
        ForEach-Object { $_.choices[0].finish_reason })
    return , $reasons
}

function Test-PortListening([int] $p) {
    return $null -ne (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue)
}

# Starts a second, throwaway NpuBridge.exe on its own port for a measurement that needs a startup
# option (e.g. --system-prompt-placement) the already-running main server was not started with.
# Waits for /healthz to report ready before returning; throws on failure. Independent of $proc/$base.
function Start-AuxServer([string] $label, [int] $port, [string[]] $extraArgs) {
    if (Test-PortListening $port) { throw "something already listens on port $port for the $label run" }
    $exe = Get-ChildItem (Join-Path $repo 'src\NpuBridge\bin') -Recurse -Filter NpuBridge.exe -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { throw 'NpuBridge.exe not found; run: dotnet build src/NpuBridge' }
    $log = Join-Path $env:TEMP "npu-bridge-smoke-$Backend-$label.log"
    $allArgs = @('--backend', $Backend, '--listen', "http://127.0.0.1:$port", '--verbose') + $extraArgs
    Write-Host "Starting $($exe.FullName) $($allArgs -join ' ') (log: $log)"
    $p = Start-Process -FilePath $exe.FullName -ArgumentList $allArgs `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -NoNewWindow

    # The process is stopped on every way out but success: a backend that reports failed used to leak
    # the server on the auxiliary port, and every later measurement on that port then found it busy.
    $deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
    $last = $null
    try {
        while ((Get-Date) -lt $deadline) {
            if ($p.HasExited) { throw "$label server process exited with code $($p.ExitCode); see $log and $log.err" }
            try {
                $r = Invoke-WebRequest -Uri "http://127.0.0.1:$port/healthz" -SkipHttpErrorCheck -TimeoutSec 5
                $last = $r.Content | ConvertFrom-Json
                if ($r.StatusCode -eq 200) { return $p }
                if ($last.status -eq 'failed') { throw "$label backend failed: $($last.error)" }
            } catch [System.Net.Http.HttpRequestException] { }
              # PowerShell 7 surfaces Invoke-WebRequest -TimeoutSec timeouts as TaskCanceledException (verified locally).
              catch [System.Threading.Tasks.TaskCanceledException] { }
            Start-Sleep -Seconds 2
        }
        throw "$label server not ready after ${ReadyTimeoutSeconds}s; last: $($last | ConvertTo-Json -Compress)"
    } catch {
        Stop-AuxServer $p $port $label
        throw
    }
}

# Polls until nothing of a server this script started is left: its own pid, any NpuBridge process that
# carries --supervisor-pid <that pid> on its command line (the activated child, D37; Win32_Process shows
# the command line of every process this user owns, and none for another user's, which then cannot
# match), and the listener on its port. Returns what is still there at the deadline: no survivors and
# Listening false when all is well. The child stops gracefully once it notices the parent has gone.
# Its worst case is a backend still initialising: BackendLifecycle.StopAsync waits on that for the
# host's 30 s shutdown budget, then DisposeAsync waits a further 15 s grace, so the deadline sits
# above the two added together. A process query that fails throws rather than reading as "nothing
# left": with $ErrorActionPreference Stop the caller records the failure instead of a clean teardown.
function Wait-ServerGone([int] $serverPid, [int] $port, [int] $seconds = 60) {
    $pattern = "--supervisor-pid\s+$serverPid(\s|$)"
    $deadline = (Get-Date).AddSeconds($seconds)
    while ($true) {
        $left = @(Get-CimInstance Win32_Process -Filter "Name = 'NpuBridge.exe'" |
            Where-Object { $_.ProcessId -eq $serverPid -or ($_.CommandLine -and $_.CommandLine -match $pattern) })
        $listening = Test-PortListening $port
        if ((-not $listening -and $left.Count -eq 0) -or (Get-Date) -ge $deadline) {
            return [pscustomobject]@{ Survivors = $left; Listening = $listening }
        }
        Start-Sleep -Milliseconds 250
    }
}

# Stops an auxiliary server and records its teardown as a row of its own: on phi-silica each aux
# server relaunches an activated child, so these are the only places D37's child-exit half is
# exercised more than once per run, and a survivor here is a shutdown defect, not a warning. Called
# from a Step's finally, so the row lands before the Step's own.
function Stop-AuxServer($p, [int] $port, [string] $label = 'aux') {
    if (-not $p) { return }
    if (-not $p.HasExited) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    # Waited out rather than slept over: the next Start-AuxServer on this port refuses to start while
    # the previous run's child is still shutting down on it.
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $gone = Wait-ServerGone $p.Id $port
    $sw.Stop()
    $elapsed = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
    $step = "teardown: the $label server (port $port) exits and the port frees"
    if ($gone.Listening -or $gone.Survivors.Count -gt 0) {
        $detail = "after the $label server (pid $($p.Id)) was stopped, listening=$($gone.Listening) and NpuBridge pid(s) $(($gone.Survivors | ForEach-Object ProcessId) -join ',') remain after $elapsed s"
        $results.Add([pscustomobject]@{ Step = $step; Result = 'FAIL'; Pins = @('D37'); Detail = $detail; DurationMs = $sw.ElapsedMilliseconds })
        Write-Host "    FAIL $detail" -ForegroundColor Red
    }
    else {
        $detail = "pid $($p.Id) gone and port $port free after $elapsed s"
        $results.Add([pscustomobject]@{ Step = $step; Result = 'PASS'; Pins = @('D37'); Detail = $detail; DurationMs = $sw.ElapsedMilliseconds })
        Write-Host "    PASS $detail" -ForegroundColor Green
    }
}

# --- start server -----------------------------------------------------------
$proc = $null
if (-not $NoStart) {
    if (Test-PortListening $Port) {
        throw "Something already listens on port $Port. Use -NoStart to test it, or -Port to pick another."
    }
    $exe = Get-ChildItem (Join-Path $repo 'src\NpuBridge\bin') -Recurse -Filter NpuBridge.exe -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { throw 'NpuBridge.exe not found; run: dotnet build src/NpuBridge' }
    $log = Join-Path $env:TEMP "npu-bridge-smoke-$Backend.log"
    Write-Host "Starting $($exe.FullName) --backend $Backend --listen $base (log: $log)"
    $proc = Start-Process -FilePath $exe.FullName -ArgumentList '--backend', $Backend, '--listen', $base, '--verbose' `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -NoNewWindow
}

try {
    # --- wait for ready -----------------------------------------------------
    Step 'healthz becomes ready' -Pins @('D77', 'D79', '#15') {
        $deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
        $last = $null
        while ((Get-Date) -lt $deadline) {
            if ($proc -and $proc.HasExited) { throw "server process exited with code $($proc.ExitCode); see $log and $log.err" }
            try {
                $r = Invoke-WebRequest -Uri "$base/healthz" -SkipHttpErrorCheck -TimeoutSec 5
                $last = $r.Content | ConvertFrom-Json
                if ($r.StatusCode -eq 200) { break }
                if ($last.status -eq 'failed') { throw "backend failed: $($last.error)" }
                if ($last.status -eq 'degraded') { throw "backend is degraded: $($last.last_generation.error)" }
            } catch [System.Net.Http.HttpRequestException] { }
              # PowerShell 7 surfaces Invoke-WebRequest -TimeoutSec timeouts as TaskCanceledException (verified locally).
              catch [System.Threading.Tasks.TaskCanceledException] { }
            Start-Sleep -Seconds 2
        }
        if (-not $last -or $last.status -ne 'ready') { throw "not ready after ${ReadyTimeoutSeconds}s; last: $($last | ConvertTo-Json -Compress)" }

        # Every chat request below names this id: the served model is the only one accepted (D77), and
        # it is not the backend selector (aion serves aion-instruct). Read before the checks below so
        # a readiness failure does not also fail every later step for want of a model id.
        $script:servedModel = $last.model
        if ([string]::IsNullOrWhiteSpace($servedModel)) { throw '/healthz carries no model id' }

        # What ready has to mean per backend, so a start without identity, or one whose runtime
        # bootstrap was skipped, cannot pass as healthy (issue #15). Phi Silica cannot be ready without
        # identity, whoever started it. The fake and aion run by path when this script starts them and
        # must say so; a server someone else started (-NoStart) is tested as found.
        if ($Backend -eq 'phi-silica') {
            if ($last.package_identity -ne $true) { throw "phi-silica is ready without package identity (package_identity=$($last.package_identity)); the relaunch through package activation did not happen" }
            if ($last.diagnostics.bootstrap -ne 'ok') { throw "phi-silica diagnostics.bootstrap='$($last.diagnostics.bootstrap)', expected 'ok'" }
        }
        elseif (-not $NoStart -and $last.package_identity -ne $false) { throw "$Backend reports package_identity=$($last.package_identity); started by path, it should have none" }
        "backend=$($last.backend) model=$($last.model) identity=$($last.package_identity) loading=$($last.loading_seconds)s diagnostics=$($last.diagnostics | ConvertTo-Json -Compress)"
    }

    Step 'startup fails promptly when the listen port is already in use' -Pins @('D37', '#15') {
        $failureExe = Get-ChildItem (Join-Path $repo 'src\NpuBridge\bin') -Recurse -Filter NpuBridge.exe -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $failureExe) { throw 'NpuBridge.exe not found; run: dotnet build src/NpuBridge' }
        $failurePort = $Port + 4
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $failurePort)
        $failureProcess = $null
        $outputLog = Join-Path $env:TEMP "npu-bridge-smoke-port-in-use-$failurePort.log"
        $errorLog = "$outputLog.err"
        $expectedMessage = 'npu-bridge could not start.'
        try {
            $listener.Start()
            $failureProcess = Start-Process -FilePath $failureExe.FullName -ArgumentList '--backend', 'fake', '--listen', "http://127.0.0.1:$failurePort" `
                -RedirectStandardOutput $outputLog -RedirectStandardError $errorLog -PassThru -NoNewWindow
            if (-not $failureProcess.WaitForExit(10000)) {
                throw "process did not exit within 10 seconds while port $failurePort was held"
            }
            if ($failureProcess.ExitCode -eq 0) { throw "process exited 0 while port $failurePort was held" }
            $output = (Get-Content -Raw $outputLog -ErrorAction SilentlyContinue) + (Get-Content -Raw $errorLog -ErrorAction SilentlyContinue)
            if (-not $output.Contains($expectedMessage)) {
                throw "exit code $($failureProcess.ExitCode) did not report '$expectedMessage': $output"
            }
            "exit=$($failureProcess.ExitCode) within 10 seconds; reported '$expectedMessage'"
        }
        finally {
            if ($failureProcess -and -not $failureProcess.HasExited) { Stop-Process -Id $failureProcess.Id -Force }
            $listener.Stop()
        }
    }

    Step '--self-relaunch off reports missing Phi Silica package identity' -Pins @('D24', 'D37', 'D38', '#15') {
        if ($Backend -ne 'phi-silica') {
            Skip '--self-relaunch off needs phi-silica; fake and aion do not require package identity.'
        }

        $identityExe = Get-ChildItem (Join-Path $repo 'src\NpuBridge\bin') -Recurse -Filter NpuBridge.exe -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $identityExe) { throw 'NpuBridge.exe not found; run: dotnet build src/NpuBridge' }
        $identityPort = $Port + 4
        $identityProcess = $null
        $expectedIdentityMessage = 'Phi Silica requires package identity, and this process has none. Register the sparse package (scripts\identity.ps1 -Install) and start npu-bridge through package activation; with --self-relaunch on (default) that happens automatically.'
        try {
            $identityProcess = Start-Process -FilePath $identityExe.FullName -ArgumentList '--self-relaunch', 'off', '--backend', 'phi-silica', '--listen', "http://127.0.0.1:$identityPort" `
                -RedirectStandardOutput (Join-Path $env:TEMP "npu-bridge-smoke-no-identity-$identityPort.log") `
                -RedirectStandardError (Join-Path $env:TEMP "npu-bridge-smoke-no-identity-$identityPort.log.err") -PassThru -NoNewWindow
            $deadline = (Get-Date).AddSeconds(30)
            $health = $null
            $statusCode = $null
            while ((Get-Date) -lt $deadline) {
                if ($identityProcess.HasExited) { throw "--self-relaunch off process exited with code $($identityProcess.ExitCode) before /healthz replied" }
                try {
                    $response = Invoke-WebRequest -Uri "http://127.0.0.1:$identityPort/healthz" -SkipHttpErrorCheck -TimeoutSec 5
                    $statusCode = [int]$response.StatusCode
                    $health = $response.Content | ConvertFrom-Json -Depth 20
                    if ($statusCode -eq 503 -and $health.status -eq 'failed') { break }
                } catch [System.Net.Http.HttpRequestException] { }
                  catch [System.Threading.Tasks.TaskCanceledException] { }
                Start-Sleep -Milliseconds 250
            }
            if ($statusCode -ne 503 -or $null -eq $health -or $health.status -ne 'failed') {
                throw "expected failed /healthz with HTTP 503 within 30 seconds; status=$statusCode body=$($health | ConvertTo-Json -Compress)"
            }
            if ($health.error -ne $expectedIdentityMessage) {
                throw "missing-identity error was '$($health.error)', expected '$expectedIdentityMessage'"
            }
            "HTTP 503 failed; reported '$expectedIdentityMessage'"
        }
        finally {
            if ($identityProcess -and -not $identityProcess.HasExited) { Stop-Process -Id $identityProcess.Id -Force }
            if ($identityProcess) {
                $gone = Wait-ServerGone $identityProcess.Id $identityPort 10
                if ($gone.Listening -or $gone.Survivors.Count -gt 0) {
                    throw "--self-relaunch off process or port $identityPort remained after stop"
                }
            }
        }
    }

    Step 'GET /v1/models lists the backend model' -Pins @('D77') {
        $m = Get-Json '/v1/models'
        if ($m.object -ne 'list' -or $m.data.Count -ne 1) { throw "unexpected: $($m | ConvertTo-Json -Compress)" }
        "model id=$($m.data[0].id)"
    }

    # --- raw generation through the backend (diagnostic endpoint) -----------
    Step 'POST /debug/generate produces text from the model' -Pins @('-') {
        $body = @{ prompt = 'In one short sentence, what is a neural processing unit?' } | ConvertTo-Json
        $generation = Get-FirstDebugGeneration $body
        $g = $generation.Json
        if ($g.status -ne 'Complete') {
            if ($generation.Retried) { throw "first-generation RPC fault: first error=$($generation.FirstError); retry error=$($generation.RetryBody)" }
            throw "status=$($g.status) detail=$($g.detail) text='$($g.text)'"
        }
        if (-not $g.text) {
            if ($generation.Retried) { throw "first-generation RPC fault: first error=$($generation.FirstError); retry error=$($generation.RetryBody)" }
            throw 'empty text'
        }
        $retryDetail = if ($generation.Retried) { '; first attempt hit known first-generation RPC fault and retry passed' } else { '' }
        "callbacks=$($g.progress_callbacks) chars=$($g.chars) ttft=$($g.ttft_ms)ms total=$($g.total_ms)ms text='$($g.text.Trim().Substring(0, [Math]::Min(120, $g.text.Trim().Length)))'$retryDetail"
    }

    Step 'preflight reports the whole short prompt as usable' -Pins @('D66', 'D73') {
        $g = Get-Json '/debug/generate' 'POST' (@{ prompt = 'Say OK.' } | ConvertTo-Json)
        if ($null -eq $g.usable_prompt_chars) {
            # Aion has no GetUsablePromptLength (D66). On the two backends that do, a null here is the
            # preflight silently gone, which is exactly what the overflow step below depends on.
            if ($Backend -ne 'aion') { throw "usable_prompt_chars is null: $Backend advertises a prompt-length preflight, so the answer must not be missing" }
            'backend has no preflight (capability absent)'
        }
        elseif ($g.usable_prompt_chars -ne $g.prompt_chars) { throw "usable=$($g.usable_prompt_chars) prompt=$($g.prompt_chars)" }
        else { "usable=$($g.usable_prompt_chars) == prompt=$($g.prompt_chars)" }
    }

    Step 'POST /debug/generate honours a system prompt' -Pins @('D45') {
        $body = @{ prompt = 'What is your name?'; system = 'You are Ada. Always answer with exactly the two words: I am Ada.' } | ConvertTo-Json
        $g = Get-Json '/debug/generate' 'POST' $body
        if ($g.status -ne 'Complete') { throw "status=$($g.status)" }
        "text='$($g.text.Trim())' (system prompt honoured: $($g.text -match 'Ada'))"
    }

    Step 'client disconnect mid-generation is survived' -Pins @('D51') {
        if ($Backend -eq 'fake') {
            Skip 'the fake backend generates with no token delay, so there is no window in which to abort; meaningful on phi-silica and aion only'
        }
        $body = @{ prompt = 'Write a very long, detailed essay about the history of computing, at least 800 words.' } | ConvertTo-Json
        $aborted = $false
        try { $null = Get-Json '/debug/generate' 'POST' $body @(200) 2 } catch { $aborted = $true }
        if (-not $aborted) { throw 'expected the 2 s client timeout to abort the request' }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $g = Get-Json '/debug/generate' 'POST' (@{ prompt = 'Say OK.' } | ConvertTo-Json)
        $sw.Stop()
        if ($g.status -ne 'Complete') { throw "follow-up status=$($g.status)" }
        "aborted request drained; next request completed in $($sw.ElapsedMilliseconds)ms"
    }

    # --- chat completions (chunks 3 and 4) -----------------------------------
    # One gate per feature: non-streaming (chunk 3) and streaming with the client-side cut (chunk 4)
    # are built now; tool calls (chunk 7) are not, and must report SKIP, not FAIL, so a clean run
    # stays "All steps passed".
    Step 'POST /v1/chat/completions rejects an empty body' -Pins @('D77') {
        $c = Get-Json '/v1/chat/completions' 'POST' '{}' @(400)
        if ($c.error.type -ne 'invalid_request_error') { throw "error.type=$($c.error.type): $($c | ConvertTo-Json -Compress)" }
        "HTTP 400 error.type=$($c.error.type)"
    }

    Step 'POST /v1/chat/completions (non-streaming)' -Pins @('D77') {
        $body = @{
            model    = $servedModel
            messages = @(
                @{ role = 'system'; content = 'You are a terse assistant.' }
                @{ role = 'user'; content = 'Reply with exactly the word PONG.' }
            )
        } | ConvertTo-Json -Depth 5
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $c = Get-Json '/v1/chat/completions' 'POST' $body
        $sw.Stop()

        if ($c.object -ne 'chat.completion') { throw "object=$($c.object)" }
        if ($c.id -notlike 'chatcmpl-*') { throw "id=$($c.id) does not start with chatcmpl-" }
        $choice = $c.choices[0]
        if ($choice.message.role -ne 'assistant') { throw "message.role=$($choice.message.role)" }
        $text = $choice.message.content
        if (-not $text) { throw "empty content: $($c | ConvertTo-Json -Compress)" }
        if ($choice.finish_reason -ne 'stop') { throw "finish_reason=$($choice.finish_reason)" }
        $u = $c.usage
        if (-not ($u.prompt_tokens -gt 0 -and $u.completion_tokens -gt 0 -and $u.total_tokens -gt 0)) {
            throw "usage not all > 0: $($u | ConvertTo-Json -Compress)"
        }
        if ($u.total_tokens -ne ($u.prompt_tokens + $u.completion_tokens)) {
            throw "total_tokens=$($u.total_tokens) != prompt_tokens+completion_tokens=$($u.prompt_tokens + $u.completion_tokens)"
        }

        # Non-streaming: the whole JSON body is written in one shot, so there is no separate
        # time-to-first-byte to observe client-side; ttft and total are the same clock reading here.
        "ttft=$($sw.ElapsedMilliseconds)ms total=$($sw.ElapsedMilliseconds)ms (non-streaming: single write, so ttft==total) usage=$($u | ConvertTo-Json -Compress) text='$($text.Substring(0, [Math]::Min(80, $text.Length)))'"
    }

    Step 'POST /v1/chat/completions (streaming SSE)' -Pins @('D52', 'D77') {
        $body = @{
            model          = $servedModel
            stream         = $true
            stream_options = @{ include_usage = $true }
            messages       = @(
                @{ role = 'system'; content = 'You are a terse assistant.' }
                @{ role = 'user'; content = 'Reply with exactly the word PONG.' }
            )
        } | ConvertTo-Json -Depth 5

        $s = Invoke-Sse '/v1/chat/completions' $body
        if ($s.StatusCode -ne 200) { throw "HTTP $($s.StatusCode): $($s.Body)" }
        if ($s.ContentType -ne 'text/event-stream') { throw "Content-Type=$($s.ContentType)" }
        if (-not $s.Done) { throw "stream did not end with the done marker: $($s.Frames -join ' | ')" }
        if ($s.Chunks.Count -lt 2) { throw "only $($s.Chunks.Count) chunk(s): $($s.Frames -join ' | ')" }

        # The role chunk opens the assistant message; OpenAI clients build the message from it.
        $first = $s.Chunks[0]
        if ($first.choices[0].delta.role -ne 'assistant') { throw "first chunk is not the role chunk: $($s.Frames[0])" }
        if ($first.id -notlike 'chatcmpl-*') { throw "id=$($first.id) does not start with chatcmpl-" }

        # One reply, one identity: a client stitching the chunks together must see the id, created and
        # model a non-streamed reply would have carried, on every chunk including the usage one.
        foreach ($c in $s.Chunks) {
            if ($c.object -ne 'chat.completion.chunk') { throw "object=$($c.object)" }
            if ($c.id -ne $first.id -or $c.created -ne $first.created -or $c.model -ne $first.model) {
                throw "identity drifted: id=$($c.id) created=$($c.created) model=$($c.model) vs first id=$($first.id) created=$($first.created) model=$($first.model)"
            }
        }

        if (-not $s.Content) { throw 'the content deltas concatenate to nothing' }

        $finishes = Get-FinishReasons $s.Chunks
        if ($finishes.Count -ne 1) { throw "$($finishes.Count) chunks carry a finish_reason, expected 1: $($finishes -join ',')" }
        if ($finishes[0] -ne 'stop') { throw "finish_reason=$($finishes[0])" }

        # stream_options.include_usage: exactly one usage chunk, last before the done marker, with an
        # empty choices array so a client indexing choices[0] on every chunk sees no phantom delta.
        $withUsage = @($s.Chunks | Where-Object { $null -ne $_.usage })
        if ($withUsage.Count -ne 1) { throw "$($withUsage.Count) usage chunks, expected exactly 1" }
        $last = $s.Chunks[-1]
        if ($null -eq $last.usage) { throw 'the usage chunk is not the last chunk before the done marker' }
        if ($last.choices.Count -ne 0) { throw "the usage chunk carries $($last.choices.Count) choice(s), expected none" }
        $u = $last.usage
        if (-not ($u.prompt_tokens -gt 0 -and $u.completion_tokens -gt 0 -and $u.total_tokens -eq ($u.prompt_tokens + $u.completion_tokens))) {
            throw "usage=$($u | ConvertTo-Json -Compress)"
        }

        # Streaming is the only place time-to-first-token is directly observable client-side. headers is
        # the D52 number: nothing is written until the first delta or the first keep-alive, so this is
        # how long a client waits on a status line.
        $preview = $s.Content.Trim()
        "chunks=$($s.Chunks.Count) ttft=$($s.FirstChunkMs)ms total=$($s.TotalMs)ms headers=$($s.HeaderMs)ms keep-alives=$($s.KeepAlives) usage=$($u | ConvertTo-Json -Compress) text='$($preview.Substring(0, [Math]::Min(80, $preview.Length)))'"
    }

    # A one-word reply says nothing about decode speed, so the throughput number comes from a reply
    # long enough to time: estimated tokens (chars/4, D44) over the decode phase after the first chunk.
    # Reported, not asserted -- a slow model is a finding, not a broken bridge.
    InfoStep 'measurement: streaming throughput (estimated tokens per second)' -Pins @('D44', 'D69', 'D80') {
        $body = @{
            model          = $servedModel
            stream         = $true
            max_tokens     = 128
            stream_options = @{ include_usage = $true }
            messages       = @(@{ role = 'user'; content = 'Explain in a paragraph how a neural processing unit differs from a GPU.' })
        } | ConvertTo-Json -Depth 5
        $s = Invoke-Sse '/v1/chat/completions' $body
        # A throughput number is only meaningful for a generation that finished: an in-stream error
        # frame or a missing done marker is a failed generation, reported as such, not timed.
        if ($s.StatusCode -ne 200) { throw "HTTP $($s.StatusCode): $($s.Body)" }
        if ($s.ErrorFrames.Count -gt 0) { throw "the generation failed in-stream: $($s.ErrorFrames[0].error | ConvertTo-Json -Compress)" }
        if (-not $s.Done) { throw 'the stream did not end with the done marker' }
        $u = @($s.Chunks | Where-Object { $null -ne $_.usage })[0].usage
        if ($null -eq $u) { throw 'no usage chunk, though stream_options.include_usage was set' }
        $decodeMs = if ($null -ne $s.FirstChunkMs) { $s.TotalMs - $s.FirstChunkMs } else { $null }
        $tokS = if ($null -ne $decodeMs -and $decodeMs -gt 0 -and $u.completion_tokens -gt 1) { [Math]::Round(($u.completion_tokens - 1) * 1000.0 / $decodeMs, 1) } else { $null }
        # completion_tokens is whatever the backend's counter says (D80): Phi-3 tokens on phi-silica,
        # the chars/4 estimate (D44) elsewhere. Name it, so the number is read in the right unit.
        $counter = (Get-Json '/debug/tokenize' 'POST' (@{ text = 'probe' } | ConvertTo-Json -Compress)).counter
        "asked: one user message, max_tokens=128, streaming`nttft=$($s.FirstChunkMs)ms total=$($s.TotalMs)ms chars=$($s.Content.Length) completion_tokens=$($u.completion_tokens) chunks=$($s.Chunks.Count) finish=$((Get-FinishReasons $s.Chunks) -join ',')`ntok/s over the decode phase: $tokS ($counter tokens per second after the first chunk; $([Math]::Round($s.Content.Length / [Math]::Max(1, $u.completion_tokens), 2)) chars per token on this reply)"
    }

    Step 'streaming client-side cut (max_tokens and stop)' -Pins @('D53', 'D80') {
        # D53/D80: the cap is a budget in the backend's own tokens (Phi-3 on phi-silica, chars/4
        # elsewhere), so usage.completion_tokens lands on the cap and never above it, and the text that
        # reached the client counts exactly what usage says. Streaming is where the cut is hardest:
        # text already written cannot be recalled.
        $cap = 8
        $capBody = @{
            model          = $servedModel
            stream         = $true
            max_tokens     = $cap
            stream_options = @{ include_usage = $true }
            messages       = @(@{ role = 'user'; content = 'Write a detailed essay of at least 400 words about the history of computing.' })
        } | ConvertTo-Json -Depth 5

        $capped = Invoke-Sse '/v1/chat/completions' $capBody
        if ($capped.StatusCode -ne 200) { throw "max_tokens: HTTP $($capped.StatusCode): $($capped.Body)" }
        if (-not $capped.Done) { throw 'max_tokens: stream did not end with the done marker' }
        $capFinishes = Get-FinishReasons $capped.Chunks
        if ($capFinishes.Count -ne 1 -or $capFinishes[0] -ne 'length') {
            throw "max_tokens: finish_reason=$($capFinishes -join ',') expected exactly one 'length' (the model may have stopped on its own before the cap)"
        }
        if (-not $capped.Content) { throw 'max_tokens: no content before the cut' }
        $capUsage = @($capped.Chunks | Where-Object { $null -ne $_.usage })[0].usage
        if ($null -eq $capUsage) { throw 'max_tokens: no usage chunk, though stream_options.include_usage was set' }
        if ($capUsage.completion_tokens -gt $cap) { throw "max_tokens: usage.completion_tokens=$($capUsage.completion_tokens) > max_tokens=$cap" }
        # The text on the wire, counted by the same counter the server used: it must be what usage
        # reports, and within the cap. A chars/4 counter makes this the old cap * 4 character check.
        $counted = Get-Json '/debug/tokenize' 'POST' (@{ text = $capped.Content } | ConvertTo-Json -Compress -EscapeHandling EscapeNonAscii)
        if ([int]$counted.tokens -ne [int]$capUsage.completion_tokens) { throw "max_tokens: the streamed text counts $($counted.tokens) $($counted.counter) tokens but usage.completion_tokens=$($capUsage.completion_tokens)" }
        if ([int]$counted.tokens -gt $cap) { throw "max_tokens: $($capped.Content.Length) chars streamed = $($counted.tokens) $($counted.counter) tokens > max_tokens=$cap" }

        # The stop string is removed from the reply rather than never produced (D53), and the streaming
        # path has to hold text back to catch one that straddles two deltas.
        $stop = 'charlie'
        $ask = 'Reply with exactly this line and nothing else: alpha bravo charlie delta'
        $stopBody = @{
            model    = $servedModel
            stream   = $true
            stop     = $stop
            messages = @(@{ role = 'user'; content = $ask })
        } | ConvertTo-Json -Depth 5

        $cut = Invoke-Sse '/v1/chat/completions' $stopBody
        if ($cut.StatusCode -ne 200) { throw "stop: HTTP $($cut.StatusCode): $($cut.Body)" }
        if (-not $cut.Done) { throw 'stop: stream did not end with the done marker' }
        $cutFinishes = Get-FinishReasons $cut.Chunks
        if ($cutFinishes.Count -ne 1 -or $cutFinishes[0] -ne 'stop') { throw "stop: finish_reason=$($cutFinishes -join ',') expected exactly one 'stop'" }
        if ($cut.Content.Contains($stop)) { throw "stop: the stop string reached the client: '$($cut.Content)'" }

        # A control run of the same prompt without the cut, so the detail can say whether there was
        # anything to truncate. A model that never emits the stop string would satisfy the assertion
        # above without the feature doing any work, and that is worth knowing rather than assuming.
        $controlBody = @{ model = $servedModel; stream = $true; messages = @(@{ role = 'user'; content = $ask }) } | ConvertTo-Json -Depth 5
        $control = Invoke-Sse '/v1/chat/completions' $controlBody
        $confirmed = $control.StatusCode -eq 200 -and $control.Content.Contains($stop)
        $evidence = if ($confirmed) {
            "confirmed against a control run: without 'stop' the same prompt produced the string ($($control.Content.Length) chars, cut to $($cut.Content.Length))"
        }
        else {
            "not confirmed: the control run did not contain '$stop' either, so nothing needed truncating this time"
        }

        "max_tokens=$cap -> finish=length, $($capped.Content.Length) chars streamed = $($counted.tokens) $($counted.counter) tokens <= $cap, completion_tokens=$($capUsage.completion_tokens); stop='$stop' -> finish=stop, absent from the reply, $evidence"
    }

    # The contract both shapes rest on: GenerationResult.Text is the concatenation of the deltas the
    # adapter delivered (ILanguageModelBackend). The fake honours it by construction, so dotnet test
    # cannot check a real adapter; this does. The adapter now returns the accumulated deltas on every
    # status and counts, in /healthz, every time the runtime's own text disagreed with them and every
    # callback that arrived after a completed generation ended. Those counters are the assertion. Whether the
    # two shapes' texts match on the wire is reported but not asserted: temperature 0 on this runtime
    # is not a documented promise of determinism, so a difference there is a finding about the model.
    Step 'both shapes return the deltas the adapter delivered (text contract)' -Pins @('D67', 'D69') {
        $prompt = 'Reply with exactly the word PONG.'
        $messages = @(
            @{ role = 'system'; content = 'You are a terse assistant.' }
            @{ role = 'user'; content = $prompt }
        )
        $json = Get-Json '/v1/chat/completions' 'POST' (@{ model = $servedModel; temperature = 0; messages = $messages } | ConvertTo-Json -Depth 5)
        $sse = Invoke-Sse '/v1/chat/completions' (@{ model = $servedModel; temperature = 0; stream = $true; messages = $messages } | ConvertTo-Json -Depth 5)
        if ($sse.StatusCode -ne 200 -or -not $sse.Done) { throw "stream: HTTP $($sse.StatusCode), done=$($sse.Done)" }

        $jsonText = $json.choices[0].message.content
        $sseText = $sse.Content
        $match = if ($jsonText -eq $sseText) { 'texts match' } else { "texts differ (json=$($jsonText.Length) chars, sse=$($sseText.Length) chars)" }

        $h = Get-Json '/healthz'
        $d = $h.diagnostics
        if ($Backend -eq 'fake') {
            Skip "$match; the fake backend keeps no text-contract counters"
        }
        if ($null -eq $d.text_mismatches -or $null -eq $d.late_deltas) {
            throw "healthz diagnostics lack text_mismatches/late_deltas: $($d | ConvertTo-Json -Compress)"
        }
        if ($d.text_mismatches -ne 0) { throw "the runtime's text disagreed with the delivered deltas $($d.text_mismatches) time(s) this run" }
        if ($d.late_deltas -ne 0) { throw "$($d.late_deltas) progress callback(s) arrived after the completion barrier this run" }

        "text_mismatches=0 late_deltas=0 over every completed generation so far (a callback after a cancelled one is exempt); $match"
    }

    # --- chunk 5: the context cache and overflow handling ----------------------------------------
    Step 'context cache: a continuing conversation reuses its context and sends only the tail' -Pins @('D71', 'D72') {
        # Three requests. The first opens a conversation; its context goes into the cache. The second
        # continues it with the reply echoed back: a hit checks that context out, sends only the new
        # turn, and stores it back. The third is the control: the same transcript with the assistant
        # text altered cannot hit and replays the whole conversation on a fresh context. /healthz's
        # hit and miss counters are the evidence (contexts_cached alone cannot tell a hit from a miss
        # once the cache is full); the TTFTs are the measurement, since a hit skips re-reading the prefix.
        $q1 = 'Name one primary colour. Reply with just the colour.'
        $q2 = 'Name a different primary colour, again with just the colour.'
        $h0 = Get-Json '/healthz'

        $first = Invoke-Sse '/v1/chat/completions' (@{ model = $servedModel; stream = $true; temperature = 0; messages = @(@{ role = 'user'; content = $q1 }) } | ConvertTo-Json -Depth 5)
        if ($first.StatusCode -ne 200 -or -not $first.Done -or $first.ErrorFrames.Count -gt 0) { throw "first turn: HTTP $($first.StatusCode) done=$($first.Done) errors=$($first.ErrorFrames.Count)" }
        $reply = $first.Content
        $h1 = Get-Json '/healthz'
        if ($h1.context_cache_misses -ne $h0.context_cache_misses + 1 -or $h1.context_cache_hits -ne $h0.context_cache_hits) { throw "first turn: hits $($h0.context_cache_hits)->$($h1.context_cache_hits) misses $($h0.context_cache_misses)->$($h1.context_cache_misses); a new conversation must miss exactly once" }
        if ($h1.contexts_cached -ne [Math]::Min($h0.contexts_cached + 1, $h0.context_cache_capacity)) { throw "contexts_cached went $($h0.contexts_cached) -> $($h1.contexts_cached) after a completed generation (capacity $($h0.context_cache_capacity)); expected one more, or the bound" }

        $continue = @{ model = $servedModel; stream = $true; temperature = 0; messages = @(
            @{ role = 'user'; content = $q1 }
            @{ role = 'assistant'; content = $reply }
            @{ role = 'user'; content = $q2 }
        ) } | ConvertTo-Json -Depth 5
        $hit = Invoke-Sse '/v1/chat/completions' $continue
        if ($hit.StatusCode -ne 200 -or -not $hit.Done -or $hit.ErrorFrames.Count -gt 0) { throw "continuation: HTTP $($hit.StatusCode) done=$($hit.Done) errors=$($hit.ErrorFrames.Count)" }
        $h2 = Get-Json '/healthz'
        if ($h2.context_cache_hits -ne $h1.context_cache_hits + 1 -or $h2.context_cache_misses -ne $h1.context_cache_misses) { throw "continuation: hits $($h1.context_cache_hits)->$($h2.context_cache_hits) misses $($h1.context_cache_misses)->$($h2.context_cache_misses); the continuation of a cached conversation must hit" }
        if ($h2.contexts_cached -ne $h1.contexts_cached) { throw "contexts_cached went $($h1.contexts_cached) -> $($h2.contexts_cached) on a hit; checkout and store back must leave it unchanged" }

        $control = @{ model = $servedModel; stream = $true; temperature = 0; messages = @(
            @{ role = 'user'; content = $q1 }
            @{ role = 'assistant'; content = "$reply (edited)" }
            @{ role = 'user'; content = $q2 }
        ) } | ConvertTo-Json -Depth 5
        $miss = Invoke-Sse '/v1/chat/completions' $control
        if ($miss.StatusCode -ne 200 -or -not $miss.Done -or $miss.ErrorFrames.Count -gt 0) { throw "control: HTTP $($miss.StatusCode) done=$($miss.Done) errors=$($miss.ErrorFrames.Count)" }
        $h3 = Get-Json '/healthz'
        if ($h3.context_cache_misses -ne $h2.context_cache_misses + 1 -or $h3.context_cache_hits -ne $h2.context_cache_hits) { throw "control: hits $($h2.context_cache_hits)->$($h3.context_cache_hits) misses $($h2.context_cache_misses)->$($h3.context_cache_misses); an altered assistant turn must miss" }

        $r1 = $reply.Trim(); $r2 = $hit.Content.Trim(); $r3 = $miss.Content.Trim()
        "hits $($h0.context_cache_hits)->$($h3.context_cache_hits) misses $($h0.context_cache_misses)->$($h3.context_cache_misses) contexts_cached $($h0.contexts_cached)->$($h1.contexts_cached)->$($h2.contexts_cached)->$($h3.contexts_cached) (capacity $($h0.context_cache_capacity))`n" +
        "first turn:          ttft=$($first.FirstChunkMs)ms total=$($first.TotalMs)ms reply='$($r1.Substring(0, [Math]::Min(60, $r1.Length)))'`n" +
        "continuation (hit):  ttft=$($hit.FirstChunkMs)ms total=$($hit.TotalMs)ms reply='$($r2.Substring(0, [Math]::Min(60, $r2.Length)))'`n" +
        "control (miss):      ttft=$($miss.FirstChunkMs)ms total=$($miss.TotalMs)ms reply='$($r3.Substring(0, [Math]::Min(60, $r3.Length)))'"
    }

    Step 'overflow: an over-length transcript is refused by the preflight, and truncated with --truncate-history' -Pins @('D55', 'D73') {
        # Eight exchanges of about two thousand characters each: some 17,000 rendered characters,
        # past the 13,429 Phi Silica said fit (D55). On a backend with a preflight the refusal must
        # arrive without a generation and therefore quickly -- the 26 s of D55 was the cost of asking
        # the generation instead. Then the same transcript against a second server started with
        # --truncate-history, which must answer, and say in a header how many turns it dropped.
        $probe = Get-Json '/debug/generate' 'POST' (@{ prompt = 'Say OK.' } | ConvertTo-Json)
        $hasPreflight = $null -ne $probe.usable_prompt_chars
        if ($Backend -eq 'fake') { Skip 'the fake backend has no context window, so nothing overflows' }

        $filler = 'The quick brown fox jumps over the lazy dog. ' * 45
        $messages = [System.Collections.Generic.List[object]]::new()
        for ($i = 1; $i -le 8; $i++) {
            $messages.Add(@{ role = 'user'; content = "Note ${i}: $filler" })
            $messages.Add(@{ role = 'assistant'; content = "Noted ${i}." })
        }
        $messages.Add(@{ role = 'user'; content = 'Reply with exactly the word PONG.' })
        $body = @{ model = $servedModel; temperature = 0; messages = $messages.ToArray() } | ConvertTo-Json -Depth 5
        $chars = ($messages | ForEach-Object { $_.content.Length } | Measure-Object -Sum).Sum
        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add("asked: $($messages.Count) messages, $chars characters of content, preflight=$hasPreflight")

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-WebRequest -Uri "$base/v1/chat/completions" -Method POST -Body $body -ContentType 'application/json' -SkipHttpErrorCheck -TimeoutSec 300
        $sw.Stop()
        $verdictMs = $sw.ElapsedMilliseconds
        if ([int]$r.StatusCode -eq 200) {
            $lines.Add("main server (no --truncate-history): HTTP 200 after $verdictMs ms -- this backend accepted the whole transcript, so there was no overflow to refuse")
            Skip ($lines -join "`n")
        }
        $e = ($r.Content | ConvertFrom-Json).error
        if ($hasPreflight) {
            if ([int]$r.StatusCode -ne 400 -or $e.code -ne 'context_length_exceeded') { throw "expected 400 context_length_exceeded from the preflight; got HTTP $($r.StatusCode) code=$($e.code): $($e.message)" }
            if ($verdictMs -gt 5000) { throw "the preflight verdict took $verdictMs ms; a refusal that costs a generation (D55) is what the preflight exists to avoid" }
            $lines.Add("main server: HTTP 400 code=context_length_exceeded after $verdictMs ms, before any generation (D55 closed): '$($e.message)'")
        }
        else {
            $lines.Add("main server (no preflight): HTTP $($r.StatusCode) code=$($e.code) after $verdictMs ms -- the verdict came from the generation: '$($e.message)'")
        }

        if ($NoStart) {
            $lines.Add('truncation half skipped: -NoStart is set, and it needs a second server started with --truncate-history')
            $lines -join "`n"
            return
        }

        $auxPort = $Port + 2
        $aux = Start-AuxServer 'truncate' $auxPort @('--truncate-history')
        try {
            $sw.Restart()
            $t = Invoke-WebRequest -Uri "http://127.0.0.1:$auxPort/v1/chat/completions" -Method POST -Body $body -ContentType 'application/json' -SkipHttpErrorCheck -TimeoutSec 300
            $sw.Stop()
            if ([int]$t.StatusCode -ne 200) { throw "--truncate-history server: HTTP $($t.StatusCode): $($t.Content)" }
            $dropped = $t.Headers['x-npu-bridge-truncated-turns']
            if (-not $dropped) { throw 'the reply carried no x-npu-bridge-truncated-turns header, so nothing was dropped -- yet the same transcript was refused above' }
            $c = $t.Content | ConvertFrom-Json
            $text = $c.choices[0].message.content
            if (-not $text) { throw "empty content after truncation: $($t.Content)" }
            $lines.Add("--truncate-history server: HTTP 200 after $($sw.ElapsedMilliseconds) ms, x-npu-bridge-truncated-turns=$dropped, finish=$($c.choices[0].finish_reason), prompt_tokens=$($c.usage.prompt_tokens), reply='$($text.Trim().Substring(0, [Math]::Min(60, $text.Trim().Length)))'")
        }
        finally {
            Stop-AuxServer $aux $auxPort 'truncate-history'
        }

        $lines -join "`n"
    }

    Step 'native system text is refused before CreateContext' -Pins @('D97') {
        # D97: this hardware probe targets the character-ceiling branch with a 1,000-character margin.
        $systemText = [string]::new('x', 33000)

        $cachedBefore = (Get-Json '/healthz').contexts_cached
        $body = @{ model = $servedModel; temperature = 0; messages = @(
            @{ role = 'system'; content = $systemText }
            @{ role = 'user'; content = 'Reply with exactly the word PONG.' }
        ) } | ConvertTo-Json -Depth 5
        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add('character ceiling probe: 33,000 system characters')

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $response = Invoke-WebRequest -Uri "$base/v1/chat/completions" -Method POST -Body $body -ContentType 'application/json' -SkipHttpErrorCheck -TimeoutSec 30
        $sw.Stop()
        $characterError = $response.Content | ConvertFrom-Json -Depth 20
        if ([int]$response.StatusCode -ne 400 -or $characterError.error.code -ne 'context_length_exceeded') { throw "character-ceiling guard: expected HTTP 400 context_length_exceeded; got HTTP $($response.StatusCode): $($response.Content)" }
        if ($characterError.error.message -notmatch 'system text alone exceeds the 32,000-character safety ceiling') { throw "character-ceiling guard: missing native-system detail: $($characterError.error.message)" }
        if ($sw.ElapsedMilliseconds -ge 2000) { throw "character-ceiling guard took $($sw.ElapsedMilliseconds) ms, not under 2,000 ms" }
        if ((Get-Json '/healthz').contexts_cached -ne $cachedBefore) { throw 'character-ceiling guard changed contexts_cached despite creating no context' }
        $lines.Add("character-ceiling guard: HTTP 400 after $($sw.ElapsedMilliseconds) ms, cache stayed $cachedBefore")

        $streamBody = @{ model = $servedModel; temperature = 0; stream = $true; messages = @(
            @{ role = 'system'; content = $systemText }
            @{ role = 'user'; content = 'Reply with exactly the word PONG.' }
        ) } | ConvertTo-Json -Depth 5
        $stream = Invoke-Sse '/v1/chat/completions' $streamBody 30
        if ($stream.StatusCode -ne 400 -or $stream.Frames.Count -ne 0) { throw "streamed character-ceiling guard: expected plain HTTP 400 before any frame; got HTTP $($stream.StatusCode), frames=$($stream.Frames.Count): $($stream.Body)" }
        $streamError = $stream.Body | ConvertFrom-Json -Depth 20
        if ($streamError.error.code -ne 'context_length_exceeded' -or $streamError.error.message -notmatch 'system text alone exceeds the 32,000-character safety ceiling') { throw "streamed character-ceiling guard returned the wrong error: $($stream.Body)" }
        $lines.Add('streamed character-ceiling guard: plain HTTP 400 before any SSE frame')
        $lines -join [Environment]::NewLine
    }

    Step 'native system text token-window guard is refused before CreateContext' -Pins @('D97') {
        # D97: this hardware probe targets the token-window branch below the character ceiling.
        $health = Get-Json '/healthz'
        if ($null -eq $health.context_window_tokens) {
            Skip 'backend does not report context_window_tokens: no token-window guard to probe'
        }

        $systemText = [string]::new('x', 20000)
        $tokenized = Get-Json '/debug/tokenize' 'POST' (@{ text = $systemText } | ConvertTo-Json -Compress)
        $reportedWindow = [int]$health.context_window_tokens
        $systemTokens = [int]$tokenized.tokens
        if ($systemText.Length -ge 32000) { throw "token-window guard probe must stay below the 32,000-character ceiling; got $($systemText.Length)" }
        if ($systemTokens -le $reportedWindow) { throw "token-window guard probe is too short: /debug/tokenize reports $systemTokens tokens, not above the $reportedWindow-token usable window" }

        $body = @{ model = $servedModel; temperature = 0; messages = @(
            @{ role = 'system'; content = $systemText }
            @{ role = 'user'; content = 'Reply with exactly the word PONG.' }
        ) } | ConvertTo-Json -Depth 5
        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add("token-window probe: $($systemText.Length) system characters, /debug/tokenize=$systemTokens tokens > context_window_tokens=$reportedWindow")

        $response = Invoke-WebRequest -Uri "$base/v1/chat/completions" -Method POST -Body $body -ContentType 'application/json' -SkipHttpErrorCheck -TimeoutSec 30
        $tokenError = $response.Content | ConvertFrom-Json -Depth 20
        if ([int]$response.StatusCode -ne 400 -or $tokenError.error.code -ne 'context_length_exceeded') { throw "token-window guard: expected HTTP 400 context_length_exceeded; got HTTP $($response.StatusCode): $($response.Content)" }
        if ($tokenError.error.message -notmatch 'Native system text alone exceeds the context window:.*tokens fills the .*token usable window') { throw "token-window guard: expected the token-window detail, got: $($tokenError.error.message)" }
        $lines.Add('token-window guard: HTTP 400 context_length_exceeded naming the token window before CreateContext')

        $stream = Invoke-Sse '/v1/chat/completions' $body 30
        if ($stream.StatusCode -ne 400 -or $stream.Frames.Count -ne 0) { throw "streamed token-window guard: expected plain HTTP 400 before any frame; got HTTP $($stream.StatusCode), frames=$($stream.Frames.Count): $($stream.Body)" }
        $streamError = $stream.Body | ConvertFrom-Json -Depth 20
        if ($streamError.error.code -ne 'context_length_exceeded' -or $streamError.error.message -notmatch 'Native system text alone exceeds the context window:.*tokens fills the .*token usable window') { throw "streamed token-window guard returned the wrong error: $($stream.Body)" }
        $lines.Add('streamed token-window guard: plain HTTP 400 context_length_exceeded before any SSE frame')
        $lines -join [Environment]::NewLine
    }

    # The D80 measurement, repeated per build: the preflight is the runtime's own tokenizer answering
    # "this many characters fit", so if the bridge's counter is the runtime's, every text's fitting
    # prefix counts the same number of tokens. Three texts with very different characters per token;
    # the preflight is read off the 400 a lone over-length user message earns, which is passed to the
    # model raw (D71), with no generation. A fake or a backend that counts chars/4 has nothing to compare.
    Step 'tokenizer: the preflight boundary is the same token count for every text (D80)' -Pins @('D55', 'D71', 'D80') {
        # Only a backend with a measured tokenizer gets here, and every such backend has the preflight the
        # tokenizer was measured against; a missing preflight shows up below as a non-400 or a message
        # without the preflight's numbers, so no generation is spent finding out first.
        $t = Get-Json '/debug/tokenize' 'POST' (@{ text = 'probe' } | ConvertTo-Json -Compress)
        if ($t.counter -eq 'chars/4') { Skip "$Backend counts chars/4: no measured tokenizer to compare with the preflight" }
        $health = Get-Json '/healthz'

        $fox = ('The quick brown fox jumps over the lazy dog. ' * 5000) + "`nSummarize the text above in one sentence."
        $json = '[' + ((1..3000 | ForEach-Object { "{`"id`":$_,`"value`":$(($_ * 7919) % 10007),`"tag`":`"item-$_`"}" }) -join ',') + ']'
        $cjk = (-join (0x673A, 0x5668, 0x5B66, 0x4E60, 0x662F, 0x4EBA, 0x5DE5, 0x667A, 0x80FD, 0x7684, 0x4E00, 0x4E2A, 0x5206, 0x652F, 0x3002 | ForEach-Object { [char]$_ })) * 3000
        $texts = [ordered]@{ 'fox filler' = $fox; 'json objects' = $json; 'cjk' = $cjk }

        $rows = foreach ($name in $texts.Keys) {
            $text = $texts[$name]
            $body = @{ model = $servedModel; messages = @(@{ role = 'user'; content = $text }) } | ConvertTo-Json -Compress -Depth 5 -EscapeHandling EscapeNonAscii
            $e = Get-Json '/v1/chat/completions' 'POST' $body -expect 400
            $m = [regex]::Match([string]$e.error.message, 'can take (\d+) characters of the (\d+)-character prompt')
            if (-not $m.Success) { throw "${name}: expected the preflight refusal, got: $($e.error.message)" }
            if ([int]$m.Groups[2].Value -ne $text.Length) { throw "${name}: the message says the prompt is $($m.Groups[2].Value) characters but the lone user message is $($text.Length); it was not passed raw" }
            $usable = [int]$m.Groups[1].Value
            $c = Get-Json '/debug/tokenize' 'POST' (@{ text = $text.Substring(0, $usable) } | ConvertTo-Json -Compress -EscapeHandling EscapeNonAscii)
            [pscustomobject]@{ Name = $name; Usable = $usable; Tokens = [int]$c.tokens; CharsPerToken = [Math]::Round($usable / [Math]::Max(1, [int]$c.tokens), 2) }
        }

        $max = ($rows | Measure-Object Tokens -Maximum).Maximum
        $min = ($rows | Measure-Object Tokens -Minimum).Minimum
        $spread = $max - $min
        $tolerance = [Math]::Ceiling($max * 0.02)
        $lines = @($rows | ForEach-Object { "$($_.Name): $($_.Usable) chars fit = $($_.Tokens) $($t.counter) tokens ($($_.CharsPerToken) chars/token)" })
        # Two per cent covers the punctuation-cluster drift measured on 2026-09-11 (about 1 %); a
        # different vocabulary, or the preflight read in the wrong units (bytes as chars puts CJK at
        # three times the count), lands far outside it.
        if ($spread -gt $tolerance) {
            throw (($lines + "the counts differ by $spread tokens, more than 2 % of ${max}: the runtime's tokenizer is not the bridge's counter, or the preflight is being read in the wrong units") -join '; ')
        }
        if ($null -eq $health.context_window_tokens) {
            $lines += 'context_window_tokens is null; D80 boundary cross-check skipped'
        }
        else {
            $reportedWindow = [int]$health.context_window_tokens
            if ($reportedWindow -lt ($min - $tolerance) -or $reportedWindow -gt ($max + $tolerance)) {
                throw (($lines + "the measured D80 boundary range is $min-$max $($t.counter) tokens, but /healthz reports context_window_tokens=$reportedWindow outside its 2 % widened bracket") -join '; ')
            }
            $lines += "context_window_tokens=$reportedWindow; measured D80 boundary=$max $($t.counter) tokens"
        }
        ($lines + "spread $spread tokens; the usable window of an empty context is about $max $($t.counter) tokens") -join "`n"
    }

    if ($ToolProbeRuns -gt 0) {
        # What the bridge guarantees and what the model manages are different questions, and this step
        # only fails on the first. The bridge must answer every run with a well-formed reply -- either
        # tool_calls with content null and finish_reason "tool_calls", or ordinary content -- and never
        # with a call to a tool that was not offered, arguments that are not JSON, or the raw protocol
        # leaking out as content. Whether the model chooses to call at all is the measurement, and PLAN
        # section 2.6 expects 60 to 80 % on a model this size: a low rate is a finding to record, not a
        # failing step.
        Step "tool-call compliance probe ($ToolProbeRuns runs)" -Pins @('D83') {
            $tools = @(@{
                type     = 'function'
                function = @{
                    name        = 'get_weather'
                    description = 'Get the current weather for a city'
                    parameters  = @{
                        type       = 'object'
                        properties = @{ location = @{ type = 'string'; description = 'City name' } }
                        required   = @('location')
                    }
                }
            })

            $called = 0
            $prose = 0
            $names = @{}
            $badArguments = 0
            $leaked = 0

            for ($i = 0; $i -lt $ToolProbeRuns; $i++) {
                $body = @{
                    model    = $servedModel
                    messages = @(@{ role = 'user'; content = 'What is the weather in Paris right now?' })
                    tools    = $tools
                } | ConvertTo-Json -Depth 10

                $r = Get-Json '/v1/chat/completions' 'POST' $body
                $choice = $r.choices[0]

                if ($choice.finish_reason -eq 'tool_calls') {
                    $called++
                    if ($null -ne $choice.message.content) {
                        throw "run $($i + 1): finish_reason tool_calls but content was not null"
                    }
                    foreach ($call in $choice.message.tool_calls) {
                        $names[$call.function.name] = $true
                        try { $null = $call.function.arguments | ConvertFrom-Json -Depth 10 }
                        catch { $badArguments++ }
                    }
                } else {
                    $prose++
                    if ($choice.finish_reason -ne 'stop' -and $choice.finish_reason -ne 'length') {
                        throw "run $($i + 1): unexpected finish_reason $($choice.finish_reason)"
                    }
                    # The protocol reaching the client as prose means the parser missed a shape the
                    # model actually produces, which is the one failure of this feature that matters.
                    if ($choice.message.content -match '"tool_calls"\s*:') { $leaked++ }
                }
            }

            if ($badArguments -gt 0) { throw "$badArguments call(s) carried arguments that are not JSON" }
            if ($leaked -gt 0) { throw "$leaked repl(y|ies) leaked tool-call JSON as content; the parser missed a real shape" }

            # A tool nobody offered is the model's mistake and the bridge is *required* to surface it
            # for the client to decide (PLAN §2.6 item 3), so it is counted and reported rather than
            # failed. Failing here would fail the probe for behaving as designed.
            $unexpected = @($names.Keys | Where-Object { $_ -ne 'get_weather' })
            $unexpectedNote = if ($unexpected.Count -gt 0) { "; surfaced unoffered tool(s): $($unexpected -join ', ')" } else { '; no unoffered tool' }

            $rate = [math]::Round(100.0 * $called / $ToolProbeRuns, 0)
            "$called/$ToolProbeRuns called the tool ($rate %), $prose answered in prose; no leaked protocol$unexpectedNote, all arguments valid JSON. PLAN expects 60-80 % on this model size."
        }
    }

    # --- chunk 8: the generation scheduler and legacy /v1/completions -----------------------------
    # Issue #4's own requirement, and the gap docs/FUTURE.md records by name: "two simultaneous requests
    # against a real NPU are entirely untested." Every step above this one has run one request at a time.
    Step 'two concurrent requests share the queue: the second genuinely waits behind the first' -Pins @('D84', 'D87') {
        # The fake backend generates with no per-token delay (the same reason 'client disconnect
        # mid-generation' skips it above), so the window in which the second request is provably still
        # queued -- rather than already finished, or never queued because the fake finished too fast to
        # overlap the poll below -- is too narrow to catch reliably. Meaningful on phi-silica and aion,
        # where a real generation runs long enough for a 100 ms poll loop to land inside it.
        if ($Backend -eq 'fake') { Skip 'the fake backend has no generation delay; the queueing window is too narrow to observe reliably' }

        $bodyA = @{ model = $servedModel; max_tokens = 64; messages = @(@{ role = 'user'; content = 'Write a detailed essay of at least 400 words about the history of computing.' }) } | ConvertTo-Json -Depth 5
        $bodyB = @{ model = $servedModel; max_tokens = 16; messages = @(@{ role = 'user'; content = 'Reply with exactly the word PONG.' }) } | ConvertTo-Json -Depth 5

        # Both fired before either is awaited (the async idiom Invoke-Sse already uses internally for its
        # own single request), so the two are genuinely concurrent rather than merely close in time.
        $pendingA = Start-JsonRequest '/v1/chat/completions' $bodyA
        $pendingB = Start-JsonRequest '/v1/chat/completions' $bodyB

        # The proof is /healthz's own live queue_depth (chunk 8), never a duration: it counts only jobs
        # the worker has not yet reached, so seeing it reach 1 while both requests are still outstanding
        # is direct evidence the second waited on the scheduler rather than getting its own context the
        # way chunk 5 left two concurrent requests able to. Polled, not slept for -- the loop's own exit
        # is bounded by the two requests finishing (or a generous deadline), never by a fixed clock, and
        # nothing below this point depends on how long either request took.
        $maxDepth = 0
        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline -and -not ($pendingA.Task.IsCompleted -and $pendingB.Task.IsCompleted)) {
            $h = Get-Json '/healthz'
            if ($h.queue_depth -gt $maxDepth) { $maxDepth = $h.queue_depth }
            if ($maxDepth -ge 1) { break }
            Start-Sleep -Milliseconds 100
        }

        $a = Complete-JsonRequest $pendingA
        $b = Complete-JsonRequest $pendingB

        if ($a.StatusCode -ne 200) { throw "request A: HTTP $($a.StatusCode): $($a.Body)" }
        if ($b.StatusCode -ne 200) { throw "request B: HTTP $($b.StatusCode): $($b.Body)" }
        if (-not $a.Json.choices[0].message.content) { throw "request A: empty content: $($a.Body)" }
        if (-not $b.Json.choices[0].message.content) { throw "request B: empty content: $($b.Body)" }
        if ($maxDepth -lt 1) { throw "queue_depth never reached 1 while both requests were in flight (observed max $maxDepth); the second request may not have queued behind the first" }

        "both requests completed (A: $($a.Json.usage.completion_tokens) completion tokens, B: $($b.Json.usage.completion_tokens)); queue_depth peaked at $maxDepth while both were outstanding, proving the second genuinely waited on the scheduler rather than getting its own context"
    }

    Step 'POST /v1/completions (legacy, non-streaming)' -Pins @('D77', 'D91') {
        $body = @{ model = $servedModel; prompt = 'Reply with exactly the word PONG.' } | ConvertTo-Json -Depth 5
        $c = Get-Json '/v1/completions' 'POST' $body
        if ($c.object -ne 'text_completion') { throw "object=$($c.object)" }
        # Same id allocator as the chat shape (ChatCompletionId.NewId): both endpoints hand out
        # chatcmpl- ids, there is no separate cmpl- prefix on this bridge.
        if ($c.id -notlike 'chatcmpl-*') { throw "id=$($c.id) does not start with chatcmpl-" }
        $choice = $c.choices[0]
        if ([string]::IsNullOrEmpty($choice.text)) { throw "empty text: $($c | ConvertTo-Json -Compress)" }
        if ($choice.index -ne 0) { throw "index=$($choice.index)" }
        if ($choice.finish_reason -ne 'stop') { throw "finish_reason=$($choice.finish_reason)" }
        if ($choice.PSObject.Properties.Name -notcontains 'logprobs') { throw 'choices[0] carries no logprobs field, even as an explicit null (D77 convention)' }
        $u = $c.usage
        if (-not ($u.prompt_tokens -gt 0 -and $u.completion_tokens -gt 0 -and $u.total_tokens -eq ($u.prompt_tokens + $u.completion_tokens))) {
            throw "usage=$($u | ConvertTo-Json -Compress)"
        }
        "id=$($c.id) text='$($choice.text.Trim())' usage=$($u | ConvertTo-Json -Compress)"
    }

    Step 'POST /v1/completions (legacy, streaming SSE)' -Pins @('D91') {
        $body = @{
            model          = $servedModel
            stream         = $true
            stream_options = @{ include_usage = $true }
            prompt         = 'Reply with exactly the word PONG.'
        } | ConvertTo-Json -Depth 5

        $s = Invoke-Sse '/v1/completions' $body
        if ($s.StatusCode -ne 200) { throw "HTTP $($s.StatusCode): $($s.Body)" }
        if ($s.ContentType -ne 'text/event-stream') { throw "Content-Type=$($s.ContentType)" }
        if (-not $s.Done) { throw "stream did not end with the done marker: $($s.Frames -join ' | ')" }
        if ($s.Chunks.Count -lt 1) { throw "no chunks: $($s.Frames -join ' | ')" }

        foreach ($c in $s.Chunks) {
            if ($c.object -ne 'text_completion') { throw "object=$($c.object)" }
        }

        # The legacy shape's own contract: text lives directly on the choice, never nested in a delta the
        # way the chat shape's chunks carry it. Invoke-Sse's frame reader knows nothing about either wire
        # shape, so $s.Content (built off .delta.content) would silently read as empty string here; this
        # step reads choices[0].text itself instead of trusting that field.
        $withChoice = @($s.Chunks | Where-Object { $_.choices.Count -gt 0 })
        if ($withChoice.Count -eq 0) { throw "no chunk carried a choice: $($s.Frames -join ' | ')" }
        foreach ($c in $withChoice) {
            if ($null -eq $c.choices[0].text) { throw "a choice-bearing chunk has no text field: $($c | ConvertTo-Json -Compress)" }
        }
        $text = -join ($withChoice | ForEach-Object { $_.choices[0].text })
        if (-not $text) { throw 'the text deltas concatenate to nothing' }

        $finishes = Get-FinishReasons $s.Chunks
        if ($finishes.Count -ne 1 -or $finishes[0] -ne 'stop') { throw "finish_reason=$($finishes -join ',') expected exactly one 'stop'" }

        $withUsage = @($s.Chunks | Where-Object { $null -ne $_.usage })
        if ($withUsage.Count -ne 1) { throw "$($withUsage.Count) usage chunks, expected exactly 1" }
        $last = $s.Chunks[-1]
        if ($last.choices.Count -ne 0) { throw "the usage chunk carries $($last.choices.Count) choice(s), expected none" }
        $u = $last.usage
        if (-not ($u.prompt_tokens -gt 0 -and $u.completion_tokens -gt 0 -and $u.total_tokens -eq ($u.prompt_tokens + $u.completion_tokens))) {
            throw "usage=$($u | ConvertTo-Json -Compress)"
        }

        "chunks=$($s.Chunks.Count) ttft=$($s.FirstChunkMs)ms total=$($s.TotalMs)ms usage=$($u | ConvertTo-Json -Compress) text='$($text.Trim())'"
    }

    Step 'queue-full: a request beyond capacity gets 429 with Retry-After' -Pins @('D84', 'D87') {
        # The fake backend's generation is fast enough that three near-simultaneous requests against a
        # capacity of one can race the worker draining the queue before the third even arrives -- the
        # same reason the concurrency step above skips it. On phi-silica and aion a real generation is
        # slow enough relative to three loopback SendAsync calls fired back to back that the race is not
        # a practical concern.
        if ($Backend -eq 'fake') { Skip 'the fake backend generates too fast for three near-simultaneous requests to reliably overlap a capacity-1 queue' }
        if ($NoStart) { Skip '-NoStart is set; this step needs a dedicated aux server started with a small --queue-capacity' }

        $auxPort = $Port + 3
        $auxProcess = Start-AuxServer 'queue-capacity-1' $auxPort @('--queue-capacity', '1')
        try {
            $auxBase = "http://127.0.0.1:$auxPort"
            $bodies = 1..3 | ForEach-Object {
                @{ model = $servedModel; max_tokens = 48; messages = @(@{ role = 'user'; content = "Write a short paragraph about topic number ${_} in the history of computing." }) } | ConvertTo-Json -Depth 5
            }

            # Fired together, not staggered: with --queue-capacity 1 the channel holds one job beyond
            # whatever the worker has already dequeued, so at most two of these three can ever be
            # admitted regardless of arrival order. The third's rejection is decided synchronously against
            # the channel's own TryWrite (GenerationScheduler.ScheduleAsync) before any generation starts,
            # so which one is rejected is not deterministic, but that at least one of three is, is.
            $pending = $bodies | ForEach-Object { Start-JsonRequest '/v1/chat/completions' $_ $auxBase }
            $completed = @($pending | ForEach-Object { Complete-JsonRequest $_ })

            $rejected = @($completed | Where-Object { $_.StatusCode -eq 429 })
            $ok = @($completed | Where-Object { $_.StatusCode -eq 200 })
            if (($ok.Count + $rejected.Count) -ne $completed.Count) {
                throw "unexpected status code(s) among the three: $(($completed | ForEach-Object StatusCode) -join ',')"
            }
            if ($rejected.Count -eq 0) {
                throw "none of 3 concurrent requests against --queue-capacity 1 got 429; got $(($completed | ForEach-Object StatusCode) -join ',')"
            }

            foreach ($r in $rejected) {
                if (-not $r.RetryAfter) { throw "429 carried no Retry-After header: $($r.Body)" }
                $e = $r.Json.error
                if ($e.type -ne 'rate_limit_error') { throw "429 error.type=$($e.type), expected rate_limit_error" }
                if ($e.code -ne 'queue_full') { throw "429 error.code=$($e.code), expected queue_full" }
                if ([string]::IsNullOrEmpty($e.message)) { throw '429 error carried no message' }
            }
            foreach ($r in $ok) {
                if (-not $r.Json.choices[0].message.content) { throw "an admitted request returned empty content: $($r.Body)" }
            }

            "of 3 concurrent requests against --queue-capacity 1: $($ok.Count) admitted (HTTP 200), $($rejected.Count) rejected (HTTP 429, Retry-After=$($rejected[0].RetryAfter)s, error.type=rate_limit_error, error.code=queue_full)"
        }
        finally {
            Stop-AuxServer $auxProcess $auxPort 'queue-capacity-1'
        }
    }

    # --- measurement 1: how far the progress-callback count and the chars/4 estimate are from the tokens
    InfoStep 'measurement: token count vs chars/4 estimate vs progress-callback count' -Pins @('D44', 'D80') {
        $prompt = 'In two or three sentences, explain what a neural processing unit does and why a Copilot+ PC has one.'

        # Single generation: read the callback count, the character count and the counted tokens off the
        # same reply, so the ratios measure the thing being decided rather than the difference between
        # two replies. On phi-silica the count is Phi-3 tokens (D80); on a chars/4 backend it is the estimate.
        $g = Get-Json '/debug/generate' 'POST' (@{ prompt = $prompt } | ConvertTo-Json)
        if ($g.status -ne 'Complete') { throw "debug/generate status=$($g.status)" }
        $callbacks = $g.progress_callbacks
        $chars = $g.chars
        $estimate = [Math]::Ceiling($chars / 4.0)
        $counted = Get-Json '/debug/tokenize' 'POST' (@{ text = [string]$g.text } | ConvertTo-Json -Compress -EscapeHandling EscapeNonAscii)
        $ratio = if ($callbacks -gt 0) { [Math]::Round($estimate / $callbacks, 2) } else { $null }
        $ratioText = if ($null -ne $ratio) { "${ratio}x" } else { 'n/a (0 callbacks)' }
        $tokenLine = if ($counted.counter -eq 'chars/4') { "counted tokens: this backend counts chars/4, so the count is the estimate ($($counted.tokens))" }
                     else { "counted tokens ($($counted.counter)) = $($counted.tokens); the chars/4 estimate is $([Math]::Round($estimate / [Math]::Max(1, [int]$counted.tokens), 2))x the count, and the callbacks $([Math]::Round($callbacks / [Math]::Max(1, [int]$counted.tokens), 2))x" }

        # Cross-check only, from a second, separate generation through the real endpoint. Deliberately
        # not folded into the ratio above: two different replies of different lengths would measure
        # that difference, not the estimate-vs-callbacks question this step exists to answer.
        $chatBody = @{ model = $servedModel; messages = @(@{ role = 'user'; content = $prompt }) } | ConvertTo-Json -Depth 5
        $c = Get-Json '/v1/chat/completions' 'POST' $chatBody
        $crossCheck = if ($c.choices[0].finish_reason -eq 'stop') {
            "usage.completion_tokens=$($c.usage.completion_tokens) for a $($c.choices[0].message.content.Length)-char reply"
        } else {
            "finish_reason=$($c.choices[0].finish_reason)"
        }

        @"
asked: one bare user-message prompt ($($prompt.Length) chars) to /debug/generate, one generation
progress-callback count (this generation) = $callbacks
raw completion character count (this generation) = $chars
chars/4 estimate derived from this generation = $estimate
$tokenLine
ratio: chars/4 estimate is $ratioText the callback count, both numbers from the same generation
cross-check (a different generation, same prompt, via /v1/chat/completions): $crossCheck -- for
comparison only, not part of the ratios above
verdict: chunk 3 chose chars/4 over counting callbacks because callbacks were measured undercounting by
roughly 4x (11 callbacks for 178 chars), and D80 replaced the estimate with the runtime's own
tokenizer on phi-silica; these ratios are both assumptions checked on one real generation.
"@
    }

    # --- measurement 2: which --system-prompt-placement does this model actually obey? -------------
    InfoStep 'measurement: system-prompt placement (native vs prompt)' -Pins @('D45', 'D50', 'D69') {
        if ($NoStart) {
            return 'skipped: -NoStart is set; this measurement starts two dedicated servers on an auxiliary port, which -NoStart precludes.'
        }

        $auxPort = $Port + 1
        $system = 'You are Ada. Always answer with exactly the two words: I am Ada.'
        $question = 'What is your name?'
        $chatBody = @{
            model    = $servedModel
            messages = @(
                @{ role = 'system'; content = $system }
                @{ role = 'user'; content = $question }
            )
        } | ConvertTo-Json -Depth 5

        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add("asked (both placements, server restarted between them): system=`"$system`" user=`"$question`"")

        $nativeUnsupported = $false
        $failures = [System.Collections.Generic.List[string]]::new()
        foreach ($placement in 'native', 'prompt') {
            $auxProc = $null
            try {
                $auxProc = Start-AuxServer "placement-$placement" $auxPort @('--system-prompt-placement', $placement)
                $r = Invoke-WebRequest -Uri "http://127.0.0.1:$auxPort/v1/chat/completions" -Method Post -Body $chatBody `
                    -ContentType 'application/json' -SkipHttpErrorCheck -TimeoutSec 120
                # D50: forcing native on a backend with no native system context rejects exactly the
                # requests that carry system text. Only on such a backend (Aion), and only for the native
                # run, is that the expected answer; the same status from Phi Silica, or from the prompt
                # run, is a regression and must read as one.
                $c = try { $r.Content | ConvertFrom-Json -Depth 20 } catch { $null }
                if ($Backend -eq 'aion' -and $placement -eq 'native' -and [int]$r.StatusCode -eq 400 -and $c.error.code -eq 'system_prompt_placement_unsupported') {
                    $nativeUnsupported = $true
                    $lines.Add("$placement -> rejected as specified (HTTP 400 system_prompt_placement_unsupported): this backend has no native system-prompt context, so a request with system text cannot be forced onto one (D50)")
                    continue
                }
                if ([int]$r.StatusCode -ne 200) { throw "HTTP $($r.StatusCode): $($r.Content)" }
                if ($null -eq $c) { throw "HTTP 200 with a body that is not JSON: $($r.Content)" }
                $text = $c.choices[0].message.content
                $obeyed = $text -match 'Ada'
                $lines.Add("$placement -> got: '$($text.Trim())' obeyed=$obeyed")
            } catch {
                $lines.Add("$placement -> error: $($_.Exception.Message)")
                $failures.Add($placement)
            } finally {
                Stop-AuxServer $auxProc $auxPort "placement=$placement"
            }
        }

        if ($failures.Count -gt 0) {
            # A run that did not answer 200 (D50's aion-native refusal excepted above) is a server that
            # failed to start under a supported option, or a request that failed on it: a defect.
            Fail (($lines + "verdict: the $($failures -join ' and ') run(s) failed; see the error line(s) above") -join "`n")
        }
        if ($nativeUnsupported) {
            $lines.Add('verdict: only folded placement exists on this backend; the "prompt" line above is whether the model obeys a system prompt rendered into the prompt body, which is the placement auto selects for it.')
        }
        else {
            $lines.Add('verdict: compare the two "obeyed" lines above. Phi Silica has twice been observed ignoring a natively delivered system prompt (docs/DECISIONS.md); this is that check run on real hardware for this build.')
        }
        $lines -join "`n"
    }

    # --- measurement 3: does cancelling a generation actually stop the accelerator? ----------------
    InfoStep 'measurement: does the client-side cut stop the NPU, or only the client?' -Pins @('D51', 'D53') {
        # The open question since the research phase: the WinRT cancel is advisory, and nobody has
        # established whether the device stops mid-generation or runs to completion regardless. The cut
        # makes it measurable, because it cancels a real generation partway through -- and because the
        # handler does not return until that generation has actually ended (cancel, drain, dispose,
        # D51), the wall clock below is the device's time and not merely the client's.
        #
        # The control side is a *generous* cap rather than no cap at all. Letting the model run to its
        # natural end took 262 s on Phi Silica and made this one step longer than the rest of the suite
        # together, for no extra information: both requests are then cut the same way, one early and one
        # late, and the question -- does the device stop when the cut fires, or keep going -- is answered
        # by the gap between them just as well.
        $prompt = 'Write a detailed essay of at least 400 words about the history of computing.'
        $earlyCap = 4                   # 16 characters: the cut fires on the first delta or two
        $lateCap = 64                   # 256 characters: enough decode to time, nowhere near the essay
        $lateBody = @{ model = $servedModel; stream = $true; max_tokens = $lateCap; messages = @(@{ role = 'user'; content = $prompt }) } | ConvertTo-Json -Depth 5
        $earlyBody = @{ model = $servedModel; stream = $true; max_tokens = $earlyCap; messages = @(@{ role = 'user'; content = $prompt }) } | ConvertTo-Json -Depth 5

        $uncapped = Invoke-Sse '/v1/chat/completions' $lateBody
        if ($uncapped.StatusCode -ne 200) { throw "control (max_tokens=$lateCap): HTTP $($uncapped.StatusCode): $($uncapped.Body)" }
        $capped = Invoke-Sse '/v1/chat/completions' $earlyBody
        if ($capped.StatusCode -ne 200) { throw "early cut (max_tokens=$earlyCap): HTTP $($capped.StatusCode): $($capped.Body)" }

        $fullMs = $uncapped.TotalMs
        $cutMs = $capped.TotalMs
        $ratio = if ($fullMs -gt 0) { [Math]::Round($cutMs / $fullMs, 2) } else { $null }

        # Prompt processing cannot be cancelled: the early-cut request can never beat its own time to
        # first token. So the decode phase -- everything after it -- is where a working cancel shows up,
        # and that ratio is the one the verdict is read off when both are measurable.
        $fullDecode = if ($null -ne $uncapped.FirstChunkMs) { $fullMs - $uncapped.FirstChunkMs } else { $null }
        $cutDecode = if ($null -ne $capped.FirstChunkMs) { $cutMs - $capped.FirstChunkMs } else { $null }
        $decodeRatio = if ($null -ne $fullDecode -and $null -ne $cutDecode -and $fullDecode -gt 0) {
            [Math]::Round($cutDecode / $fullDecode, 2)
        }
        else { $null }

        $judged = if ($null -ne $decodeRatio) { $decodeRatio } else { $ratio }
        $verdict = if ($null -eq $judged) {
            'inconclusive: neither request took measurable time, so there is nothing to compare.'
        }
        elseif ($null -ne $ratio -and $ratio -gt 1) {
            # The decode ratio alone must not declare victory while the early cut took longer overall:
            # a verdict that contradicts the numbers printed above it is worse than no verdict.
            'inconclusive: the early cut did not finish sooner end to end, so the two runs are too close to separate -- time to first token is dominating and the decode figures are noise. On a backend whose generation takes seconds this cannot happen by accident; re-run before reading anything into it.'
        }
        elseif ($judged -le 0.5) {
            'cancellation really does stop the device. The early cut finished in a small fraction of the later one, and neither request is answered until its generation has ended, so the work stopped -- the NPU was not left running behind a returned response.'
        }
        elseif ($judged -ge 0.8) {
            "cancellation is advisory only. Cutting after 16 characters took about as long as cutting after 256, so the accelerator kept generating regardless of the cut; the cap saves the client's time and its token count, not the device's work."
        }
        else {
            'inconclusive: the early cut was faster but not decisively so. The device may be stopping at the next token boundary rather than at once; re-run before quoting this.'
        }

        $caveat = if ($Backend -eq 'fake') {
            "caveat: this is the fake backend, which generates with no per-token delay; the numbers exercise the measurement, they do not answer the hardware question."
        }
        else { $null }

        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add("asked: the same prompt twice on the streaming path ($($prompt.Length) chars), cut early (max_tokens=$earlyCap, a $($earlyCap * 4)-char budget) against a control cut late (max_tokens=$lateCap, $($lateCap * 4) chars). The control is a generous cap rather than no cap, so neither side runs a full-length generation.")
        $lines.Add("control (late cut):  $fullMs ms total, ttft $($uncapped.FirstChunkMs) ms, $($uncapped.Content.Length) chars, finish=$((Get-FinishReasons $uncapped.Chunks) -join ',')")
        $lines.Add("early cut:           $cutMs ms total, ttft $($capped.FirstChunkMs) ms, $($capped.Content.Length) chars, finish=$((Get-FinishReasons $capped.Chunks) -join ',')")
        $decodeText = if ($null -ne $decodeRatio) { "$decodeRatio of it counting only the decode phase after the first token" } else { 'decode phase not separately measurable' }
        $lines.Add("ratio: the early cut took $ratio of the control end to end, $decodeText")
        $lines.Add("verdict: $verdict")
        if ($caveat) { $lines.Add($caveat) }
        $lines -join "`n"
    }

    # --- measurement 4: how long does an over-length prompt take to be refused, and how? -----------
    InfoStep 'measurement: over-length prompt -- verdict latency and where the verdict lands (D52)' -Pins @('D52', 'D55') {
        # StreamingOptions.DefaultFirstKeepAliveDelay, as the running server reports it on /healthz: the
        # number shipped code uses, not a copy of it that could drift. D52 turns on which side of it
        # the verdict lands: the streaming path writes nothing until the first token or the first
        # keep-alive, and once a keep-alive has committed the headers a failure can only travel as an
        # in-stream error frame.
        $firstKeepAliveMs = (Get-Json '/healthz').first_keep_alive_ms
        if ($null -eq $firstKeepAliveMs) { Fail '/healthz carries no first_keep_alive_ms, so the D52 margin cannot be read off the server' }

        # As big as is cheap to build rather than marginal, so the verdict is unambiguous. The last line
        # is short on purpose: the fake backend echoes it, and a backend with no context window would
        # otherwise answer with a megabyte of JSON.
        $filler = 'The quick brown fox jumps over the lazy dog. ' * 5000
        $prompt = "$filler`nSummarize the text above in one sentence."
        $body = @{ model = $servedModel; stream = $true; messages = @(@{ role = 'user'; content = $prompt }) } | ConvertTo-Json -Depth 5

        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add("asked: one user message of $($prompt.Length) chars on the streaming path")

        $s = Invoke-Sse '/v1/chat/completions' $body

        # Three ways this can end, and the difference between them is the whole point of the step. An
        # ordinary HTTP status means the verdict beat the header commit. An error frame means it did not
        # -- and that is invisible to anything reading only the status code or the finish reason, which
        # is how an earlier version of this step concluded a refused prompt had been accepted.
        $err = if ($s.ErrorFrames.Count -gt 0) { $s.ErrorFrames[0].error } else { $null }

        if ($s.StatusCode -ne 200) {
            $e = ($s.Body | ConvertFrom-Json).error
            $verdictMs = $s.HeaderMs
            $lines.Add("verdict: HTTP $($s.StatusCode) type=$($e.type) code=$($e.code) after $verdictMs ms, before a single byte was written -- the status line was still the server's to set")
            $lines.Add("message: '$($e.message)'")
            $margin = if ($verdictMs -lt ($firstKeepAliveMs * 0.2)) {
                "comfortable: the verdict lands in under a fifth of the ${firstKeepAliveMs} ms first keep-alive, so 1 s is not close to the edge"
            }
            elseif ($verdictMs -lt ($firstKeepAliveMs * 0.5)) {
                "adequate but not generous: the verdict uses more than a fifth of the ${firstKeepAliveMs} ms first keep-alive; do not lower that default"
            }
            elseif ($verdictMs -lt $firstKeepAliveMs) {
                "uncomfortable: the verdict uses more than half of the ${firstKeepAliveMs} ms first keep-alive, so a slower run would commit the headers and lose the status; raise the default"
            }
            else {
                # Not a contradiction, though it reads like one: the keep-alive timer starts only once a
                # generation is being waited on, after the body parse, the cache lookup and the preflight
                # (where the backend has one), so a refusal that phase took this long over still lands
                # as a status. The streaming tests prove the timer commits the headers; this number says
                # how slow the pre-generation verdict was.
                "exceeded: the status arrived after $verdictMs ms, past the ${firstKeepAliveMs} ms first keep-alive; the verdict came from the pre-generation phase (body parse, cache lookup, the preflight where one exists), which runs before the keep-alive timer exists, so this measures that phase's latency rather than the header deferral"
            }
            $lines.Add("margin: $margin")
        }
        elseif ($null -ne $err) {
            $over = if ($s.ErrorMs -gt 0) { [Math]::Round($s.ErrorMs / $firstKeepAliveMs, 1) } else { $null }
            $roleChunks = @($s.Chunks | Where-Object { $_.choices.Count -gt 0 -and $_.choices[0].delta.role })
            $lines.Add("verdict: an in-stream error frame after $($s.ErrorMs) ms -- type=$($err.type) code=$($err.code) param=$($err.param)")
            $lines.Add("message: '$($err.message)'")
            $lines.Add("headers: already committed at $($s.HeaderMs) ms by $($s.KeepAlives) keep-alive comment(s); role chunks before the error=$($roleChunks.Count); done marker=$($s.Done); HTTP status seen by the client=200")
            $lines.Add("margin: none. The verdict took ${over}x the ${firstKeepAliveMs} ms first keep-alive, so the headers were spent long before it arrived and no HTTP status could carry it. The stream ending in an error frame followed by the done marker is the specified behaviour for exactly this case -- it is the header deferral being unable to help, not the deferral failing.")
            if ($err.code -ne 'context_length_exceeded') {
                $lines.Add("note: the code is '$($err.code)', not 'context_length_exceeded' -- this backend did not report the prompt as too long, it reported a generic failure. See the preflight number in the cross-check below, and D55.")
            }
        }
        else {
            $finishes = (Get-FinishReasons $s.Chunks) -join ','
            $lines.Add("verdict: none. HTTP 200 after $($s.TotalMs) ms, finish=$finishes, $($s.Content.Length) chars generated, no error frame -- this backend generated a reply from the whole prompt, so there was no refusal to time (the fake has no context window).")
        }

        # Cross-check without the HTTP round trip: /debug/generate reports the server-side elapsed time
        # for the same prompt, and -- unlike the chat path -- calls the prompt-length preflight first.
        # That preflight number is the one that matters for chunk 5: it says how much of the prompt fits
        # even when the generation itself refuses with something other than PromptLargerThanContext.
        try {
            $g = Get-Json '/debug/generate' 'POST' (@{ prompt = $prompt } | ConvertTo-Json)
            $lines.Add("cross-check (/debug/generate, server-side clock, preflight included): status=$($g.status) detail='$($g.detail)' total=$($g.total_ms)ms preflight usable_prompt_chars=$($g.usable_prompt_chars) of $($g.prompt_chars)")
            if ($null -ne $g.usable_prompt_chars -and $g.usable_prompt_chars -lt $g.prompt_chars) {
                $lines.Add("preflight is decisive and cheap: it knew $($g.usable_prompt_chars) of $($g.prompt_chars) chars fit, whatever status the generation went on to report.")
            }
        }
        catch {
            $lines.Add("cross-check (/debug/generate): $($_.Exception.Message)")
        }

        $lines.Add("verdict for the log: D52 shipped a ${firstKeepAliveMs} ms first keep-alive as a reasoned default and said this script owed the measurement. The numbers above are it (D52, D55).")
        $lines -join "`n"
    }

    InfoStep 'final generation health' -Pins @('-') {
        $health = Get-Json '/healthz' -expect 200,503
        "last_generation=$($health.last_generation | ConvertTo-Json -Compress -Depth 5) consecutive_backend_faults=$($health.consecutive_backend_faults)"
    }
} finally {
    if ($proc) {
        $teardownStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        # Stopping the by-path process stops the activated instance it supervises (D37). That half of
        # the contract went unchecked on every run until now. On phi-silica the child must be there
        # before the stop, found by the contract itself (--supervisor-pid <our pid> on its command
        # line), and nothing of either process may survive it; on the fake and aion there is no child,
        # so the check is the parent and the port. Runs whether the parent was stopped here or had
        # already exited on its own (issue #15). -NoStart started nothing and checks nothing.
        $teardown = if ($Backend -eq 'phi-silica') { 'teardown: the parent and its activated child exit and the port frees' } else { 'teardown: the server exits and the port frees' }
        $problems = [System.Collections.Generic.List[string]]::new()
        $childrenBefore = @()
        $elapsed = 0
        try {
            $childPattern = "--supervisor-pid\s+$($proc.Id)(\s|$)"
            $parentExited = $proc.HasExited
            $childrenBefore = @(Get-CimInstance Win32_Process -Filter "Name = 'NpuBridge.exe'" |
                Where-Object { $_.CommandLine -and $_.CommandLine -match $childPattern } | ForEach-Object ProcessId)

            if ($parentExited) {
                Write-Host "Server (pid $($proc.Id)) had already exited with code $($proc.ExitCode)"
            }
            else {
                Write-Host "Stopping server (pid $($proc.Id))"
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            }

            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $gone = Wait-ServerGone $proc.Id $Port
            $sw.Stop()
            $elapsed = [Math]::Round($sw.Elapsed.TotalSeconds, 1)

            if ($Backend -eq 'phi-silica' -and $childrenBefore.Count -eq 0) {
                # A parent that died on its own (the model runtime's RPC fault, say) takes its child with it
                # through WatchParent before this code runs, so an empty list then says nothing about D37.
                if ($parentExited) { $problems.Add("the parent had already exited with code $($proc.ExitCode) before teardown, so its activated child could not be observed") }
                else { $problems.Add("no activated child carrying --supervisor-pid $($proc.Id) existed before the stop") }
            }
            if ($gone.Listening) { $problems.Add("port $Port still listening after $elapsed s") }
            if ($gone.Survivors.Count -gt 0) { $problems.Add("NpuBridge pid(s) $(($gone.Survivors | ForEach-Object ProcessId) -join ',') still alive after $elapsed s") }
        }
        catch {
            # A process or port query that failed is not evidence that nothing is left.
            $problems.Add("could not verify the teardown: $($_.Exception.Message)")
        }

        $teardownStopwatch.Stop()
        if ($problems.Count -gt 0) {
            $detail = $problems -join '; '
            $results.Add([pscustomobject]@{ Step = $teardown; Result = 'FAIL'; Pins = @('D37', '#15'); Detail = $detail; DurationMs = $teardownStopwatch.ElapsedMilliseconds })
            Write-Host "    FAIL $detail" -ForegroundColor Red
        }
        else {
            $child = if ($childrenBefore.Count -gt 0) { " and child pid $($childrenBefore -join ',')" } else { '' }
            $detail = "pid $($proc.Id)$child gone and port $Port free after $elapsed s"
            $results.Add([pscustomobject]@{ Step = $teardown; Result = 'PASS'; Pins = @('D37', '#15'); Detail = $detail; DurationMs = $teardownStopwatch.ElapsedMilliseconds })
            Write-Host "    PASS $detail" -ForegroundColor Green
        }
    }
}

Write-Host ''
$results | Select-Object Step, Result, Detail | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$passed = @($results | Where-Object Result -eq 'PASS').Count
$failed = @($results | Where-Object Result -eq 'FAIL').Count
$skipped = @($results | Where-Object Result -eq 'SKIP').Count
$info = @($results | Where-Object Result -eq 'INFO').Count
$pins = @($results | ForEach-Object { $_.Pins } | Where-Object { $_ -and $_ -ne '-' } | Sort-Object -Unique)
Write-Host "pins: $($pins -join ' ')"

if ($JsonOut) {
    $commit = $null
    try {
        $commit = (& git -C $repo rev-parse HEAD 2>$null).Trim()
        if ($LASTEXITCODE -ne 0) { $commit = $null }
    }
    catch { $commit = $null }

    $summary = [ordered]@{
        backend    = $Backend
        port       = $Port
        startedAt  = $startedAt
        finishedAt = (Get-Date).ToUniversalTime().ToString('o')
        commit     = $commit
        verdict    = if ($failed -eq 0) { 'pass' } else { 'fail' }
        counts     = [ordered]@{ pass = $passed; fail = $failed; skip = $skipped; info = $info }
        pins       = $pins
        firstGenerationRetries = $firstGenerationRpcRetries
        steps      = @($results | ForEach-Object {
            [ordered]@{
                name       = $_.Step
                result     = $_.Result.ToLowerInvariant()
                pins       = @($_.Pins)
                detail     = $_.Detail
                durationMs = $_.DurationMs
            }
        })
    }
    $json = $summary | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($JsonOut, $json, [System.Text.UTF8Encoding]::new($false))
}

if ($failed -gt 0) { Write-Host "first-generation RPC retry: $firstGenerationRpcRetries"; Write-Host "$failed step(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "All steps passed ($skipped skipped, $info informational)" -ForegroundColor Green
Write-Host "first-generation RPC retry: $firstGenerationRpcRetries"
