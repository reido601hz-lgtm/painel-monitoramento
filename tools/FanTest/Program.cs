using System.Text;
using LibreHardwareMonitor.Hardware;

// FanTest: descobre qual ventoinha esta em qual cabecote da placa-mae (ITE IT8689E, Gigabyte B660M).
//
//   FanTest --so-ler          so' lista cabecotes, rpm e modo dos controles (nao escreve nada)
//   FanTest --identificar     em cada cabecote (Fan #2, #3, #4): 0% por 8 s e depois 100% por 14 s, anotando
//                             a rotacao minima e a maxima, e compara com as ventoinhas FT12 SE do AIO
//                             DeepCool MYSTIQUE 240 (500 a 2150 rpm +-10%)
//   FanTest                   acelera a 100% uma por vez (8 s)
//   FanTest --parar           PARA (0%) uma por vez (6 s)
//   FanTest --fan 3           so' a Fan #3      FanTest --duracao 12   duracao (3 a 25 s; parada nunca passa de 10 s)
//
// Seguranca:
//  - a Fan #1 (bomba do AIO) NUNCA e' tocada;
//  - recusa rodar com CPU acima de 40% de carga ou 60 C (feche o jogo e espere esfriar);
//  - tudo que foi alterado volta ao modo padrao (SetDefault) ao fim de cada ensaio, em caso de erro e no Ctrl+C.
// Precisa de administrador (driver PawnIO). Relatorio em resultado.txt na pasta de execucao.

var soLer = args.Contains("--so-ler");
var parar = args.Contains("--parar");
var identificar = args.Contains("--identificar");
var soLista = new List<int>();   // --fan 3  ou  --fan 3,2 (a ordem informada e' a ordem do teste)
var duracaoArg = 0;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--fan")
        foreach (var parte in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(parte, out var n) && !soLista.Contains(n)) soLista.Add(n);
    if (args[i] == "--duracao" && int.TryParse(args[i + 1], out var d)) duracaoArg = Math.Clamp(d, 3, 25);
}

var relatorio = new StringBuilder();
void Log(string s) { Console.WriteLine(s); relatorio.AppendLine(s); }

var pc = new Computer { IsMotherboardEnabled = true, IsCpuEnabled = true };
pc.Open();

IEnumerable<IHardware> Todos(IEnumerable<IHardware> hs)
{
    foreach (var h in hs)
    {
        yield return h;
        foreach (var s in Todos(h.SubHardware)) yield return s;
    }
}
void Atualizar() { foreach (var h in Todos(pc.Hardware)) h.Update(); }

var alterados = new List<IControl>();
void RestaurarTudo()
{
    foreach (var c in alterados)
    {
        try { c.SetDefault(); } catch (Exception ex) { Console.WriteLine("falha ao restaurar: " + ex.Message); }
    }
    alterados.Clear();
}
Console.CancelKeyPress += (_, _) => { RestaurarTudo(); };

