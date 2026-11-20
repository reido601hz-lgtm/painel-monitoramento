#Requires -RunAsAdministrator
<#
    Reinicia o painel SEM copiar nada (use depois de editar o config.json, cujos nomes so' sao lidos na
    partida). Para o painel e o PresentMon filho, encerra a sessao ETW do FPS e inicia a tarefa de novo.
    Para publicar uma versao nova use o atualizar.ps1. ASCII de proposito (PowerShell 5.1).
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$tarefa = 'PainelMonitor'
$porta = 8080

Write-Host '== parando o painel' -ForegroundColor Cyan
Get-Process PainelMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'PresentMon*' -and $_.Path -like "$root\tools\*" } | Stop-Process -Force
& logman stop painelmonitor_fps -ets 2>&1 | Out-Null
Start-Sleep -Seconds 2

Write-Host '== iniciando a tarefa' -ForegroundColor Cyan
Start-ScheduledTask -TaskName $tarefa
Start-Sleep -Seconds 8

$ouvindo = Get-NetTCPConnection -State Listen -LocalPort $porta -ErrorAction SilentlyContinue
if ($ouvindo) {
    Write-Host "OK: painel ouvindo em http://localhost:$porta (PID $($ouvindo[0].OwningProcess))" -ForegroundColor Green
} else {
    Write-Host "FALHOU: porta $porta nao esta ouvindo. Veja $env:LOCALAPPDATA\PainelMonitor\logs\err.log" -ForegroundColor Red
    exit 1
}
