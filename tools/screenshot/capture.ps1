# capture.ps1 — captura a JANELA do jogo num PNG usando PrintWindow, que renderiza o CONTEUDO
# proprio da janela mesmo se ela estiver coberta ou noutro monitor (ao contrario de CopyFromScreen,
# que pega o que esta visivel na tela). Se der preto, o jogo esta em fullscreen EXCLUSIVO — mudar
# pra borderless/janela resolve.
param(
    [string]$Out = "$env:TEMP\cs2_shot.png",
    [string]$Process = "Cities2",
    [int]$MaxWidth = 1500
)

Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public class Cap {
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
            PrintWindow(h, hdc, 2); // PW_RENDERFULLCONTENT — renderiza DX
            g.ReleaseHdc(hdc);
        }
        return bmp;
    }
}
"@

$p = Get-Process -Name $Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW ($Process)"; exit 1 }

$full = [Cap]::Grab($p.MainWindowHandle)
if ($null -eq $full) { Write-Output "BAD_RECT"; exit 1 }

if ($MaxWidth -gt 0 -and $full.Width -gt $MaxWidth) {
    $h = [int]($full.Height * $MaxWidth / $full.Width)
    $small = New-Object System.Drawing.Bitmap($MaxWidth, $h)
    $g2 = [System.Drawing.Graphics]::FromImage($small)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.DrawImage($full, 0, 0, $MaxWidth, $h)
    $g2.Dispose(); $full.Dispose()
    $small.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $small.Dispose()
    Write-Output "SAVED $Out (${MaxWidth}x${h}) via PrintWindow"
}
else {
    $full.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output "SAVED $Out ($($full.Width)x$($full.Height)) via PrintWindow"
    $full.Dispose()
}
