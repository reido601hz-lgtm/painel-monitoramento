using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace PainelMonitor;

public sealed class SensorHub : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ILogger<SensorHub> _log;
    private Computer? _computer;
    private NvmlGpu? _nvml;
    private string _json = "{\"erro\":\"iniciando\"}";
    private string _raw = string.Empty;
    private string _fanDebug = string.Empty;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _nomes = new(StringComparer.OrdinalIgnoreCase);
    private string _configUsado = "(nenhum)";
    private long _lastCollect;

    public SensorHub(ILogger<SensorHub> log) => _log = log;

    public bool Started { get; private set; }

    public long AgeSeconds => _lastCollect == 0 ? -1 : DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastCollect;

    public string Json => Volatile.Read(ref _json);

    public string RawReport => Volatile.Read(ref _raw);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();

        LoadNames();

        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsStorageEnabled = true,
                IsNetworkEnabled = true
            };
            _computer.Open();
            UpdateAll();
            Started = true;
            _log.LogInformation("sensores LHM abertos: {N} hardwares", AllHardware().Count());
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "falha ao abrir o LibreHardwareMonitor");
        }

        try
        {
            _nvml = NvmlGpu.TryCreate();
            _log.LogInformation("NVML: {Nome}", _nvml?.Name ?? "indisponivel");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "NVML indisponivel, usando GPU do LHM");
        }

        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Snapshot snap;
                lock (_gate)
                {
                    snap = Collect();
                    Volatile.Write(ref _json, JsonSerializer.Serialize(snap, JsonOpts));
                    _lastCollect = snap.Ts;

                    if (tick++ % 30 == 0) Volatile.Write(ref _raw, BuildRaw());
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "falha ao coletar sensores");
            }

            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        try
        {
            _computer?.Close();
        }
        catch
        {
            // ignorado
        }
    }

    /// <summary>
    /// Le os nomes amigaveis de config.json (sec  "temperaturas" e "ventoinhas").
    /// Procura na pasta do executavel e nas pastas acima (app\ -> raiz do projeto).
    /// Sem o arquivo, o painel segue mostrando os nomes genericos do LHM.
    /// </summary>
    private void LoadNames()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var candidatos = new[]
            {
                Path.Combine(baseDir, "config.json"),
                Path.Combine(baseDir, "..", "config.json"),
                Path.Combine(baseDir, "..", "..", "config.json")
            };

            var caminho = candidatos.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
            if (caminho is null)
            {
                _log.LogWarning("config.json nao encontrado; usando nomes do LHM");
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(caminho));
            foreach (var secao in new[] { "temperaturas", "ventoinhas" })
            {
                if (!doc.RootElement.TryGetProperty(secao, out var bloco) ||
                    bloco.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var item in bloco.EnumerateObject())
                {
                    var novo = item.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(novo)) _nomes[item.Name.Trim()] = novo.Trim();
                }
            }

            _configUsado = caminho + " (" + _nomes.Count + " nomes)";
            _log.LogInformation("nomes carregados de {Caminho}: {N}", caminho, _nomes.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "falha ao carregar config.json");
        }
    }

    private string Map(string nome) => _nomes.TryGetValue(nome.Trim(), out var novo) ? novo : nome;

    private void UpdateAll()
    {
        if (_computer is null) return;
        foreach (var hw in _computer.Hardware) UpdateTree(hw);
    }

    private static void UpdateTree(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) UpdateTree(sub);
    }

    private IEnumerable<IHardware> AllHardware()    {
        if (_computer is null) yield break;
        foreach (var hw in _computer.Hardware)
            foreach (var x in Walk(hw))
                yield return x;
    }

    private static IEnumerable<IHardware> Walk(IHardware hw)    {
        yield return hw;
        foreach (var sub in hw.SubHardware)
            foreach (var x in Walk(sub))
                yield return x;
    }

    private Snapshot Collect()
    {
        UpdateAll();

        var snap = new Snapshot
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Host = Environment.MachineName,
            UptimeSec = Environment.TickCount64 / 1000
        };

        var all = AllHardware().ToList();
        var hasCoreMax = all.Any(h => h.HardwareType == HardwareType.Cpu &&
                                      h.Sensors.Any(x => x.SensorType == SensorType.Temperature && x.Name == "Core Max"));

        var cpu = all.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu is not null)
        {
            snap.Cpu = new CpuRead
            {
                Name = cpu.Name,
                Load = SensorValue(cpu, SensorType.Load, "CPU Total", "Total"),
                Temp = SensorValue(cpu, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core Max", "Core Average"),
                Power = SensorValue(cpu, SensorType.Power, "Package", "CPU Package")
            };

            snap.Cpu.Cores = cpu.Sensors
                .Where(s => s.SensorType == SensorType.Load && s.Name.Contains("CPU Core #") && s.Value.HasValue)
                .OrderBy(s => s.Index)
                .Select(s => Round(s.Value!.Value, 0))
                .ToList();
        }

        var gpu = ReadGpu(all);
        if (gpu is not null) snap.Gpu = gpu;

        var mem = NativeMemory.Read();
        snap.Ram = new MemRead
        {
            UsedGb = Round(mem.UsedGb, 1),
            TotalGb = Round(mem.TotalGb, 1),
            Percent = mem.TotalGb > 0 ? Round(mem.UsedGb / mem.TotalGb * 100, 0) : 0
        };

        var fanNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dbg = new StringBuilder();
        foreach (var hw in all)
        {
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Fan) continue;
                dbg.Append(hw.Name).Append('|').Append(s.Name).Append('=')
                   .Append(s.Value.HasValue ? s.Value.Value.ToString("0.#") : "null").Append("; ");

                if (!s.Value.HasValue || s.Value.Value <= 0) continue;
                var name = Map(s.Name);
                if (!fanNames.Add(name))
                {
                    name = hw.Name + " " + name;
                    if (!fanNames.Add(name)) continue;
                }

                snap.Fans.Add(new FanRead { Name = name, Rpm = Round(s.Value.Value, 0) });
            }
        }

        _fanDebug = "adicionados=" + snap.Fans.Count + " :: " + dbg;

        if (snap.Gpu is not null && snap.Gpu.FanPercent > 0)
            snap.Fans.Insert(0, new FanRead { Name = "GPU", Rpm = 0, Percent = Round(snap.Gpu.FanPercent, 0) });

        foreach (var hw in all)
        {
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Temperature || !s.Value.HasValue) continue;
                var v = s.Value.Value;
                if (v <= 0) continue;
                if (s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)) continue;
                if (s.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase)) continue;
                if (s.Name.Contains("Resolution", StringComparison.OrdinalIgnoreCase)) continue;
                if (hasCoreMax && (s.Name.StartsWith("P-Core #", StringComparison.Ordinal) ||
                                   s.Name.StartsWith("E-Core #", StringComparison.Ordinal))) continue;

                var name = Map(s.Name);
                if (hw.HardwareType == HardwareType.Storage) name = hw.Name;
                if (hw.Name.StartsWith("Corsair", StringComparison.OrdinalIgnoreCase) ||
                    hw.Name.Contains("DIMM", StringComparison.OrdinalIgnoreCase)) name = "RAM " + s.Name;

                if (snap.Temps.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                if (snap.Temps.Count >= 10) break;

                snap.Temps.Add(new TempRead { Name = name, Value = Round(v, 0) });
            }

            if (hw.HardwareType == HardwareType.Storage)
            {
                var used = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Level && s.Name.Contains("Used", StringComparison.OrdinalIgnoreCase));
                var diskName = hw.Name.Trim();
                var dupe = snap.Disks.Count(d => d.Name == diskName);
                if (dupe > 0) diskName += " (" + (dupe + 1) + ")";

                snap.Disks.Add(new DiskRead
                {
                    Name = diskName,
                    Temp = Round(SensorValue(hw, SensorType.Temperature), 0),
                    UsedPercent = Round(used?.Value ?? 0, 0)
                });
            }

            if (hw.HardwareType == HardwareType.Network)
            {
                var down = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name.Contains("Down", StringComparison.OrdinalIgnoreCase));
                var up = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name.Contains("Up", StringComparison.OrdinalIgnoreCase));

                snap.Net.Add(new NetRead
                {
                    Name = hw.Name,
                    Down = Round(down?.Value ?? 0, 0),
                    Up = Round(up?.Value ?? 0, 0)
                });
            }
        }

        return snap;
    }

    private GpuRead? ReadGpu(List<IHardware> all)
    {
        if (_nvml is not null)
        {
            try
            {
                var g = _nvml.Read();
                if (g.Load > 0 || g.Temp > 0 || g.MemTotalMb > 0) return g;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "leitura NVML falhou");
            }
        }

        var hw = all.FirstOrDefault(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel);
        if (hw is null) return null;

        var gpuMemUsed = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase));
        var gpuMemTotal = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase));

        return new GpuRead
        {
            Name = hw.Name,
            Source = "lhm",
            Load = SensorValue(hw, SensorType.Load, "GPU Core"),
            Temp = SensorValue(hw, SensorType.Temperature, "GPU Core"),
            Power = SensorValue(hw, SensorType.Power, "GPU Package", "GPU Power"),
            ClockMhz = SensorValue(hw, SensorType.Clock, "GPU Core"),
            MemUsedMb = Round(gpuMemUsed?.Value ?? 0, 0),
            MemTotalMb = Round(gpuMemTotal?.Value ?? 0, 0),
            FanPercent = SensorValue(hw, SensorType.Control, "GPU Fan")
        };
    }

    private static float SensorValue(IHardware hw, SensorType type, params string[] names)
    {
        foreach (var n in names)
        {
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != type || !s.Value.HasValue) continue;
                if (s.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return Round(s.Value.Value, 1);
            }
        }

        foreach (var s in hw.Sensors)
            if (s.SensorType == type && s.Value.HasValue) return Round(s.Value.Value, 1);

        return 0;
    }

    private static float Round(double v, int digits) => (float)Math.Round(v, digits);

    /// <summary>Diagnostico sob demanda: valores crus agora, com o mesmo ciclo de Update do coletor.</summary>
    public string DebugDump()
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            if (_computer is null) return "computador nao aberto";
            UpdateAll();

            sb.AppendLine($"elevado={Environment.IsPrivilegedProcess} pid={Environment.ProcessId}");
            foreach (var hw in AllHardware())
            {
                sb.AppendLine($"[{hw.HardwareType}] {hw.Name}");
                for (var pass = 0; pass < 2; pass++)
                {
                    if (pass == 0) UpdateTree(hw);

                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType is not (SensorType.Fan or SensorType.Control or SensorType.Temperature)) continue;
                        var val = s.Value.HasValue ? s.Value.Value.ToString("0.##") : "null";
                        sb.AppendLine($"    pass{pass} {s.SensorType,-12} {s.Name,-34} {val}");
                    }
                }
            }
        }

        return sb.ToString();
    }

    private string BuildRaw()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# elevado={Environment.IsPrivilegedProcess} nomes={_configUsado}");
        sb.AppendLine($"# ventoinhas: {_fanDebug}");
        foreach (var hw in AllHardware())
        {
            sb.AppendLine($"[{hw.HardwareType}] {hw.Name}  ({hw.Identifier})");
            foreach (var s in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
                sb.AppendLine($"    {s.SensorType,-12} {s.Name,-36} {(s.Value.HasValue ? s.Value.Value.ToString("0.##") : "-")}");
        }

        return sb.ToString();
    }

    private static class NativeMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        public static (double UsedGb, double TotalGb) Read()
        {
            var st = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref st)) return (0, 0);

            const double gb = 1024.0 * 1024.0 * 1024.0;
            return ((st.TotalPhys - st.AvailPhys) / gb, st.TotalPhys / gb);
        }
    }
}

