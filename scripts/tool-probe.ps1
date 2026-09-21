<#
.SYNOPSIS
  Issue #21 hard-case tool-call compliance probe against a live npu-bridge server.

.DESCRIPTION
  scripts/smoke.ps1's tool-call probe (D83) offers exactly one tool and gets a 20/20 call rate --
  the easy case. This script measures the hard cases PLAN and issue #21 leave open: many tools
  offered at once (a real agent client's ~40 KB of schema JSON hit an HTTP 502 in prior probing),
  deep nested schemas, a system prompt competing with the tool-instruction block for the same
  context window, and a multi-step tool-result round trip. It does not start or stop a server; it
  expects one already listening and ready (check with GET /healthz first) and talks to it only over
  HTTP, exactly as a client would.

  Reporting follows scripts/smoke.ps1's house style: Write-Host per step, a PASS/INFO/DEFECT line,
  a summary table at the end. Unlike smoke.ps1's Step/InfoStep split, every measurement here is
  inherently an InfoStep: the model's choice not to call a tool, to call the wrong one, or to invent
  an argument is a finding, never a script failure, and is reported per-cell rather than as one
  aggregate rate (the brief this script was written against is explicit that a low call rate is not
  a failure). The one thing that DOES count as a defect -- recorded, printed in red, and rolled into
  -JsonOut's "defects" array, but does NOT stop the run -- is the bridge itself misbehaving: a
  response body that is not valid JSON, finish_reason "tool_calls" with non-null content, a
  tool_calls argument string that is not valid JSON, tool-call protocol leaking into ordinary
  content as prose, or an error body missing one of the four OpenAI error keys (D77). The run keeps
  going after a defect (and after every HTTP 502, which is itself a data point, not a script
  failure) so one bad cell does not cost the rest of the probe's real NPU time.

.PARAMETER BaseUrl
  The already-running bridge. Default http://127.0.0.1:5273. This script never starts or stops
  a server.

.PARAMETER Model
  The served model id to send as "model" in every request. Default phi-silica.

.PARAMETER Runs
  Runs per cell for every dimension, including dimension 5's occupancy cells, except the tool-count
  sweep's count=1 cell, which always uses -HeadlineRuns instead so it is directly comparable to
  smoke.ps1's single-tool probe. Default 3.

.PARAMETER HeadlineRuns
  Runs for the tool-count sweep's count=1 cell. Default 5, matching the brief's "comparable with
  the existing smoke probe's 20/20" instruction (smoke.ps1 defaults to 5 runs too).

.PARAMETER Include
  Which dimensions to run: ToolCountSweep, SchemaDepth, SystemPromptPressure, MultiStep,
  WindowOccupancy, or All (default). A re-run can pass e.g. -Include SchemaDepth to redo just one
  section. WindowOccupancy holds the tool count fixed at 8, always with get_weather rotated to the
  midpoint of the catalog (matching dimensions 1 and 2's own position-bias control), and scales each
  tool's own description and parameter descriptions (realistic verbose documentation, not filler) to
  sweep the rendered tool-instruction block -- reproduced byte-for-byte from
  src/NpuBridge.Core/Tools/ToolSchemaRenderer.cs, not estimated -- across roughly 25/50/70/85/95/110/
  150% of Phi Silica's 3,581-token empty-context window. It never sends more than 30,000 rendered
  chars (Build-OccupancyCell's structural clamp): the ~40,000-44,000-char regime where issue #29's
  COMException lives is measured and cited from a separate investigation, not reproduced by this
  script, because an over-large system prompt there does not merely error -- it fail-fasts
  WorkloadsSessionHost.exe and wedges the whole Phi Silica subsystem for minutes. This is why
  ToolCountSweep (dimension 1) never got near that wall even at 25 tools: its catalog renders to only
  2,487 chars (Get-CompactBlockFromFlatWireTools), an order of magnitude under the boundary, because
  its descriptions are short -- roughly 100 chars/tool, not the ~1,600 chars/tool a real agent's
  schemas carry.

.PARAMETER OccupancyCells
  Which of dimension 5's cells to run: any of 25%, 50%, 70%, 70%-reversed, 85%, 95%, 110%, 150%.
  Default is all of them except 70%-reversed, which is the repetition-order control for the 70% cell
  (same target token count, same tool catalog, but each tool's padding pool consumed back-to-front
  instead of front-to-back) and only means something run alongside a forward 70% cell in the same
  session, so it is opt-in.

.PARAMETER Stream
  Re-runs the count=1 and 70% occupancy fixtures with stream: true and temperature: 0, then compares
  their assembled SSE tool_calls and finish_reason with the JSON-shape run of the same fixture.

.PARAMETER SelfTest
  Runs the offline SSE assembly, parity, and wire-shape fixtures without contacting a bridge.

.PARAMETER JsonOut
  Where to write the machine-readable summary (default a timestamped file under $env:TEMP, so
  successive runs never silently overwrite each other's evidence). Every number in the human report
  can be re-derived from this file without re-running the probe against the NPU.

.PARAMETER TimeoutSec
  Per-request HTTP timeout. Default 60s -- generous against the observed 3-13s successes and ~7s
  failures (both the 400 and 502 regimes), so a timeout in this script always means the bridge or
  the network hung, never a slow-but-normal generation.

.EXAMPLE
  .\scripts\tool-probe.ps1
  .\scripts\tool-probe.ps1 -Include SchemaDepth,MultiStep -JsonOut C:\temp\probe.json
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = 'http://127.0.0.1:5273',
    [string] $Model = 'phi-silica',
    [int] $Runs = 3,
    [int] $HeadlineRuns = 5,
    [ValidateSet('All', 'ToolCountSweep', 'SchemaDepth', 'SystemPromptPressure', 'MultiStep', 'WindowOccupancy')]
    [string[]] $Include = @('All'),
    [ValidateSet('25%', '50%', '70%', '70%-reversed', '85%', '95%', '110%', '150%')]
    [string[]] $OccupancyCells = @('25%', '50%', '70%', '85%', '95%', '110%', '150%'),
    [switch] $Stream,
    [switch] $SelfTest,
    [string] $JsonOut = (Join-Path $env:TEMP "npu-bridge-tool-probe-result-$(Get-Date -Format 'yyyyMMdd-HHmmss').json"),
    [int] $TimeoutSec = 60
)

$ErrorActionPreference = 'Stop'
$base = $BaseUrl.TrimEnd('/')
$dimensions = if ($Include -contains 'All') { @('ToolCountSweep', 'SchemaDepth', 'SystemPromptPressure', 'MultiStep', 'WindowOccupancy') } else { $Include }

$script:defects = [System.Collections.Generic.List[object]]::new()
$script:cells = [System.Collections.Generic.List[object]]::new()   # one row per cell (per dimension x parameter value)
$script:calls = [System.Collections.Generic.List[object]]::new()   # one row per individual HTTP call, for the JSON dump
$script:streamedCalls = [System.Collections.Generic.List[object]]::new()
$script:parityResults = [System.Collections.Generic.List[object]]::new()
$script:cacheBrackets = [System.Collections.Generic.List[object]]::new()

function Write-Section([string] $name) {
    Write-Host ''
    Write-Host "==> $name" -ForegroundColor Cyan
}

function Write-Info([string] $line) {
    Write-Host "    $line" -ForegroundColor Yellow
}

function Add-Defect([string] $context, [string] $description, $evidence) {
    $d = [pscustomobject]@{ Context = $context; Description = $description; Evidence = "$evidence" }
    $script:defects.Add($d)
    Write-Host "    DEFECT [$context] $description" -ForegroundColor Red
    if ($evidence) { Write-Host "      $evidence" -ForegroundColor Red }
}


# --- streamed tool-call helpers ----------------------------------------------------------------------

function Test-IntegerValue($value) {
    $value -is [byte] -or $value -is [sbyte] -or $value -is [int16] -or $value -is [uint16] -or
    $value -is [int32] -or $value -is [uint32] -or $value -is [int64] -or $value -is [uint64]
}

# Returns descriptions rather than reporting them directly so -SelfTest can exercise the checks without
# pretending that fixture failures are bridge defects.
function Get-ToolCallShapeViolations($calls, [string] $finishReason, [bool] $isStream) {
    $violations = [System.Collections.Generic.List[string]]::new()
    $nonNullCalls = @($calls | Where-Object { $null -ne $_ })

    if ($nonNullCalls.Count -gt 0 -and $finishReason -ne 'tool_calls' -and $finishReason -ne 'length') {
        $violations.Add("non-empty tool_calls has finish_reason='$finishReason', not tool_calls or length")
    }

    for ($i = 0; $i -lt $nonNullCalls.Count; $i++) {
        $call = $nonNullCalls[$i]
        $prefix = "tool_calls[$i]"
        $properties = @($call.PSObject.Properties.Name)
        if (-not ($properties -contains 'id') -or -not ($call.id -is [string]) -or [string]::IsNullOrWhiteSpace($call.id)) {
            $violations.Add("$prefix.id is missing or not a non-empty string")
        }
        if (-not ($properties -contains 'type') -or $call.type -ne 'function') {
            $violations.Add("$prefix.type is not 'function'")
        }

        $function = if ($properties -contains 'function') { $call.function } else { $null }
        $functionProperties = if ($null -ne $function) { @($function.PSObject.Properties.Name) } else { @() }
        if (-not ($functionProperties -contains 'name') -or -not ($function.name -is [string]) -or [string]::IsNullOrWhiteSpace($function.name)) {
            $violations.Add("$prefix.function.name is missing or not a non-empty string")
        }
        if (-not ($functionProperties -contains 'arguments') -or -not ($function.arguments -is [string])) {
            $violations.Add("$prefix.function.arguments is missing or not a string")
        }

        if ($isStream) {
            if (-not ($properties -contains 'index') -or -not (Test-IntegerValue $call.index)) {
                $violations.Add("$prefix.index is missing or not an integer on the streamed shape")
            }
        }
        elseif ($properties -contains 'index') {
            $violations.Add("$prefix.index is present on the JSON shape")
        }
    }

    $violations.ToArray()
}

# Canonicalise objects recursively so key insertion order never affects parity. At the call-object
# boundary, omit wire metadata that is expected to differ between response shapes or responses.
function ConvertTo-CanonicalValue($value, [bool] $omitCallMetadata = $false) {
    if ($null -eq $value) { return $null }
    if ($value -is [string] -or $value.GetType().IsPrimitive -or $value -is [decimal]) { return $value }
    if ($value -is [System.Collections.IDictionary]) {
        $copy = [ordered]@{}
        foreach ($key in @($value.Keys | Sort-Object)) {
            $copy[[string] $key] = ConvertTo-CanonicalValue $value[$key]
        }
        return [pscustomobject] $copy
    }
    if ($value -is [System.Collections.IEnumerable]) {
        return @($value | ForEach-Object { ConvertTo-CanonicalValue $_ })
    }

    $copy = [ordered]@{}
    foreach ($property in @($value.PSObject.Properties | Sort-Object Name)) {
        if ($omitCallMetadata -and ($property.Name -eq 'index' -or $property.Name -eq 'id')) { continue }
        $copy[$property.Name] = ConvertTo-CanonicalValue $property.Value
    }
    [pscustomobject] $copy
}

function Get-ToolCallIds($calls) {
    @($calls | Where-Object { $null -ne $_ } | ForEach-Object {
        if ($_.PSObject.Properties.Name -contains 'id') { [string] $_.id } else { $null }
    })
}

function Get-ToolCallKeyOrder($calls) {
    @($calls | Where-Object { $null -ne $_ } | ForEach-Object {
        $callOrder = (@($_.PSObject.Properties.Name) -join ',')
        $function = if ($_.PSObject.Properties.Name -contains 'function') { $_.function } else { $null }
        $functionOrder = if ($null -ne $function) { (@($function.PSObject.Properties.Name) -join ',') } else { '' }
        "call=[$callOrder]; function=[$functionOrder]"
    }) -join ' | '
}

function ConvertTo-CanonicalToolCallJson($calls) {
    $canonical = @($calls | Where-Object { $null -ne $_ } | ForEach-Object {
        ConvertTo-CanonicalValue $_ $true
    })
    ConvertTo-Json -InputObject $canonical -Compress -Depth 10
}

function Compare-ToolCallShapes($jsonCalls, [string] $jsonFinishReason, $streamCalls, [string] $streamFinishReason) {
    $jsonSerialisation = ConvertTo-CanonicalToolCallJson $jsonCalls
    $streamSerialisation = ConvertTo-CanonicalToolCallJson $streamCalls
    $callsIdentical = $jsonSerialisation -ceq $streamSerialisation
    $finishReasonIdentical = $jsonFinishReason -ceq $streamFinishReason
    $jsonKeyOrder = Get-ToolCallKeyOrder $jsonCalls
    $streamKeyOrder = Get-ToolCallKeyOrder $streamCalls
    [pscustomobject]@{
        JsonToolCalls = $jsonSerialisation
        StreamToolCalls = $streamSerialisation
        JsonToolCallIds = @(Get-ToolCallIds $jsonCalls)
        StreamToolCallIds = @(Get-ToolCallIds $streamCalls)
        JsonKeyOrder = $jsonKeyOrder
        StreamKeyOrder = $streamKeyOrder
        KeyOrderDifferent = $jsonKeyOrder -cne $streamKeyOrder
        JsonFinishReason = $jsonFinishReason
        StreamFinishReason = $streamFinishReason
        ToolCallsIdentical = $callsIdentical
        FinishReasonIdentical = $finishReasonIdentical
        Identical = $callsIdentical -and $finishReasonIdentical
    }
}

# Reads the SSE stream without assuming the current one-chunk implementation. A future emitter may split
# arguments across chunks, so fragments are appended by the wire's per-call index.
function ConvertFrom-ToolCallSse([string] $body) {
    $errors = [System.Collections.Generic.List[string]]::new()
    $callsByIndex = [ordered]@{}
    $finishReason = $null
    $content = ''
    $done = $false
    $frameCount = 0
    $missingIndex = 0
    $streamFailed = $false
    $errorType = $null
    $errorCode = $null
    $errorMessage = $null

    foreach ($line in ($body -split "\r?\n")) {
        if ($line.StartsWith(':')) { continue }
        if (-not $line.StartsWith('data:')) { continue }
        $payload = $line.Substring(5).TrimStart()
        if ($payload -eq '[DONE]') {
            $done = $true
            break
        }

        try { $frame = $payload | ConvertFrom-Json -Depth 24 }
        catch {
            $errors.Add("invalid SSE data frame: $($_.Exception.Message)")
            continue
        }
        $frameCount++
        if ($frame.PSObject.Properties.Name -contains 'error' -and $null -ne $frame.error) {
            $streamFailed = $true
            $errorProperties = @($frame.error.PSObject.Properties.Name)
            if ($errorProperties -contains 'type') { $errorType = [string] $frame.error.type }
            if ($errorProperties -contains 'code') { $errorCode = [string] $frame.error.code }
            if ($errorProperties -contains 'message') { $errorMessage = [string] $frame.error.message }
            continue
        }
        foreach ($choice in @($frame.choices)) {
            if ($null -eq $choice) { continue }
            $choiceProperties = @($choice.PSObject.Properties.Name)
            if ($choiceProperties -contains 'finish_reason' -and $null -ne $choice.finish_reason) {
                $finishReason = [string] $choice.finish_reason
            }
            $delta = if ($choiceProperties -contains 'delta') { $choice.delta } else { $null }
            if ($null -eq $delta) { continue }
            $deltaProperties = @($delta.PSObject.Properties.Name)
            if ($deltaProperties -contains 'content' -and $null -ne $delta.content) {
                $content += [string] $delta.content
            }
            if (-not ($deltaProperties -contains 'tool_calls')) { continue }

            foreach ($fragment in @($delta.tool_calls)) {
                if ($null -eq $fragment) { continue }
                $fragmentProperties = @($fragment.PSObject.Properties.Name)
                $hasIndex = $fragmentProperties -contains 'index'
                $key = if ($hasIndex) { "index:$($fragment.index)" } else { "missing:$missingIndex" }
                if (-not $hasIndex) { $missingIndex++ }
                if (-not $callsByIndex.Contains($key)) {
                    $callsByIndex[$key] = [pscustomobject] [ordered]@{}
                }
                $call = $callsByIndex[$key]

                foreach ($name in @('index', 'id', 'type')) {
                    if (-not ($fragmentProperties -contains $name)) { continue }
                    if ($call.PSObject.Properties.Name -contains $name) { $call.$name = $fragment.$name }
                    else { $call | Add-Member -NotePropertyName $name -NotePropertyValue $fragment.$name }
                }
                if (-not ($fragmentProperties -contains 'function')) { continue }
                if (-not ($call.PSObject.Properties.Name -contains 'function')) {
                    $call | Add-Member -NotePropertyName 'function' -NotePropertyValue ([pscustomobject] [ordered]@{})
                }
                if ($null -eq $fragment.function) {
                    $call.function = $null
                    continue
                }
                if ($null -eq $call.function) { $call.function = [pscustomobject] [ordered]@{} }
                $functionProperties = @($fragment.function.PSObject.Properties.Name)
                foreach ($name in @('name', 'arguments')) {
                    if (-not ($functionProperties -contains $name)) { continue }
                    if ($call.function.PSObject.Properties.Name -contains $name) {
                        if ($name -eq 'arguments' -and $null -ne $call.function.arguments -and $null -ne $fragment.function.arguments) {
                            $call.function.arguments = ([string] $call.function.arguments) + ([string] $fragment.function.arguments)
                        }
                        elseif ($name -ne 'arguments' -or $null -eq $call.function.arguments) {
                            $call.function.$name = $fragment.function.$name
                        }
                    }
                    else {
                        $call.function | Add-Member -NotePropertyName $name -NotePropertyValue $fragment.function.$name
                    }
                }
            }
        }
    }

    [pscustomobject]@{
        Calls = @($callsByIndex.Values)
        FinishReason = $finishReason
        Content = if ($content.Length -gt 0) { $content } else { $null }
        Done = $done
        Failed = $streamFailed
        ErrorType = $errorType
        ErrorCode = $errorCode
        ErrorMessage = $errorMessage
        FrameCount = $frameCount
        ParseErrors = $errors.ToArray()
    }
}

function Test-ToolCallReply([string] $context, $calls, [string] $finishReason, $content, [bool] $isStream, $evidence) {
    $nonNullCalls = @($calls | Where-Object { $null -ne $_ })
    if ($finishReason -eq 'tool_calls' -and $null -ne $content) {
        Add-Defect $context 'finish_reason=tool_calls but message content is not null' $evidence
    }
    foreach ($violation in @(Get-ToolCallShapeViolations $nonNullCalls $finishReason $isStream)) {
        Add-Defect $context $violation $evidence
    }
    foreach ($call in $nonNullCalls) {
        $function = if ($call.PSObject.Properties.Name -contains 'function') { $call.function } else { $null }
        if ($null -eq $function -or -not ($function.PSObject.Properties.Name -contains 'arguments') -or -not ($function.arguments -is [string])) { continue }
        try { $null = $function.arguments | ConvertFrom-Json -Depth 20 }
        catch { Add-Defect $context "tool_calls argument string is not valid JSON for tool '$($function.name)'" $function.arguments }
    }
    if ($nonNullCalls.Count -eq 0 -and ($finishReason -eq 'stop' -or $finishReason -eq 'length') -and $content -and
        ($content -match '"tool_calls"\s*:' -or $content -match '"function"\s*:\s*\{\s*"name"')) {
        Add-Defect $context 'tool-call protocol appears to have leaked into content as prose' $content
    }
}

function Get-HealthzSnapshot {
    $health = Invoke-WebRequest -Uri "$base/healthz" -TimeoutSec 10 -SkipHttpErrorCheck
    try { $parsed = $health.Content | ConvertFrom-Json -Depth 12 }
    catch { throw "GET /healthz returned invalid JSON: $($_.Exception.Message)" }
    $statusCode = [int] $health.StatusCode
    if ($statusCode -ne 200 -and -not ($statusCode -eq 503 -and $parsed.status -eq 'degraded')) {
        throw "GET /healthz returned HTTP $statusCode`: $($health.Content)"
    }
    foreach ($field in @('context_cache_hits', 'context_cache_misses')) {
        if (-not ($parsed.PSObject.Properties.Name -contains $field) -or -not (Test-IntegerValue $parsed.$field)) {
            throw "GET /healthz returned no usable '$field' field: $($health.Content)"
        }
    }
    [pscustomobject]@{
        StatusCode = $statusCode
        Status = [string] $parsed.status
        ContextCacheHits = [int64] $parsed.context_cache_hits
        ContextCacheMisses = [int64] $parsed.context_cache_misses
        Raw = $health.Content
    }
}

function Get-ToolCallParityAssessment($jsonFacts, $streamFacts) {
    $comparison = Compare-ToolCallShapes $jsonFacts.Calls $jsonFacts.FinishReason $streamFacts.Calls $streamFacts.FinishReason
    $reasons = [System.Collections.Generic.List[string]]::new()
    if ($jsonFacts.StatusCode -ne 200 -or $streamFacts.StatusCode -ne 200) {
        $reasons.Add("HTTP statuses are not both 200 (json=$($jsonFacts.StatusCode), stream=$($streamFacts.StatusCode))")
    }
    if ($streamFacts.StreamFailed) {
        $reasons.Add("stream carried $($streamFacts.ErrorCode)")
    }
    if (@($jsonFacts.Calls).Count -eq 0 -or @($streamFacts.Calls).Count -eq 0) {
        $reasons.Add("tool_calls missing (json=$(@($jsonFacts.Calls).Count), stream=$(@($streamFacts.Calls).Count))")
    }
    if (-not $streamFacts.StreamDone) { $reasons.Add('stream did not end with data: [DONE]') }
    if (@($streamFacts.StreamParseErrors).Count -gt 0) { $reasons.Add('stream had SSE parse errors') }
    $comparable = $reasons.Count -eq 0
    $verdict = if ($streamFacts.StreamFailed) { "not comparable: stream carried $($streamFacts.ErrorCode)" }
               elseif (-not $comparable) { 'not comparable' } elseif ($comparison.Identical) { 'identical' } else { 'MISMATCH' }
    [pscustomobject]@{
        Comparison = $comparison
        Comparable = $comparable
        Verdict = $verdict
        Reason = $reasons -join '; '
    }
}

function Invoke-StreamParityRuns([string] $dimension, [string] $cell, $jsonRuns, [hashtable] $body, [int] $runCount) {
    $parities = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $runCount; $i++) {
        $streamBody = $body.Clone()
        $streamBody.stream = $true
        $streamResult = Invoke-ChatOnce $streamBody
        $context = "$cell stream run=$($i + 1)"
        Test-BridgeDefect $context $streamResult
        $streamFacts = Get-ChatFacts $streamResult
        $jsonRun = $jsonRuns[$i]
        $jsonFacts = $jsonRun.Facts
        $assessment = Get-ToolCallParityAssessment $jsonFacts $streamFacts
        $comparison = $assessment.Comparison
        $comparable = $assessment.Comparable
        $verdict = $assessment.Verdict
        $reason = $assessment.Reason
        $parity = [pscustomobject]@{
            Dimension = $dimension; Cell = $cell; Run = $i + 1; Verdict = $verdict; Comparable = $comparable
            JsonStatusCode = $jsonFacts.StatusCode; StreamStatusCode = $streamFacts.StatusCode
            JsonFinishReason = $comparison.JsonFinishReason; StreamFinishReason = $comparison.StreamFinishReason
            JsonToolCalls = $comparison.JsonToolCalls; StreamToolCalls = $comparison.StreamToolCalls
            JsonToolCallIds = @($comparison.JsonToolCallIds); StreamToolCallIds = @($comparison.StreamToolCallIds)
            JsonKeyOrder = $comparison.JsonKeyOrder; StreamKeyOrder = $comparison.StreamKeyOrder
            KeyOrderDifferent = $comparison.KeyOrderDifferent
            StreamDone = $streamFacts.StreamDone; StreamParseErrors = @($streamFacts.StreamParseErrors)
            Reason = $reason
        }
        $streamRow = [pscustomobject]@{
            Dimension = $dimension; Cell = $cell; Run = $i + 1; StatusCode = $streamFacts.StatusCode
            FinishReason = $streamFacts.FinishReason; LatencyMs = $streamResult.LatencyMs
            Called = $streamFacts.Called; ToolNames = $streamFacts.ToolNames -join ','
            ToolCalls = $streamFacts.Calls; StreamDone = $streamFacts.StreamDone
            StreamParseErrors = @($streamFacts.StreamParseErrors); ParityVerdict = $verdict; ParityReason = $reason
        }
        $script:parityResults.Add($parity)
        $script:streamedCalls.Add($streamRow)
        $script:calls.Add($streamRow)
        $parities.Add($parity)
        if ($verdict -eq 'MISMATCH') {
            $evidence = "json tool_calls=$($comparison.JsonToolCalls); stream tool_calls=$($comparison.StreamToolCalls); json finish=$($comparison.JsonFinishReason); stream finish=$($comparison.StreamFinishReason); comparable=$comparable"
            Add-Defect $context 'stream tool-call parity mismatch' $evidence
        }
        if ($comparison.KeyOrderDifferent) {
            Write-Info "$cell stream run=$($i + 1): informational key order differs (json=$($comparison.JsonKeyOrder); stream=$($comparison.StreamKeyOrder)); ids json=$($comparison.JsonToolCallIds -join ',') stream=$($comparison.StreamToolCallIds -join ',')"
        }
        Write-Info "$cell stream run=$($i + 1): HTTP json=$($jsonFacts.StatusCode) stream=$($streamFacts.StatusCode) finish=$($streamFacts.FinishReason) parity=$verdict$(if ($reason) { " reason=$reason" }) ($($streamResult.LatencyMs)ms)"
    }
    $mismatchN = @($parities | Where-Object { $_.Verdict -eq 'MISMATCH' }).Count
    $notComparableN = @($parities | Where-Object { $_.Verdict -like 'not comparable*' }).Count
    $cellVerdict = if ($mismatchN -gt 0) { 'MISMATCH' } elseif ($notComparableN -gt 0) { "not comparable ($notComparableN/$runCount pairs)" } else { 'identical' }
    Write-Info "stream parity: $cellVerdict"
    $script:cells.Add([pscustomobject]@{ Dimension = $dimension; Cell = "$cell (stream)"; Runs = $runCount; Summary = "stream parity: $cellVerdict" })
}

function Invoke-ToolProbeSelfTest {
    $fixtureSse = @'
: keep-alive
data: {"choices":[{"delta":{"role":"assistant"}}]}
data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_stream","type":"function","function":{"name":"get_weather","arguments":"{\"location\":\""}}]}}]}
data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"Paris\"}"}}]}}]}
data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}
data: [DONE]
'@
    $jsonMessage = '{"tool_calls":[{"function":{"arguments":"{\"location\":\"Paris\"}","name":"get_weather"},"type":"function","id":"call_json"}]}' | ConvertFrom-Json -Depth 12
    $assembled = ConvertFrom-ToolCallSse $fixtureSse
    if (-not $assembled.Done -or $assembled.FrameCount -ne 4 -or @($assembled.ParseErrors).Count -ne 0) { throw 'SSE fixture did not assemble cleanly' }
    if (@($assembled.Calls).Count -ne 1 -or $assembled.Calls[0].function.arguments -ne '{"location":"Paris"}') { throw 'SSE fixture did not concatenate arguments by index' }
    Write-Host 'SELFTEST: SSE assembly passed'

    $errorFixtureSse = @'
