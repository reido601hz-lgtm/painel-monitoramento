#Requires -RunAsAdministrator
<#
    Atualiza o painel em uso a partir de app-new\ (resultado de "dotnet publish"), sem recriar a regra
    de firewall nem a tarefa agendada. Use o instalar.ps1 so' quando uma dessas precisar ser refeita.

    - para o painel (o PresentMon filho morre junto pelo Job Object; qualquer sobra daqui tambem)
    - copia app-new\ por cima de app\ (robocopy /MIR: so' o que mudou) e remove app-new\
    - inicia a tarefa PainelMonitor e confere a porta 8080

    ASCII de proposito (Windows PowerShell 5.1 le arquivo sem BOM como ANSI).
#>

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app  = Join-Path $root 'app'
$new  = Join-Path $root 'app-new'
$tarefa = 'PainelMonitor'
$porta = 8080

if (-not (Test-Path -LiteralPath $new)) { throw "app-new nao existe ($new): publique antes (dotnet publish ... -o app-new)" }

Write-Host '== parando o painel' -ForegroundColor Cyan
Get-Process PainelMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
# sobra do PresentMon do painel (so' os que estao em tools\ deste projeto)
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'PresentMon*' -and $_.Path -like "$root\tools\*" } | Stop-Process -Force
# matar o PresentMon deixa a sessao ETW dele viva e sem consumidor; encerra para nao acumular
& logman stop painelmonitor_fps -ets 2>&1 | Out-Null
Start-Sleep -Seconds 2

Write-Host '== atualizando app\ (robocopy /MIR)' -ForegroundColor Cyan
# Copia por cima em vez de renomear a pasta: o Windows segura um handle em app\ neste PC e o Drive
# criava copias "(1)" ao ver a pasta renomeada.
robocopy $new $app /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /MT:8 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy falhou (codigo $LASTEXITCODE)" }
Remove-Item -LiteralPath $new -Recurse -Force -ErrorAction SilentlyContinue

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
