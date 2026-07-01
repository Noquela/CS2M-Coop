# =============================================================================
# CS2M - teste autonomo de sincronizacao em UMA maquina, com DUAS instancias
# do jogo falando por 127.0.0.1. Sem segundo humano, sem mouse.
#
# 1. Sobe a instancia HOST com -continuelastsave (auto-carrega o ultimo save).
#    O mod, com CS2M_AUTOPILOT=host, auto-hospeda quando a cidade carrega.
# 2. Espera o host publicar o servidor (lendo host.log do autopilot).
# 3. Sobe a instancia CLIENTE. Com CS2M_AUTOPILOT=client ela auto-conecta no
#    127.0.0.1 e baixa o mapa do host.
# 4. Quando o cliente entra (PLAYING), o host roda um roteiro: arvore, predio,
#    estrada, delete-arvore - pelos MESMOS comandos dos detectores reais.
# 5. O cliente registra quantos objetos/edges remotos materializou.
# 6. No fim, imprime as duas transcricoes [Auto] e um veredito.
#
# Cada instancia escreve seu PROPRIO log (CS2M_AP_LOG) porque as duas
# compartilham o CS2M.log do jogo - assim o sinal de cada lado fica limpo.
#
# Uso: powershell -ExecutionPolicy Bypass -File run_localhost_test.ps1
# =============================================================================
param(
    [int]$Port = 1111,
    [int]$HostLoadTimeoutSec = 300,
    [int]$TestTimeoutSec = 240,
    [switch]$KillWhenDone
)

$ErrorActionPreference = 'Stop'

$GameDir = 'C:\JogosCrackeados\Cities.Skylines.II.v1.5.3f1\game'
$Exe     = Join-Path $GameDir 'Cities2.exe'
$GameLog = "$env:USERPROFILE\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CS2M.log"

$OutDir    = Join-Path $PSScriptRoot 'out'
$HostLog   = Join-Path $OutDir 'host.log'
$ClientLog = Join-Path $OutDir 'client.log'

function Say($msg, $color = 'Cyan') { Write-Host "[autotest] $msg" -ForegroundColor $color }

if (-not (Test-Path $Exe)) { Say "NAO achei o jogo em $Exe" 'Red'; exit 1 }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Remove-Item $HostLog, $ClientLog -ErrorAction SilentlyContinue

function Wait-ForLine($file, $pattern, $timeoutSec, $what) {
    Say "Esperando: $what (padrao '$pattern', ate ${timeoutSec}s)"
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $file) {
            $hit = Select-String -Path $file -Pattern $pattern -SimpleMatch -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($hit) { Say "OK -> $($hit.Line.Trim())" 'Green'; return $true }
        }
        Start-Sleep -Seconds 2
    }
    Say "TIMEOUT esperando '$pattern' em $file" 'Yellow'
    return $false
}

# --- 1) HOST -----------------------------------------------------------------
Say "===> Subindo HOST (Cities2.exe -continuelastsave)"
$env:CS2M_AUTOPILOT = 'host'
$env:CS2M_AP_PORT   = "$Port"
$env:CS2M_AP_LOG    = $HostLog
$env:CS2M_AP_TEST   = '1'
Remove-Item Env:CS2M_AP_IP -ErrorAction SilentlyContinue
$hostProc = Start-Process -FilePath $Exe -WorkingDirectory $GameDir -ArgumentList '-continuelastsave' -PassThru
Say "HOST pid=$($hostProc.Id). Carregando cidade... (isso demora)"

if (-not (Wait-ForLine $HostLog 'HOST StartServer' $HostLoadTimeoutSec 'host publicar o servidor')) {
    Say "O host nao chegou a hospedar. Veja se a cidade carregou e se o mod ativou (host.log)." 'Red'
    Say "PID host=$($hostProc.Id). Feche manualmente quando quiser." 'Yellow'
    exit 2
}

# --- 2) CLIENTE --------------------------------------------------------------
Say "===> Subindo CLIENTE (Cities2.exe conecta em 127.0.0.1:$Port)"
$env:CS2M_AUTOPILOT = 'client'
$env:CS2M_AP_IP     = '127.0.0.1'
$env:CS2M_AP_PORT   = "$Port"
$env:CS2M_AP_LOG    = $ClientLog
$env:CS2M_AP_TEST   = '1'
$clientProc = Start-Process -FilePath $Exe -WorkingDirectory $GameDir -PassThru
Say "CLIENTE pid=$($clientProc.Id). Conectando e baixando o mapa..."

if (-not (Wait-ForLine $ClientLog 'CLIENT PLAYING' $HostLoadTimeoutSec 'cliente entrar (PLAYING)')) {
    Say "O cliente nao entrou. Pode ser: crack bloqueando 2a instancia, ou falha de conexao localhost." 'Red'
    Say "Veja client.log. PIDs: host=$($hostProc.Id) client=$($clientProc.Id)." 'Yellow'
    exit 3
}

# --- 3) Roteiro de teste -----------------------------------------------------
Wait-ForLine $HostLog 'scripted test DONE' $TestTimeoutSec 'host terminar o roteiro' | Out-Null
Say "Aguardando o cliente processar as ultimas aplicacoes..."
Start-Sleep -Seconds 8

# --- 4) Relatorio ------------------------------------------------------------
Write-Host ""
Say "================= HOST ($HostLog) =================" 'Magenta'
if (Test-Path $HostLog) { Get-Content $HostLog | ForEach-Object { Write-Host "  $_" } }
Write-Host ""
Say "================ CLIENTE ($ClientLog) ================" 'Magenta'
if (Test-Path $ClientLog) { Get-Content $ClientLog | ForEach-Object { Write-Host "  $_" } }

Write-Host ""
Say "===================== VEREDITO =====================" 'Magenta'

function Has($file, $pat) {
    if (-not (Test-Path $file)) { return $false }
    return [bool](Select-String -Path $file -Pattern $pat -SimpleMatch -Quiet)
}

function Report($label, $ok) {
    $mark = '???'
    $color = 'Yellow'
    if ($ok) { $mark = 'PASS'; $color = 'Green' }
    Say ("{0,-46}: {1}" -f $label, $mark) $color
}

$vObj = (Has $ClientLog 'remoteObjects=1') -or (Has $ClientLog 'remoteObjects=2')

$vEdge = $false
if (Test-Path $ClientLog) {
    $edgeVals = @(Select-String -Path $ClientLog -Pattern 'totalEdges=(\d+)' | ForEach-Object { [int]$_.Matches[0].Groups[1].Value })
    if ($edgeVals.Count -ge 2 -and $edgeVals[-1] -gt $edgeVals[0]) { $vEdge = $true }
}

$vApplied = Has $GameLog 'APPLIED'
$vNet     = Has $GameLog 'INJECT'

Report 'Objetos remotos no cliente (remoteObjects>=1)' $vObj
Report 'Estrada remota (totalEdges aumentou)' $vEdge
Report 'Place APPLIED no CS2M.log do jogo' $vApplied
Report 'Net INJECT no CS2M.log do jogo' $vNet

Write-Host ""
Say "Logs salvos em: $OutDir" 'Cyan'
Say "PIDs: host=$($hostProc.Id) client=$($clientProc.Id)" 'Cyan'

if ($KillWhenDone) {
    Say "Fechando as duas instancias (-KillWhenDone)..."
    Stop-Process -Id $hostProc.Id, $clientProc.Id -Force -ErrorAction SilentlyContinue
} else {
    Say "As duas instancias continuam abertas pra inspecao. Feche quando quiser." 'Cyan'
}
