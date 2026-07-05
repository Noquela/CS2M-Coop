# =============================================================================
# CS2M - SELFTEST em UMA instancia (sem 2o humano, sem Sandboxie).
# CS2M_AUTOPILOT=selftest faz StartServer (fica PLAYING sem cliente) e injeta os
# MESMOS comandos dos detectores nas filas de apply, lendo o estado do jogo de
# volta pra validar CADA feature. Inclui o passo net-xcross (v55): cruzamento X
# pelo caminho autoritativo HasNodes com no central DERIVANDO — pega o bug de
# juncao que os testes antigos (HasNodes=false) nunca alcancavam.
#
# Uso: powershell -ExecutionPolicy Bypass -File run_selftest.ps1 [-TimeoutSec 420] [-Kill]
# =============================================================================
param(
    [int]$LoadTimeoutSec = 300,
    [int]$TestTimeoutSec = 600,   # v55: era 220 -> dava timeout no passo ~22 quando a janela fica em background (framerate baixo). 30 passos * ~200 frames precisam de folga.
    [switch]$Kill
)

$ErrorActionPreference = 'Stop'
$GameDir = 'C:\JogosCrackeados\Cities.Skylines.II.v1.5.3f1\game'
$Exe     = Join-Path $GameDir 'Cities2.exe'
$OutDir  = Join-Path $PSScriptRoot 'out'
$Log     = Join-Path $OutDir 'selftest.log'

function Say($m,$c='Cyan'){ Write-Host "[selftest] $m" -ForegroundColor $c }
if (-not (Test-Path $Exe)) { Say "NAO achei $Exe" 'Red'; exit 1 }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Remove-Item $Log -ErrorAction SilentlyContinue

Get-Process Cities2 -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2

$env:CS2M_AUTOPILOT = 'selftest'
$env:CS2M_AP_TEST   = '1'
$env:CS2M_AP_LOG    = $Log
Remove-Item Env:CS2M_AP_IP -ErrorAction SilentlyContinue

Say "===> Subindo SELFTEST (Cities2.exe -continuelastsave)"
$proc = Start-Process -FilePath $Exe -WorkingDirectory $GameDir -ArgumentList '-continuelastsave' -PassThru
Say "pid=$($proc.Id). Carregando cidade..."

function Wait-Line($pat,$sec,$what){
  Say "Esperando: $what ('$pat', ate ${sec}s)"
  $deadline=(Get-Date).AddSeconds($sec)
  while((Get-Date) -lt $deadline){
    if(Test-Path $Log){
      $hit = Select-String -Path $Log -Pattern $pat -SimpleMatch -ErrorAction SilentlyContinue | Select-Object -First 1
      if($hit){ Say "OK -> $($hit.Line.Trim())" 'Green'; return $true }
    }
    Start-Sleep -Seconds 3
  }
  Say "TIMEOUT '$pat'" 'Yellow'; return $false
}

if(-not (Wait-Line 'SELFTEST beginning' $LoadTimeoutSec 'cidade carregar e selftest comecar')){
  Say "Selftest nao comecou. Veja $Log. pid=$($proc.Id)" 'Red'; exit 2
}
Wait-Line 'scripted test DONE' $TestTimeoutSec 'selftest terminar' | Out-Null
Start-Sleep -Seconds 3

Write-Host ""
Say "===================== RESULTS =====================" 'Magenta'
if(Test-Path $Log){
  Select-String -Path $Log -Pattern 'RESULT ' | ForEach-Object {
    $line = $_.Line
    $c = if($line -match ': PASS'){'Green'}elseif($line -match ': FAIL'){'Red'}else{'Gray'}
    Write-Host "  $($line.Substring($line.IndexOf('RESULT')))" -ForegroundColor $c
  }
}
Write-Host ""
$pass = @(Select-String -Path $Log -Pattern ': PASS').Count
$fail = @(Select-String -Path $Log -Pattern ': FAIL').Count
Say "TOTAL: $pass PASS / $fail FAIL" $(if($fail -eq 0){'Green'}else{'Red'})
$xc = Select-String -Path $Log -Pattern 'net-xcross:' | Select-Object -Last 1
if($xc){ Say "FOCO -> $($xc.Line.Substring($xc.Line.IndexOf('net-xcross')))" $(if($xc.Line -match 'PASS'){'Green'}else{'Red'}) }
Say "log: $Log  pid=$($proc.Id)" 'Cyan'
if($Kill){ Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Say "instancia encerrada" }
