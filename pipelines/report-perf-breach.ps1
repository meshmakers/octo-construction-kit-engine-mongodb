<#
.SYNOPSIS
    Files (or updates) the Bug for a performance budget breach of the nightly perf run (AB#8004).

.DESCRIPTION
    Runs as the last step of azure-pipelines-performance.yml when the Performance tests failed.
    Looks for an open Bug tagged 'perf-budget-breach' + 'engine-mongodb' in the Core Platform area:
      - found     -> adds a comment with the run link (one Bug per breach period, no daily duplicates);
      - not found -> creates a Bug under the parent Feature (default: Core Platform Sustain Feature 5953).
    The Bug is the notification channel to the Core maintainers; the red run stays the primary signal.
    Any REST failure (for example: the project Build Service may not create work items in that area)
    is logged as a warning and the script exits 0 - it must never mask or replace the red test step.

    Uses only predefined pipeline variables plus SYSTEM_ACCESSTOKEN and PERF_BUG_PARENT_ID from the
    step's env block. Prints no token and no header.
#>
[CmdletBinding()]
param(
    [string]$CollectionUri = $env:SYSTEM_COLLECTIONURI,
    [string]$Project = $env:SYSTEM_TEAMPROJECT,
    [int]$ParentId = $(if ($env:PERF_BUG_PARENT_ID) { [int]$env:PERF_BUG_PARENT_ID } else { 5953 }),
    [string]$AreaPath = 'OctoMesh\Product Team\Core Platform',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$tagBreach = 'perf-budget-breach'
$tagRepo = 'engine-mongodb'
$titlePrefix = 'Performance budget breach in octo-construction-kit-engine-mongodb nightly perf run'

function Write-Warn([string]$message) { Write-Host "##vso[task.logissue type=warning]$message" }

try {
    if (-not $env:SYSTEM_ACCESSTOKEN -and -not $DryRun) {
        Write-Warn 'SYSTEM_ACCESSTOKEN is not mapped into the step; no Bug filed. The red run is the signal.'
        exit 0
    }

    $base = $CollectionUri.TrimEnd('/') + '/' + [Uri]::EscapeDataString($Project)
    $runUrl = "$base/_build/results?buildId=$($env:BUILD_BUILDID)&view=ms.vss-test-web.build-test-results-tab"
    $branch = $env:BUILD_SOURCEBRANCH
    $commit = $env:BUILD_SOURCEVERSION
    $reason = $env:BUILD_REASON
    $headers = @{ Authorization = "Bearer $($env:SYSTEM_ACCESSTOKEN)" }

    $details = "Run <a href=""$runUrl"">$($env:BUILD_BUILDNUMBER)</a> (build $($env:BUILD_BUILDID), reason $reason) on <code>$branch</code> " +
               "at <code>$commit</code> failed the Category=Performance or Category=Stress tests. Open the run's Tests tab for the failed test and its " +
               'message, and the log of the step "Performance tests" for every measured median / p95 (octo-progress lines). ' +
               'Judge load first: did a release train or many agent builds run at the same time? Budget: decision AB#6930; ' +
               'pipeline and triage: AB#8004, tests/Runtime.Engine.MongoDb.IntegrationTests/README.md.'

    $wiql = @{
        query = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] = 'Bug' " +
                "AND [System.State] NOT IN ('Closed', 'Removed', 'Resolved') AND [System.Tags] CONTAINS '$tagBreach' " +
                "AND [System.Tags] CONTAINS '$tagRepo' ORDER BY [System.Id] ASC"
    } | ConvertTo-Json

    if ($DryRun) {
        Write-Host "DryRun: would query open Bugs with tags $tagBreach + $tagRepo, then comment or create under Feature $ParentId."
        Write-Host "DryRun: details: $details"
        exit 0
    }

    $found = Invoke-RestMethod -Method Post -Uri "$base/_apis/wit/wiql?api-version=7.1" -Headers $headers `
        -ContentType 'application/json' -Body $wiql
    $existing = @($found.workItems) | Select-Object -First 1

    if ($existing) {
        $comment = @{ text = "[perf-pipeline] Budget breached again. $details" } | ConvertTo-Json
        Invoke-RestMethod -Method Post -Uri "$base/_apis/wit/workItems/$($existing.id)/comments?api-version=7.1-preview.4" `
            -Headers $headers -ContentType 'application/json' -Body $comment | Out-Null
        Write-Host "Commented on the open breach Bug $($existing.id)."
        Write-Host "##vso[build.addbuildtag]perf-budget-breach"
        exit 0
    }

    $parentUrl = "$($CollectionUri.TrimEnd('/'))/_apis/wit/workItems/$ParentId"
    $patch = @(
        @{ op = 'add'; path = '/fields/System.Title'; value = "${titlePrefix}" }
        @{ op = 'add'; path = '/fields/System.AreaPath'; value = $AreaPath }
        @{ op = 'add'; path = '/fields/System.Tags'; value = "$tagBreach; $tagRepo" }
        @{ op = 'add'; path = '/fields/Microsoft.VSTS.TCM.ReproSteps'; value = "<p>[perf-pipeline] $details</p>" }
        @{ op = 'add'; path = '/relations/-'; value = @{ rel = 'System.LinkTypes.Hierarchy-Reverse'; url = $parentUrl } }
    ) | ConvertTo-Json -Depth 5

    $bug = Invoke-RestMethod -Method Post -Uri "$base/_apis/wit/workitems/`$Bug?api-version=7.1" -Headers $headers `
        -ContentType 'application/json-patch+json' -Body $patch
    Write-Host "Filed breach Bug $($bug.id) under Feature $ParentId."
    Write-Host "##vso[build.addbuildtag]perf-budget-breach"
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    Write-Warn "Could not file or update the breach Bug (HTTP $status): $($_.Exception.Message). The red run is the signal; check that the project Build Service may create and edit work items in '$AreaPath'."
}
exit 0