: keep-alive
data: {"error":{"type":"server_error","code":"backend_fault","message":"generation failed"}}
data: [DONE]
'@
    $errorAssembled = ConvertFrom-ToolCallSse $errorFixtureSse
    if (-not $errorAssembled.Failed -or $errorAssembled.ErrorType -ne 'server_error' -or
        $errorAssembled.ErrorCode -ne 'backend_fault' -or $errorAssembled.ErrorMessage -ne 'generation failed' -or
        -not $errorAssembled.Done -or @($errorAssembled.ParseErrors).Count -ne 0) {
        throw 'SSE error fixture did not record the streamed error after keep-alive'
    }
    $errorStreamFacts = Get-ChatFacts ([pscustomobject]@{
        NetworkOk = $true; StatusCode = 200; IsStream = $true; RawBody = $errorFixtureSse
    })
    $errorJsonFacts = [pscustomobject]@{ StatusCode = 200; FinishReason = $null; Calls = @() }
    $errorParity = Get-ToolCallParityAssessment $errorJsonFacts $errorStreamFacts
    if ($errorParity.Verdict -ne 'not comparable: stream carried backend_fault' -or
        $errorStreamFacts.ErrorType -ne 'server_error' -or $errorStreamFacts.ErrorCode -ne 'backend_fault' -or
        $errorStreamFacts.ErrorMessage -ne 'generation failed') {
        throw "streamed error was not surfaced in parity: verdict=$($errorParity.Verdict)"
    }
    Write-Host 'SELFTEST: streamed error after keep-alive passed'

    $same = Compare-ToolCallShapes $jsonMessage.tool_calls 'tool_calls' $assembled.Calls $assembled.FinishReason
    if (-not $same.Identical) { throw "expected identical parity, got JSON=$($same.JsonToolCalls) stream=$($same.StreamToolCalls)" }
    if ($same.JsonToolCallIds[0] -eq $same.StreamToolCallIds[0] -or -not $same.KeyOrderDifferent) {
        throw 'parity fixture did not exercise distinct ids and key order'
    }
    Write-Host "SELFTEST: parity identical passed (ids json=$($same.JsonToolCallIds -join ',') stream=$($same.StreamToolCallIds -join ','); key order differs)"

    $mutated = $fixtureSse.Replace('Paris\"}', 'Lyon\"}')
    $different = ConvertFrom-ToolCallSse $mutated
    $mismatch = Compare-ToolCallShapes $jsonMessage.tool_calls 'tool_calls' $different.Calls $different.FinishReason
    if ($mismatch.Identical) { throw 'expected mutated argument to produce MISMATCH' }
    Write-Host 'SELFTEST: parity mismatch detected'

    $stream502Facts = [pscustomobject]@{
        StatusCode = 502; FinishReason = $null; Calls = @(); StreamDone = $false; StreamParseErrors = @()
    }
    $json200Facts = [pscustomobject]@{
        StatusCode = 200; FinishReason = 'tool_calls'; Calls = @($jsonMessage.tool_calls)
    }
    $defectsBeforeNotComparable = $script:defects.Count
    $notComparable = Get-ToolCallParityAssessment $json200Facts $stream502Facts
    if ($notComparable.Verdict -ne 'not comparable' -or $notComparable.Comparable -or $script:defects.Count -ne $defectsBeforeNotComparable -or
        $notComparable.Reason -notmatch 'stream=502') {
        throw "expected 502 stream fixture to be not comparable without a defect row, got verdict=$($notComparable.Verdict) reason=$($notComparable.Reason)"
    }
    Write-Host 'SELFTEST: 502 stream parity is not comparable without defect'

    $missingId = [pscustomobject]@{ index = 0; type = 'function'; function = [pscustomobject]@{ name = 'get_weather'; arguments = '{}' } }
    $jsonWithIndex = [pscustomobject]@{ id = 'call_2'; index = 0; type = 'function'; function = [pscustomobject]@{ name = 'get_weather'; arguments = '{}' } }
    $shapeMissingId = @(Get-ToolCallShapeViolations @($missingId) 'tool_calls' $true)
    $shapeJsonIndex = @(Get-ToolCallShapeViolations @($jsonWithIndex) 'tool_calls' $false)
    $shapeBiconditional = @(Get-ToolCallShapeViolations @($jsonMessage.tool_calls) 'stop' $false)
    if (-not ($shapeMissingId -match 'id') -or -not ($shapeJsonIndex -match 'index') -or -not ($shapeBiconditional -match 'finish_reason')) {
        throw 'shape checks did not catch missing id, JSON index, and finish_reason biconditional violations'
    }
    Write-Host 'SELFTEST: shape checks caught missing id, JSON index, and biconditional violations'
    Write-Host 'SELFTEST PASS'
}

