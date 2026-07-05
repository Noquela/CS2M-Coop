# compare_sims.ps1 — veredito do teste "2 sims reais" (host normal + client sandboxed).
# Le os logs dos DOIS lados (o client escreve isolado dentro do box) e reporta:
#   - marcos de sessao (host publicou, client entrou)
#   - materializacao do roteiro no client (remoteObjects / totalEdges subiram)
#   - [Hash] DRIFT em qualquer lado (o radar de divergencia)
#   - CRITICAL / Exception (crash)
param(
    [string]$HostAuto = "C:\Users\Bruno\CS2M-Coop\tools\autotest\out\host_sbx.log",
    [string]$HostCS2M = "$env:USERPROFILE\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CS2M.log",
    [string]$BoxCS2M  = "C:\Sandbox\Bruno\CS2Coop\user\current\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CS2M.log"
)

function Has($file, $pat) {
    if (-not (Test-Path $file)) { return $false }
    return [bool](Select-String -Path $file -Pattern $pat -SimpleMatch -Quiet)
}
function LastLine($file, $pat) {
    if (-not (Test-Path $file)) { return $null }
    $h = Select-String -Path $file -Pattern $pat | Select-Object -Last 1
    if ($h) { return $h.Line.Trim() } else { return $null }
}
function Mark($label, $ok) {
    $m = if ($ok) { "PASS" } else { "----" }
    $c = if ($ok) { "Green" } else { "Yellow" }
    Write-Host ("  {0,-42}: {1}" -f $label, $m) -ForegroundColor $c
}

Write-Host "===================== VEREDITO 2 SIMS =====================" -ForegroundColor Magenta

# --- sessao ---
$hStart  = Has $HostAuto "StartServer on"
$hJoined = Has $HostAuto "client joined"
$hDone   = Has $HostAuto "scripted test DONE"
$cConn   = Has $BoxCS2M  "connect attempt"
$cPlay   = Has $BoxCS2M  "watching for placements"
Mark "HOST publicou servidor (:1111)" $hStart
Mark "HOST viu o client entrar" $hJoined
Mark "CLIENT (sandbox) tentou conectar" $cConn
Mark "CLIENT (sandbox) entrou PLAYING" $cPlay
Mark "HOST rodou o roteiro ate o fim" $hDone

# --- materializacao no client ---
$verify = LastLine $BoxCS2M "remoteObjects="
$vObj = $false; $vEdge = $false
if ($verify -match "remoteObjects=(\d+)") { $vObj = [int]$Matches[1] -ge 1 }
# edges: comparar primeiro vs ultimo totalEdges
if (Test-Path $BoxCS2M) {
    $edges = @(Select-String -Path $BoxCS2M -Pattern "totalEdges=(\d+)" | ForEach-Object { [int]$_.Matches[0].Groups[1].Value })
    if ($edges.Count -ge 2 -and $edges[-1] -gt $edges[0]) { $vEdge = $true }
}
Mark "CLIENT materializou objetos (remoteObjects>=1)" $vObj
Mark "CLIENT materializou estrada (totalEdges subiu)" $vEdge
Write-Host "    ult VERIFY: $verify" -ForegroundColor DarkGray

# --- radar de divergencia ---
$driftClient = LastLine $BoxCS2M  "[Hash] DRIFT"
$driftHost   = LastLine $HostCS2M "[Hash] DRIFT"
$anyDrift = $driftClient -or $driftHost
Write-Host ""
if ($anyDrift) {
    Write-Host "  [Hash] DRIFT DETECTADO (worlds divergindo!):" -ForegroundColor Red
    if ($driftClient) { Write-Host "    client: $driftClient" -ForegroundColor Red }
    if ($driftHost)   { Write-Host "    host:   $driftHost" -ForegroundColor Red }
} else {
    Write-Host "  [Hash] SEM DRIFT — hashes de conteudo batem entre as 2 sims" -ForegroundColor Green
}

# --- crash ---
$critClient = Has $BoxCS2M "CRITICAL"
$critHost   = Has $HostCS2M "CRITICAL"
Write-Host ""
if ($critClient -or $critHost) {
    Write-Host "  CRITICAL encontrado (client=$critClient host=$critHost)" -ForegroundColor Red
} else {
    Write-Host "  Sem CRITICAL em nenhum lado" -ForegroundColor Green
}

$procs = @(Get-Process -Name "Cities2" -ErrorAction SilentlyContinue).Count
Write-Host ""
Write-Host "  instancias Cities2 vivas agora: $procs (esperado 2)" -ForegroundColor Cyan
Write-Host "===========================================================" -ForegroundColor Magenta
