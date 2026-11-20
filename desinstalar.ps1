#Requires -RunAsAdministrator
<#
    Remove o que o instalar.ps1 criou. Nao apaga a pasta do projeto nem os logs.
#>

$ErrorActionPreference = 'Continue'

$tarefa = 'PainelMonitor'
$regra = 'Painel de monitoramento (TCP 8080)'

Write-Host '== parando tarefa e processo' -ForegroundColor Cyan
Stop-ScheduledTask -TaskName $tarefa -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $tarefa -Confirm:$false -ErrorAction SilentlyContinue
Get-Process PainelMonitor -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host '== removendo regra de firewall' -ForegroundColor Cyan
Get-NetFirewallRule -DisplayName $regra -ErrorAction SilentlyContinue | Remove-NetFirewallRule

Write-Host 'pronto' -ForegroundColor Green
