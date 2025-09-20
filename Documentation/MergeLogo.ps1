param(
    [string]$srcPath = "C:\GitHubRepos\SilicaDB\Documentation\Tutorials\Logo",
    [string]$dstPath = "C:\temp\",
    [int]$fileSize = 80000
)

# Ensure destination path exists
if (!(Test-Path $dstPath)) {
    New-Item -ItemType Directory -Path $dstPath | Out-Null
}

# Define combined file path
$combinedFile = Join-Path $dstPath "Logo_All.txt"

# Remove old combined file if it exists
Remove-Item $combinedFile -ErrorAction SilentlyContinue

# Combine all files into one text file with #Filename markers, excluding Merge.ps1
Get-ChildItem -recurse -Path $srcPath -File | Where-Object { $_.Name -ne "Merge.ps1" } | ForEach-Object {
    Add-Content -Path $combinedFile -Value ("`n#`n#Filename: " + $_.FullName + "`n#`n")
    Get-Content $_.FullName | Add-Content -Path $combinedFile
    Add-Content -Path $combinedFile -Value ""   # blank line separator
}

Write-Host "Combined file created at: $combinedFile"

# Split the combined file into chunks
$bytes = [System.IO.File]::ReadAllBytes($combinedFile)
$totalSize = $bytes.Length
$part = 0
$offset = 0

while ($offset -lt $totalSize) {
    $chunkSize = [Math]::Min($fileSize, $totalSize - $offset)
    $chunk = New-Object byte[] $chunkSize
    [Array]::Copy($bytes, $offset, $chunk, 0, $chunkSize)

    $partFile = Join-Path $dstPath ("Tutorials_All_Part{0}.txt" -f $part)
    [System.IO.File]::WriteAllBytes($partFile, $chunk)

    Write-Host "Created: $partFile ($chunkSize bytes)"

    $offset += $chunkSize
    $part++
}
