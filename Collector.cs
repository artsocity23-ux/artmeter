// Samples CPU, RAM, GPUs and per-app usage. Everything runs on the UI thread once a second and costs ~2 ms:
// NVML for GPU totals (same numbers as nvidia-smi), PDH for CPU utility and per-process VRAM / GPU load
// (same numbers as Task Manager), one NtQuerySystemInformation call for every process's RAM and CPU time.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

sealed class History {
  readonly float[] values;
  int head;
  public int Count;

  public History(int length) { values = new float[length]; }

  public int Length { get { return values.Length; } }

  public void Add(float v) {
    values[head] = v;
    head = (head + 1) % values.Length;
    if (Count < values.Length) Count++;
  }

  // 0 = oldest kept sample
  public float this[int i] { get { return values[(head - Count + i + values.Length) % values.Length]; } }
}

sealed class Gpu {
  public int Index;
  public IntPtr Handle;
  public string FullName = "GPU", Name = "GPU";
  public int Bus = -1;
  public bool Display;
  public double Util, Used, Free, Total, Reserved, Watts = -1;
  public int Temp = -1;
  public readonly History UtilHistory = new History(Collector.HistoryLength);

  public double MemPercent { get { return Total > 0 ? Used * 100 / Total : 0; } }
}

// One program in the top lists: all its processes summed, like the Processes tab of Task Manager.
sealed class AppRow {
  public readonly string Name, Path;
  public readonly List<int> Pids = new List<int>(2);
  public readonly double[] VramOn, UtilOn;
  public double Ram, Cpu, Vram, GpuUtil;
  public int Gpu = -1;

  public AppRow(string name, string path, int gpus) {
    Name = name;
    Path = path;
    VramOn = new double[gpus];
    UtilOn = new double[gpus];
  }
}

sealed unsafe class Collector : IDisposable {
  public const int HistoryLength = 60;

  public double Cpu, RamTotal, RamUsed, RamAvail, Commit, CommitLimit;
  public float[] Cores = new float[0];
  public readonly History CpuHistory = new History(HistoryLength), RamHistory = new History(HistoryLength);
  public readonly List<Gpu> Gpus = new List<Gpu>();
  public readonly List<AppRow> TopRam = new List<AppRow>(), TopVram = new List<AppRow>();
  public string CpuName = "CPU", Driver = "";
  public int Rows = 5, AppsEveryMs = 2000;

  public double RamPercent { get { return RamTotal > 0 ? RamUsed * 100 / RamTotal : 0; } }

  sealed class ProcInfo {
    public long Create, LastCpu = -1;
    public string Name, Path;
    public int Seen;
  }

  readonly PdhQuery sysQuery = new PdhQuery(), gpuQuery = new PdhQuery();
  PdhArray cores, vram, engines;
  IntPtr modified;
  int[] coreKeys = new int[256];
  float[] coreVals = new float[256];

  IntPtr spi;
  int spiSize;
  long lastAppsTicks, nextApps;
  int tick, appsTick;
  readonly int threads = Environment.ProcessorCount;
  readonly Dictionary<int, ProcInfo> procs = new Dictionary<int, ProcInfo>();
  readonly Dictionary<string, AppRow> groups = new Dictionary<string, AppRow>(StringComparer.OrdinalIgnoreCase);
  readonly Dictionary<int, AppRow> pidRow = new Dictionary<int, AppRow>();
  readonly Dictionary<long, double> engineMax = new Dictionary<long, double>();
  readonly Dictionary<ulong, int> luidGpu = new Dictionary<ulong, int>();
  readonly List<AppRow> sortBuf = new List<AppRow>();
  bool nvml;

  public void Init() {
    try {
      using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")) {
        if (key != null) CpuName = string.Join(" ", ((string)key.GetValue("ProcessorNameString", "CPU")).Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
      }
    } catch {
      // name is cosmetic
    }
    cores = new PdhArray(sysQuery.Add(@"\Processor Information(*)\% Processor Utility"), Native.PDH_FMT_DOUBLE);
    modified = sysQuery.Add(@"\Memory\Modified Page List Bytes");
    vram = new PdhArray(gpuQuery.Add(@"\GPU Process Memory(*)\Dedicated Usage"), Native.PDH_FMT_LARGE);
    engines = new PdhArray(gpuQuery.Add(@"\GPU Engine(*)\Utilization Percentage"), Native.PDH_FMT_DOUBLE);
    sysQuery.Collect();
    InitNvml();
  }

