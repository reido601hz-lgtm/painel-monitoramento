using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PainelMonitor;

/// <summary>
/// FPS e frametime do jogo em primeiro plano. Roda o console oficial do PresentMon (Intel, MIT) como
/// processo filho: ele le eventos ETW de apresentacao de quadros do Windows (nao injeta nada nos
/// jogos) e imprime um CSV por quadro no stdout. Aqui o CSV e' agregado e servido em /api/fps.
///
/// - O painel roda elevado (o ETW exige) e o .exe fica numa pasta gravavel pelo usuario: por isso o
///   SHA-256 e' conferido antes de cada execucao e o filho morre junto com o painel (Job Object).
/// - A linha do tempo e' a do proprio PresentMon (TimeInMs), nao a de chegada das linhas: o stdout
///   pode vir em rajadas.
/// - Se o PresentMon faltar, divergir no hash ou cair, so' o FPS some; o resto do painel segue.
/// </summary>
public sealed class FrameHub : BackgroundService
{
    private const string ExeRelativo = @"tools\PresentMon\PresentMon-2.6.0-x64.exe";
    private const string Sha256Padrao = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
    private const int JanelaSerieMs = 12000;   // largura da linha de frametime
    private const int BucketMs = 100;          // 120 pontos = 12 s
    private const int NBuckets = JanelaSerieMs / BucketMs;
    private const double SemLinhasMs = 3000;   // sem linhas por 3 s = nada sendo apresentado
    private const double GracaMs = 10000;      // alt-tab: segura o jogo por 10 s
    private const int CapturaSegundos = 3600;  // captura cronometrada; ao terminar o painel reinicia o PresentMon

    // processos que apresentam quadros o tempo todo mas nao sao jogos (sem ".exe", minusculo)
    private static readonly string[] ExcluirPadrao =
    {
        "dwm", "explorer", "msedgewebview2", "msedge", "chrome", "firefox", "opera", "brave", "vivaldi",
        "steam", "steamwebhelper", "discord", "claude", "whatsapp", "whatsapp.root", "spotify", "vlc", "mpv",
        "obs64", "applicationframehost", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "textinputhost", "widgets", "powershell", "windowsterminal", "epicgameslauncher", "epicwebhelper",
        "eosoverlayrenderer-win64-shipping", "nvidia overlay", "nvidia app", "systemsettings", "taskmgr"
    };

    private readonly record struct Quadro(double T, float Ms, float Cpu, float Gpu);

    private sealed class Proc
    {
        public int Pid;
        public string Nome = "";
        public string Modo = "";
        public double Taxa;                       // quadros/s nos ultimos 3 s (calculado em Calcular)
        public readonly List<Quadro> Q = new();
    }

    private sealed class Opcoes
    {
        public bool Ativo = true;
        public double MinFps = 25;
        public string? Exe;
        public string Sha256 = Sha256Padrao;
        public readonly HashSet<string> Excluir = new(ExcluirPadrao, StringComparer.OrdinalIgnoreCase);
    }

    private readonly ILogger<FrameHub> _log;
    private readonly object _gate = new();
    private readonly Dictionary<int, Proc> _procs = new();
    private Opcoes _op = new();

    private bool _temCabecalho;
    private int _iApp, _iPid, _iModo, _iT, _iMs, _iCpu, _iGpu, _nCols;
    private double _agora;                        // maior TimeInMs visto (linha do tempo do PresentMon)
    private long _ultimaLinhaTicks;
    private int _jogoPid;
    private long _jogoVistoTicks;
    private int _linhasDesdeLimpeza;
    private int _diagLinhas;                      // quantas linhas cruas ja foram para o log (diagnostico)
    private volatile bool _rodando;
    private volatile string _erro = "";

    private string _cacheJson = "{\"ativo\":false}";
    private long _cacheTicks;

    public FrameHub(ILogger<FrameHub> log) => _log = log;

    // ------------------------------------------------------------------ execucao do PresentMon

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        CarregarOpcoes();
        if (!_op.Ativo)
        {
            _log.LogInformation("fps: desativado em config.json");
            return;
        }

        var exe = LocalizarExe();
        if (exe is null)
        {
            _erro = "PresentMon nao encontrado";
            _log.LogWarning("fps: {Rel} nao encontrado; FPS desativado", ExeRelativo);
            return;
        }

