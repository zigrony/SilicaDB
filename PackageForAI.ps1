<#
.SYNOPSIS
  Package up one or more SilicaDB projects into text dumps, split files, and an optional Markdown map.

.DESCRIPTION
  This script exports source files from SilicaDB projects into plain text dumps for analysis or AI ingestion.
  Each project produces:
    - A single combined dump (<Project>_AllContent.txt)
    - A series of split files (<Project>_0.txt, <Project>_1.txt, …)

  Additionally, all projects can be combined into:
    - AllProjects_AllCombined.txt
    - AllProjects_<0-n>.txt (split by size)

  If -GenerateMap is specified, a Markdown map is created showing:
    - Folder & file tree
    - Types & signatures
    - Project references

.PARAMETER srcPath
  Root folder for your SilicaDB repo (default: C:\GitHubRepos\SilicaDB\src\).

.PARAMETER dstPath
  Where to emit the dumps & map (default: C:\temp\SilicaDBExport\).

.PARAMETER Projects
  Which project(s) to export. Use "All" (case-insensitive) to pick up every folder under srcPath.

.PARAMETER fileSize
  Maximum bytes per split file (default 80 000).

.PARAMETER IncludeTests
  If present, includes any folder or file matching *Tests*. Otherwise those are filtered out.

.PARAMETER IncludeWeb
  If present, includes web‑related files (*.js, *.css, *.html, *.htm, *.md).

.PARAMETER GenerateMap
  If present, emits a Markdown map (.md) showing file structure, types, and project references.

.PARAMETER MapOutput
  Path (including filename) for the generated Markdown map. Defaults to "$dstPath\ProjectMap.md".

.EXAMPLE
  .\PackageForAI.ps1 -Projects All -IncludeTests -IncludeWeb -GenerateMap
#>

param(
  [ValidateNotNullOrEmpty()][string]$srcPath      = "C:\GitHubRepos\SilicaDB\src\",
  [ValidateNotNullOrEmpty()][string]$dstPath      = "C:\temp\SilicaDBExport\",
  [string[]]                        $Projects     = @("All"),
  [int]                             $fileSize     = 80000,
  [switch]                          $IncludeTests,
  [switch]                          $IncludeWeb,
  [switch]                          $GenerateMap,
  [string]                          $MapOutput    = ""
)

# Ensure output folder exists
if (-not (Test-Path $dstPath)) {
  New-Item -Path $dstPath -ItemType Directory | Out-Null
}

# Default Markdown path
if ($GenerateMap -and [string]::IsNullOrEmpty($MapOutput)) {
  $MapOutput = Join-Path $dstPath "ProjectMap.md"
}

# Master list of all known projects
$AllProjectsList = @(
  "Silica.Authentication", "Silica.BufferPool", "Silica.Certificates",
  "Silica.Common","Silica.Concurrency", "Silica.DiagnosticsCore",
  "Silica.Durability","Silica.Evictions", "Silica.Exceptions",
  "Silica.FrontEnds", "Silica.PageAccess","Silica.Sessions",
  "Silica.Sql.Lexer","Silica.Storage", "Silica.UI", 
  "Silica.Storage.Compression", "Silica.StorageAllocation", 
  "Silica.StorageAllocation.Sql", "Silica.Storage.SqlMinidriver","test.app"
)

# Resolve which projects to export (case-insensitive handling of "All")
$useAll = (($Projects | ForEach-Object { $_.ToLower() }) -contains "all")
$ProjectList = if ($useAll) { $AllProjectsList } else { $Projects }

#---------------------------------------
# Utility Functions
#---------------------------------------

function Get-IncludedExtensions {
  param([bool]$IncludeWeb)

  $extensions = @("*.cs","*.csproj","*.sln","*.config","*.resx",
                  "*.xaml","*.json","*.xml","*.user","*.props","*.targets")
  if ($IncludeWeb) {
    $extensions += @("*.js","*.css","*.html","*.htm","*.md")
  }
  return $extensions
}