  void InitNvml() {
    try {
      int r = Native.nvmlInit_v2();
      if (r != 0) {
        Log.Write("NVML init failed: " + r);
        return;
      }
      uint count;
      Native.nvmlDeviceGetCount_v2(out count);
      var sb = new StringBuilder(96);
      if (Native.nvmlSystemGetDriverVersion(sb, 96) == 0) Driver = sb.ToString();
      for (uint i = 0; i < count; i++) {
        IntPtr handle;
        if (Native.nvmlDeviceGetHandleByIndex_v2(i, out handle) != 0) continue;
        var g = new Gpu { Index = (int)i, Handle = handle };
        sb.Length = 0;
        if (Native.nvmlDeviceGetName(handle, sb, 96) == 0) g.FullName = sb.ToString();
        g.Name = g.FullName.Replace("NVIDIA ", "").Replace("GeForce ", "");
        var pci = new Native.NvmlPci();
        if (Native.nvmlDeviceGetPciInfo(handle, ref pci) == 0) g.Bus = (int)pci.bus;
        int active;
        if (Native.nvmlDeviceGetDisplayActive(handle, out active) == 0) g.Display = active != 0;
        Gpus.Add(g);
        Log.Write("GPU " + i + ": " + g.FullName + ", PCI bus " + g.Bus.ToString("X2") + (g.Display ? ", display" : ""));
      }
      nvml = true;
    } catch (Exception e) {
      Log.Write("NVML unavailable: " + e.GetType().Name + " " + e.Message);
    }
  }

  // Totals are sampled on every call; the per-app lists only every AppsEveryMs, since the process snapshot
  // (750 processes, 14k threads here) is by far the most expensive part, ~11 ms.
  public void Sample(bool apps) {
    tick++;
    sysQuery.Collect();
    SampleCpu();
    SampleRam();
    SampleGpus();
    long now = DateTime.UtcNow.Ticks;
    if (!apps) {
      lastAppsTicks = 0;
      nextApps = 0;
    } else if (now >= nextApps) {
      SampleApps(now);
      nextApps = now + (AppsEveryMs - 100) * 10000L;
    }
  }

  void SampleCpu() {
    if (cores.Read()) {
      if (coreKeys.Length < cores.Count) {
        coreKeys = new int[cores.Count];
        coreVals = new float[cores.Count];
      }
      int n = 0;
      for (int i = 0; i < cores.Count; i++) {
        Native.PdhItem* it = cores.Items + i;
        if (!it->Ok) continue;
        char* s = it->Name;
        // Utility counts turbo clocks, so it goes past 100; Task Manager shows it capped.
        if (s[0] == '_') { // "_Total"
          Cpu = Math.Min(100, it->Double);
          continue;
        }
        int group = Native.ReadInt(ref s); // "group,number", "group,_Total"
        if (*s != ',' || s[1] == '_') continue;
        s++;
        coreKeys[n] = group * 64 + Native.ReadInt(ref s);
        coreVals[n] = (float)Math.Min(100, it->Double);
        n++;
      }
      Array.Sort(coreKeys, coreVals, 0, n);
      if (Cores.Length != n) Cores = new float[n];
      Array.Copy(coreVals, Cores, n);
    }
    CpuHistory.Add((float)Cpu);
  }

  void SampleRam() {
    var m = new Native.MEMORYSTATUSEX { dwLength = (uint)sizeof(Native.MEMORYSTATUSEX) };
    if (!Native.GlobalMemoryStatusEx(ref m)) return;
    double modifiedBytes = 0;
    int type;
    Native.PdhValue v;
    if (modified != IntPtr.Zero && Native.PdhGetFormattedCounterValue(modified, Native.PDH_FMT_LARGE, out type, out v) == 0 && v.Ok) {
      modifiedBytes = v.Large;
    }
    RamTotal = m.ullTotalPhys;
    RamAvail = m.ullAvailPhys;
    // Task Manager's "In use" leaves out the modified list (it counts as cached), so do the same.
    RamUsed = Math.Max(0, RamTotal - RamAvail - modifiedBytes);
    Commit = m.ullTotalPageFile - m.ullAvailPageFile;
    CommitLimit = m.ullTotalPageFile;
    RamHistory.Add((float)RamPercent);
  }

