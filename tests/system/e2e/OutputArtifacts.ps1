#Requires -Version 7.0
# Location adapter only, not a test oracle. Published immutable render evidence
# is stored as Manifest/<artifact-id>.json; filenames need not start "manifest".
function Find-TestRenderManifests {
    param([Parameter(Mandatory)][string]$Workspace)
    Get-ChildItem -LiteralPath $Workspace -Recurse -File -Filter '*.json' -ErrorAction Stop |
        Where-Object {
            $_.Name -like 'manifest*.json' -or
            (Split-Path -Leaf $_.DirectoryName) -eq 'Manifest'
        } |
        Sort-Object FullName
}