# --- HTTP -----------------------------------------------------------------------------------------

# One POST to /v1/chat/completions. Never throws on a non-200 status (400/429/502/503 are all data
# points this probe wants, not failures) and never throws on an unparsable body -- that is itself
# the "malformed JSON body" defect the caller checks for. Only a network-level failure (connection
# refused, timeout) is exceptional, and even that is caught and returned as a result rather than
# thrown, so one flaky call cannot abort the whole probe.
function Invoke-ChatOnce($body, [int] $timeoutSec = $TimeoutSec) {
    $isStream = $body.ContainsKey('stream') -and [bool] $body.stream
    $json = $body | ConvertTo-Json -Depth 16
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $r = Invoke-WebRequest -Uri "$base/v1/chat/completions" -Method POST -Body $json -ContentType 'application/json' `
            -TimeoutSec $timeoutSec -SkipHttpErrorCheck
    }
    catch {
        $sw.Stop()
        return [pscustomobject]@{
            NetworkOk = $false; NetworkError = $_.Exception.Message
            LatencyMs = [Math]::Round($sw.Elapsed.TotalMilliseconds, 1)
            StatusCode = $null; Json = $null; RawBody = $null; ParseError = $null; IsStream = $isStream
        }
    }
    $sw.Stop()
    $raw = $r.Content
    $parsed = $null
    $parseError = $null
    try { $parsed = $raw | ConvertFrom-Json -Depth 24 } catch { $parseError = $_.Exception.Message }
    [pscustomobject]@{
        NetworkOk = $true; NetworkError = $null
        LatencyMs = [Math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        StatusCode = [int] $r.StatusCode; Json = $parsed; RawBody = $raw; ParseError = $parseError; IsStream = $isStream
    }
}

# Every bridge-side defect this script watches for (see the header comment). Returns nothing; adds
# straight to $script:defects. Deliberately blind to model *choices* -- not calling, calling the
# wrong tool, a plausible-but-wrong argument value -- those are measurements, handled by the caller.
function Test-BridgeDefect([string] $context, $result) {
    if (-not $result.NetworkOk) {
        return   # a network failure is not a claim about the bridge's HTTP contract
    }
    if (-not $result.IsStream -and $null -eq $result.Json) {
        Add-Defect $context 'HTTP 200 (or other) body is not valid JSON' $result.ParseError
        return
    }
    if ($result.StatusCode -ne 200) {
        if ($null -eq $result.Json) {
            Add-Defect $context "HTTP $($result.StatusCode) body is not valid JSON" $result.ParseError
            return
        }
        $e = $result.Json.error
        if ($null -eq $e) {
            Add-Defect $context "HTTP $($result.StatusCode) body carries no 'error' object" $result.RawBody
        }
        elseif (-not ($e.PSObject.Properties.Name -contains 'message' -and $e.PSObject.Properties.Name -contains 'type' -and
                       $e.PSObject.Properties.Name -contains 'param' -and $e.PSObject.Properties.Name -contains 'code')) {
            Add-Defect $context "HTTP $($result.StatusCode) error object is missing one of message/type/param/code (D77)" $result.RawBody
        }
        return
    }

    $facts = Get-ChatFacts $result
    if ($result.IsStream) {
        foreach ($parseError in @($facts.StreamParseErrors)) {
            Add-Defect $context 'stream response has an invalid SSE data frame' $parseError
        }
        if (-not $facts.StreamDone) {
            Add-Defect $context 'stream response ended without data: [DONE]' $result.RawBody
        }
        Test-ToolCallReply $context $facts.Calls $facts.FinishReason $facts.Content $true $result.RawBody
        return
    }

    $choice = $result.Json.choices[0]
    if ($null -eq $choice) {
        Add-Defect $context 'HTTP 200 but choices[0] is missing' $result.RawBody
        return
    }
    Test-ToolCallReply $context $facts.Calls $facts.FinishReason $facts.Content $false $result.RawBody
}

# Pulls the measurement fields a caller needs out of one Invoke-ChatOnce result, without deciding
# anything about correctness (that is question-specific and lives in each dimension's own code).
function Get-ChatFacts($result) {
    if (-not $result.NetworkOk -or $result.StatusCode -ne 200) {
        return [pscustomobject]@{
            StatusCode = $result.StatusCode; FinishReason = $null; Called = $false
            ToolNames = @(); Calls = @(); Content = $null
            ErrorType = if ($result.Json) { $result.Json.error.type } else { $null }
            ErrorCode = if ($result.Json) { $result.Json.error.code } else { $null }
            ErrorMessage = if ($result.Json) { $result.Json.error.message } else { $result.NetworkError }
            StreamDone = $false; StreamParseErrors = @()
        }
    }
    if ($result.IsStream) {
        $stream = ConvertFrom-ToolCallSse $result.RawBody
        $calls = @($stream.Calls | Where-Object { $null -ne $_ })
        return [pscustomobject]@{
            StatusCode = $result.StatusCode; FinishReason = $stream.FinishReason
            Called = $stream.FinishReason -eq 'tool_calls'
            ToolNames = @($calls | ForEach-Object { $_.function.name })
            Calls = $calls; Content = $stream.Content
            ErrorType = $stream.ErrorType; ErrorCode = $stream.ErrorCode; ErrorMessage = $stream.ErrorMessage
            StreamFailed = $stream.Failed
            StreamDone = $stream.Done; StreamParseErrors = @($stream.ParseErrors)
        }
    }
    if ($null -eq $result.Json) {
        return [pscustomobject]@{
            StatusCode = $result.StatusCode; FinishReason = $null; Called = $false
            ToolNames = @(); Calls = @(); Content = $null
            ErrorType = $null; ErrorCode = $null; ErrorMessage = $result.ParseError
            StreamDone = $false; StreamParseErrors = @()
        }
    }
    $choice = $result.Json.choices[0]
    if ($null -eq $choice) {
        return [pscustomobject]@{
            StatusCode = $result.StatusCode; FinishReason = $null; Called = $false
            ToolNames = @(); Calls = @(); Content = $null
            ErrorType = $null; ErrorCode = $null; ErrorMessage = 'choices[0] is missing'
            StreamDone = $false; StreamParseErrors = @()
        }
    }
    $calls = if ($null -eq $choice.message.tool_calls) { @() } else { @($choice.message.tool_calls) }
    [pscustomobject]@{
        StatusCode = $result.StatusCode; FinishReason = $choice.finish_reason
        Called = $choice.finish_reason -eq 'tool_calls'
        ToolNames = @($calls | ForEach-Object { $_.function.name })
        Calls = $calls; Content = $choice.message.content
        ErrorType = $null; ErrorCode = $null; ErrorMessage = $null
        StreamDone = $false; StreamParseErrors = @()
    }
}

# --- tool catalogs ----------------------------------------------------------------------------------

# The target tool for the tool-count sweep and the system-prompt-pressure sweep: identical to
# smoke.ps1's single-tool probe, so the count=1 cell is a direct rerun of that measurement.
function Get-WeatherTool {
    @{
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
    }
}

# 24 plausible agent tools that are never the right answer to the weather question. Each has one
# required string parameter, deliberately as flat as get_weather itself, so the tool-count sweep's
# JSON size scales with count alone and is not confounded by schema depth (that is dimension 2's job).
function Get-DistractorTools {
    $specs = @(
        @('read_file', 'Read the contents of a file at a given path', 'path', 'Path to the file'),
        @('write_file', 'Write content to a file at a given path', 'path', 'Path to the file'),
        @('edit_file', 'Apply a text edit to an existing file', 'path', 'Path to the file'),
        @('list_directory', 'List the files in a directory', 'path', 'Path to the directory'),
        @('grep_search', 'Search file contents for a regular expression', 'pattern', 'Regex pattern to search for'),
        @('run_bash', 'Execute a shell command and return its output', 'command', 'Shell command to run'),
        @('web_fetch', 'Fetch the contents of a URL', 'url', 'URL to fetch'),
        @('git_status', 'Show the working tree status of a git repository', 'repo_path', 'Path to the repository'),
        @('git_commit', 'Create a git commit with the given message', 'message', 'Commit message'),
        @('http_request', 'Make an HTTP GET request to a URL', 'url', 'URL to request'),
        @('send_email', 'Send an email to a recipient', 'to', 'Recipient email address'),
        @('schedule_meeting', 'Schedule a meeting with a title', 'title', 'Meeting title'),
        @('search_web', 'Search the web for a query', 'query', 'Search query'),
        @('translate_text', 'Translate text into another language', 'text', 'Text to translate'),
        @('summarize_document', 'Summarize a document by its id', 'document_id', 'Document identifier'),
        @('convert_currency', 'Convert an amount between currencies', 'amount', 'Amount to convert, e.g. "100 USD to EUR"'),
        @('execute_python', 'Execute a snippet of Python code', 'code', 'Python source code'),
        @('query_database', 'Run a SQL query against a database', 'sql', 'SQL query text'),
        @('send_slack_message', 'Send a message to a Slack channel', 'channel', 'Slack channel name'),
        @('create_github_issue', 'Create a GitHub issue with a title', 'title', 'Issue title'),
        @('resize_image', 'Resize an image file', 'path', 'Path to the image'),
        @('transcribe_audio', 'Transcribe an audio file to text', 'path', 'Path to the audio file'),
        @('generate_image', 'Generate an image from a text prompt', 'prompt', 'Image generation prompt'),
        @('lookup_stock_price', 'Look up the current price of a stock ticker', 'ticker', 'Stock ticker symbol')
    )
    $specs | ForEach-Object {
        @{
            type     = 'function'
            function = @{
                name        = $_[0]
                description = $_[1]
                parameters  = @{
                    type       = 'object'
                    properties = @{ ($_[2]) = @{ type = 'string'; description = $_[3] } }
                    required   = @($_[2])
                }
            }
        }
    }
}

# Builds an N-tool offer for the sweep: the target plus (N-1) distractors, target inserted at the
# midpoint rather than first or last so the sweep is not measuring a position bias instead of a
# count effect.
function New-ToolOffer([int] $count) {
    $target = Get-WeatherTool
    if ($count -le 1) { return , @($target) }
    $distractors = @(Get-DistractorTools) | Select-Object -First ($count - 1)
    $mid = [Math]::Floor($count / 2)
    $list = [System.Collections.Generic.List[object]]::new()
    $list.AddRange([object[]]$distractors[0..($mid - 1)])
    $list.Add($target)
    if ($mid -le $distractors.Count - 1) { $list.AddRange([object[]]$distractors[$mid..($distractors.Count - 1)]) }
    , $list.ToArray()
}

# A minimal reproduction of ToolSchemaRenderer's compact format for the flat, single-required-string-
# parameter tool shape every wire tool in dimensions 1 and 3 uses (Get-WeatherTool,
# Get-DistractorTools): real rendered chars, not the JSON request body's length. Quoting the latter
# next to dimension 5's rendered-chars figures overstated dimension 1's load by roughly 2.4x
# (adversarial review): the JSON payload carries `{"type":"function","function":{...}}` scaffolding
# the model never sees, and D83's compact renderer strips exactly that scaffolding down to a one-line
# signature per tool. (Depends on Collapse-Text, defined in the dimension 5 section below -- available
# by the time this is actually called, since every function in this file is defined, top to bottom,
# before "# --- main" calls any of them.)
function Get-CompactBlockFromFlatWireTools($wireTools) {
    $sb = [System.Text.StringBuilder]::new()
    [void] $sb.Append('You can call tools. Available tools:')
    foreach ($wt in $wireTools) {
        $fn = $wt.function
        $propName = @($fn.parameters.properties.Keys)[0]
        $propDesc = $fn.parameters.properties[$propName].description
        [void] $sb.Append("`n- ").Append($fn.name).Append('(').Append($propName).Append(': string')
        if ($propDesc) { [void] $sb.Append(' (').Append((Collapse-Text $propDesc)).Append(')') }
        [void] $sb.Append(')')
        if ($fn.description) { [void] $sb.Append(" `u{2014} ").Append((Collapse-Text $fn.description)) }
    }
    [void] $sb.Append("`n").Append('To call one or more tools, reply with ONLY this JSON in a ```json fence and nothing else:')
    [void] $sb.Append("`n").Append('{"tool_calls":[{"name":"<tool>","arguments":{...}}]}')
    [void] $sb.Append("`n").Append('If no tool is needed, answer normally in plain text. Never reply with an empty tool_calls list.')
    $sb.ToString()
}

