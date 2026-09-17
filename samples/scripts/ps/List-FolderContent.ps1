#| NetRatel-MANIFEST
# yaml
name: List-FolderContent
version: 1
params:
  - name: Path
    type: string
    required: true
    description: Folder to list
  - name: Recurse
    type: bool
    required: false
    default: "false"
#| END

param([Parameter(Mandatory=$true)][string]$Path,[switch]$Recurse)
Write-Host "Listing $Path"
