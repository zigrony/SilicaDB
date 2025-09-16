param(
    [string]$srcPath = "C:\GitHubRepos\SilicaDB\src\",
    [string]$dstPath = "C:\temp\",
    [ValidateSet(
        "All",
        "Silica.BufferPool",
        "Silica.Common",
        "Silica.Concurrency",
        "Silica.DiagnosticsCore",
        "Silica.Durability",
        "Silica.Evictions",
        "Silica.Exceptions",
        "Silica.Storage",
        "Silica.PageAccess",
        #"Silica.Storage.Encryption",
        "test.app"
    )]
    [string[]]$Projects = @("All"),
    [int]$fileSize = 80000
)

function Split-LargeFile {
    param (
        [string]$FilePath,
        [int]$MaxSize,
        [string]$prefix = "file_"
    )

    if (-Not (Test-Path -Path $FilePath)) {
        Write-Error "The specified file does not exist: $FilePath"
        return
    }

    $FolderPath = Split-Path -Path $FilePath
    $FileIndex = 0
    $CurrentSize = 0
    $OutputFile = Join-Path $FolderPath ("{0}_{1}.txt" -f $prefix, $FileIndex)
    $Writer = [System.IO.StreamWriter]::new($OutputFile, $false, [System.Text.Encoding]::UTF8)

    try {
        foreach ($Line in Get-Content -Path $FilePath) {
            $LineSize = [System.Text.Encoding]::UTF8.GetByteCount($Line + [Environment]::NewLine)
            if (($CurrentSize + $LineSize) -gt $MaxSize) {
                $Writer.Close()
                $FileIndex++
                $OutputFile = Join-Path $FolderPath ("{0}_{1}.txt" -f $prefix, $FileIndex)
                $Writer = [System.IO.StreamWriter]::new($OutputFile, $false, [System.Text.Encoding]::UTF8)
                $CurrentSize = 0
            }
            $Writer.WriteLine($Line)
            $CurrentSize += $LineSize
        }
    }
    finally {
        $Writer.Close()
    }

    Write-Host "Splitting complete. Files saved in $FolderPath"
}

# Master project list
$AllProjectsList = @(
    "Silica.BufferPool",
    "Silica.Common",
    "Silica.Concurrency",
    "Silica.DiagnosticsCore",
    "Silica.Durability",
    "Silica.Evictions",
    "Silica.Exceptions",
    "Silica.Storage",
    "Silica.PageAccess",
    #"Silica.Storage.Encryption",
    "test.app"
)

# Resolve project list
if ($Projects -contains "All") {
    $ProjectList = $AllProjectsList
} else {
    $ProjectList = $Projects
}

# --- Per-project processing ---
foreach ($project in $ProjectList) {
    Write-Host "Processing project: $project"

    $fullSrc = Join-Path $srcPath $project
    if (-Not (Test-Path $fullSrc)) {
        Write-Warning "Source path does not exist: $fullSrc"
        continue
    }

    $fullDst = Join-Path $dstPath "$($project)_AllContent.txt"
    $writer = [System.IO.StreamWriter]::new($fullDst, $false, [System.Text.Encoding]::UTF8)

    # Skip files containing 'Tests' in the path
    $files = Get-ChildItem -Recurse -File -Path $fullSrc -Filter "*.cs" |
             Where-Object { $_.FullName -notmatch "Tests" } |
             Sort-Object FullName

    foreach ($file in $files) {
        Write-Host "`tFileName[$($file.FullName)]"
        $relPath = $file.FullName.Substring($fullSrc.TrimEnd('\').Length).TrimStart('\')
        $writer.WriteLine("// Filename: $relPath")
        $writer.WriteLine()
        foreach ($line in Get-Content $file.FullName) {
            $writer.WriteLine($line)
        }
        $writer.WriteLine()
    }
    $writer.Close()

    Split-LargeFile -FilePath $fullDst -MaxSize $fileSize -prefix $project
}

# --- Combined AllProjects processing ---
Write-Host "Creating combined AllProjects.txt"

$allDst = Join-Path $dstPath "AllProjects.txt"
$writer = [System.IO.StreamWriter]::new($allDst, $false, [System.Text.Encoding]::UTF8)

foreach ($project in $ProjectList) {
    $fullSrc = Join-Path $srcPath $project
    if (-Not (Test-Path $fullSrc)) {
        Write-Warning "Source path does not exist: $fullSrc"
        continue
    }

    $writer.WriteLine("//")
    $writer.WriteLine("// Start Project: $project")
    $writer.WriteLine("//")
    $writer.WriteLine()

    # Skip files containing 'Tests' in the path
    $files = Get-ChildItem -Recurse -File -Path $fullSrc -Filter "*.cs" |
             Where-Object { $_.FullName -notmatch "Tests" } |
             Sort-Object FullName

    foreach ($file in $files) {
        Write-Host "`t[AllProjects] FileName[$($file.FullName)]"
        $relPath = Join-Path $project ($file.FullName.Substring($fullSrc.TrimEnd('\').Length).TrimStart('\'))
        $writer.WriteLine("// RelativeFilePath: $relPath")
        $writer.WriteLine()
        foreach ($line in Get-Content $file.FullName) {
            $writer.WriteLine($line)
        }
        $writer.WriteLine()
    }

    $writer.WriteLine("//")
    $writer.WriteLine("// End Project: $project")
    $writer.WriteLine("//")
    $writer.WriteLine()
}
$writer.Close()

Split-LargeFile -FilePath $allDst -MaxSize $fileSize -prefix "AllProjects"

Write-Host "All processing complete."