public sealed class Snapshot
{
    public long Ts { get; set; }
    public string Host { get; set; } = "";
    public long UptimeSec { get; set; }
    public CpuRead? Cpu { get; set; }
    public GpuRead? Gpu { get; set; }
    public MemRead Ram { get; set; } = new();
    public List<FanRead> Fans { get; set; } = new();
    public List<TempRead> Temps { get; set; } = new();
    public List<DiskRead> Disks { get; set; } = new();
    public List<NetRead> Net { get; set; } = new();
}

public sealed class CpuRead
{
    public string Name { get; set; } = "";
    public float Load { get; set; }
    public float Temp { get; set; }
    public float Power { get; set; }
    public List<float> Cores { get; set; } = new();
}

public sealed class MemRead
{
    public float UsedGb { get; set; }
    public float TotalGb { get; set; }
    public float Percent { get; set; }
}

public sealed class FanRead
{
    public string Name { get; set; } = "";
    public float Rpm { get; set; }
    public float Percent { get; set; }
}

public sealed class TempRead
{
    public string Name { get; set; } = "";
    public float Value { get; set; }
}

public sealed class DiskRead
{
    public string Name { get; set; } = "";
    public float Temp { get; set; }
    public float UsedPercent { get; set; }
}

public sealed class NetRead
{
    public string Name { get; set; } = "";
    public float Down { get; set; }
    public float Up { get; set; }
}
