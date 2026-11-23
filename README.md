# Painel de Monitoramento do BROK

Substituto caseiro do **TRIGONE Remote System Monitor** (RSM), que dependia do
driver `WinRing0` — vulnerável (CVE-2020-14979, leitura/escrita de MSR e portas
de I/O por qualquer processo) e sinalizado pelo Defender.

Aqui o PC roda um servidor .NET que lê os sensores direto e publica um painel
web. O tablet HP antigo (Android 4.2.2, Chrome 42) abre a página no navegador.
**Nenhum driver vulnerável é usado**: a leitura de sensores fica com o
LibreHardwareMonitorLib, que usa o driver **PawnIO** (assinado).

## Como funciona

```
PC (Windows)                                          Tablet HP
┌───────────────────────────────────────────┐         ┌──────────────────┐
│ PainelMonitor.exe (ASP.NET Core 8)        │  HTTP   │ Chrome           │
│  ├─ SensorHub (BackgroundService, 1 s)    │ :8080   │ index.html       │
│  │   ├─ LibreHardwareMonitorLib 0.9.6     │◄────────┤ app.js (ES5)     │
│  │   │   └─ \\.\PawnIO  (exige elevação)  │  JSON   │ polling 1 s      │
│  │   ├─ NVML (nvidia-smi) p/ GPU NVIDIA   │         └──────────────────┘
│  │   └─ GlobalMemoryStatusEx p/ RAM       │
│  └─ wwwroot/ (HTML + CSS + JS estático)   │
└───────────────────────────────────────────┘
```

- O app **se auto-eleva** (`app.manifest` com `requireAdministrator`). Sem
  elevação o `\\.\PawnIO` nega acesso e o painel fica sem temperatura de CPU,
  ventoinhas e discos.
- A coleta roda a cada 1 s em background e guarda o último JSON pronto; cada
  requisição só devolve a string — não há leitura de hardware por requisição.
- O cliente é **ES5 puro** (sem `fetch`, `Promise`, `let`, `const`, arrow
  functions), porque o Chrome do tablet é o 42.

## Estrutura

```
painel-monitoramento\
├─ config.json          nomes amigáveis das leituras (fonte de verdade)
├─ instalar.ps1         instala/atualiza: troca app\, firewall, tarefa, inicia
├─ atualizar.ps1        so atualiza app\ a partir de app-new\ e reinicia a tarefa (admin)
├─ tools\PresentMon\    PresentMon (Intel, MIT) usado pelo FPS + LEIAME.txt com versao e SHA-256
├─ iniciar.ps1          wrapper chamado pela tarefa (roda escondido, grava log)
├─ desinstalar.ps1      remove tarefa, processo e regra de firewall
├─ app\                 build publicado em uso (app-old\ = versão anterior)
├─ logs\                (legado) os logs agora ficam em %LOCALAPPDATA%\PainelMonitor\logs
└─ src\PainelMonitor\   código-fonte
   ├─ Program.cs        endpoints e porta
   ├─ SensorHub.cs      coleta (LHM + NVML + RAM) e nomes
   ├─ Nvml.cs           P/Invoke da NVML
   ├─ FrameHub.cs       FPS/frametime do jogo (PresentMon filho) -> /api/fps
   ├─ app.manifest      requireAdministrator
   └─ wwwroot\          index.html, estilo.css, app.js
```

## Instalação

Pré-requisito para compilar: .NET 8 SDK (nesta máquina em
`%LOCALAPPDATA%\Microsoft\dotnet`, fora do PATH).

```powershell
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
& $dotnet publish '.\src\PainelMonitor\PainelMonitor.csproj' `
    -c Release -r win-x64 --self-contained true -o '.\app-new'
```

Depois, num PowerShell **como administrador**:

```powershell
.\instalar.ps1
```

O `instalar.ps1`:

1. para a instância atual;
2. troca `app\` pelo conteúdo de `app-new\` (a anterior vai para `app-old\`);
3. recria a regra de firewall **TCP 8080, entrada, perfil Any, origem
   LocalSubnet** (perfil Any porque a rede de casa está categorizada como
   *Public* neste PC; o alcance continua restrito ao próprio subnet);
4. recria a tarefa agendada **PainelMonitor** (no logon, privilégio máximo,
   reinício automático em caso de falha);
5. inicia e confere que a porta está ouvindo.

Testar antes de trocar a instalação: rode o exe de `app-new\` com
`PAINEL_URL=http://0.0.0.0:8081` e abra `http://127.0.0.1:8081`.

