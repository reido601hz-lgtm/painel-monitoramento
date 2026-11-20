#Requires -RunAsAdministrator
<#
    Instala/atualiza o Painel de Monitoramento do BROK.

    - atualiza a pasta app\ com o conteudo de app-new\ (se existir; robocopy /MIR)
    - cria a regra de firewall (TCP 8080, perfil particular, so rede local)
    - cria a tarefa "PainelMonitor" que inicia no logon com privilegio maximo
    - inicia agora e confere a porta

    Desfazer: desinstalar.ps1
#>

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$app = Join-Path $root 'app'
$new = Join-Path $root 'app-new'
$exe = Join-Path $app 'PainelMonitor.exe'
$iniciar = Join-Path $root 'iniciar.ps1'

$tarefa = 'PainelMonitor'
$regra = 'Painel de monitoramento (TCP 8080)'
$porta = 8080

function Write-Passo($texto) { Write-Host "== $texto" -ForegroundColor Cyan }

Write-Passo 'parando instancia atual'
Get-Process PainelMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

if (Test-Path -LiteralPath $new) {
    # Copia por cima em vez de renomear a pasta: neste PC o Windows segura um handle em app\
    # (mesmo com o processo parado) e o Move-Item falha; alem disso, a pasta do projeto e' sincronizada
    # pelo Drive, que criava centenas de copias "(1)" ao ver a pasta renomeada. O robocopy /MIR
    # so' copia o que mudou.
    Write-Passo 'atualizando a pasta app'
    robocopy $new $app /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /MT:8 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy falhou (codigo $LASTEXITCODE)" }
    Remove-Item -LiteralPath $new -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $exe)) { throw "executavel nao encontrado: $exe" }

Write-Passo 'regra de firewall'
Get-NetFirewallRule -DisplayName $regra -ErrorAction SilentlyContinue | Remove-NetFirewallRule
# Perfil Any porque a rede de casa aparece como Public neste PC; o alcance
# continua limitado ao proprio subnet local (LocalSubnet).
New-NetFirewallRule -DisplayName $regra -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort $porta -Program $exe `
    -Profile Any -RemoteAddress LocalSubnet | Out-Null

Write-Passo 'tarefa de logon'
# Dono da sessao interativa: a tarefa tem de iniciar no logon de quem usa o PC,
# senao o painel nao sobe no proximo boot. O usuario precisa ser administrador:
# basta abrir o PowerShell elevado, o UAC so pede Sim/Nao.
# Se um dia a elevacao for por outra conta, esta linha mantem a tarefa no usuario da sessao.
$usuario = (Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).UserName
if (-not $usuario) { $usuario = "$env:USERDOMAIN\$env:USERNAME" }
Write-Host "   tarefa registrada para: $usuario"

$acao = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument ('-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $iniciar + '"')

$gatilho = New-ScheduledTaskTrigger -AtLogOn -User $usuario

$principal = New-ScheduledTaskPrincipal -UserId $usuario -LogonType Interactive -RunLevel Highest

$config = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $tarefa -Action $acao -Trigger $gatilho -Principal $principal `
    -Settings $config -Description 'Painel de monitoramento do PC no tablet (sem driver vulneravel)' -Force | Out-Null

Write-Passo 'iniciando'
Start-ScheduledTask -TaskName $tarefa
Start-Sleep -Seconds 8

$ouvindo = Get-NetTCPConnection -State Listen -LocalPort $porta -ErrorAction SilentlyContinue
if ($ouvindo) {
    Write-Host "OK: painel ouvindo em http://localhost:$porta (PID $($ouvindo[0].OwningProcess))" -ForegroundColor Green
    Write-Host "Tablet: http://<IP-DO-PC>:$porta"
} else {
    Write-Host "FALHOU: porta $porta nao esta ouvindo. Veja $env:LOCALAPPDATA\PainelMonitor\logs\err.log (ou $root\logs\err.log em instalacao antiga)" -ForegroundColor Red
    exit 1
}