try
{
    Atualizar();
    var superio = Todos(pc.Hardware).FirstOrDefault(h => h.HardwareType == HardwareType.SuperIO);
    if (superio is null) { Log("SuperIO nao encontrado (o painel precisa estar com o driver PawnIO funcionando)."); return 1; }
    Log($"{DateTime.Now:HH:mm:ss}  SuperIO: {superio.Name}");

    var cpu = Todos(pc.Hardware).FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
    float carga = cpu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name == "CPU Total")?.Value ?? 0;
    float temp = cpu?.Sensors.Where(s => s.SensorType == SensorType.Temperature && (s.Name.Contains("Package") || s.Name.Contains("Max"))).Select(s => s.Value ?? 0).DefaultIfEmpty(0).Max() ?? 0;
    Log($"CPU: carga {carga:N0}%  temperatura {temp:N0} C");

    Log("");
    Log("Cabecote   rpm      controle: modo / minimo-maximo");
    foreach (var s in superio.Sensors.Where(s => s.SensorType == SensorType.Fan).OrderBy(s => s.Name))
    {
        var c = superio.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Control && x.Name == s.Name)?.Control;
        Log($"{s.Name,-9} {s.Value ?? 0,6:N0}    " + (c is null ? "sem controle" : $"{c.ControlMode} / {c.MinSoftwareValue:N0}-{c.MaxSoftwareValue:N0}"));
    }
    if (soLer) { Log(""); Log("(--so-ler: nada foi escrito)"); return 0; }

    if (carga > 40 || temp > 60)
    {
        Log("");
        Log($"RECUSADO: CPU com {carga:N0}% de carga / {temp:N0} C. Feche o jogo, espere esfriar (abaixo de 40% e 60 C) e rode de novo.");
        return 2;
    }
    if (soLista.Contains(1)) { Log("A Fan #1 e' a bomba do AIO e nunca e' tocada."); return 3; }
    var alvos = soLista.Count == 0 ? new List<int> { 2, 3, 4 } : soLista.Where(n => n is >= 2 and <= 4).ToList();
    if (alvos.Count == 0) { Log("Nenhum cabecote valido em --fan (use 2, 3 e/ou 4)."); return 3; }

    // um ensaio: leva o cabecote ao nivel (%) por 'duracao' s e restaura; devolve antes/min/max/depois (rpm)
    (float antes, float min, float max, float depois)? Ensaio(int n, float nivel, int duracao)
    {
        var nome = $"Fan #{n}";
        var sensor = superio.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Name == nome);
        var ctl = superio.Sensors.FirstOrDefault(c => c.SensorType == SensorType.Control && c.Name == nome)?.Control;
        if (sensor is null || ctl is null) { Log($"{nome}: sem sensor ou controle, pulando."); return null; }

        var basal = new List<float>();
        for (var k = 0; k < 3; k++) { Atualizar(); basal.Add(sensor.Value ?? 0); Thread.Sleep(700); }
        var rpm0 = basal.Average();
        if (rpm0 < 100) { Log($"{nome}: sem rotacao no momento ({rpm0:N0} rpm), pulando."); return null; }
        if (nivel < 100) duracao = Math.Min(duracao, 10);

        Log("");
        for (var c = 5; c > 0; c--) { Console.Write($"\r{nome} ({rpm0:N0} rpm) vai a {nivel:N0}% em {c} s... OBSERVE qual ventoinha muda   "); Thread.Sleep(1000); }
        Console.WriteLine();

        var minimo = float.MaxValue; var maximo = 0f;
        alterados.Add(ctl);
        ctl.SetSoftware(nivel);
        try { Console.Beep(1000, 250); } catch { }
        Log($"{DateTime.Now:HH:mm:ss}  {nome}: {nivel:N0}% por {duracao} s  <<< AGORA");
        for (var t = 1; t <= duracao; t++)
        {
            Thread.Sleep(1000); Atualizar();
            var v = sensor.Value ?? 0; minimo = Math.Min(minimo, v); maximo = Math.Max(maximo, v);
            Log($"   +{t,2} s  {v,6:N0} rpm");
        }
        ctl.SetDefault();
        alterados.Remove(ctl);
        try { Console.Beep(600, 250); } catch { }
        Log($"{DateTime.Now:HH:mm:ss}  {nome}: restaurado ao modo padrao");

        var volta = 0f;
        for (var t = 0; t < 15; t++)
        {
            Thread.Sleep(1000); Atualizar(); volta = sensor.Value ?? 0;
            if (Math.Abs(volta - rpm0) <= rpm0 * 0.2) break;
        }
        Log($"{nome} a {nivel:N0}%: antes {rpm0:N0} | durante min {minimo:N0} max {maximo:N0} | depois {volta:N0} rpm");
        return (rpm0, minimo, maximo, volta);
    }

    Log("");
    if (identificar)
    {
        Log("MODO IDENTIFICAR: cada cabecote vai a 0% (8 s) e depois a 100% (14 s). Fan #1 (bomba) fica intocada.");
        Log("Referencia: ventoinhas FT12 SE do AIO DeepCool MYSTIQUE 240 = 500 a 2150 rpm (+-10%); bomba 3400 rpm (+-10%).");
        Log("");
        Console.WriteLine("Fique ao lado do gabinete, de olho no radiador e nas ventoinhas. Cada mudanca e' anunciada por um BIP.");
        Console.WriteLine($"Ordem: {string.Join(", depois ", alvos.Select(a => $"Fan #{a}"))}. Em cada uma: PARA por 8 s, volta ao normal, e depois vai a 100% por 14 s.");
        Console.Write("Tecle Enter quando estiver pronto (Ctrl+C cancela e restaura tudo): ");
        Console.ReadLine();
        var resumo = new List<string>();
        foreach (var n in alvos)
        {
            var baixo = Ensaio(n, 0f, 8);
            Thread.Sleep(3000);
            var alto = Ensaio(n, 100f, 14);
            Console.WriteLine();
            Console.Write($"Fan #{n}: qual ventoinha PAROU e qual ACELEROU? (descreva e tecle Enter; so Enter se nao viu): ");
            var obs = (Console.ReadLine() ?? "").Trim();
            relatorio.AppendLine($"OBSERVACAO Fan #{n}: {(obs.Length == 0 ? "(nao viu)" : obs)}");
            if (baixo is null || alto is null) { resumo.Add($"Fan #{n}: sem dados"); continue; }
            var min0 = baixo.Value.min; var max100 = alto.Value.max;
            var bateMin = min0 >= 450 && min0 <= 700;   // 500 rpm -10%/+40% (folga para a leitura)
            var bateMax = max100 >= 1900 && max100 <= 2400;   // 2150 +-10%
            var veredito = (bateMin, bateMax, min0 < 50) switch
            {
                (true, true, _) => "BATE com as ventoinhas FT12 SE do AIO (minimo ~500 e maximo ~2150)",
                (_, true, _) => "maximo bate com as FT12 SE (~2150), minimo nao",
                (true, _, _) => "minimo bate com as FT12 SE (~500), maximo nao",
                (_, _, true) => "PARA por completo em 0%: nao e' PWM de minimo 500; provavel ventoinha de gabinete/controladora",
                _ => "nao bate com as FT12 SE do AIO"
            };
            resumo.Add($"Fan #{n}: base {baixo.Value.antes:N0} | 0% -> min {min0:N0} | 100% -> max {max100:N0}  =>  {veredito}");
        }
        Log("");
        Log("=== RESUMO ===");
        foreach (var l in resumo) Log(l);
    }
    else
    {
        var nivel = parar ? 0f : 100f;
        Log(parar ? "MODO PARAR (0%). Fan #1 (bomba) fica intocada." : "MODO ACELERAR (100%). Fan #1 (bomba) fica intocada.");
        Console.WriteLine($"Ordem: {string.Join(", depois ", alvos.Select(a => $"Fan #{a}"))}. Cada mudanca e' anunciada por um BIP.");
        if (parar) Console.WriteLine("Atencao: ventoinha PWM de AIO nao para em 0%, so' cai para o minimo (~500 rpm); as de gabinete costumam parar de vez.");
        Console.Write("Fique ao lado do gabinete e tecle Enter quando estiver pronto (Ctrl+C cancela e restaura tudo): ");
        Console.ReadLine();
        foreach (var n in alvos)
        {
            Ensaio(n, nivel, duracaoArg > 0 ? duracaoArg : (parar ? 10 : 8));
            Console.WriteLine();
            Console.Write($"Fan #{n}: o que mudou (qual ventoinha parou/desacelerou)? (descreva e tecle Enter; so Enter se nao viu): ");
            var obs = (Console.ReadLine() ?? "").Trim();
            relatorio.AppendLine($"OBSERVACAO Fan #{n}: {(obs.Length == 0 ? "(nao viu)" : obs)}");
        }
    }
    Log("");
    Log("Fim. Tudo restaurado ao modo padrao.");
    return 0;
}
catch (Exception ex)
{
    Log("ERRO: " + ex.Message);
    return 9;
}
finally
{
    RestaurarTudo();
    try { pc.Close(); } catch { }
    try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "resultado.txt"), relatorio.ToString(), new UTF8Encoding(false)); } catch { }
}