        var espera = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            var inicio = Stopwatch.GetTimestamp();
            try
            {
                await RodarAsync(exe, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (InvalidDataException ex)
            {
                _erro = ex.Message;
                _log.LogError("fps: {Msg}; FPS desativado", ex.Message);
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "fps: falha ao executar o PresentMon");
            }

            _rodando = false;
            if (ct.IsCancellationRequested) break;
            if (Ms(Stopwatch.GetTimestamp() - inicio) > 60000) espera = TimeSpan.FromSeconds(5);
            _log.LogWarning("fps: PresentMon encerrou; nova tentativa em {S} s", espera.TotalSeconds);
            try { await Task.Delay(espera, ct); } catch (OperationCanceledException) { break; }
            espera = TimeSpan.FromSeconds(Math.Min(espera.TotalSeconds * 2, 60));
        }
    }

    private async Task RodarAsync(string exe, CancellationToken ct)
    {
        // integridade: o .exe esta numa pasta gravavel pelo usuario e este processo e' elevado
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant();
        if (!string.Equals(hash, _op.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 do PresentMon diferente do esperado (" + hash[..12] + "...)");

        // Mesmos argumentos do teste que funcionou (captura cronometrada, reiniciada ao fim de cada
        // hora) e, como nesse teste, o filho herda o console do pai (CreateNoWindow = false; o painel
        // roda com console escondido). Sessao ETW com nome proprio: nao encerra sessoes de outras
        // ferramentas.
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in new[]
                 {
                     "--output_stdout", "--no_console_stats", "--stop_existing_session", "--session_name", "painelmonitor_fps",
                     "--timed", CapturaSegundos.ToString(CultureInfo.InvariantCulture), "--terminate_after_timed"
                 })
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        ReiniciarEstado();
        _diagLinhas = 0;
        proc.OutputDataReceived += (_, e) => OnLinha(e.Data);
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) _log.LogWarning("fps: PresentMon stderr: {L}", Trunc(e.Data, 300));
        };

        proc.Start();
        AtribuirAoJob(proc);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _rodando = true;
        _erro = "";
        _log.LogInformation("fps: PresentMon iniciado (PID {Pid})", proc.Id);

        // diagnostico: avisa enquanto o filho nao entrega o cabecalho do CSV
        using var parar = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var vigia = Task.Run(async () =>
        {
            var s = 0;
            try
            {
                while (!parar.IsCancellationRequested && !proc.HasExited)
                {
                    await Task.Delay(30000, parar.Token);
                    s += 30;
                    // area de trabalho parada nao gera quadros: avisa nos 3 primeiros minutos e depois a cada 10 min
                    if (!_temCabecalho && (s <= 90 || s % 600 == 0))
                        _log.LogWarning("fps: PresentMon sem saida ha {S} s (CPU do filho {Cpu:N2} s)", s, proc.TotalProcessorTime.TotalSeconds);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }, CancellationToken.None);

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw;
        }
        finally
        {
            parar.Cancel();
            // matar o PresentMon deixa a sessao ETW viva e SEM consumidor (ela continua enchendo buffers
            // do kernel e, pior, uma sessao orfa impediu sessoes novas de receber eventos em 2026-11-18)
            EncerrarSessaoEtw();
        }

        _erro = "PresentMon encerrou (codigo " + proc.ExitCode + ")";
        if (proc.ExitCode == 0) _log.LogInformation("fps: PresentMon terminou a captura cronometrada; reiniciando");
        else _log.LogWarning("fps: PresentMon saiu com codigo {Cod}", proc.ExitCode);
        await vigia;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private const string SessaoEtw = "painelmonitor_fps";

    /// <summary>logman stop &lt;sessao&gt; -ets: melhor esforco; erro "nao encontrada" e' normal (saida limpa).</summary>
    private void EncerrarSessaoEtw()
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "logman.exe"), $"stop {SessaoEtw} -ets")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "fps: nao consegui encerrar a sessao ETW");
        }
    }

    private void ReiniciarEstado()
    {
        lock (_gate)
        {
            _procs.Clear();
            _temCabecalho = false;
            _agora = 0;
            _ultimaLinhaTicks = 0;
            _jogoPid = 0;
            _linhasDesdeLimpeza = 0;
        }
    }

    private string? LocalizarExe()
    {
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(_op.Exe))
        {
            var p = Path.IsPathRooted(_op.Exe) ? _op.Exe : Path.Combine(baseDir, "..", _op.Exe);
            p = Path.GetFullPath(p);
            return File.Exists(p) ? p : null;
        }

        // mesma logica do config.json: pasta do exe e uma/duas acima (app\ -> raiz do projeto)
        var candidatos = new[]
        {
            Path.Combine(baseDir, ExeRelativo),
            Path.Combine(baseDir, "..", ExeRelativo),
            Path.Combine(baseDir, "..", "..", ExeRelativo)
        };
        return candidatos.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private void CarregarOpcoes()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var caminho = new[]
            {
                Path.Combine(baseDir, "config.json"),
                Path.Combine(baseDir, "..", "config.json"),
                Path.Combine(baseDir, "..", "..", "config.json")
            }.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
            if (caminho is null) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(caminho));
            if (!doc.RootElement.TryGetProperty("fps", out var s) || s.ValueKind != JsonValueKind.Object) return;

            if (s.TryGetProperty("ativo", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) _op.Ativo = v.GetBoolean();
            if (s.TryGetProperty("minFps", out v) && v.TryGetDouble(out var min) && min > 0) _op.MinFps = min;
            if (s.TryGetProperty("exe", out v) && v.ValueKind == JsonValueKind.String) _op.Exe = v.GetString();
            if (s.TryGetProperty("sha256", out v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: 64 } h) _op.Sha256 = h;
            if (s.TryGetProperty("excluir", out v) && v.ValueKind == JsonValueKind.Array)
                foreach (var item in v.EnumerateArray())
                    if (item.GetString() is { Length: > 0 } nome) _op.Excluir.Add(SemExe(nome));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "fps: falha ao ler a secao fps do config.json (usando padroes)");
        }
    }

    // ------------------------------------------------------------------ leitura do CSV

    /// <summary>Uma linha do stdout do PresentMon (cabecalho ou quadro).</summary>
    public void OnLinha(string? linha)
    {
        if (string.IsNullOrEmpty(linha)) return;
        if (_diagLinhas < 4)
        {
            _diagLinhas++;
            _log.LogInformation("fps: stdout[{N}]: {L}", _diagLinhas, Trunc(linha, 160));
        }
        lock (_gate)
        {
            if (!_temCabecalho)
            {
                if (linha.TrimStart('﻿').StartsWith("Application,", StringComparison.Ordinal))
                {
                    DefinirCabecalho(linha.TrimStart('﻿').Split(','));
                    if (_temCabecalho) _log.LogInformation("fps: PresentMon entregando dados (cabecalho recebido)");
                }
                return;
            }

            var f = linha.Split(',');
            if (f.Length < _nCols) return;   // linha cortada
            if (!Num(f[_iT], out var t) || !Num(f[_iMs], out var ms) || ms <= 0) return;
            if (!int.TryParse(f[_iPid], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return;
            var cpu = 0.0; var gpu = 0.0;
            if (_iCpu >= 0) Num(f[_iCpu], out cpu);
            if (_iGpu >= 0) Num(f[_iGpu], out gpu);

            if (!_procs.TryGetValue(pid, out var p))
            {
                p = new Proc { Pid = pid, Nome = f[_iApp] };
                _procs[pid] = p;
            }
            if (_iModo >= 0) p.Modo = f[_iModo];
            p.Q.Add(new Quadro(t, (float)ms, (float)cpu, (float)gpu));
            if (t > _agora) _agora = t;
            _ultimaLinhaTicks = Stopwatch.GetTimestamp();
            if (++_linhasDesdeLimpeza >= 2000) Limpar();
        }
    }

    private void DefinirCabecalho(string[] cols)
    {
        int Idx(string n) => Array.FindIndex(cols, c => string.Equals(c.Trim(), n, StringComparison.OrdinalIgnoreCase));
        _iApp = Idx("Application"); _iPid = Idx("ProcessID"); _iT = Idx("TimeInMs"); _iMs = Idx("MsBetweenPresents");
        _iModo = Idx("PresentMode"); _iCpu = Idx("MsCPUBusy"); _iGpu = Idx("MsGPUBusy");
        _nCols = cols.Length;
        if (_iApp < 0 || _iPid < 0 || _iT < 0 || _iMs < 0)
        {
            _erro = "cabecalho do PresentMon sem as colunas esperadas";
            _log.LogError("fps: cabecalho inesperado: {Cab}", string.Join(',', cols));
            return;
        }
        _temCabecalho = true;
    }

    private static bool Num(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>Descarta quadros com mais de 20 s e processos que sumiram.</summary>
    private void Limpar()
    {
        _linhasDesdeLimpeza = 0;
        var limite = _agora - 20000;
        foreach (var p in _procs.Values)
        {
            var k = 0;
            while (k < p.Q.Count && p.Q[k].T < limite) k++;
            if (k > 0) p.Q.RemoveRange(0, k);
        }
        foreach (var pid in _procs.Where(kv => kv.Value.Q.Count == 0).Select(kv => kv.Key).ToList())
            _procs.Remove(pid);
    }

    // ------------------------------------------------------------------ agregacao

    /// <summary>JSON de /api/fps. Cache de 250 ms (a tela consulta a cada 500 ms).</summary>
    public string ObterJson()
    {
        var agora = Stopwatch.GetTimestamp();
        if (_cacheTicks != 0 && Ms(agora - _cacheTicks) < 250) return _cacheJson;
        var j = Calcular();
        _cacheJson = j;
        _cacheTicks = agora;
        return j;
    }

    private string Calcular()
    {
        lock (_gate)
        {
            var ticks = Stopwatch.GetTimestamp();
            if (!_temCabecalho || _ultimaLinhaTicks == 0 || Ms(ticks - _ultimaLinhaTicks) > SemLinhasMs)
                return JsonInativo();

            // candidatos: >= minFps nos ultimos 3 s e fora da lista de exclusao
            var corte = _agora - 3000;
            var fg = PidPrimeiroPlano();
            Proc? escolhido = null;
            double melhorTaxa = 0;
            foreach (var p in _procs.Values)
            {
                var n = 0;
                for (var i = p.Q.Count - 1; i >= 0 && p.Q[i].T >= corte; i--) n++;
                p.Taxa = n / 3.0;
                if (p.Taxa < _op.MinFps || Excluido(p.Nome)) continue;
                if (p.Pid == fg) { escolhido = p; break; }       // o jogo em primeiro plano ganha
                // tela cheia direta (jogo) sem ser o primeiro plano: aceita o de maior taxa
                if (p.Modo.StartsWith("Hardware: Independent Flip", StringComparison.OrdinalIgnoreCase) && p.Taxa > melhorTaxa)
                {
                    escolhido = p;
                    melhorTaxa = p.Taxa;
                }
            }

            if (escolhido is not null)
            {
                _jogoPid = escolhido.Pid;
                _jogoVistoTicks = ticks;
            }
            else if (_jogoPid != 0 && _procs.TryGetValue(_jogoPid, out var ant) &&
                     ant.Taxa >= _op.MinFps * 0.4 && Ms(ticks - _jogoVistoTicks) < GracaMs)
            {
                escolhido = ant;                                  // alt-tab: segura por 10 s
            }

            if (escolhido is null) return JsonInativo();

            var q = escolhido.Q;
            if (q.Count == 0) return JsonInativo();

            // ultimo 1 s: FPS, frametime medio, CPU e GPU ocupadas
            var t1 = _agora - 1000;
            int n1 = 0; double sMs = 0, sC = 0, sG = 0;
            for (var i = q.Count - 1; i >= 0 && q[i].T >= t1; i--) { n1++; sMs += q[i].Ms; sC += q[i].Cpu; sG += q[i].Gpu; }
            var fps = (double)n1;
            var ft = n1 > 0 ? sMs / n1 : 0;

            // 1% low: p99 do frametime nos ultimos 10 s
            var t10 = _agora - 10000;
            var lst = new List<float>();
            for (var i = q.Count - 1; i >= 0 && q[i].T >= t10; i--) lst.Add(q[i].Ms);
            var fps1 = fps;
            if (lst.Count >= 30)
            {
                lst.Sort();
                var p99 = lst[Math.Min(lst.Count - 1, (int)Math.Ceiling(0.99 * lst.Count) - 1)];
                fps1 = 1000.0 / p99;
            }

            // serie: maximo por bucket de 100 ms nos ultimos 12 s (os picos nao podem sumir)
            var serie = new double[NBuckets];
            var tem = new bool[NBuckets];
            var ini = _agora - JanelaSerieMs;
            for (var i = q.Count - 1; i >= 0 && q[i].T >= ini; i--)
            {
                var b = Math.Min(NBuckets - 1, (int)((q[i].T - ini) / BucketMs));
                if (q[i].Ms > serie[b]) serie[b] = q[i].Ms;
                tem[b] = true;
            }
            var primeiro = Array.IndexOf(tem, true);
            if (primeiro < 0) return JsonInativo();
            for (var b = 0; b < primeiro; b++) serie[b] = serie[primeiro];
            var ult = serie[primeiro];
            for (var b = primeiro; b < NBuckets; b++) { if (tem[b]) ult = serie[b]; else serie[b] = ult; }

            var sb = new StringBuilder(1024);
            var inv = CultureInfo.InvariantCulture;
            sb.Append("{\"ativo\":true,\"jogo\":").Append(JsonSerializer.Serialize(NomeAmigavel(escolhido.Nome)));
            sb.Append(",\"pid\":").Append(escolhido.Pid.ToString(inv));
            sb.Append(",\"fps\":").Append(fps.ToString("F1", inv));
            sb.Append(",\"fps1\":").Append(fps1.ToString("F1", inv));
            sb.Append(",\"ft\":").Append(ft.ToString("F2", inv));
            sb.Append(",\"cpu\":").Append((n1 > 0 ? sC / n1 : 0).ToString("F2", inv));
            sb.Append(",\"gpu\":").Append((n1 > 0 ? sG / n1 : 0).ToString("F2", inv));
            sb.Append(",\"modo\":").Append(JsonSerializer.Serialize(escolhido.Modo));
            sb.Append(",\"serie\":[");
            for (var b = 0; b < NBuckets; b++) { if (b > 0) sb.Append(','); sb.Append(serie[b].ToString("F1", inv)); }
            sb.Append("]}");
            return sb.ToString();
        }
    }

    private string JsonInativo() =>
        "{\"ativo\":false,\"coletando\":" + (_rodando && _temCabecalho ? "true" : "false") +
        (_erro.Length > 0 ? ",\"erro\":" + JsonSerializer.Serialize(_erro) : "") + "}";

    private bool Excluido(string nome) => _op.Excluir.Contains(SemExe(nome));

    private static string SemExe(string nome) =>
        nome.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? nome[..^4] : nome;

    /// <summary>"FortniteClient-Win64-Shipping.exe" -> "Fortnite".</summary>
    private static string NomeAmigavel(string app)
    {
        var s = SemExe(app);
        foreach (var sufixo in new[] { "-Win64-Shipping", "-Win32-Shipping", "_x64", "-Shipping", "Client" })
            if (s.EndsWith(sufixo, StringComparison.OrdinalIgnoreCase) && s.Length > sufixo.Length) s = s[..^sufixo.Length];
        s = s.Replace('_', ' ').Trim();
        return s.Length > 24 ? s[..24] : s;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    // ------------------------------------------------------------------ Win32

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private static int PidPrimeiroPlano()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(h, out var pid);
            return (int)pid;
        }
        catch { return 0; }
    }

    // Job Object: o PresentMon morre junto com o painel (o instalar.ps1 mata o painel com Stop-Process)
    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasico
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobEstendido
    {
        public JobBasico Basico;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr atributos, string? nome);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int classe, IntPtr info, uint tamanho);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr processo);

    private static IntPtr _job;

    private void AtribuirAoJob(Process proc)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var info = new JobEstendido();
                info.Basico.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                var tam = Marshal.SizeOf<JobEstendido>();
                var ptr = Marshal.AllocHGlobal(tam);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(_job, 9, ptr, (uint)tam);   // JobObjectExtendedLimitInformation
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            AssignProcessToJobObject(_job, proc.Handle);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "fps: nao consegui colocar o PresentMon no Job Object");
        }
    }
}
