# capture-multi.ps1 — captura TODAS as janelas Cities2 (host + sandboxed) de uma vez,
# uma por PID, via PrintWindow (pega o conteudo da janela mesmo coberta/noutro monitor).
# Uso:
#   capture-multi.ps1                      -> captura cada instancia em <OutDir>\cs2_<PID>.png
#   capture-multi.ps1 -TargetPid 47264 -Out foo.png  -> so essa instancia
param(
    [int]$TargetPid = 0,
    [string]$Out = "",
    [string]$OutDir = "$env:TEMP\claude\cs2shots",
    [int]$MaxWidth = 1400
)

Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public class CapM {
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    public static Bitmap Grab(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        if (w <= 0 || ht <= 0) return null;
        var bmp = new Bitmap(w, ht);
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr hdc = g.GetHdc();
            PrintWindow(h, hdc, 2); // PW_RENDERFULLCONTENT
            g.ReleaseHdc(hdc);
        }
        return bmp;
    }
}
"@

function Save-Shot([System.Drawing.Bitmap]$full, [string]$path, [int]$maxW) {
    if ($maxW -gt 0 -and $full.Width -gt $maxW) {
        $h = [int]($full.Height * $maxW / $full.Width)
        $small = New-Object System.Drawing.Bitmap($maxW, $h)
        $g2 = [System.Drawing.Graphics]::FromImage($small)
        $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g2.DrawImage($full, 0, 0, $maxW, $h); $g2.Dispose()
        $small.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $small.Dispose()
        return "$($maxW)x$h"
    } else {
        $full.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        return "$($full.Width)x$($full.Height)"
    }
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$procs = Get-Process -Name "Cities2" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }
if ($TargetPid -gt 0) { $procs = $procs | Where-Object { $_.Id -eq $TargetPid } }
if (-not $procs) { Write-Output "NO_WINDOW (nenhuma janela Cities2 com handle)"; exit 1 }

foreach ($p in $procs) {
    $full = [CapM]::Grab($p.MainWindowHandle)
    if ($null -eq $full) { Write-Output "PID $($p.Id): BAD_RECT"; continue }
    $path = if ($Out -ne "" -and $TargetPid -gt 0) { $Out } else { Join-Path $OutDir "cs2_$($p.Id).png" }
    $dim = Save-Shot $full $path $MaxWidth
    $full.Dispose()
    Write-Output "PID $($p.Id) [$($p.MainWindowTitle)]: SAVED $path ($dim)"
}
