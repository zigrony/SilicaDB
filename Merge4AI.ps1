param(
    [string]$srcPath = "C:\GitHubRepos\SilicaDB\src\",
    [string]$dstPath = "C:\temp\",
    [string]$folder = "SilicaDB",
    [int]$fileSize = 100000
)


function Split-LargeFile {
    param (
        [string]$FilePath,
        [int]$MaxSize,
		[string]$prefix = "file_"
    )
#wait-debugger
    # Ensure the input file exists
    if (-Not (Test-Path -Path $FilePath)) {
        Write-Error "The specified file does not exist."
        return
    }

    # Initialize variables
    $FolderPath = Split-Path -Path $FilePath
    $BaseFileName = "$($prefix)_File_"
    $CurrentSize = 0
    $FileIndex = 0
    $OutputFile = Join-Path -Path $FolderPath -ChildPath "${BaseFileName}${FileIndex}.txt"

    # Open the input file for reading
    $InputFile = Get-Content -Path $FilePath

    # Create a StreamWriter to output the first file
    $Writer = [System.IO.StreamWriter]::new($OutputFile)

    # Process lines in the input file
    foreach ($Line in $InputFile) {
        $LineSize = ($Line.Length + [Environment]::NewLine.Length)
        if (($CurrentSize + $LineSize) -gt $MaxSize) {
            # Close the current file and start a new one
            $Writer.Close()
            $FileIndex++
            $OutputFile = Join-Path -Path $FolderPath -ChildPath "${BaseFileName}${FileIndex}.txt"
            $Writer = [System.IO.StreamWriter]::new($OutputFile)
            $CurrentSize = 0
        }

        # Write the line to the current file
        $Writer.WriteLine($Line)
        $CurrentSize += $LineSize
    }

    # Close the final file
    $Writer.Close()

    Write-Host "Splitting complete. Files have been saved in $FolderPath"
}


$fileType = "*.cs"
$allAllContent = ""
$allAllContentDst = "$($dstPath)AllContent.txt"
$fileIndex = 0
$outfile = "$($dstPath)File$($fileIndex).txt"
# Concatenate file contents and save to temp file
$allContent = ""
$fullSrc = [System.IO.Path]::Combine($srcPath,$folder)
$fullDst = [System.IO.Path]::Combine($dstPath,"$($folder)_AllContent.txt")
write-host "Full Source $($fullSrc)"
write-host "  Full Dest $($fullDst)"
$files = Get-ChildItem -Recurse -file -Path $fullSrc -Filter $fileType
Foreach($file in $files) { 
		write-host "`tFileName[$($file.FullName)]"
		$content = Get-Content $file.FullName 
		if($content.Length -gt 0){
			$relPath = $file.FullName.substring($fullSrc.length)
			$allContent += "`r`n// Filename: $($relPath)`r`n`r`n" + ($content -join "`r`n")
			}
		}
$allContent | Set-Content $fullDst 
Split-LargeFile $fullDst $fileSize "$($folder)_"