# --- dimension 1: tool count sweep ------------------------------------------------------------------

function Invoke-ToolCountSweep {
    Write-Section 'dimension 1: tool count sweep (1, 3, 5, 8, 12, 16, 20, 25)'
    $question = 'What is the weather in Paris right now?'
    $counts = @(1, 3, 5, 8, 12, 16, 20, 25)
    $cliff = $null

    foreach ($count in $counts) {
        $tools = New-ToolOffer $count
        $renderedChars = (Get-CompactBlockFromFlatWireTools $tools).Length
        # Not $runs: PowerShell variable names are case-insensitive, so a local $runs is the same
        # storage slot as the -Runs parameter. Assigning $HeadlineRuns to it on the count=1 cell would
        # silently overwrite -Runs itself for every later cell (found the hard way: the first real run
        # of this script gave every non-headline cell 5 runs instead of 3).
        $cellRuns = if ($count -eq 1) { $HeadlineRuns } else { $Runs }
        $rows = [System.Collections.Generic.List[object]]::new()
        $jsonRuns = [System.Collections.Generic.List[object]]::new()

        for ($i = 0; $i -lt $cellRuns; $i++) {
            $body = @{ model = $Model; messages = @(@{ role = 'user'; content = $question }); tools = $tools; temperature = 0 }
            $r = Invoke-ChatOnce $body
            $ctx = "tool-count=$count run=$($i + 1)"
            Test-BridgeDefect $ctx $r
            $facts = Get-ChatFacts $r

            $correctTool = $facts.Called -and $facts.ToolNames.Count -eq 1 -and $facts.ToolNames[0] -eq 'get_weather'
            $argsValid = $false
            $argsCorrect = $false
            if ($facts.Called -and $facts.Calls.Count -gt 0) {
                try {
                    $parsedArgs = $facts.Calls[0].function.arguments | ConvertFrom-Json -Depth 10
                    $argsValid = $true
                    $argsCorrect = $correctTool -and $parsedArgs.location -and ($parsedArgs.location -match '(?i)paris')
                }
                catch { $argsValid = $false }
            }

            $row = [pscustomobject]@{
                Dimension = 'ToolCountSweep'; Cell = "count=$count"; Run = $i + 1
                RenderedChars = $renderedChars
                StatusCode = $facts.StatusCode; FinishReason = $facts.FinishReason; LatencyMs = $r.LatencyMs
                Called = $facts.Called; ToolNames = $facts.ToolNames -join ','
                CorrectTool = $correctTool; ArgsValid = $argsValid; ArgsCorrect = $argsCorrect
                ErrorCode = $facts.ErrorCode
            }
            $rows.Add($row)
            $script:calls.Add($row)
            $jsonRuns.Add([pscustomobject]@{ Facts = $facts; Result = $r })
            if ($facts.StatusCode -eq 502 -and $null -eq $cliff) { $cliff = $count }
            Write-Info "count=$count run=$($i+1): HTTP $($facts.StatusCode) finish=$($facts.FinishReason) called=$($facts.Called) tool=$($facts.ToolNames -join ',') argsValid=$argsValid argsCorrect=$argsCorrect ($($r.LatencyMs)ms)"
        }

        $statuses = ($rows | Group-Object StatusCode | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ' '
        $calledN = @($rows | Where-Object Called).Count
        $correctN = @($rows | Where-Object CorrectTool).Count
        $validN = @($rows | Where-Object ArgsValid).Count
        $accN = @($rows | Where-Object ArgsCorrect).Count
        $summary = "count=$count ($($tools.Count) tools offered, $renderedChars rendered chars): statuses [$statuses], called $calledN/$cellRuns, correct-tool $correctN/$cellRuns, args-valid $validN/$cellRuns, args-correct(value fidelity) $accN/$cellRuns"
        Write-Host "    CELL $summary" -ForegroundColor Green
        $script:cells.Add([pscustomobject]@{ Dimension = 'ToolCountSweep'; Cell = "count=$count"; Runs = $cellRuns; Summary = $summary })
        if ($Stream -and $count -eq 1) {
            $streamBody = @{ model = $Model; messages = @(@{ role = 'user'; content = $question }); tools = $tools; temperature = 0 }
            Invoke-StreamParityRuns 'ToolCountSweep' 'count=1' $jsonRuns.ToArray() $streamBody $cellRuns
        }
    }

    if ($cliff) { Write-Host "    502 regime begins at tool count = $cliff" -ForegroundColor Magenta }
    else { Write-Host '    no HTTP 502 observed anywhere in the sweep' -ForegroundColor Magenta }
    $cliff
}

# --- dimension 2: schema depth -----------------------------------------------------------------------

function Get-FlatTool {
    @{
        type     = 'function'
        function = @{
            name        = 'schedule_reminder'
            description = 'Set a reminder with a short text note'
            parameters  = @{
                type       = 'object'
                # [ordered] because the properties order the server sees is the document order
                # ToolSchemaRenderer walks (adversarial review: a plain @{} hashtable's enumeration
                # order is not guaranteed to match authoring order, so the rendered signature's
                # parameter order was never actually pinned to what this function appears to write).
                # One property here, so it made no visible difference -- fixed for correctness and
                # consistency with Get-DeepTool below, where it does matter.
                properties = [ordered]@{ text = @{ type = 'string'; description = 'The reminder text' } }
                required   = @('text')
            }
        }
    }
}

function Get-DeepTool {
    @{
        type     = 'function'
        function = @{
            name        = 'create_calendar_event'
            description = 'Create an event on the user''s calendar'
            parameters  = @{
                type       = 'object'
                properties = [ordered]@{
                    title      = @{ type = 'string'; description = 'Event title' }
                    start_time = @{ type = 'string'; description = 'Start time, ISO-8601' }
                    attendees  = @{ type = 'array'; items = @{ type = 'string' }; description = 'Attendee names' }
                    location   = @{
                        type        = 'object'
                        description = 'Where the event happens'
                        properties  = [ordered]@{
                            room     = @{ type = 'string'; description = 'Room name or number' }
                            building = @{ type = 'string'; description = 'Building name' }
                        }
                    }
                    recurrence = @{ type = 'string'; enum = @('none', 'daily', 'weekly', 'monthly'); description = 'How often the event repeats' }
                }
                required   = @('title', 'start_time', 'attendees')
            }
        }
    }
}

function Invoke-SchemaDepthProbe {
    Write-Section 'dimension 2: schema depth (flat vs. deep) at a fixed working tool count (5)'
    $distractors = @(Get-DistractorTools) | Select-Object -First 4

    $cells = @(
        @{ Name = 'flat'; Tool = (Get-FlatTool); Question = 'Set a reminder: "Call the dentist tomorrow morning".'
           # Not $args: it is PowerShell's automatic all-unbound-arguments variable, and a
           # param($args) does not rebind it the way an ordinary name would -- it arrives as a
           # boxed Object[] wrapper, not the pscustomobject passed in, so every field read comes
           # back empty and the check silently always fails (found the hard way: the first real
           # run showed fidelity=False on 3/3 flat-schema calls whose raw arguments were correct).
           Check = { param($callArgs) $callArgs.text -and ($callArgs.text -match '(?i)dentist') } }
        @{ Name = 'deep'; Tool = (Get-DeepTool)
           # Room and building are in the question specifically so the nested `location` object (the
           # one genuinely "deep" part of this schema) is actually exercised: an earlier version asked
           # nothing that needed it, so a model that never filled `location` at all still passed
           # (adversarial review). start_time is likewise now in the required-fields check below: it
           # is one of the three required properties and was previously never verified either.
           Question = 'Create a calendar event titled "Budget Review" with Alice and Bob, starting 2026-09-15T15:00:00, in Room 402 of Building 7, recurring weekly.'
           Check = {
               param($callArgs)
               $title = $callArgs.title -and ($callArgs.title -match '(?i)budget review')
               $names = @($callArgs.attendees) -join ' '
               $att = $names -match '(?i)alice' -and $names -match '(?i)bob'
               $start = $callArgs.start_time -and ($callArgs.start_time -match '2026-09-15') -and ($callArgs.start_time -match '15:00')
               $required = $title -and $att -and $start
               $loc = $callArgs.location
               $locCorrect = $loc -and ("$($loc.room)" -match '(?i)402') -and ("$($loc.building)" -match '(?i)\b7\b')
               [pscustomobject]@{ RequiredCorrect = $required; LocationCorrect = $locCorrect; RecurrenceCorrect = ($callArgs.recurrence -eq 'weekly') }
           }
        }
    )

    foreach ($cell in $cells) {
        $tools = @($distractors + $cell.Tool)
        $mid = [Math]::Floor($tools.Count / 2)
        # Same midpoint placement as the count sweep, for the same reason.
        $ordered = @($distractors[0..($mid - 1)]) + @($cell.Tool) + @($distractors[$mid..($distractors.Count - 1)])
        $rows = [System.Collections.Generic.List[object]]::new()

        for ($i = 0; $i -lt $Runs; $i++) {
            $body = @{ model = $Model; messages = @(@{ role = 'user'; content = $cell.Question }); tools = $ordered; temperature = 0 }
            $r = Invoke-ChatOnce $body
            $ctx = "schema=$($cell.Name) run=$($i + 1)"
            Test-BridgeDefect $ctx $r
            $facts = Get-ChatFacts $r

            $correctTool = $facts.Called -and $facts.ToolNames.Count -eq 1 -and $facts.ToolNames[0] -eq $cell.Tool.function.name
            $argsValid = $false
            $fidelity = $null
            if ($facts.Called -and $facts.Calls.Count -gt 0) {
                try {
                    $parsedArgs = $facts.Calls[0].function.arguments | ConvertFrom-Json -Depth 10
                    $argsValid = $true
                    if ($correctTool) { $fidelity = & $cell.Check $parsedArgs }
                }
                catch { $argsValid = $false }
            }

            $row = [pscustomobject]@{
                Dimension = 'SchemaDepth'; Cell = $cell.Name; Run = $i + 1
                StatusCode = $facts.StatusCode; FinishReason = $facts.FinishReason; LatencyMs = $r.LatencyMs
                Called = $facts.Called; ToolNames = $facts.ToolNames -join ','
                CorrectTool = $correctTool; ArgsValid = $argsValid; Fidelity = "$fidelity"
            }
            $rows.Add($row)
            $script:calls.Add($row)
            Write-Info "schema=$($cell.Name) run=$($i+1): HTTP $($facts.StatusCode) finish=$($facts.FinishReason) called=$($facts.Called) tool=$($facts.ToolNames -join ',') argsValid=$argsValid fidelity=$fidelity ($($r.LatencyMs)ms)"
        }

        $calledN = @($rows | Where-Object Called).Count
        $correctN = @($rows | Where-Object CorrectTool).Count
        $validN = @($rows | Where-Object ArgsValid).Count
        $summary = "schema=$($cell.Name): called $calledN/$Runs, correct-tool $correctN/$Runs, args-valid $validN/$Runs, fidelity: $(($rows | ForEach-Object { $_.Fidelity }) -join ' | ')"
        Write-Host "    CELL $summary" -ForegroundColor Green
        $script:cells.Add([pscustomobject]@{ Dimension = 'SchemaDepth'; Cell = $cell.Name; Runs = $Runs; Summary = $summary })
    }
}

# --- dimension 3: system prompt pressure --------------------------------------------------------------

# Builds a system prompt of roughly $targetTokens Phi-3 tokens by repeating an agent-style paragraph
# and measuring the real count through /debug/tokenize (the runtime's own tokenizer, D80) rather than
# assuming a chars-per-token ratio, then trims to the closest whole repeat under the target. "Roughly"
# per the brief -- this does not bisect to an exact count, one measured repeat count is enough.
function New-SystemPromptOfLength([int] $targetTokens) {
    if ($targetTokens -le 0) { return '' }
    $unit = 'You are an autonomous coding agent operating inside a sandboxed repository on behalf of the user. Always follow the project''s existing conventions, prefer the smallest correct diff, and explain your reasoning before making a change. Verify that your changes build and that the relevant tests pass before reporting the task complete. Never fabricate a result you have not actually observed. '
    # Invoke-Tokenize (defined below, but every function in this file is available by the time any of
    # them is actually called from "# --- main") throws on a failed or unusable tokenize response
    # rather than returning something a caller could silently floor to 1 -- see its own comment for why
    # that matters here specifically (adversarial review finding #1).
    $tokensPerUnit = (Invoke-Tokenize $unit).tokens
    $repeats = [Math]::Max(1, [Math]::Round($targetTokens / $tokensPerUnit))
    $text = $unit * $repeats
    $actual = (Invoke-Tokenize $text).tokens
    [pscustomobject]@{ Text = $text; Tokens = $actual }
}

function Invoke-SystemPromptPressureProbe {
    Write-Section 'dimension 3: system prompt pressure (~0, ~500, ~1500 Phi-3 tokens) at a fixed working tool count (5)'
    $question = 'What is the weather in Paris right now?'
    $distractors = @(Get-DistractorTools) | Select-Object -First 4
    $target = Get-WeatherTool
    $tools = @($distractors[0..1]) + @($target) + @($distractors[2..3])

    foreach ($targetTokens in @(0, 500, 1500)) {
        $sys = if ($targetTokens -eq 0) { [pscustomobject]@{ Text = ''; Tokens = 0 } } else { New-SystemPromptOfLength $targetTokens }
        $rows = [System.Collections.Generic.List[object]]::new()

        for ($i = 0; $i -lt $Runs; $i++) {
            $messages = [System.Collections.Generic.List[object]]::new()
            if ($sys.Text) { $messages.Add(@{ role = 'system'; content = $sys.Text }) }
            $messages.Add(@{ role = 'user'; content = $question })
            $body = @{ model = $Model; messages = $messages.ToArray(); tools = $tools; temperature = 0 }
            $r = Invoke-ChatOnce $body
            $ctx = "sysprompt~$targetTokens run=$($i + 1)"
            Test-BridgeDefect $ctx $r
            $facts = Get-ChatFacts $r

            $correctTool = $facts.Called -and $facts.ToolNames.Count -eq 1 -and $facts.ToolNames[0] -eq 'get_weather'
            $row = [pscustomobject]@{
                Dimension = 'SystemPromptPressure'; Cell = "~$targetTokens tokens (actual $($sys.Tokens))"; Run = $i + 1
                StatusCode = $facts.StatusCode; FinishReason = $facts.FinishReason; LatencyMs = $r.LatencyMs
                Called = $facts.Called; ToolNames = $facts.ToolNames -join ','; CorrectTool = $correctTool
                ErrorCode = $facts.ErrorCode
            }
            $rows.Add($row)
            $script:calls.Add($row)
            Write-Info "sysprompt~$targetTokens(actual $($sys.Tokens)) run=$($i+1): HTTP $($facts.StatusCode) finish=$($facts.FinishReason) called=$($facts.Called) tool=$($facts.ToolNames -join ',') ($($r.LatencyMs)ms)"
        }

        $statuses = ($rows | Group-Object StatusCode | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ' '
        $calledN = @($rows | Where-Object Called).Count
        $correctN = @($rows | Where-Object CorrectTool).Count
        $summary = "system prompt ~$targetTokens tokens (actual $($sys.Tokens)): statuses [$statuses], called $calledN/$Runs, correct-tool $correctN/$Runs"
        Write-Host "    CELL $summary" -ForegroundColor Green
        $script:cells.Add([pscustomobject]@{ Dimension = 'SystemPromptPressure'; Cell = "~$targetTokens tokens"; Runs = $Runs; Summary = $summary })
    }
}

# --- dimension 4: multi-step tool result round trip -----------------------------------------------------

function Invoke-MultiStepProbe {
    Write-Section 'dimension 4: multi-step -- a tool result fed back on a second turn (one case)'
    $question = 'What is the weather in Paris right now?'
    # This cell alone offers a zero-argument tool so the next hardware run can measure whether the
    # model uses its short {"name":"get_time"} form without changing the other dimensions' counts.
    $tools = @(
        (Get-WeatherTool)
        @{
            type     = 'function'
            function = @{
                name        = 'get_time'
                description = 'Get the current time'
            }
        }
    )

    $firstCallJson = $null
    $attempts = 0
    $result = $null
    $healthBefore = Get-HealthzSnapshot
    while ($null -eq $firstCallJson -and $attempts -lt 3) {
        $attempts++
        $body = @{ model = $Model; messages = @(@{ role = 'user'; content = $question }); tools = $tools; temperature = 0 }
        $result = Invoke-ChatOnce $body
        Test-BridgeDefect "multistep turn1 attempt=$attempts" $result
        $facts = Get-ChatFacts $result
        Write-Info "turn 1 attempt $attempts`: HTTP $($facts.StatusCode) finish=$($facts.FinishReason) called=$($facts.Called) ($($result.LatencyMs)ms)"
        if ($facts.Called -and $facts.Calls.Count -gt 0) {
            try { $null = $facts.Calls[0].function.arguments | ConvertFrom-Json; $firstCallJson = $facts.Calls[0] } catch {}
        }
    }

    if ($null -eq $firstCallJson) {
        $healthAfter = Get-HealthzSnapshot
        $bracket = [pscustomobject]@{
            Dimension = 'MultiStep'; Cell = 'round-trip'; Turn2Attempted = $false
            Before = $healthBefore; After = $healthAfter
            HitsDelta = $healthAfter.ContextCacheHits - $healthBefore.ContextCacheHits
            MissesDelta = $healthAfter.ContextCacheMisses - $healthBefore.ContextCacheMisses
            Verdict = 'not exercised: no valid turn-1 tool call'
        }
        $script:cacheBrackets.Add($bracket)
        $summary = "could not obtain a tool call after $attempts attempt(s); multi-step round trip not exercised this run; cache bracket: $($bracket.Verdict) (hits $($healthBefore.ContextCacheHits)->$($healthAfter.ContextCacheHits), misses $($healthBefore.ContextCacheMisses)->$($healthAfter.ContextCacheMisses))"
        Write-Host "    CELL $summary" -ForegroundColor Green
        $script:cells.Add([pscustomobject]@{ Dimension = 'MultiStep'; Cell = 'round-trip'; Runs = $attempts; Summary = $summary; CacheBracket = $bracket })
        return
    }

    $toolResult = @{ temperature_c = 18; condition = 'cloudy'; city = 'Paris' } | ConvertTo-Json -Compress
    $turn2Messages = @(
        @{ role = 'user'; content = $question }
        @{ role = 'assistant'; content = $null; tool_calls = @(@{ id = $firstCallJson.id; type = 'function'; function = @{ name = $firstCallJson.function.name; arguments = $firstCallJson.function.arguments } }) }
        @{ role = 'tool'; tool_call_id = $firstCallJson.id; name = $firstCallJson.function.name; content = $toolResult }
        @{ role = 'user'; content = 'Given that weather result, should I bring an umbrella? Answer in one short sentence.' }
    )
    $body2 = @{ model = $Model; messages = $turn2Messages; tools = $tools; temperature = 0 }
    $r2 = Invoke-ChatOnce $body2
    Test-BridgeDefect 'multistep turn2' $r2
    $facts2 = Get-ChatFacts $r2
    $healthAfter = Get-HealthzSnapshot
    $hitsDelta = $healthAfter.ContextCacheHits - $healthBefore.ContextCacheHits
    $missesDelta = $healthAfter.ContextCacheMisses - $healthBefore.ContextCacheMisses
    $cacheVerdict = if ($hitsDelta -eq 1) { 'hit increased by exactly 1' } else { "FINDING: expected hits +1, observed $hitsDelta" }
    $bracket = [pscustomobject]@{
        Dimension = 'MultiStep'; Cell = 'round-trip'; Turn2Attempted = $true
        Before = $healthBefore; After = $healthAfter; HitsDelta = $hitsDelta; MissesDelta = $missesDelta; Verdict = $cacheVerdict
    }
    $script:cacheBrackets.Add($bracket)
    Write-Info "cache bracket: $cacheVerdict (hits $($healthBefore.ContextCacheHits)->$($healthAfter.ContextCacheHits), misses $($healthBefore.ContextCacheMisses)->$($healthAfter.ContextCacheMisses))"

    # Named for exactly what this checks (adversarial review: the old name "Sensible" implied the reply
    # was verified to relate to the injected tool result -- it isn't, only that a 200 with no repeated
    # tool call carries non-empty content). "Sensible" is a judgment about *content*; this is a fact
    # about *shape*.
    $answeredInProse = $facts2.StatusCode -eq 200 -and -not $facts2.Called -and $facts2.Content -and $facts2.Content.Length -gt 0
    $repeatedCall = $facts2.Called -and $facts2.ToolNames -contains 'get_weather'
    $verdict = if ($answeredInProse) { 'second turn produced ordinary content (answered in prose)' }
               elseif ($repeatedCall) { 'second turn called get_weather again instead of answering from the tool result' }
               else { "second turn: HTTP $($facts2.StatusCode) finish=$($facts2.FinishReason) called=$($facts2.Called)" }

    $row = [pscustomobject]@{
        Dimension = 'MultiStep'; Cell = 'round-trip'; Run = 1
        StatusCode = $facts2.StatusCode; FinishReason = $facts2.FinishReason; LatencyMs = $r2.LatencyMs
        Called = $facts2.Called; AnsweredInProse = $answeredInProse; RepeatedCall = $repeatedCall
        Content = $facts2.Content; CacheBracket = $bracket
    }
    $script:calls.Add($row)
    Write-Info "turn 2: HTTP $($facts2.StatusCode) finish=$($facts2.FinishReason) called=$($facts2.Called) content='$($facts2.Content)' ($($r2.LatencyMs)ms)"

    $summary = "turn1 attempts=$attempts, tool=$($firstCallJson.function.name) args=$($firstCallJson.function.arguments) -> turn2: $verdict; cache bracket: $cacheVerdict (hits $($healthBefore.ContextCacheHits)->$($healthAfter.ContextCacheHits), misses $($healthBefore.ContextCacheMisses)->$($healthAfter.ContextCacheMisses))"
    Write-Host "    CELL $summary" -ForegroundColor Green
    $script:cells.Add([pscustomobject]@{ Dimension = 'MultiStep'; Cell = 'round-trip'; Runs = $attempts + 1; Summary = $summary; CacheBracket = $bracket })
}

# --- dimension 5: window occupancy ------------------------------------------------------------------

# scripts/tool-probe.ps1's own count sweep (dimension 1) never got near issue #29's COMException wall:
# its 24 invented distractors are deliberately flat, and the resulting 25-tool catalog RENDERS to only
# 2,487 characters (Get-CompactBlockFromFlatWireTools; NOT the ~10 KB uncompressed JSON request body --
# quoting that figure here in an earlier version of this comment overstated dimension 1's load by
# roughly 2.4x, adversarial review), an order of magnitude under the 40,000-44,000-char boundary a
# plain system message was measured to hit. A real agent's 25-tool catalog renders to ~40,361 chars --
# ~1,614 chars per tool, not ~100. This dimension holds tool COUNT fixed at 8 and instead scales each
# tool's own
# description and its parameters' descriptions with realistic, verbose documentation prose (the kind a
# real agent's tool schemas actually carry: usage notes, caveats, examples) until the *rendered*
# tool-instruction block -- not an approximation of it -- reaches each target occupancy.
#
# "Rendered" is not a figure of speech here: ToolSchemaRenderer's compact format (the default,
# src/NpuBridge.Core/Tools/ToolSchemaRenderer.cs) is reproduced byte-for-byte below (Get-CompactBlock),
# not estimated, because no debug endpoint exposes the actual system text the bridge builds from
# `tools`. Since these requests carry no system message of their own, PromptTemplate.Combine (D71/D83)
# makes the final system text exactly equal to this block (no separator, nothing else folded in), so
# reproducing the renderer exactly is what makes the char/token figures reported here real measurements
# against the bridge's own tokenizer (via POST /debug/tokenize), not guesses.

# One realistic tool per line: a short base description plus a pool of realistic extension sentences
# (usage notes, caveats, examples -- the kind of prose real tool docs carry) used to grow that tool's
# description when a cell needs more content than the base catalog provides. get_weather is the target
# (question: "What is the weather in Paris right now?"); the other seven are plausible agent tools that
# are never the right answer, exactly as in dimension 1, but here every one of the eight -- target
# included -- gets padded, because a real agent's tool schemas are uniformly verbose, not just the
# distractors.
function Get-OccupancyToolLibrary {
    @(
        @{
            Name = 'get_weather'; ParamName = 'location'
            Description = 'Retrieves the current weather conditions for a given city, including temperature, precipitation, and general conditions.'
            ParamDescription = "Name of the city to look up, e.g. 'Paris' or 'Tokyo'."
            Pool = @(
                'Results are sourced from a live meteorological feed and are typically accurate to within the last fifteen minutes.'
                'If the city name is ambiguous, the tool resolves to the most populous match and includes the resolved region in its response.'
                'Temperature is returned in Celsius by default; callers that need Fahrenheit should convert client-side.'
                'This tool does not accept postal codes or GPS coordinates, only free-text city names.'
                'Rate limits apply after approximately sixty calls per minute from a single session.'
                'Historical weather data is not available through this tool; only the current snapshot is returned.'
                "For cities with the same name in multiple countries, prefer appending the country, e.g. 'Paris, France'."
                'The underlying provider occasionally reports stale data during severe weather events; treat extreme readings with caution.'
            )
        }
        @{
            Name = 'read_file'; ParamName = 'path'
            Description = 'Reads the full contents of a text file at the given path and returns it as a UTF-8 string.'
            ParamDescription = 'Absolute or workspace-relative path to the file to read.'
            Pool = @(
                'Binary files are not supported and will return a decoding error rather than raw bytes.'
                'Files larger than a few megabytes may be truncated; check the response for a truncation flag.'
                'Symbolic links are followed, but a link cycle will cause the call to fail after a bounded number of hops.'
                "Line endings are normalized to a single newline character regardless of the file's original encoding on disk."
                'A missing file returns a structured not-found error rather than throwing, so callers should check the status field.'
                'Reading a directory instead of a file is treated as an error, not as a directory listing.'
                'This tool does not modify the file in any way; use the write or edit tool for changes.'
                'Relative paths are resolved against the current working directory of the agent session.'
            )
        }
        @{
            Name = 'grep_search'; ParamName = 'pattern'
            Description = 'Searches file contents for lines matching a regular expression pattern and returns the matches with file and line context.'
            ParamDescription = 'A regular expression in standard PCRE-like syntax.'
            Pool = @(
                'Matching is case-sensitive by default; prefix the pattern with (?i) for case-insensitive search.'
                'Binary files are skipped automatically to avoid returning unreadable matches.'
                'Results are capped at a few hundred matches per call to keep responses a manageable size.'
                'The search recurses into subdirectories unless a more specific path is supplied.'
                'Multi-line patterns are not supported; each line is matched independently.'
                'Hidden files and directories beginning with a dot are excluded from the search by default.'
                'A malformed regular expression returns a parse error describing the offending position.'
                'This tool only searches; it never modifies any file it scans.'
            )
        }
        @{
            Name = 'run_bash'; ParamName = 'command'
            Description = 'Executes a shell command in the current working directory and returns its combined standard output and error.'
            ParamDescription = 'The full shell command line to execute, exactly as it would be typed in a terminal.'
            Pool = @(
                'Commands run with a timeout; a process that does not exit in time is forcibly terminated.'
                'Interactive commands that expect terminal input will hang and should be avoided.'
                'Destructive commands are not blocked automatically; the caller is responsible for confirming intent first.'
                'Environment variables from the parent session are inherited unless explicitly overridden.'
                'Output beyond a size limit is truncated, with a marker indicating how much was cut.'
                'The exit code of the command is always included in the response, even on success.'
                'Piping and shell redirection are supported since the command runs through the system shell.'
                'This tool does not persist working-directory changes between calls; each invocation starts fresh.'
            )
        }
        @{
            Name = 'web_fetch'; ParamName = 'url'
            Description = 'Fetches the contents of a URL over HTTP or HTTPS and returns the response body as text.'
            ParamDescription = 'A fully qualified URL beginning with http:// or https://.'
            Pool = @(
                'Redirects are followed automatically, up to a small fixed number of hops.'
                'Responses larger than a configured size are truncated before being returned.'
                'Binary content types such as images are rejected rather than returned as garbled text.'
                'A request that times out returns a timeout error rather than partial content.'
                'This tool does not execute any JavaScript on the page; only the raw response body is returned.'
                'Authentication headers are not supported; only publicly accessible URLs can be fetched.'
                'HTML responses are returned as raw markup, not converted to plain text or markdown.'
                'Repeated calls to the same URL are not cached; each call performs a fresh network request.'
            )
        }
        @{
            Name = 'send_email'; ParamName = 'to'
            Description = 'Sends an email to the given recipient with a subject and body composed by the caller.'
            ParamDescription = "The recipient's email address."
            Pool = @(
                'Attachments are not supported through this interface; only plain text or HTML bodies may be sent.'
                'The sender address is fixed to the account configured for this agent and cannot be overridden.'
                'Emails are queued for delivery and this tool returns as soon as the message is accepted, not once delivered.'
                'Sending to more than one recipient at a time is not supported by this version of the tool.'
                'A malformed email address returns a validation error before any send attempt is made.'
                'This tool should be used sparingly; it is intended for genuine notifications, not bulk messaging.'
                'Delivery failures after acceptance are not reported back through this tool''s response.'
                'The email body is sent as-is; no signature or footer is appended automatically.'
            )
        }
        @{
            Name = 'git_commit'; ParamName = 'message'
            Description = 'Creates a git commit in the current repository with the given commit message, committing all currently staged changes.'
            ParamDescription = "The commit message to use, ideally following the repository's existing conventions."
            Pool = @(
                'Nothing is committed if there are no staged changes; the call reports this rather than creating an empty commit.'
                'This tool does not stage files automatically; changes must already be added to the index.'
                'Commit hooks configured in the repository still run and may reject the commit.'
                'The resulting commit hash is included in the response for reference.'
                'This tool never force-pushes or rewrites history; it only creates new commits.'
                "Author and committer identity are taken from the repository's existing git configuration."
                'A multi-line commit message is supported; the first line is treated as the summary.'
                'This tool does not push the commit to any remote; that is a separate step.'
            )
        }
        @{
            Name = 'create_github_issue'; ParamName = 'title'
            Description = 'Creates a new issue in the configured GitHub repository with the given title.'
            ParamDescription = 'A short, descriptive title for the issue.'
            Pool = @(
                'The issue body and labels are optional and default to empty when not otherwise specified.'
                'Issues are created against the repository the agent session is currently configured for.'
                'Duplicate-looking titles are still created as separate issues; no deduplication is performed.'
                'This tool requires the configured credentials to have issue-creation permission on the repository.'
                "The newly created issue's number and URL are returned in the response."
                "Very long titles may be truncated by GitHub's own limits before the issue is created."
                'This tool does not search for existing issues; pair it with a search tool first if duplication matters.'
                'Rate limits from the GitHub API apply and may cause this call to fail during heavy usage.'
            )
        }
    )
}

# Every whitespace run to one space, trimmed -- ToolSchemaRenderer.Collapse's effect on plain
# single-spaced prose (our descriptions never carry embedded newlines or doubled spaces, so this is
# only ever a safety net).
function Collapse-Text([string] $text) {
    if ([string]::IsNullOrEmpty($text)) { return $text }
    ($text -replace '\s+', ' ').Trim()
}

# ToolSchemaRenderer.Render's compact mode, reproduced exactly (see the block comment above this
# section): same literal preamble/call-instruction/envelope/closing strings, same "- name(params) —
# description" shape, same "name: string (description)" / "name?: string (description)" parameter
# shape (every parameter here is a plain required string, so RenderType always resolves to "string" and
# the enum/array/union branches never trigger), same em-dash separator (U+2014, padded with a space on
# each side, matching ToolSchemaRenderer.DescriptionSeparator), and the same OptionalClosing line that
# a request with no tool_choice (Auto) resolves to (ToolCatalog.ToolChoice, ToolSchemaRenderer.Closing).
function Get-CompactBlock($tools) {
    $sb = [System.Text.StringBuilder]::new()
    [void] $sb.Append('You can call tools. Available tools:')
    foreach ($t in $tools) {
        [void] $sb.Append("`n- ").Append($t.Name).Append('(')
        [void] $sb.Append($t.ParamName).Append(': string')
        if ($t.ParamDescription) { [void] $sb.Append(' (').Append((Collapse-Text $t.ParamDescription)).Append(')') }
        [void] $sb.Append(')')
        if ($t.Description) { [void] $sb.Append(" `u{2014} ").Append((Collapse-Text $t.Description)) }
    }
    [void] $sb.Append("`n").Append('To call one or more tools, reply with ONLY this JSON in a ```json fence and nothing else:')
    [void] $sb.Append("`n").Append('{"tool_calls":[{"name":"<tool>","arguments":{...}}]}')
    [void] $sb.Append("`n").Append('If no tool is needed, answer normally in plain text. Never reply with an empty tool_calls list.')
    $sb.ToString()
}

# The wire-shape tools array a client actually sends -- independent of Get-CompactBlock, which exists
# only to measure what the bridge will render from this same catalog.
function ConvertTo-WireTools($tools) {
    $tools | ForEach-Object {
        @{
            type     = 'function'
            function = @{
                name        = $_.Name
                description = $_.Description
                parameters  = @{
                    type       = 'object'
                    properties = @{ ($_.ParamName) = @{ type = 'string'; description = $_.ParamDescription } }
                    required   = @($_.ParamName)
                }
            }
        }
    }
}

# SAFETY-CRITICAL (adversarial review finding #1): every caller of this function divides a char
# count by the "tokens" it returns to calibrate how much text to send. /debug/tokenize is
# loopback-gated, so -SkipHttpErrorCheck's usual "never throw on a non-200" rule does NOT apply
# here -- a non-200, or a 200 body with no usable 'tokens' field, must throw rather than let the
# caller's [Math]::Max(1, ...) floor a null to 1 and turn a calibration ratio into thousands. That
# exact bug, undetected, computed a ~9.5-million-char goal for the 150% cell -- 200x past the
# ~44,000-char boundary that fail-fasts WorkloadsSessionHost.exe (STATUS_STACK_BUFFER_OVERRUN) and
# wedges the whole Phi Silica subsystem for minutes. This throw, plus the 30,000-char clamp in
# Build-OccupancyCell and the iteration cap in Add-OccupancyPadding, are the three structural layers
# that are supposed to make that boundary un-crossable by construction, not by luck.
function Invoke-Tokenize([string] $text) {
    $body = @{ text = $text } | ConvertTo-Json -Compress
    $r = Invoke-WebRequest -Uri "$base/debug/tokenize" -Method POST -Body $body -ContentType 'application/json' -TimeoutSec 15 -SkipHttpErrorCheck
    if ([int] $r.StatusCode -ne 200) {
        throw "POST /debug/tokenize returned HTTP $($r.StatusCode), refusing to calibrate a request size from a failed tokenize call: $($r.Content)"
    }
    $parsed = $r.Content | ConvertFrom-Json
    if ($null -eq $parsed -or $null -eq $parsed.tokens -or $parsed.tokens -le 0) {
        throw "POST /debug/tokenize returned no usable 'tokens' field (body: $($r.Content)); refusing to calibrate a request size from it"
    }
    $parsed
}

# Grows each tool's description by appending its next pool sentence, round-robin across all eight
# tools (so padding is spread evenly rather than dumped onto one tool), cycling each tool's own pool
# again (a little repetition, never filler) once its eight unique sentences are exhausted. Tracks the
# block length incrementally rather than re-rendering the whole block every iteration, which matters
# once a cell needs a couple hundred sentences. $reverse walks each tool's pool back-to-front instead
# of front-to-back -- same sentences, same repetition depth, opposite order -- for the repetition-vs-
# occupancy control cell (adversarial review: forward cells are nested supersets, so "degrades at 70%"
# is confounded with "drowning in the Nth repeat of the same boilerplate" unless something holds
# occupancy fixed while changing wording order).
#
# SAFETY-CRITICAL (adversarial review finding #1): $maxIterations is the second of three structural
# layers (with Invoke-Tokenize's throw and Build-OccupancyCell's 30,000-char clamp) that make the
# ~44,000-char WorkloadsSessionHost.exe crash boundary un-crossable by this script even if a caller
# somehow passes a bad target -- 5,000 iterations at this catalog's shortest pool sentence (~90 chars)
# is several hundred KB, so the cap throws long before the loop itself could build anything near the
# boundary; it exists as the backstop, not the primary control.
function Add-OccupancyPadding($tools, [int] $targetChars, [bool] $reverse = $false) {
    $current = (Get-CompactBlock $tools).Length
    if ($current -ge $targetChars) { return $tools }
    $poolIndex = @{}
    foreach ($t in $tools) { $poolIndex[$t.Name] = 0 }
    $i = 0
    $maxIterations = 5000
    while ($current -lt $targetChars -and $i -lt $maxIterations) {
        $t = $tools[$i % $tools.Count]
        $n = $poolIndex[$t.Name] % $t.Pool.Count
        $idx = if ($reverse) { $t.Pool.Count - 1 - $n } else { $n }
        $sentence = $t.Pool[$idx]
        $before = $t.Description.Length
        $t.Description = ($t.Description.TrimEnd() + ' ' + $sentence)
        $current += ($t.Description.Length - $before)
        $poolIndex[$t.Name]++
        $i++
    }
    if ($i -ge $maxIterations) {
        throw "Add-OccupancyPadding hit its $maxIterations-iteration safety cap before reaching $targetChars chars (stopped at $current chars) -- refusing to keep growing the request; this should never happen with the 30,000-char clamp in Build-OccupancyCell, so something upstream passed a bad target"
    }
    $tools
}

# SAFETY-CRITICAL cap (adversarial review finding #1, third of the three structural layers): no cell
# built by this function is ever allowed to target more than this many characters of rendered system
# text, full stop -- clamped below regardless of what a token-percentage calibration computes or what
# an absolute char target asks for. It sits comfortably under the ~40,000-char measured-safe ceiling
# (clean 400, no crash) and well under the ~44,000-char boundary that fail-fasts
# WorkloadsSessionHost.exe. This is what makes the boundary structural rather than incidental: even a
# caller asking for 500% of the window, or a tokenize ratio that came back wrong, cannot produce a
# request this script will actually send past this line.
$script:MaxOccupancyChars = 30000

# The real never-exceeded backstop (see Build-OccupancyCell's closing assertion): $MaxOccupancyChars
# above is the calibration TARGET, which Add-OccupancyPadding can overshoot by up to one sentence's
# length because it grows in whole sentences. This is the hard ceiling, generous enough that normal
# sentence-granularity overshoot never trips it, but still comfortably under the ~40,000-char
# measured-safe boundary and far under the ~44,000-char crash regime.
$script:HardCeilingChars = 32000

# Builds one cell's tool catalog and measures the block it actually renders to. For a token-percentage
# target, calibrates a char goal from the *current* catalog's own real chars-per-token ratio (measured
# through /debug/tokenize, never assumed -- and never a silent 1 on failure, since Invoke-Tokenize now
# throws instead of returning something ratio math could floor), grows to that goal, re-measures, and
# takes one correction pass if the real token count still misses the target by more than ~8% -- the
# same calibrate-then-verify approach dimension 3 uses for its system-prompt sizes, extended with a
# second pass since these targets range two orders of magnitude wider. A char-regime cell (absolute
# target, no token goal) skips calibration entirely: the target chars are the ground truth.
#
# $reverse threads through to Add-OccupancyPadding for the repetition-order control cell.
# get_weather is rotated to the midpoint of the 8-tool catalog before any padding happens (matching
# New-ToolOffer's placement in dimension 1 and the midpoint placement in dimension 2's schema-depth
# cells) so this dimension's call rates are not confounded by position bias the way an earlier version
# was: Get-OccupancyToolLibrary lists it first, and dimension 5 never reordered it.
function Build-OccupancyCell([int] $targetTokens, [int] $targetChars, [bool] $reverse = $false) {
    $raw = @(Get-OccupancyToolLibrary | ForEach-Object { [hashtable] $_.Clone() })
    $target = @($raw | Where-Object { $_.Name -eq 'get_weather' })[0]
    $distractors = @($raw | Where-Object { $_.Name -ne 'get_weather' })
    $mid = [Math]::Floor($raw.Count / 2)
    $tools = @($distractors[0..($mid - 1)]) + @($target) + @($distractors[$mid..($distractors.Count - 1)])

    if ($targetChars -gt 0) {
        $goalChars = $targetChars
    }
    else {
        $block0 = Get-CompactBlock $tools
        $tok0 = (Invoke-Tokenize $block0).tokens
        $ratio = $block0.Length / $tok0
        $goalChars = [Math]::Round($targetTokens * $ratio)
    }
    $goalChars = [Math]::Min($goalChars, $script:MaxOccupancyChars)

    $tools = Add-OccupancyPadding $tools $goalChars $reverse
    $block = Get-CompactBlock $tools
    $measured = Invoke-Tokenize $block

    if ($targetChars -le 0) {
        $missPct = [Math]::Abs($measured.tokens - $targetTokens) / $targetTokens
        if ($missPct -gt 0.08) {
            $ratio2 = $block.Length / $measured.tokens
            $goalChars2 = [Math]::Min([Math]::Round($targetTokens * $ratio2), $script:MaxOccupancyChars)
            $tools = Add-OccupancyPadding $tools $goalChars2 $reverse
            $block = Get-CompactBlock $tools
            $measured = Invoke-Tokenize $block
        }
    }

    # The $goalChars clamp above is a target, not a guarantee: Add-OccupancyPadding grows in whole
    # sentences, so the actual block can overshoot the goal by up to one sentence's length. Verified
    # (offline, no NPU) at a deliberately absurd target: a 999,999-token goal still clamped to a
    # 30,010-char block, not the tens of millions of characters the unclamped ratio math would have
    # produced. $script:HardCeilingChars is the real structural backstop -- a hard assertion, not a
    # best-effort clamp, with generous headroom under the ~40,000-char measured-safe ceiling so normal
    # sentence-granularity overshoot never trips it, but nothing built by this function can ever leave
    # here having actually crossed it.
    if ($block.Length -gt $script:HardCeilingChars) {
        throw "Build-OccupancyCell produced a $($block.Length)-char block, over the $($script:HardCeilingChars)-char hard ceiling -- refusing to return it rather than risk a request anywhere near the ~40,000-44,000-char crash regime"
    }

    [pscustomobject]@{
        Tools = $tools; Block = $block; Chars = $block.Length; Tokens = $measured.tokens
        TargetTokens = $targetTokens; TargetChars = $targetChars; Reversed = $reverse
    }
}

# A cheap, tool-free control call (the same one-liner smoke.ps1 and every other dimension's headline
# case uses) run before each occupancy cell. The Phi Silica RPC service has been observed to crash
# outright under sustained load (session-verified: after ~86 prior generations, every request --
# including this exact one-liner -- started failing with "COMException: The RPC server is
# unavailable" and stayed dead until the process was recreated). A cell built on top of a dead backend
# produces uniform sub-20ms 502s that look nothing like a real context-size failure (those take
# hundreds of ms to seconds) and are not a finding about window occupancy at all, so this check exists
# to catch that condition immediately rather than let a whole sweep run against a corpse.
function Test-BackendLiveness {
    $body = @{ model = $Model; messages = @(@{ role = 'user'; content = 'Reply with exactly the word PONG.' }); temperature = 0 }
    $r = Invoke-ChatOnce $body 30
    $facts = Get-ChatFacts $r
    $ok = $facts.StatusCode -eq 200 -and $facts.Content -and ($facts.Content -match 'PONG')
    [pscustomobject]@{ Ok = $ok; StatusCode = $facts.StatusCode; LatencyMs = $r.LatencyMs; Content = $facts.Content; ErrorMessage = $facts.ErrorMessage }
}

# Checked twice before the sweep gives up on the backend (adversarial review finding: a single
# `-match 'PONG'` miss used to abort the whole run, and a reply like "P O N G" -- or one genuinely
# transient hiccup -- would have been indistinguishable from the real, sustained RPC death this gate
# exists to catch). Only two-for-two failures, back to back, count as dead.
function Test-BackendLivenessWithRetry {
    $first = Test-BackendLiveness
    if ($first.Ok) { return $first }
    Write-Info "liveness check missed once (HTTP $($first.StatusCode)); retrying once before declaring the backend dead"
    Test-BackendLiveness
}

function Invoke-WindowOccupancyProbe {
    param(
        [string[]] $CellLabels = @('25%', '50%', '70%', '85%', '95%', '110%', '150%')
    )
    Write-Section 'dimension 5: window occupancy -- tool count fixed at 8, description size swept'
    $question = 'What is the weather in Paris right now?'
    $windowTokens = 3581   # measured D80 usable window of an empty Phi Silica context

    # (occupancy label, target token % of the window, absolute char override, reverse pool order). Runs
    # comes from -Runs (the hardcoded 3 an earlier version had was its own bug -- it silently
    # contradicted this script's own help text). The two char-regime cells this dimension originally
    # planned (~40,000 and ~48,000 chars, to bracket issue #29's own boundary directly) are not in this
    # table: an over-large system prompt fail-fasts WorkloadsSessionHost.exe (0xc0000409
    # STATUS_STACK_BUFFER_OVERRUN) rather than merely erroring, and repeated crashes wedge the whole
    # Phi Silica subsystem for minutes. The measured-safe boundary (clean 400 at 40,000 chars, crashes
    # at 44,000+) is cited from that investigation, not re-measured here; Build-OccupancyCell's
    # 30,000-char clamp makes 40,000+ structurally unreachable from this table regardless.
    #
    # '70%-reversed' is the repetition-order control (adversarial review): same target token count as
    # '70%', same tool catalog, but each tool's padding pool is consumed back-to-front instead of
    # front-to-back. Forward cells are nested supersets of each other, so repetition DEPTH grows with
    # occupancy -- by 70% each tool's 8-sentence pool has already cycled about 1.5 times. Reversing the
    # consumption order holds the repetition depth and the occupancy both fixed while changing which
    # sentences land in which position, so if 70%-reversed is clean where forward-70% was not, the
    # forward dip was about that specific wording, not about window occupancy. It is opt-in (not in the
    # default $CellLabels) because it only means something paired with a same-session forward-70% run.
    $allCells = @(
        @{ Label = '25%';          Pct = 25;  Chars = 0; Reverse = $false }
        @{ Label = '50%';          Pct = 50;  Chars = 0; Reverse = $false }
        @{ Label = '70%';          Pct = 70;  Chars = 0; Reverse = $false }
        @{ Label = '70%-reversed'; Pct = 70;  Chars = 0; Reverse = $true }
        @{ Label = '85%';          Pct = 85;  Chars = 0; Reverse = $false }
        @{ Label = '95%';          Pct = 95;  Chars = 0; Reverse = $false }
        @{ Label = '110%';         Pct = 110; Chars = 0; Reverse = $false }
        @{ Label = '150%';         Pct = 150; Chars = 0; Reverse = $false }
    )
    $plan = @($allCells | Where-Object { $CellLabels -contains $_.Label })
    if ($plan.Count -eq 0) { throw "no cells matched -CellLabels $($CellLabels -join ','); valid labels are $(($allCells | ForEach-Object Label) -join ', ')" }

    $cliff400 = $null
    $cliff502 = $null
    $degradeAt = $null
    $deadAtCell = $null
    $deadStatus = $null
    $observedHttp400 = $false
    $observedHttp502 = $false
    $completedCells = 0

    foreach ($cell in $plan) {
        # Liveness gate (coordinator-requested, after the prior run's void 24/24 that turned out to be
        # a dead backend, not a real finding): confirm the backend can still answer the simplest
        # possible request before spending this cell's generations on it. A failure here stops the
        # whole sweep immediately -- no restart attempted, none of the remaining cells run -- and is
        # itself the report: how far the sweep got before the backend died is real information about
        # sustained-load stability, which none of the four earlier dimensions individually exercised
        # at this volume. Retried once (Test-BackendLivenessWithRetry) before being believed.
        $live = Test-BackendLivenessWithRetry
        Write-Info "liveness check before cell $($cell.Label): HTTP $($live.StatusCode) ok=$($live.Ok) ($($live.LatencyMs)ms)"
        if (-not $live.Ok) {
            if ($live.StatusCode -eq 400) { $observedHttp400 = $true }
            if ($live.StatusCode -eq 502) { $observedHttp502 = $true }
            Write-Host "    BACKEND DEAD before cell $($cell.Label): HTTP $($live.StatusCode) $($live.ErrorMessage) -- stopping the sweep now. $completedCells of $($plan.Count) cells completed." -ForegroundColor Red
            $deadAtCell = $cell.Label
            $deadStatus = $live.StatusCode
            break
        }

        $targetTokens = if ($cell.Pct -gt 0) { [Math]::Round($windowTokens * $cell.Pct / 100.0) } else { 0 }
        $built = Build-OccupancyCell $targetTokens $cell.Chars $cell.Reverse
        $wireTools = @(ConvertTo-WireTools $built.Tools)
        $pctOfWindow = [Math]::Round(100.0 * $built.Tokens / $windowTokens, 1)
        Write-Info "cell $($cell.Label): rendered block = $($built.Chars) chars / $($built.Tokens) tokens ($pctOfWindow% of the $windowTokens-token window)"

        $rows = [System.Collections.Generic.List[object]]::new()
        $jsonRuns = [System.Collections.Generic.List[object]]::new()
        for ($i = 0; $i -lt $Runs; $i++) {
            $body = @{ model = $Model; messages = @(@{ role = 'user'; content = $question }); tools = $wireTools; temperature = 0 }
            $r = Invoke-ChatOnce $body
            $ctx = "occupancy=$($cell.Label) run=$($i + 1)"
            Test-BridgeDefect $ctx $r
            $facts = Get-ChatFacts $r

            $correctTool = $facts.Called -and $facts.ToolNames.Count -eq 1 -and $facts.ToolNames[0] -eq 'get_weather'
            $argsValid = $false
            $argsCorrect = $false
            if ($facts.Called -and $facts.Calls.Count -gt 0) {
                try {
                    $parsedArgs = $facts.Calls[0].function.arguments | ConvertFrom-Json -Depth 10
                    $argsValid = $true
                    $argsCorrect = $correctTool -and $parsedArgs.location -and ($parsedArgs.location -match '(?i)paris')
                }
                catch { $argsValid = $false }
            }

            if ($facts.StatusCode -eq 400) { $observedHttp400 = $true }
            if ($facts.StatusCode -eq 502) { $observedHttp502 = $true }
            if ($facts.StatusCode -eq 400 -and $null -eq $cliff400) { $cliff400 = $built }
            if ($facts.StatusCode -eq 502 -and $null -eq $cliff502) { $cliff502 = $built }
            if ($null -eq $degradeAt -and $facts.StatusCode -eq 200 -and (-not $correctTool -or -not $argsValid -or -not $argsCorrect)) {
                $degradeAt = $pctOfWindow
            }

            $row = [pscustomobject]@{
                Dimension = 'WindowOccupancy'; Cell = $cell.Label; Run = $i + 1
                RenderedChars = $built.Chars; RenderedTokens = $built.Tokens; PctOfWindow = $pctOfWindow
                StatusCode = $facts.StatusCode; ErrorCode = $facts.ErrorCode; FinishReason = $facts.FinishReason
                LatencyMs = $r.LatencyMs; Called = $facts.Called; ToolNames = $facts.ToolNames -join ','
                CorrectTool = $correctTool; ArgsValid = $argsValid; ArgsCorrect = $argsCorrect
            }
            $rows.Add($row)
            $script:calls.Add($row)
            $jsonRuns.Add([pscustomobject]@{ Facts = $facts; Result = $r })
            Write-Info "occupancy=$($cell.Label) run=$($i+1): HTTP $($facts.StatusCode)$(if ($facts.ErrorCode) { " code=$($facts.ErrorCode)" }) finish=$($facts.FinishReason) called=$($facts.Called) tool=$($facts.ToolNames -join ',') argsValid=$argsValid argsCorrect=$argsCorrect ($($r.LatencyMs)ms)"
        }

        $statuses = ($rows | Group-Object StatusCode | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ' '
        $calledN = @($rows | Where-Object Called).Count
        $correctN = @($rows | Where-Object CorrectTool).Count
        $validN = @($rows | Where-Object ArgsValid).Count
        $accN = @($rows | Where-Object ArgsCorrect).Count
        $lat = @($rows | ForEach-Object LatencyMs)
        $latMin = [Math]::Round(($lat | Measure-Object -Minimum).Minimum, 0)
        $latMax = [Math]::Round(($lat | Measure-Object -Maximum).Maximum, 0)
        $latAvg = [Math]::Round(($lat | Measure-Object -Average).Average, 0)
        $summary = "occupancy=$($cell.Label) ($($built.Chars) chars / $($built.Tokens) tokens, $pctOfWindow% of window): statuses [$statuses], called $calledN/$Runs, correct-tool $correctN/$Runs, args-valid $validN/$Runs, args-correct $accN/$Runs, latency min/avg/max = $latMin/$latAvg/$($latMax)ms"
        Write-Host "    CELL $summary" -ForegroundColor Green
        $script:cells.Add([pscustomobject]@{ Dimension = 'WindowOccupancy'; Cell = $cell.Label; Runs = $Runs; Summary = $summary })
        if ($Stream -and $cell.Label -eq '70%') {
            $streamBody = @{ model = $Model; messages = @(@{ role = 'user'; content = $question }); tools = $wireTools; temperature = 0 }
            Invoke-StreamParityRuns 'WindowOccupancy' '70%' $jsonRuns.ToArray() $streamBody $Runs
        }
        $completedCells++
    }

    if ($deadAtCell) {
        Write-Host "    SWEEP STOPPED EARLY: backend died before cell '$deadAtCell'. $completedCells of $($plan.Count) cells completed before the failure." -ForegroundColor Red
    }
    if ($deadAtCell -and $deadStatus -eq 502) {
        Write-Host "    sweep stopped on a 502 at cell '$deadAtCell'" -ForegroundColor Red
    }
    if ($degradeAt) { Write-Host "    compliance first degraded at ~$degradeAt% of the window (still HTTP 200)" -ForegroundColor Magenta }
    elseif (-not ($deadAtCell -and $deadStatus -eq 502)) { Write-Host '    no compliance degradation observed on any HTTP 200 cell (only the hard 400/502 failures, if any)' -ForegroundColor Magenta }
    if ($cliff400) { Write-Host "    200->400 flip: first seen at $($cliff400.Chars) chars / $($cliff400.Tokens) tokens" -ForegroundColor Magenta }
    elseif (-not $observedHttp400) { Write-Host '    no HTTP 400 observed in this sweep' -ForegroundColor Magenta }
    else { Write-Host '    HTTP 400 observed in this sweep' -ForegroundColor Magenta }
    if ($cliff502) { Write-Host "    400->502 flip: first seen at $($cliff502.Chars) chars / $($cliff502.Tokens) tokens" -ForegroundColor Magenta }
    elseif (-not $observedHttp502) { Write-Host '    no HTTP 502 observed in this sweep' -ForegroundColor Magenta }
    elseif (-not ($deadAtCell -and $deadStatus -eq 502)) { Write-Host '    HTTP 502 observed in this sweep' -ForegroundColor Magenta }

    [pscustomobject]@{ DegradeAtPct = $degradeAt; Cliff400 = $cliff400; Cliff502 = $cliff502; DeadAtCell = $deadAtCell; CompletedCells = $completedCells; PlannedCells = $plan.Count }
}

# --- main -------------------------------------------------------------------------------------------

if ($SelfTest) {
    try {
        Invoke-ToolProbeSelfTest
        exit 0
    }
    catch {
        Write-Host "SELFTEST FAIL: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

Write-Host "npu-bridge hard-case tool-call probe against $base (model=$Model)" -ForegroundColor Cyan
$health = Invoke-WebRequest -Uri "$base/healthz" -TimeoutSec 10 -SkipHttpErrorCheck
if ([int] $health.StatusCode -ne 200) { throw "GET /healthz returned HTTP $($health.StatusCode): $($health.Content)" }
$h = $health.Content | ConvertFrom-Json
if ($h.status -ne 'ready') { throw "bridge is not ready: status=$($h.status)" }
if ($h.model -ne $Model) { throw "bridge serves model '$($h.model)', not '$Model' -- pass -Model to match, or check what is actually running" }
Write-Host "healthz: backend=$($h.backend) model=$($h.model) contexts_cached=$($h.contexts_cached)/$($h.context_cache_capacity) queue_depth=$($h.queue_depth)" -ForegroundColor Cyan

$cliffCount = $null
$occupancy = $null
if ($dimensions -contains 'ToolCountSweep') { $cliffCount = Invoke-ToolCountSweep }
if ($dimensions -contains 'SchemaDepth') { Invoke-SchemaDepthProbe }
if ($dimensions -contains 'SystemPromptPressure') { Invoke-SystemPromptPressureProbe }
if ($dimensions -contains 'MultiStep') { Invoke-MultiStepProbe }
if ($dimensions -contains 'WindowOccupancy') { $occupancy = Invoke-WindowOccupancyProbe -CellLabels $OccupancyCells }

Write-Host ''
Write-Host '=== per-cell summary ===' -ForegroundColor Cyan
$script:cells | Format-Table Dimension, Cell, Runs, Summary -AutoSize -Wrap | Out-String -Width 220 | Write-Host

Write-Host '=== defects (bridge misbehavior, not model choices) ===' -ForegroundColor Cyan
if ($script:defects.Count -eq 0) {
    Write-Host 'none' -ForegroundColor Green
}
else {
    $script:defects | Format-Table Context, Description -AutoSize -Wrap | Out-String -Width 220 | Write-Host
}

if ($cliffCount) { Write-Host "502 cliff: begins at tool count = $cliffCount" -ForegroundColor Magenta }

$out = [pscustomobject]@{
    RunAtUtc     = (Get-Date).ToUniversalTime().ToString('o')
    BaseUrl      = $base
    Model        = $Model
    Runs         = $Runs
    HeadlineRuns = $HeadlineRuns
    Dimensions   = $dimensions
    Stream       = [bool] $Stream
    CliffToolCount = $cliffCount
    Cells        = $script:cells
    Calls        = $script:calls
    StreamedCalls = $script:streamedCalls
    ParityResults = $script:parityResults
    CacheBrackets = $script:cacheBrackets
    Occupancy = $occupancy
    DegradeAtPct = if ($null -ne $occupancy) { $occupancy.DegradeAtPct } else { $null }
    Defects      = $script:defects
}
$out | ConvertTo-Json -Depth 12 | Set-Content -Path $JsonOut -Encoding utf8
Write-Host ''
Write-Host "machine-readable summary written to $JsonOut" -ForegroundColor Cyan
Write-Host "$($script:defects.Count) defect(s) found across $($script:calls.Count) calls" -ForegroundColor $(if ($script:defects.Count -gt 0) { 'Red' } else { 'Green' })
