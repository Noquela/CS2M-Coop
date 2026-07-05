# =============================================================================
# run_2sims.ps1 — teste de DUAS sims REAIS numa maquina so:
#   HOST  = instancia normal (fora do box), carrega o ultimo save e hospeda
#   CLIENT= instancia SANDBOXED (Sandboxie box), dribla o lock de instancia unica,
#           conecta em 127.0.0.1 e baixa o mundo do host
#
# Faz tudo: limpa -> sobe host -> espera publicar -> sobe client sandboxed ->
# detecta PLAYING (sucesso) vs "Could not open file multiplayer" crescendo (o
# travamento de async-I/O do load sob Sandboxie) -> veredito + screenshots.
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File run_2sims.ps1 [-Test 1] [-Kill] [-Shots]
# =============================================================================
param(
    [string]$Box          = "CS2Coop",
    [string]$Test         = "0",     # "1" = host roda roteiro (arvore/predio/estrada) quando o client entra
    [int]$HostTimeout     = 240,
    [int]$ClientTimeout   = 320,
    [switch]$Kill,                   # matar as duas instancias no fim
    [switch]$Shots,                  # screenshots das 2 janelas no fim
    [switch]$Concurrent              # CS2M_AP_CONCURRENT=1: os DOIS lados estampam a mesma rua
)
$conc = if ($Concurrent) { "1" } else { "" }

$ErrorActionPreference = 'Continue'
$GameDir  = 'C:\JogosCrackeados\Cities.Skylines.II.v1.5.3f1\game'
$Exe      = Join-Path $GameDir 'Cities2.exe'
$Start    = 'C:\Program Files\Sandboxie-Plus\Start.exe'
$OutDir   = Join-Path $PSScriptRoot 'out'
$HostLog  = Join-Path $OutDir 'host_2sims.log'
$BoxRoot  = "C:\Sandbox\$env:USERNAME\$Box\user\current\AppData\LocalLow\Colossal Order\Cities Skylines II"
$BoxCS2M  = "$BoxRoot\Logs\CS2M.log"
$BoxPlayer= "$BoxRoot\Player.log"
$HostCS2M = "$env:USERPROFILE\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CS2M.log"
$ShotDir  = "$env:TEMP\claude\cs2shots"

function Say($m,$c='Cyan'){ Write-Host "[2sims] $m" -ForegroundColor $c }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# --- 0) limpar -------------------------------------------------------------
Say "limpando instancias antigas..."
& $Start "/box:$Box" /terminate 2>&1 | Out-Null
Get-Process -Name "Cities2" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

# --- 1) HOST normal --------------------------------------------------------
Say "subindo HOST normal (-continuelastsave, autopilot=host, test=$Test)"
$env:CS2M_AUTOPILOT="host"; $env:CS2M_AP_PORT="1111"; $env:CS2M_AP_TEST=$Test; $env:CS2M_AP_IP=""; $env:CS2M_AP_CONCURRENT=$conc
$env:CS2M_AP_LOG=$HostLog
try { Clear-Content $HostLog -ErrorAction Stop } catch {}
$hostProc = Start-Process -FilePath $Exe -WorkingDirectory $GameDir -ArgumentList '-continuelastsave' -PassThru
Say "HOST pid=$($hostProc.Id). Esperando publicar servidor..."

$deadline=(Get-Date).AddSeconds($HostTimeout); $ok=$false
while((Get-Date) -lt $deadline){
    if(Test-Path $HostLog){ if(Select-String -Path $HostLog -Pattern "StartServer on" -SimpleMatch -Quiet){$ok=$true;break} }
    if(-not (Get-Process -Id $hostProc.Id -ErrorAction SilentlyContinue)){ Say "HOST morreu" Red; exit 2 }
    Start-Sleep -Seconds 4
}
if(-not $ok){ Say "HOST nao publicou em ${HostTimeout}s" Red; exit 2 }
Say "HOST publicou :1111" Green

