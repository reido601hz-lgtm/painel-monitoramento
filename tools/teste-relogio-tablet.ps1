#Requires -RunAsAdministrator
<#
    Teste do modo relogio no tablet de verdade, sem desligar o PC: derruba o painel por alguns segundos E
    bloqueia (sem resposta, como PC desligado) a porta 8080 para o IP do tablet, depois desfaz tudo.

    - para o painel e o PresentMon filho (mesmo que o reiniciar-painel.ps1);
    - cria uma regra de firewall TEMPORARIA que bloqueia a entrada TCP 8080 vinda do tablet;
    - espera -Segundos (padrao 70; o relogio entra em ~15 a 30 s);
    - remove a regra e inicia a tarefa PainelMonitor de novo, conferindo a porta.
    O bloco finally remove a regra e religa o painel mesmo com erro ou Ctrl+C.
    Nao rode com jogo em tela cheia: o prompt de UAC pode tirar o jogo do foco.
    Uso (como administrador): .\tools\teste-relogio-tablet.ps1 [-Segundos 70] [-Tablet <IP-DO-TABLET>]
    ASCII de proposito (Windows PowerShell 5.1).
#>
param([int]$Segundos = 70, [string]$Tablet = '<IP-DO-TABLET>')

$ErrorActionPreference = 'Stop'
# confere ANTES de parar o painel (na copia publicada o IP padrao e' um placeholder)
if ($Tablet -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { throw "informe o IP do tablet: -Tablet <IP do tablet> (recebi '$Tablet')" }
$root = Split-Path -Parent $PSScriptRoot
$tarefa = 'PainelMonitor'
$porta = 8080
$regra = 'TESTE relogio tablet (temporario)'

Get-NetFirewallRule -DisplayName $regra -ErrorAction SilentlyContinue | Remove-NetFirewallRule
try {
    Write-Host ('{0}  parando o painel' -f (Get-Date -Format 'HH:mm:ss')) -ForegroundColor Cyan
    Get-Process PainelMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'PresentMon*' -and $_.Path -like "$root\tools\*" } | Stop-Process -Force
    & logman stop painelmonitor_fps -ets 2>&1 | Out-Null

    New-NetFirewallRule -DisplayName $regra -Direction Inbound -Action Block -Protocol TCP -LocalPort $porta -RemoteAddress $Tablet -Profile Any | Out-Null
    Write-Host ('{0}  painel parado e porta {1} bloqueada para {2}; olhe o tablet ({3} s)' -f (Get-Date -Format 'HH:mm:ss'), $porta, $Tablet, $Segundos) -ForegroundColor Yellow
    Start-Sleep -Seconds $Segundos
}
finally {
    Get-NetFirewallRule -DisplayName $regra -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Host ('{0}  regra temporaria removida; iniciando o painel' -f (Get-Date -Format 'HH:mm:ss')) -ForegroundColor Cyan
    Start-ScheduledTask -TaskName $tarefa
    Start-Sleep -Seconds 8
    $ouvindo = Get-NetTCPConnection -State Listen -LocalPort $porta -ErrorAction SilentlyContinue
    if ($ouvindo) { Write-Host ('OK: painel de volta em http://localhost:{0} (PID {1})' -f $porta, $ouvindo[0].OwningProcess) -ForegroundColor Green }
    else { Write-Host ('FALHOU: porta {0} nao esta ouvindo; rode reiniciar-painel.ps1' -f $porta) -ForegroundColor Red }
}
