#Requires -Version 7.0
param(
    [Parameter(Mandatory)]
    [ValidateSet('All','AC-132','AC-133','AC-134','AC-135','AC-150','AC-151','AC-152','AC-153','AC-154','AC-155','AC-156','AC-157','AC-158','AC-159','AC-160','AC-161')]
    [string]$Ac,
    # Explicitly NOT a full System Test result. No app/UIA process is started.
    [switch]$SupportingChecksOnly,
    [switch]$Preflight
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$web = Join-Path $repo 'src\HubRadio.Web'
$webTests = @{}
$native = @{}
$ui = @{}
foreach ($id in 'AC-132','AC-133','AC-134','AC-135') {
    # FR-131: actual React components in jsdom, without a desktop window.
    $webTests[$id] = @('jobs/JobThumbnail', 'jobs/AllProjectJobsPanel', 'jobs/ProjectJobList', 'jobs/JobProgressPanel', 'results/JobResultsBrowser')
    $ui[$id] = if ($id -eq 'AC-132') { @('fr-131-job-thumbnails.ps1') } else { @('fr-131-thumbnails-integration.ps1', '-Ac', $id) }
}
# FR-136 AC-150..152 / FR-137 AC-153..155: preserve the production
# integration runner and existing independent assertions; do not copy them.
$webTests['AC-150'] = @('jobs/StyleTemplatePicker')
$webTests['AC-152'] = @('jobs/StyleTemplatePicker')
$webTests['AC-155'] = @('jobs/CharacterPortraitRegistration', 'jobs/JobCreationActionPortraits', 'jobs/StyleTemplatePicker')
$webTests['AC-157'] = @('jobs/TalkShowInputPanel')
$native['AC-150'] = 'Name=CatalogListsTheSevenDefinitionsWithinTheColorAndFontLimits|Name=ChoosingATemplateAloneAppliesItsPresentation|Name=ASelectedTemplateColorsTheBurnedInSubtitle|Name=TheRegenerateBridgeRequestAcceptsADefinedTemplateAndNoneAndRejectsAnUnknownOne'
$native['AC-151'] = 'Name=WithoutATemplateTheDefaultOutputIsUnchanged|Name=WithoutATemplateTheBurnedInOutputIsTheDefaultOne'
$native['AC-152'] = 'Name=TheJobRecordsTheTemplateAndReproducesItAndMarksAdjustments|Name=TheJobRecordKeepsTheTemplateAndShowsTheAdjustedLabel'
$native['AC-153'] = 'Name=CatalogListsTheSevenDefinitionsWithinTheColorAndFontLimits|Name=EveryTemplateKeepsTextVerbatimAndInsideTheSubtitleLimits|Name=FreeTelopTextOnlyComesFromSubtitlesUserInputOrConfirmedText'
$native['AC-154'] = 'Name=ShortPopShowsFewWordsAndColorsOnlyTheSpokenWord|Name=VarietyFollowsTheSoundEffectSettingAndFinishesMotionQuickly|Name=VerticalShortPopBurnsShrinkingWordCaptionsInsideTheMargins'
$native['AC-155'] = 'FullyQualifiedName~TemplateFontsAndCharacterDialogueTests.AC155'
# FR-138 AC-156..158: includes positive and negative placement/input/hook cases.
$native['AC-156'] = 'Name=VerticalShortPopBurnsShrinkingWordCaptionsInsideTheMargins|Name=VerticalGraphicsAndTelopsStayInsideTheMarginsForEveryVerticalTemplate|Name=VerticalRecordPassesInsideTheMarginsAndTheRegionWithItsEvidence|Name=VerticalRecordFailsWhenTheSubtitleIsInsideTheMargin|Name=VerticalSubtitleBoxIsInsideTheMarginsUnlessMovedIntoThem'
$native['AC-157'] = 'FullyQualifiedName~TalkShowRenderingTests|Name=ATopicSideTelopLastsUntilTheTopicChangesAndTheOldOneIsGone|Name=TheNameBandShowsOnlyEnteredOrConfirmedNamesAndNeverGuessesOrAnonymousLabels'
$native['AC-158'] = 'FullyQualifiedName~VerticalThreeBandTests|Name=TheVerticalThreeTierOutputIs1080By1920WithTheFifteenToTwentyCharacterSubtitle|Name=TheVerticalFrameHasTitleOnTopVideoInTheMiddleAndTheSubtitleBelowInsideTheMargins'
# FR-139 AC-159..161: a guide/catalogue scan is supporting evidence, NOT
# a replacement for the semantic manual review required by AC-161.
$native['AC-159'] = 'Name=EveryTemplateTextReadsOnItsBackgroundAtFourPointFiveToOne|Name=EmphasisIsBoldAndLargerThanTheSubtitleAndMovesWithTheTemplate|Name=AutomaticSubtitlesKeepNegationsAndConditionsAndTheTextIsNotRewritten'
$native['AC-160'] = 'FullyQualifiedName~TemplateFontsAndCharacterDialogueTests.AC160|Name=SoundEffectsAreOnlyTheTwoSynthesizedOnesAndOnlyWhenTheSettingIsOn|Name=AFontIsRecordedWithItsVersionAndLicenseAndOneThatMayNotBeBurnedInIsRefused'
$native['AC-161'] = 'Name=TemplateTextsMakeNoEffectGuaranteeAndNoImitationClaim'
$webTests['AC-161'] = @('jobs/StyleTemplatePicker')
foreach ($id in 'AC-150','AC-151','AC-152') { $ui[$id] = @('fr-136-style-template.ps1', '-Ac', $id) }
foreach ($id in 'AC-153','AC-154') { $ui[$id] = @('fr-137-template-kinds.ps1', '-Ac', $id) }
foreach ($id in 'AC-155','AC-160') { $ui[$id] = @('fr-137-139-character-fonts.ps1', '-Ac', $id) }
foreach ($id in 'AC-156','AC-157','AC-158') { $ui[$id] = @('fr-138-layouts.ps1', '-Ac', $id) }
foreach ($id in 'AC-159','AC-161') { $ui[$id] = @('fr-139-quality.ps1', '-Ac', $id) }
$ids = if ($Ac -eq 'All') { @($ui.Keys | Sort-Object) } else { @($Ac) }

Push-Location $repo
try {
    $files = @($ids | ForEach-Object { if ($webTests.ContainsKey($_)) { $webTests[$_] } } | Sort-Object -Unique |
        ForEach-Object { "src/features/$_.test.tsx" })
    if ($files.Count -gt 0) {
        Push-Location $web
        try {
            # Local pinned runner: no npx download and no desktop/browser session.
            $args = @('run') + $files + @('--maxWorkers', '1')
            if ($Preflight) { $args = @('list') + $files }
            & node node_modules/vitest/vitest.mjs @args
            if ($LASTEXITCODE -ne 0) { throw "web integration runner failed: $LASTEXITCODE" }
        } finally { Pop-Location }
    }
    $clauses = @($ids | ForEach-Object { if ($native.ContainsKey($_)) { $native[$_] -split '\|' } } | Sort-Object -Unique)
    if ($clauses.Count -gt 0) {
        $args = @('test', '--project', 'tests/HubRadio.App.Tests/HubRadio.App.Tests.csproj', '-c', 'Release',
            '--filter', ($clauses -join '|'))
        if ($Preflight) { $args += '--list-tests' }
        & dotnet @args
        if ($LASTEXITCODE -ne 0) { throw "native integration runner failed: $LASTEXITCODE" }
        if ($Preflight) {
            # dotnet test's MTP discovery summary alone can report success with
            # zero selected tests. Inspect the built runner's actual method list
            # for EACH AC, and every exact Name clause, before accepting it.
            $runner = Join-Path $repo 'tests\HubRadio.App.Tests\bin\Release\net10.0-windows10.0.26100.0\win-x64\HubRadio.App.Tests.exe'
            foreach ($id in $ids | Where-Object { $native.ContainsKey($_) }) {
                $listing = @(& $runner --list-tests --filter $native[$id] --no-ansi)
                if ($LASTEXITCODE -ne 0) { throw "$id native test discovery failed" }
                $methods = @($listing | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ -match '^[A-Za-z][A-Za-z0-9_]+$' })
                if ($methods.Count -eq 0) { throw "$id native filter selected zero tests" }
                foreach ($clause in $native[$id] -split '\|') {
                    if ($clause.StartsWith('Name=')) {
                        $method = $clause.Substring(5)
                        if ($method -notin $methods) { throw "$id has an undiscovered native test: $method" }
                    }
                }
                Write-Host "DISCOVERED $id native methods=$($methods.Count)"
            }
        }
    }
    if ($Preflight) {
        foreach ($id in $ids) {
            $file = Join-Path $PSScriptRoot "..\e2e\$(@($ui[$id])[0])"
            $tokens = $null; $errors = $null
            $null = [Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors)
            if ($errors.Count -gt 0) { throw ($errors | Out-String) }
        }
        Write-Host "PREFLIGHT: $($ids -join ',') (discovery/parser only; no AC verdict)"
        exit 0
    }
    if ($SupportingChecksOnly) {
        Write-Host "SUPPORTING CHECKS ONLY: $($ids -join ','). Desktop integration/manual obligations remain; NOT a ledger/AC PASS."
        exit 0
    }
    foreach ($id in $ids) {
        $entry = @($ui[$id])
        $script = Join-Path $PSScriptRoot "..\e2e\$($entry[0])"
        $arguments = if ($entry.Count -gt 1) { $entry[1..($entry.Count - 1)] } else { @() }
        & pwsh -NoProfile -File $script @arguments
        if ($LASTEXITCODE -ne 0) { throw "$id desktop/description check failed: $LASTEXITCODE" }
        if ($id -eq 'AC-161') {
            throw 'AC-161 is manual: regex/catalogue checks cannot certify all semantic descriptions; independent manual evidence required'
        }
    }
} finally { Pop-Location }