function Split-LargeFile {
  param(
    [string] $FilePath,
    [int]    $MaxSize,
    [string] $Prefix
  )
  if (-not (Test-Path $FilePath)) { return }

  $dir    = Split-Path $FilePath -Parent
  $index  = 0
  $size   = 0
  $out    = Join-Path $dir ("{0}_{1}.txt" -f $Prefix, $index)
  $writer = [System.IO.StreamWriter]::new($out, $false, [System.Text.Encoding]::UTF8)

  foreach ($line in Get-Content $FilePath) {
    $bytes = [System.Text.Encoding]::UTF8.GetByteCount($line + "`r`n")
    if (($size + $bytes) -gt $MaxSize) {
      $writer.Close()
      $index++
      $size = 0
      $out    = Join-Path $dir ("{0}_{1}.txt" -f $Prefix, $index)
      $writer = [System.IO.StreamWriter]::new($out, $false, [System.Text.Encoding]::UTF8)
    }
    $writer.WriteLine($line)
    $size += $bytes
  }

  $writer.Close()
  Write-Host "  Split into files with prefix '$Prefix'"
}

function Export-Project {
  param(
    [string]   $Project,
    [string]   $SrcPath,
    [string]   $DstPath,
    [int]      $FileSize,
    [switch]   $IncludeTests,
    [switch]   $IncludeWeb
  )

  Write-Host "Exporting project: $Project"
  $root = Join-Path $SrcPath $Project
  if (-not (Test-Path $root)) {
    Write-Warning "  Skipped (not found): $root"
    return
  }

  $outFile = Join-Path $DstPath "$Project`_AllContent.txt"
  $sw      = [System.IO.StreamWriter]::new($outFile, $false, [System.Text.Encoding]::UTF8)

  $extensions = Get-IncludedExtensions -IncludeWeb:$IncludeWeb
  $files = Get-ChildItem -Path $root -Recurse -File -Include $extensions

  if (-not $IncludeTests) {
    $files = $files | Where-Object { $_.FullName -notmatch "\\Tests\\" }
  }

  $files = $files | Sort-Object FullName

  foreach ($f in $files) {
    $rel = $f.FullName.Substring($root.Length + 1)
    $sw.WriteLine("// File: $rel")
    $sw.WriteLine()
    Get-Content $f.FullName | ForEach-Object { $sw.WriteLine($_) }
    $sw.WriteLine()
  }

  $sw.Close()
  Split-LargeFile -FilePath $outFile -MaxSize $FileSize -Prefix $Project
}

