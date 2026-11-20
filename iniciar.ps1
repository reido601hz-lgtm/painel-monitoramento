# executa o painel escondido, gravando log (usado pela tarefa PainelMonitor)

$root = $PSScriptRoot
$app = Join-Path $root 'app'
# Logs FORA do Drive: a pasta do projeto e' sincronizada e o Drive reenviava o out.log inteiro
# a cada gravacao. Sem LOCALAPPDATA, cai para a pasta do projeto.
$logs = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'PainelMonitor\logs' } else { Join-Path $root 'logs' }
$exe = Join-Path $app 'PainelMonitor.exe'

New-Item -ItemType Directory -Force -Path $logs | Out-Null

# rotaciona o log antigo para nao crescer sem limite
$out = Join-Path $logs 'out.log'
$err = Join-Path $logs 'err.log'
if (Test-Path -LiteralPath $out) { Move-Item -LiteralPath $out -Destination (Join-Path $logs 'out-anterior.log') -Force }
if (Test-Path -LiteralPath $err) { Move-Item -LiteralPath $err -Destination (Join-Path $logs 'err-anterior.log') -Force }

Start-Process -FilePath $exe -WorkingDirectory $app `
    -RedirectStandardOutput $out -RedirectStandardError $err `
    -WindowStyle Hidden
