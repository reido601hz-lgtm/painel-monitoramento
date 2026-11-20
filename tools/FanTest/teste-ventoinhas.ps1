# Abre o FanTest como administrador e mostra o resultado. NAO rode com jogo aberto.
# Uso:  teste-ventoinhas.ps1 -SoLer        (so' lista cabecotes e controles; nao escreve nada)
#       teste-ventoinhas.ps1               (acelera uma por vez a 100% por 8 s)
#       teste-ventoinhas.ps1 -Parar        (para uma por vez por 6 s; so' com CPU fria)
#       teste-ventoinhas.ps1 -Fan 3        (so' a Fan #3)
#       teste-ventoinhas.ps1 -Identificar  (recomendado: cada uma PARA 8 s e depois vai a 100% por 14 s; bip a cada mudanca;
#                                           pergunta o que voce viu e compara os rpm com as FT12 SE do AIO)
# ASCII de proposito (Windows PowerShell 5.1).
param([switch]$SoLer, [switch]$Parar, [switch]$Identificar, [string]$Fan = '', [int]$Duracao = 0)
$ErrorActionPreference = 'Stop'
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Abra como ADMINISTRADOR.' -ForegroundColor Yellow; Read-Host 'Enter para fechar' | Out-Null; exit 1
}
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dll = Join-Path $PSScriptRoot 'bin\Release\net8.0\FanTest.dll'
if (-not (Test-Path -LiteralPath $dotnet)) { throw "dotnet nao encontrado: $dotnet" }
if (-not (Test-Path -LiteralPath $dll)) { throw "FanTest.dll nao encontrado: $dll (compile com dotnet build -c Release)" }
$a = @()
if ($SoLer) { $a += '--so-ler' }
if ($Parar) { $a += '--parar' }
if ($Identificar) { $a += '--identificar' }
if ($Fan) { $a += '--fan'; $a += "$Fan" }   # ex.: -Fan 3  ou  -Fan 3,2 (nessa ordem)
if ($Duracao -gt 0) { $a += '--duracao'; $a += "$Duracao" }
& $dotnet $dll @a
Write-Host ''
Write-Host "Relatorio salvo em $(Join-Path $PSScriptRoot 'bin\Release\net8.0\resultado.txt')" -ForegroundColor Green
Read-Host 'Enter para fechar' | Out-Null
