[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string]$BackupRoot,

    [ValidateRange(1, 8192)]
    [int]$Width = 512,

    [ValidateRange(1, 8192)]
    [int]$Height = 320
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sourcePath = [System.IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
$backupPath = [System.IO.Path]::GetFullPath($BackupRoot).TrimEnd('\')
if (-not [System.IO.Directory]::Exists($sourcePath)) {
    throw "Flag source does not exist: $sourcePath"
}
if ($backupPath.StartsWith($sourcePath + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'BackupRoot must be outside SourceRoot.'
}

[System.IO.Directory]::CreateDirectory($backupPath) | Out-Null
$files = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -File -Filter '*.png')
$beforeBytes = ($files | Measure-Object -Property Length -Sum).Sum
$optimized = 0

foreach ($file in $files) {
    $relativePath = $file.FullName.Substring($sourcePath.Length).TrimStart('\')
    $backupFile = [System.IO.Path]::GetFullPath((Join-Path $backupPath $relativePath))
    if (-not $backupFile.StartsWith($backupPath + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe backup path: $relativePath"
    }
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($backupFile)) | Out-Null
    [System.IO.File]::Copy($file.FullName, $backupFile, $true)

    $image = [System.Drawing.Image]::FromFile($file.FullName)
    try {
        if ($image.Width -le $Width -and $image.Height -le $Height) {
            continue
        }

        $scale = [System.Math]::Min($Width / [double]$image.Width, $Height / [double]$image.Height)
        $drawWidth = [System.Math]::Max(1, [int][System.Math]::Round($image.Width * $scale))
        $drawHeight = [System.Math]::Max(1, [int][System.Math]::Round($image.Height * $scale))
        $drawX = [int][System.Math]::Floor(($Width - $drawWidth) / 2.0)
        $drawY = [int][System.Math]::Floor(($Height - $drawHeight) / 2.0)
        $bitmap = New-Object System.Drawing.Bitmap($Width, $Height,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $destination = New-Object System.Drawing.Rectangle($drawX, $drawY, $drawWidth, $drawHeight)
                $graphics.DrawImage($image, $destination, 0, 0, $image.Width, $image.Height,
                    [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally {
                $graphics.Dispose()
            }

            $temporaryFile = Join-Path $file.DirectoryName ('.' + $file.Name + '.' +
                [System.Guid]::NewGuid().ToString('N') + '.tmp.png')
            $bitmap.Save($temporaryFile, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $image.Dispose()
    }

    $check = [System.Drawing.Image]::FromFile($temporaryFile)
    try {
        if ($check.Width -ne $Width -or $check.Height -ne $Height) {
            throw "Generated flag has unexpected dimensions: $relativePath"
        }
    }
    finally {
        $check.Dispose()
    }

    $replacementBackup = $file.FullName + '.' + [System.Guid]::NewGuid().ToString('N') + '.replace-backup'
    [System.IO.File]::Replace($temporaryFile, $file.FullName, $replacementBackup)
    [System.IO.File]::Delete($replacementBackup)
    $optimized++
}

$afterFiles = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -File -Filter '*.png')
$afterBytes = ($afterFiles | Measure-Object -Property Length -Sum).Sum
[pscustomobject]@{
    SourceRoot = $sourcePath
    BackupRoot = $backupPath
    FileCount = $afterFiles.Count
    OptimizedCount = $optimized
    BeforeBytes = $beforeBytes
    AfterBytes = $afterBytes
}
