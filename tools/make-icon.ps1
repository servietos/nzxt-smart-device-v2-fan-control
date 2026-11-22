# Erzeugt app.ico (Lüfter-Symbol, 16–256 px) für NzxtFan.
param([string]$Out = "$PSScriptRoot\..\app.ico")
Add-Type -AssemblyName System.Drawing

function New-FanPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    # abgerundetes Quadrat mit Farbverlauf
    $r = [single]($s * 0.22); $rect = New-Object System.Drawing.RectangleF 0, 0, ($s - 1), ($s - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, 2*$r, 2*$r, 180, 90); $path.AddArc($rect.Right-2*$r, $rect.Y, 2*$r, 2*$r, 270, 90)
    $path.AddArc($rect.Right-2*$r, $rect.Bottom-2*$r, 2*$r, 2*$r, 0, 90); $path.AddArc($rect.X, $rect.Bottom-2*$r, 2*$r, 2*$r, 90, 90); $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255,167,120,255)), ([System.Drawing.Color]::FromArgb(255,76,29,149)), 45
    $g.FillPath($bg, $path)
    # Rotorblätter
    $c = $s / 2.0; $g.TranslateTransform($c, $c)
    # Rahmenring
    $ringW = [single]([Math]::Max(1.5, $s * 0.055)); $ro = [single]($s * 0.40)
    $g.DrawEllipse((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200,255,255,255)), $ringW), -$ro, -$ro, 2*$ro, 2*$ro)
    # gebogene, schräg gestellte Blätter
    $blade = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(245,255,255,255))
    $n = 7
    for ($i = 0; $i -lt $n; $i++) {
        $st = $g.Save()
        $g.RotateTransform([single](360.0 / $n * $i))
        $bp = New-Object System.Drawing.Drawing2D.GraphicsPath
        $u = $s / 100.0
        $bp.AddBezier([single](0), [single](-9*$u), [single](14*$u), [single](-14*$u), [single](22*$u), [single](-26*$u), [single](14*$u), [single](-33*$u))
        $bp.AddBezier([single](14*$u), [single](-33*$u), [single](6*$u), [single](-26*$u), [single](-2*$u), [single](-18*$u), [single](-6*$u), [single](-8*$u))
        $bp.CloseFigure()
        $g.FillPath($blade, $bp)
        $g.Restore($st)
    }
    $g.ResetTransform()
    $hub = [single]($s * 0.20)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,91,33,182))), [single]($c-$hub/2), [single]($c-$hub/2), $hub, $hub)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($sz in $sizes) { [byte[]]$b = New-FanPng $sz; $pngs.Add($b) }
$fs = [System.IO.File]::Create($Out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"geschrieben: $Out"