function Export-AllProjects {
  param(
    [string[]] $Projects,
    [string]   $SrcPath,
    [string]   $DstPath,
    [int]      $FileSize,
    [switch]   $IncludeTests,
    [switch]   $IncludeWeb
  )

  Write-Host "Exporting combined AllProjects"

  $combinedFile = Join-Path $DstPath "AllProjects_AllCombined.txt"
  $swAll        = [System.IO.StreamWriter]::new($combinedFile, $false, [System.Text.Encoding]::UTF8)

  foreach ($proj in $Projects) {
    $root = Join-Path $SrcPath $proj
    if (-not (Test-Path $root)) {
      Write-Warning "  Skipped (not found): $root"
      continue
    }

    $extensions = Get-IncludedExtensions -IncludeWeb:$IncludeWeb
    $files = Get-ChildItem -Path $root -Recurse -File -Include $extensions

    if (-not $IncludeTests) {
      $files = $files | Where-Object { $_.FullName -notmatch "\\Tests\\" }
    }

    $files = $files | Sort-Object FullName

    foreach ($f in $files) {
      $rel = $f.FullName.Substring($root.Length + 1)
      $swAll.WriteLine("// Project: $proj | File: $rel")
      $swAll.WriteLine()
      Get-Content $f.FullName | ForEach-Object { $swAll.WriteLine($_) }
      $swAll.WriteLine()
    }
  }

  $swAll.Close()
  Split-LargeFile -FilePath $combinedFile -MaxSize $FileSize -Prefix "AllProjects"
}
function Generate-ProjectMap {
  param(
    [string]    $BasePath,
    [string[]]  $Projects,
    [bool]      $IncludeTests,
    [bool]      $IncludeWeb,
    [string]    $OutputFile
  )

  $mapLines = @("# SilicaDB Project Map", "")

  foreach ($proj in $Projects) {
    $root = Join-Path $BasePath $proj
    if (-not (Test-Path $root)) { continue }

    $mapLines += "## Project: $proj"
    $mapLines += ""
    $mapLines += "### Folder & File Tree"
    $mapLines += ""

    $extensions = Get-IncludedExtensions -IncludeWeb:$IncludeWeb
    $all = Get-ChildItem -Path $root -Recurse -File -Include $extensions

    if (-not $IncludeTests) {
      $all = $all | Where-Object { $_.FullName -notmatch "\\Tests\\" }
    }

    $relPaths = $all |
      ForEach-Object { $_.FullName.Substring($root.Length + 1).Replace('\','/') } |
      Sort-Object

    foreach ($p in $relPaths) {
      $mapLines += "- $p"
    }

    $mapLines += ""
    $mapLines += "### Types & Signatures"
    $mapLines += ""

    foreach ($file in $all | Where-Object { $_.Extension -eq ".cs" }) {
      $rel = $file.FullName.Substring($root.Length + 1).Replace('\','/')
      $mapLines += "**$rel**"
      Get-Content $file.FullName |
        Where-Object {
          $_ -match '^\s*namespace\s+' -or
          $_ -match '\s*(class|interface|enum|struct)\s+\w+' -or
          $_ -match '\s*(public|protected|internal)\s+\w+.*\('
        } |
        ForEach-Object { $mapLines += "- " + $_.Trim() }
      $mapLines += ""
    }

    $mapLines += ""
    $mapLines += "### Project References"
    $mapLines += ""

    $csprojFile = Join-Path $root ("$proj.csproj")
    if (Test-Path $csprojFile) {
      [xml]$xmlDoc = Get-Content $csprojFile
      $refs = $xmlDoc.Project.ItemGroup.ProjectReference |
              ForEach-Object { $_.Include.Split('\')[-1] }
      foreach ($r in $refs) {
        $mapLines += "- $r"
      }
    }
    else {
      $mapLines += "_No .csproj found_"
    }

    $mapLines += ""
    $mapLines += "---"
    $mapLines += ""
  }

  $outDir = Split-Path $OutputFile -Parent
  if (-not (Test-Path $outDir)) {
    New-Item -Path $outDir -ItemType Directory | Out-Null
  }

  $mapLines | Set-Content -Path $OutputFile -Encoding UTF8
  Write-Host "Markdown map generated at $OutputFile"
}

#---------------------------------------
# Main Orchestration
#---------------------------------------

# 1) Per‑Project Export
foreach ($proj in $ProjectList) {
  Export-Project -Project $proj -SrcPath $srcPath -DstPath $dstPath `
                 -FileSize $fileSize -IncludeTests:$IncludeTests -IncludeWeb:$IncludeWeb
}

# 2) Combined Export
Export-AllProjects -Projects $ProjectList -SrcPath $srcPath -DstPath $dstPath `
                   -FileSize $fileSize -IncludeTests:$IncludeTests -IncludeWeb:$IncludeWeb

# 3) Markdown Map (optional)
if ($GenerateMap) {
  Generate-ProjectMap -BasePath $srcPath -Projects $ProjectList `
                      -IncludeTests:$IncludeTests -IncludeWeb:$IncludeWeb `
                      -OutputFile $MapOutput
}