# --- 2) CLIENT sandboxed ---------------------------------------------------
Say "subindo CLIENT sandboxed (box=$Box, autopilot=client)"
$env:CS2M_AUTOPILOT="client"; $env:CS2M_AP_IP="127.0.0.1"; $env:CS2M_AP_PORT="1111"; $env:CS2M_AP_TEST=$Test; $env:CS2M_AP_LOG=""; $env:CS2M_AP_CONCURRENT=$conc
Push-Location $GameDir
& $Start "/box:$Box" /silent $Exe
Pop-Location
Say "CLIENT lancado. Monitorando join/load..."

# --- 3) detectar PLAYING vs travamento -------------------------------------
$deadline=(Get-Date).AddSeconds($ClientTimeout); $result="timeout"
while((Get-Date) -lt $deadline){
    Start-Sleep -Seconds 6
    $nproc=@(Get-Process -Name "Cities2" -ErrorAction SilentlyContinue).Count
    $cno = if(Test-Path $BoxPlayer){ (Get-Content $BoxPlayer | Select-String "Could not open file multiplayer" -SimpleMatch).Count } else {0}
    # v55: buscar no arquivo TODO — o marcador "watching for placements" e logado UMA vez e os
    # heartbeats o empurram pra fora de qualquer janela -Tail (era por isso que dava timeout falso).
    $play= if(Test-Path $BoxCS2M){ [bool](Select-String -Path $BoxCS2M -Pattern "watching for placements" -SimpleMatch -Quiet) } else {$false}
    Say ("proc={0} CouldNotOpen={1} clientPLAYING={2}" -f $nproc,$cno,$play)
    if($play){ $result="playing"; break }
    if($cno -gt 250){ $result="stuck"; break }
    if($nproc -lt 2){ $result="crash"; break }
}

# --- 4) veredito -----------------------------------------------------------
Write-Host ""
Say "===================== VEREDITO 2 SIMS =====================" Magenta
if     ($result -eq "playing") { Say "CLIENT ENTROU PLAYING - 2 sims reais no mesmo mundo!" Green }
elseif ($result -eq "stuck")   { Say "CLIENT TRAVOU no load - async IO do Sandboxie" Red }
elseif ($result -eq "crash")   { Say "CLIENT caiu - menos de 2 processos" Red }
else                           { Say "TIMEOUT - client nao entrou nem travou" Yellow }

# v55: com -Test 1 o roteiro do host so COMECA ~600 frames apos o client conectar; sem esperar ele
# terminar, as instancias morrem antes de zona/agua rodarem e o DRIFT nao reflete as acoes. Esperar.
if($result -eq "playing" -and $Test -eq "1"){
    Say "Esperando o roteiro do host terminar (scripted test DONE, ate 500s)..."
    $rd=(Get-Date).AddSeconds(500)
    while((Get-Date) -lt $rd){
        if((Test-Path $HostLog) -and (Select-String -Path $HostLog -Pattern "scripted test DONE" -SimpleMatch -Quiet)){ Say "roteiro terminou" Green; break }
        Start-Sleep -Seconds 5
    }
    Start-Sleep -Seconds 12  # deixar o StateHash (~10s) comparar apos a ultima acao
}

$drift = if(Test-Path $BoxCS2M){ (Select-String -Path $BoxCS2M -Pattern "[Hash] DRIFT" | Select-Object -Last 1) } else {$null}
if($drift){ Say "DRIFT: $($drift.Line.Trim())" Red } else { Say "sem [Hash] DRIFT" Green }

if($Shots){
    & "$PSScriptRoot\..\screenshot\capture-multi.ps1" -OutDir $ShotDir | ForEach-Object { Say $_ }
}
Say "logs: HOST=$HostLog | CLIENT(box)=$BoxCS2M" Cyan
Say "PIDs: host=$($hostProc.Id)  (client sandboxed no box $Box)" Cyan

if($Kill){
    Say "fechando as duas instancias..."
    & $Start "/box:$Box" /terminate 2>&1 | Out-Null
    Get-Process -Name "Cities2" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
exit $(if($result -eq "playing"){0}else{1})