  void SampleGpus() {
    if (!nvml) return;
    foreach (var g in Gpus) {
      Native.NvmlUtil u;
      if (Native.nvmlDeviceGetUtilizationRates(g.Handle, out u) == 0) g.Util = u.gpu;
      var m2 = new Native.NvmlMem2 { version = (uint)sizeof(Native.NvmlMem2) | 2u << 24 };
      if (Native.nvmlDeviceGetMemoryInfo_v2(g.Handle, ref m2) == 0) {
        g.Total = m2.total;
        g.Used = m2.used;
        g.Free = m2.free;
        g.Reserved = m2.reserved;
      } else {
        Native.NvmlMem m1;
        if (Native.nvmlDeviceGetMemoryInfo(g.Handle, out m1) == 0) {
          g.Total = m1.total;
          g.Used = m1.used;
          g.Free = m1.free;
        }
      }
      uint t;
      g.Temp = Native.nvmlDeviceGetTemperature(g.Handle, 0, out t) == 0 ? (int)t : -1;
      uint mw;
      g.Watts = Native.nvmlDeviceGetPowerUsage(g.Handle, out mw) == 0 ? mw / 1000.0 : -1;
      if (tick % 30 == 0) {
        int active;
        if (Native.nvmlDeviceGetDisplayActive(g.Handle, out active) == 0) g.Display = active != 0;
      }
      g.UtilHistory.Add((float)g.Util);
    }
  }

  // ---------- per-app RAM, CPU, VRAM, GPU load ----------

  void SampleApps(long now) {
    if (!SnapshotProcesses()) return;
    appsTick++;
    long elapsed = lastAppsTicks > 0 ? now - lastAppsTicks : 0;
    if (elapsed > 100000000) elapsed = 0; // >10 s since the last detailed sample: CPU deltas would be stale
    lastAppsTicks = now;

    groups.Clear();
    pidRow.Clear();
    int gpus = Gpus.Count;
    byte* p = (byte*)spi;
    for (;;) {
      int pid = (int)*(long*)(p + Native.SpiPid);
      if (pid != 0) {
        long create = *(long*)(p + Native.SpiCreateTime);
        long cpu = *(long*)(p + Native.SpiUserTime) + *(long*)(p + Native.SpiKernelTime);
        ProcInfo info;
        if (!procs.TryGetValue(pid, out info) || info.Create != create) {
          char* name = *(char**)(p + Native.SpiNameBuffer);
          int len = *(ushort*)(p + Native.SpiNameLength) / 2;
          info = Describe(pid, create, name == null ? "pid " + pid : new string(name, 0, len));
          procs[pid] = info;
        }
        double cpuPct = info.LastCpu >= 0 && elapsed > 0 ? (cpu - info.LastCpu) * 100.0 / ((double)elapsed * threads) : 0;
        info.LastCpu = cpu;
        info.Seen = appsTick;
        AppRow row;
        if (!groups.TryGetValue(info.Name, out row)) {
          row = new AppRow(info.Name, info.Path, gpus);
          groups.Add(info.Name, row);
        }
        row.Ram += *(long*)(p + Native.SpiPrivateWorkingSet);
        row.Cpu += Math.Max(0, cpuPct);
        row.Pids.Add(pid);
        pidRow[pid] = row;
      }
      uint next = *(uint*)(p + Native.SpiNext);
      if (next == 0) break;
      p += next;
    }
    if (appsTick % 30 == 0) PruneExited();

    if (gpus > 0 && gpuQuery.Collect()) {
      if (vram.Read()) {
        for (int i = 0; i < vram.Count; i++) {
          Native.PdhItem* it = vram.Items + i;
          int pid, gi;
          ulong luid;
          AppRow row;
          if (!it->Ok || !Native.ParseGpuInstance(it->Name, out pid, out luid)) continue;
          if ((gi = GpuOfLuid(luid)) < 0 || !pidRow.TryGetValue(pid, out row)) continue;
          row.VramOn[gi] += it->Large;
        }
      }
      if (engines.Read()) {
        // Like Task Manager: a process's load on a GPU is its busiest engine there (3D, compute, copy, video...).
        engineMax.Clear();
        for (int i = 0; i < engines.Count; i++) {
          Native.PdhItem* it = engines.Items + i;
          int pid, gi;
          ulong luid;
          if (!it->Ok || it->Double <= 0 || !Native.ParseGpuInstance(it->Name, out pid, out luid)) continue;
          if ((gi = GpuOfLuid(luid)) < 0) continue;
          long key = (long)pid << 4 | (uint)gi;
          double v;
          if (!engineMax.TryGetValue(key, out v) || it->Double > v) engineMax[key] = it->Double;
        }
        foreach (var kv in engineMax) {
          AppRow row;
          if (pidRow.TryGetValue((int)(kv.Key >> 4), out row)) row.UtilOn[(int)(kv.Key & 15)] += kv.Value;
        }
      }
    }

    foreach (var row in groups.Values) {
      for (int gi = 0; gi < gpus; gi++) {
        row.Vram += row.VramOn[gi];
        if (row.Gpu < 0 || row.VramOn[gi] > row.VramOn[row.Gpu]) row.Gpu = gi;
      }
      if (row.Gpu >= 0) row.GpuUtil = Math.Min(100, row.UtilOn[row.Gpu]);
    }
    Top(TopRam, r => r.Ram, 0);
    Top(TopVram, r => r.Vram, 16 << 20);
  }