Depois, no tablet: **http://<IP-DO-PC>:8080**.

## Desinstalar

PowerShell como administrador:

```powershell
.\desinstalar.ps1
```

Remove a tarefa, o processo e a regra de firewall. **Não** apaga a pasta do
projeto nem os logs.

## FPS e frametime do jogo (2026-11-18)

Com um jogo em primeiro plano, o card "Núcleos" do tablet vira "Frametime": FPS, 1% low, CPU/GPU
ocupadas e uma linha de 12 s (3 px). Sem jogo, volta aos núcleos.

- **Fonte:** `FrameHub.cs` roda o console oficial do **PresentMon** (Intel, MIT; `tools\PresentMon\`,
  ver o `LEIAME.txt` de lá) como processo filho. Ele só lê eventos ETW de apresentação de quadros
  do Windows (não injeta nada nos jogos) e imprime um CSV por quadro; o painel agrega.
- **Endpoint:** `GET /api/fps` -> `{"ativo":false,"coletando":true}` sem jogo; com jogo
  `{"ativo":true,"jogo","fps","fps1","ft","cpu","gpu","modo","serie":[120 valores]}`. A série é o
  máximo de cada 100 ms dos últimos 12 s (os picos não somem). O tablet consulta a cada 500 ms com
  jogo e a cada 2 s sem jogo.
- **Detecção do jogo:** processo com >= `minFps` (25) nos últimos 3 s, fora da lista de exclusão
  (`dwm`, `msedgewebview2`, navegadores, launchers...), e que seja o da janela em primeiro plano; na falta
  dele, o de maior taxa em `Hardware: Independent Flip` (tela cheia). Alt-tab segura o jogo por 10 s.
- **`config.json`, seção `fps`:** `ativo` (desliga tudo), `minFps`, `excluir` (nomes a ignorar além da
  lista padrão), `exe` e `sha256` (outra versão do PresentMon).
- **Segurança:** o painel é elevado e o `.exe` fica numa pasta gravável pelo usuário. O SHA-256 é
  conferido antes de cada execução (diferente = não executa) e o filho morre junto com o painel (Job
  Object). A assinatura (Intel) foi conferida ao baixar. `/api/fps` expõe o nome do jogo à rede local.
- **Custo:** o PresentMon fica rodando o tempo todo (captura cronometrada de 1 h, reiniciada sozinha).
  Medido com o Fortnite aberto (2026-11-18): PresentMon ~1,6% de 1 núcleo e ~16 MB; o painel ~0,7%.
  Sem jogo, o PresentMon fica em ~0%.
- **Delta Force (anti-cheat, 2026-11-18 21:33):** detectado sozinho ("DeltaForce"), ~155-163 fps, o card
  Frametime apareceu no tablet; PresentMon ~0,65% de 1 núcleo e 5 MB; sem erro nem bloqueio da coleta ETW. Não
  há garantia de que o anti-cheat do jogo aceite qualquer ferramenta de monitoramento: o PresentMon só lê
  eventos do Windows, mas o risco de conta é de quem joga.
- **Validado ao vivo (2026-11-18 20:29, Fortnite):** o card Frametime apareceu no tablet com 134-175 fps,
  1% low, CPU/GPU em ms e a linha de 12 s. A escala da linha usa o percentil 95 (um pico isolado sai
  cortado no topo e não achata o resto).
- **Sessão ETW órfã (armadilha):** matar o PresentMon na mão deixa a sessão `painelmonitor_fps` viva e
  sem consumidor, e **uma sessão órfã impediu sessões novas de receber eventos** (o painel ficou sem
  FPS por horas em 2026-11-18). O painel encerra a sessão ao parar e o `atualizar.ps1` também; se
  algum teste for interrompido, rode como administrador `logman stop <nome da sessão> -ets`
  (`logman query -ets` lista as sessões).
- **Atualizar o painel sem refazer firewall/tarefa:** publicar em `app-new` e rodar `atualizar.ps1`
  como administrador (para o painel, copia por cima com robocopy e reinicia a tarefa).
  O `instalar.ps1` só quando a regra de firewall ou a tarefa precisarem ser recriadas.

## Descobrir qual ventoinha é qual (FanTest, 2026-11-18)

O LibreHardwareMonitor lista controles (`Control`) para `Fan #1..#6` do SuperIO ITE IT8689E da placa, o que
indica que dá para mandar uma ventoinha acelerar ou parar por software; se a placa Gigabyte aceita a escrita só
se sabe testando. `tools\FanTest` (uso manual, como administrador, **fora do painel**: o painel não aceita
comandos de controle) faz isso uma ventoinha por vez e restaura o padrão:

- `teste-ventoinhas.ps1 -SoLer`: só lista cabeçotes, rpm e modo dos controles (não escreve nada).
- sem argumentos: acelera a 100% por 8 s cada (Fan #2, #3, #4); `-Parar`: 0% por 10 s cada; `-Fan N` ou
  `-Fan 3,2` (nessa ordem): só essas. **`-Identificar` (recomendado):** cada cabeçote para por 8 s e depois vai a
  100% por 14 s, com bip a cada mudança, espera um Enter antes de começar, pergunta o que você viu e grava a
  resposta no relatório; compara o mínimo/máximo medido com a ficha das FT12 SE (500 a 2150 rpm).
  Ventoinha PWM de AIO não para em 0% (cai para ~500 rpm); as de gabinete costumam parar.
  **Não feche a janela com o X enquanto uma ventoinha estiver parada** (só o Ctrl+C restaura o padrão).
- **A Fan #1 (bomba do AIO) nunca é tocada.** Recusa rodar com CPU acima de 40% de carga ou 60 °C e restaura
  tudo (`SetDefault`) ao fim, em caso de erro e no Ctrl+C. **Não rodar com jogo aberto.**
- Relatório em `tools\FanTest\bin\Release\net8.0\resultado.txt`. Compilar: `dotnet build -c Release`
  (o `.exe` não acha o .NET do usuário; usar `dotnet FanTest.dll`, que é o que o `.ps1` faz).

## Modo relógio: PC desligado (2026-11-22)

Quando o servidor fica **15 s sem responder** (PC desligado, em repouso ou painel fora do ar), a página do
tablet vira um **relógio digital minimalista em tela cheia** e **volta sozinha ao painel** no primeiro poll que
der certo. Visual (pedido do usuário em 2026-11-22): fundo **preto**, tudo em **branco**, sem barra de segundos
e sem segmentos apagados; `HH:MM` grande centralizado (os dois pontos ficam no eixo da tela, x = 512), segundos
pequenos à direita, uma linha de data ("Domingo, 22 de novembro de 2026") e uma nota discreta em cinza no rodapé
("PC desligado ou painel fora do ar · hora sincronizada em dd/mm às hh:mm"). O grupo hora + data fica no meio da
altura. Só `wwwroot` mudou (HTML, CSS e `app.js`, bloco "modo relógio"); o servidor não foi tocado.

- **De onde vem a hora:** o RTC do tablet está em 1970 (sem bateria), então a hora é a do PC aprendida enquanto
  ele estava ligado (`offsetMs`, a mesma que o relógio do topo do painel usa, UTC-3 fixo). O relógio do sistema
  do tablet continua contando, então a hora segue certa **até o tablet reiniciar**; a deriva do cristal é de
  poucos segundos por dia (não medida). O rodapé mostra até quando a hora veio do PC. Sem sincronização mostra
  "sem hora do PC" e nenhum dígito aceso (hora errada é pior que nenhuma).
- **Dígitos em SVG de 7 segmentos**, não em texto: não dependem das fontes do Android 4.2. Redesenha na virada
  de cada segundo da hora do PC.
- **Guarda de 6 s em cada requisição** do poll: o WebKit do Android 4.2 pode ignorar `xhr.timeout`, e com o PC
  desligado a conexão ficaria pendurada por minutos. Com a guarda o relógio entra em ~15 a 27 s.
- **Limite conhecido:** só aparece se a página **já estava carregada** quando o PC desligou. Se o tablet
  reiniciar com o PC desligado, o PainelKiosk (carrega a URL uma vez, sem retry nem página local; o fonte não
  está no PC) mostra a página de erro do WebView e, de qualquer forma, o relógio do tablet estaria em 1970. O
  Vigia reabre o app quando o PC volta. Corrigir isso exigiria refazer o APK com uma página local de fallback.
- **Para o tablet ganhar a mudança** é preciso recarregar a página (reiniciar o app PainelKiosk por ADB ou
  reiniciar o tablet): a página já aberta continua com o `app.js` antigo.
- **Testado** no navegador a 1024x600 simulando o servidor fora do ar (requisição que falha e requisição que
  nunca responde) e **no tablet de verdade** em 2026-11-22 com `tools\teste-relogio-tablet.ps1` (para o painel e
  bloqueia a 8080 para o IP do tablet, sem resposta, como PC desligado; depois desfaz). Resultado, medido por
  fotos via ADB: 21 s depois da queda ainda o painel com o chip "Sem conexão"; aos 36 s o relógio, com hora e
  data certas (o RTC do tablet está em 1970, a hora veio do offset) e os dois pontos piscando; o painel voltou
  sozinho em até 12 s depois do servidor subir. A versão minimalista (preto e branco, hora centralizada) foi
  refeita e testada do mesmo jeito no tablet: relógio já na tela 30 s depois da queda, hora certa, painel de volta
  em até 12 s. Detalhe do 7 segmentos: sem os segmentos apagados, o "1" ficava colado à
  direita da sua casa e abria um vão ("2 1:27"); a pedido do usuário o "1" agora é deslocado para o meio da casa
  (`setDigito`: `translate` nos segmentos b e c quando o valor é 1). Esse ajuste foi conferido só no navegador a
  1024x600, não no tablet. **Não testado** com o PC desligado de verdade (o teste simula isso do lado do
  servidor) nem depois de muitas horas (deriva do relógio do tablet).
- **Cache:** o servidor manda `ETag` e `Last-Modified`, sem `Cache-Control`. O tablet pegou o `app.js` novo logo
  depois de reiniciar o app, mas se algum dia ele insistir no JS antigo, a saída é `Cache-Control: no-cache` nos
  arquivos estáticos (`UseStaticFiles` no `Program.cs`).

## Cores das temperaturas (2026-11-18)

Verde = normal, **laranja = atenção**, **vermelho = perto do limite**. Os limites são por tipo de sensor
(`LIM_TEMP` em `app.js`), não um valor só para tudo. Pesquisa na internet em 2026-11-18; o que é limite de
fabricante e o que é escolha minha:

| Sensor | Laranja a partir de | Vermelho a partir de | Base | Certeza |
|---|---|---|---|---|
| CPU (anel) | 70 °C | 90 °C | [Intel ARK](https://www.intel.com/content/www/us/en/products/sku/236799/specifications.html): i5-14600K, Tjunction 100 °C. A Intel diz que ficar perto do máximo em carga sustentada [não é necessariamente problema](https://www.intel.com/content/www/us/en/support/articles/000005597/processors.html) e não publica faixa "normal". Os 70/90 são escolha minha (10 °C de folga). | limite: certo; cores: escolha |
| GPU (anel) | 75 °C | 85 °C | Máximo da RTX 4060 Ti: [90 °C](https://amp.hothardware.com/reviews/nvidia-geforce-rtx-4060-ti-gpu-review?page=1) (veja também [este artigo](https://bottleneckpc.com/blog/gpu-temperature-limits)); em carga a faixa típica é 60-75 °C. Um guia isolado cita 83 °C e não bate com as outras fontes. | limite: provável; cores: escolha |
| VRM MOS | 80 °C | 100 °C | (laranja baixado de 85 para 80 em 2026-11-18: jogando Delta Force o VRM chegou a 82 °C, bem acima dos 55-65 °C dos testes de referência.) Sem limite de fabricante achado. Testes de placas B660 medem [55-65 °C em carga total](https://www.hwcooling.net/en/asus-tuf-gaming-b660-plus-wifi-d4-entry-level-for-core-i9-too/5/). Controladoras de VRM costumam limitar por volta de 100-125 °C (não verificado). | chute |
| Chipset (PCH) | 70 °C | 90 °C | Sem número da B660. Chipsets Intel 495/595/700 têm [limite de junção ~110 °C](https://community.intel.com/t5/Processors/TjMAX-is-set-to-115-C-by-default/m-p/1430468) (relato indireto). | chute |
| RAM DDR4 | 65 °C | 80 °C | Limite de operação [85 °C de case](https://www.omo-ic.com/pdf/693014/MTA9ASF1G72PZ-2G3B1/P16-P18.html) (datasheets Micron); de 85 a 95 °C só com refresh dobrado. | limite: certo; cores: escolha |
| Placa (PCIe x16, Sistema 1/2) | 55 °C | 70 °C | Sensores de placa, sem limite oficial. | chute |
| SSD SATA (Kingston A400 etc.) | 55 °C | 65 °C | [Kingston: operação 0-70 °C](https://www.kingston.com/datasheets/SA400S37_us.pdf); 5 °C de folga. | limite: certo |
| NVMe | 65 °C | 75 °C | Throttling costuma começar em [70-80 °C](https://www.seagate.com/gb/en/blog/does-nvme-ssd-need-heatsink/); exemplo de [fabricante: 70 °C sem queda, queda de escrita aos 76 °C](https://docs.oracle.com/cd/E65386_01/html/E65389/gokfv.html). Depende do modelo. | provável |
| HDD (Seagate ST...) | 45 °C | 55 °C | [Seagate: operação 5-50 °C, modelos novos até 60 °C](https://www.seagate.com/support/kb/what-is-the-normal-operating-temperature-for-seagate-disk-drives-193771en/). | limite: certo |

- O tipo do disco vem do nome: `ST` + número = HDD; "NVMe" no nome = NVMe; o resto (inclusive o
  "WALRAM 512G") é tratado como SSD SATA, com limites mais rigorosos. Se um disco for NVMe, os limites dele
  ficam mais folgados do que o painel usa.
- Para mudar um limite, edite `LIM_TEMP` no `app.js` e mantenha as duas cópias do `wwwroot` iguais.

## Mais itens tirados (2026-11-18)

Ventoinhas: as duas da GPU viram uma linha "GPU" (rpm da mais rápida; some quando a GPU está parada e o
servidor não informa rpm). Temperaturas: sem "CPU (socket)". Rede e discos: só os 3 discos mais quentes
(mostrados em ordem de nome) e uma linha de Ethernet com `↓` e `↑` (K = KB/s, M = MB/s). Sob os anéis: sem o
nome do processador/placa, só a temperatura colorida.

## Tipografia no tablet (2026-11-18)

O PainelKiosk amplia a página 1,72x (`innerWidth=594`, `devicePixelRatio=1`) e a
renderização é nativa (medido), mas texto de 14 a 15 px reais em 7" a 1024x600 sai suave.
Estilo escolhido pelo usuário: texto principal **22 px branco** (Roboto), valores em
monospace bold, anéis dos gauges com traço grosso e ponta reta. Consequências no layout:

- Temperaturas: uma coluna com 6 itens, na ordem de `TEMP_ORDEM` em `app.js` (os demais não
  aparecem). Para mostrar outros, edite `TEMP_ORDEM`/`TEMP_MAX`.
- Rede e discos: até 5 discos (nome curto, sem repetidos, ordem fixa) + 2 linhas de rede, 20 px. Os discos
  mostram só a temperatura (o % de uso foi tirado em 2026-11-18 por poluir). Temperaturas e discos usam
  "46°C", sem espaço antes do grau (o espaço da fonte monoespaçada abria um buraco).
- Nenhum texto abaixo de 16 px. `style.css` documenta o tamanho de cada bloco.
- **Itens tirados por poluir (2026-11-18):** subtítulo "HARDWARE SUITE", selos "20 threads" e "8 GB",
  "ligado há ...", potência (W) e clock (MHz) dos anéis (ficou só a temperatura) e o chip de status, que só
  aparece quando há falha ("Sem conexão com o servidor"). Os elementos continuam no HTML, escondidos
  (`display:none`), porque o `render()` do `app.js` ainda preenche os ids deles.
- **Ventoinhas (teste de carga, 2026-11-18):** com a CPU de 8% a 100% (76 °C) a rotação de "CPU" ficou fixa em
  ~3330 rpm (é a bomba), "Sistema 1" subiu de ~1200 para ~1920 rpm (segue a temperatura da CPU: radiador),
  "Sistema 2" ficou em 1342 rpm e "Sistema 3" em ~800 rpm **só com carga de CPU**; jogando Delta Force
  (CPU 53%, GPU 79%) subiram para 1541 e 948 rpm, ou seja, não são fixas: seguem a temperatura da placa/GPU,
  não a da CPU. Os nomes ficam em `config.json`
  (`ventoinhas`, `Fan #1..#4`) e só valem depois de reiniciar o painel (`reiniciar-painel.ps1`, como
  administrador). **Mapa físico confirmado em 2026-11-18** com o `FanTest -Identificar` (cada cabeçote
  parado/acelerado por software, usuário olhando o gabinete): Fan #1 "Bomba" (bomba do AIO, 3342 rpm fixa; não
  testada de propósito), Fan #2 "Radiador push" (a que empurra/puxa o ar de fora para o radiador; **para por
  completo em 0%**, máx. ~1934 rpm), Fan #3 "Radiador pull" (a que puxa o ar do radiador; **só cai para ~500
  rpm em 0%**, máx. ~2157 rpm, o que bate com a ficha das FT12 SE do DeepCool MYSTIQUE 240: 500 a 2150 rpm),
  Fan #4 "Controladora" (ventoinhas ligadas à controladora; para por completo, máx. ~1849 rpm). Os nomes
  antigos ("Radiador 1", "Controladora" na Fan #3, "Radiador 2") eram palpite e estavam errados. Fan #2 e #4
  têm perfil parecido (param em 0%) e a #3 não: [Provável] a #2 é de outro modelo que não as FT12 SE.

**Ao mudar o `wwwroot`, mantenha as 3 cópias iguais** (`app\wwwroot`, `app-new\wwwroot`,
`src\PainelMonitor\wwwroot`): o `instalar.ps1` copia `app-new` por cima de `app`.

## Nomes dos sensores (`config.json`)

O LibreHardwareMonitor expõe o SuperIO ITE IT8689E como `Temperature #1..#6` e
`Fan #1..#4`. Os nomes de verdade foram levantados no HWiNFO64 8.41 (mesma
placa GIGABYTE B660M GAMING DDR4), lendo a memória compartilhada **uma vez**,
sem dependência em runtime. A ordem dos registradores é:

| LHM          | HWiNFO     | Nome no painel  |
|--------------|------------|-----------------|
| Temperature #1 | System1  | Sistema 1       |
| Temperature #2 | PCH      | Chipset (PCH)   |
| Temperature #3 | CPU      | CPU (socket)    |
| Temperature #4 | PCIEX16  | PCIe x16        |
| Temperature #5 | VRM MOS  | VRM MOS         |
| Temperature #6 | System2  | Sistema 2       |

Edite `config.json` e reinicie a tarefa (`Restart-ScheduledTask` como admin, ou
`Stop-Process PainelMonitor` e iniciar de novo) para aplicar. O app procura o
arquivo na pasta do executável, um e dois níveis acima — por isso ele fica na
raiz do projeto e não é copiado para `app\`. Sem o arquivo, o painel mostra os
nomes genéricos do LHM.

## Endpoints

| Rota           | Para quê                                                        |
|----------------|-----------------------------------------------------------------|
| `/`            | painel web                                                      |
| `/api/metrics` | JSON com CPU/GPU/RAM/temps/ventoinhas/discos/rede (1 s)         |
| `/api/raw`     | lista crua de sensores + nomes carregados (diagnóstico)         |
| `/api/debug`   | dump sob demanda, com duas passadas de `Update` (diagnóstico)   |
| `/api/health`  | `{ ok, age }` — se a coleta está viva                           |

Porta padrão `8080`, sobrescrita pela variável `PAINEL_URL` (ex.:
`http://0.0.0.0:8081`).

## Logs e diagnóstico

- Os logs ficam em `%LOCALAPPDATA%\PainelMonitor\logs\` (desde 2026-11-18), **fora do Drive**:
  a pasta do projeto era sincronizada com o Google Drive, que reenviava o `out.log` inteiro a cada gravação. Em
  instalação antiga ainda estavam em `logs\` na pasta do projeto (sem uso depois da troca;
  pode apagar).
- `out.log`: início/porta, `SensorHub` e avisos/erros do ASP.NET; reiniciado a cada início
  (`out-anterior.log` guarda o anterior). O log de **cada requisição** foi desligado
  (`Program.cs`, filtro `Microsoft.AspNetCore` = Warning): o painel é consultado a cada 1 s e
  gerava ~65 MB/dia. Para confirmar que o tablet está consultando, veja a conexão
  `ESTABLISHED` do `<IP-DO-TABLET>` na porta 8080 (`Get-NetTCPConnection -LocalPort 8080`).
- `err.log`: saída de erro do processo.
- Temperatura de CPU `0` / lista de ventoinhas vazia ⇒ o processo **não** está
  elevado. Confira `# elevado=True` na primeira linha de `/api/raw`.
- Tablet não conecta ⇒ confira `Get-NetConnectionProfile` (se a rede estiver
  como *Public*, a regra precisa ser `-Profile Any`) e se a porta está
  `LISTENING` em `0.0.0.0`.

## Consumo de recursos (2026-11-20)

Medido com `Win32_PerfFormattedData_PerfProc_Process`, 60 amostras de ~1 s, 20 CPUs lógicas, tablet conectado:

| | Antes (Server GC) | Depois (`ServerGarbageCollection=false`) |
|---|---|---|
| Memória privada do `PainelMonitor` | ~285-300 MB | ~57-78 MB |
| Threads / handles | 63-65 / ~820 | 24-32 / ~600 |
| CPU do `PainelMonitor` (média, % do PC) | 0,11% (sem jogo) | 0,04% (com jogo a 60 fps) |
| CPU do PresentMon (média, % do PC) | 0,01% (sem jogo) | 0,05% (com jogo a 60 fps) |

- O Web SDK liga o **Server GC** (um heap por núcleo; 20 aqui) e o painel ficava em ~300 MB. O `.csproj` agora
  tem `<ServerGarbageCollection>false</ServerGarbageCollection>`. **O nome certo é `ServerGarbageCollection`**:
  com `ServerGarbageCollector` o build ignora sem avisar e o `PainelMonitor.runtimeconfig.json` continua com
  `"System.GC.Server": true` (confira esse arquivo em `app\` depois de publicar).
- Não medido: jogo pesado (centenas de fps, onde o PresentMon processa mais quadros), efeito no FPS do jogo e
  custo das consultas à GPU (NVML).

## Pendências conhecidas

- O driver do RSM (`R0RemoteSystemMonitorServer`) segue instalado e em execução
  até o próximo reboot.
- Não há exclusão de antivírus nem atualização para o RSM 3.98.

## Licença e componentes de terceiros

Código deste repositório sob **licença MIT** (arquivo `LICENSE`). Usa o pacote NuGet
**LibreHardwareMonitorLib** (MPL 2.0) e, para o FPS, o **PresentMon** da Intel (MIT).

O `.exe` do PresentMon **não** vem neste repositório. Baixe a versão indicada em
`tools\PresentMon\LEIAME.txt` (link da release oficial e SHA-256) e coloque em `tools\PresentMon\`.
O painel confere o hash antes de executar; sem o arquivo ele registra um aviso e segue sem FPS.

Este projeto foi feito para um PC e um tablet específicos (placa Gigabyte B660M com ITE IT8689E, tablet
HP com Android 4.2.2); os nomes das leituras em `config.json` e as notas do README refletem esse hardware.