  void Top(List<AppRow> into, Func<AppRow, double> by, double min) {
    sortBuf.Clear();
    foreach (var row in groups.Values) if (by(row) > min) sortBuf.Add(row);
    sortBuf.Sort((a, b) => by(b).CompareTo(by(a)));
    into.Clear();
    for (int i = 0; i < sortBuf.Count && i < Rows; i++) into.Add(sortBuf[i]);
  }

  bool SnapshotProcesses() {
    for (int attempt = 0; attempt < 4; attempt++) {
      if (spi == IntPtr.Zero) {
        spiSize = Math.Max(spiSize, 1 << 20);
        spi = Marshal.AllocHGlobal(spiSize);
      }
      int needed;
      int r = Native.NtQuerySystemInformation(Native.SystemProcessInformation, spi, spiSize, out needed);
      if (r == 0) return true;
      if (r != Native.STATUS_INFO_LENGTH_MISMATCH) {
        Log.Write("NtQuerySystemInformation 0x" + r.ToString("X8"));
        return false;
      }
      Marshal.FreeHGlobal(spi);
      spi = IntPtr.Zero;
      spiSize = needed + (256 << 10);
    }
    return false;
  }

  void PruneExited() {
    var gone = new List<int>();
    foreach (var kv in procs) if (kv.Value.Seen != appsTick) gone.Add(kv.Key);
    foreach (int pid in gone) procs.Remove(pid);
  }

  int GpuOfLuid(ulong luid) {
    int gi;
    if (luidGpu.TryGetValue(luid, out gi)) return gi;
    gi = -1;
    int bus = Native.BusOfLuid(luid);
    foreach (var g in Gpus) if (bus >= 0 && g.Bus == bus) gi = g.Index;
    luidGpu[luid] = gi;
    Log.Write("adapter luid " + luid.ToString("x") + " -> bus " + bus + " -> GPU " + gi);
    return gi;
  }

  // ---------- naming ----------

  static readonly HashSet<string> Interpreters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "python", "pythonw", "node", "java", "javaw", "dotnet", "hython",
  };
  static readonly HashSet<string> GenericDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "bin", "scripts", "lib", "env", "venv", ".venv", "x64", "x86", "win64", "release", "app", "runtime", "runtimes", "current", "dist",
  };
  static readonly HashSet<string> StopDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "programs", "program files", "program files (x86)", "appdata", "local", "roaming", "users", "windows", "system32", "tools",
  };

  ProcInfo Describe(int pid, long create, string image) {
    string name = image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image.Substring(0, image.Length - 4) : image;
    string path = ImagePath(pid);
    // python.exe of ComfyUI and of Houdini are different programs: tell them apart by the folder they live in.
    if (path != null && (Interpreters.Contains(name) || name.StartsWith("python", StringComparison.OrdinalIgnoreCase))) {
      string hint = FolderHint(path);
      if (hint != null) name += " · " + hint;
    }
    return new ProcInfo { Create = create, Name = name, Path = path ?? image };
  }

  static string FolderHint(string exePath) {
    try {
      for (var dir = new DirectoryInfo(Path.GetDirectoryName(exePath)); dir != null && dir.Parent != null; dir = dir.Parent) {
        string n = dir.Name;
        if (StopDirs.Contains(n)) return null;
        if (GenericDirs.Contains(n) || n.EndsWith("env", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith("python", StringComparison.OrdinalIgnoreCase) || n.StartsWith("node", StringComparison.OrdinalIgnoreCase)) continue;
        return n;
      }
    } catch {
      // odd paths just get no hint
    }
    return null;
  }

  static readonly StringBuilder pathBuf = new StringBuilder(1024);

  static string ImagePath(int pid) {
    IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
    if (h == IntPtr.Zero) return null;
    try {
      int size = pathBuf.Capacity;
      pathBuf.Length = 0;
      return Native.QueryFullProcessImageName(h, 0, pathBuf, ref size) ? pathBuf.ToString(0, size) : null;
    } finally {
      Native.CloseHandle(h);
    }
  }

  public void Dispose() {
    sysQuery.Dispose();
    gpuQuery.Dispose();
    if (cores != null) cores.Dispose();
    if (vram != null) vram.Dispose();
    if (engines != null) engines.Dispose();
    if (spi != IntPtr.Zero) Marshal.FreeHGlobal(spi);
    spi = IntPtr.Zero;
    if (nvml) Native.nvmlShutdown();
    nvml = false;
  }
}
