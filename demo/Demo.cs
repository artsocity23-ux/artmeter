// Renders the README demo: the real widget painted from made-up numbers with neutral program names, one PNG per sample.
// A render starts, loads every CPU thread, fills RAM and one GPU's VRAM past the red 90% mark, then ends; the loop repeats.
// Built and run by demo\make-demo.cmd together with the app sources (csc -main:Demo); never part of ArtMeter.exe.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

static class Demo {
  const int Period = 26;     // samples in one loop; the GIF shows each for 0.5 s (the app samples once a second)
  const int StillSample = 15; // mid-render: docs\widget.png and docs\compact.png
  const double GB = 1 << 30;

  sealed class FakeApp {
    public string Name;
    public int Count;
    public double Ram, Cpu, Vram, GpuUtil;
    public int Gpu = 1;
  }

  [STAThread]
  static void Main(string[] args) {
    if (args.Length != 2) {
      Console.Error.WriteLine("usage: demo <frames dir> <docs dir>");
      Environment.Exit(2);
    }
    Native.SetProcessDPIAware();
    Directory.CreateDirectory(args[0]);
    var cfg = new Settings();
    using (var data = new Collector()) { // Init() is never called: no counters, no NVML, nothing read from this PC
      data.CpuName = "Demo CPU";
      data.Driver = "demo";
      data.Cores = new float[56];
      for (int i = 0; i < 2; i++) {
        data.Gpus.Add(new Gpu {
          Index = i, Name = "RTX 3090", FullName = "NVIDIA GeForce RTX 3090", Bus = 0x41 + i * 0x20,
          Display = i == 1, Total = 24 * GB, Reserved = 0.33 * GB
        });
      }
      // The sparklines show the last minute, so play earlier loops first: frame 0 then follows the last frame seamlessly.
      for (int s = -3 * Period; s < 0; s++) Fill(data, s);
      using (var widget = new Widget(data, cfg, null)) {
        for (int s = 0; s < Period; s++) {
          Fill(data, s);
          cfg.Compact = false;
          Save(widget, Path.Combine(args[0], "full-" + s.ToString("000") + ".png"));
          cfg.Compact = true;
          Save(widget, Path.Combine(args[0], "compact-" + s.ToString("000") + ".png"));
          if (s == StillSample) {
            cfg.Compact = false;
            Save(widget, Path.Combine(args[1], "widget.png"));
            cfg.Compact = true;
            Save(widget, Path.Combine(args[1], "compact.png"));
          }
        }
      }
    }
  }

  static void Save(Widget widget, string path) {
    using (var bmp = widget.Snapshot()) bmp.Save(path, ImageFormat.Png);
  }

  // Sample s of the scenario. Everything, noise included, depends on s only through s mod Period, so the loop is exact.
  static void Fill(Collector data, int s) {
    int t = (s % Period + Period) % Period;
    double cpuLoad = Pulse(t, 5, 7, 19, 20.5), ramLoad = Pulse(t, 5, 10, 19.5, 21), vramLoad = Pulse(t, 6, 10, 19.5, 21),
      gpuLoad = Pulse(t, 6.5, 8, 19, 20), heat = Pulse(t, 7, 15, 20, 25.9);

    // CPU: render threads join one by one; when idle a few threads do a little work.
    double sum = 0;
    for (int i = 0; i < data.Cores.Length; i++) {
      double n = Noise(t, i), idle = n < 0.85 ? n * 0.6 : (n - 0.85) / 0.15 * 40;
      double share = Clamp((cpuLoad - Noise(0, 1000 + i) * 0.6) / 0.4);
      double v = idle + (82 + 18 * Noise(t, 200 + i) - idle) * share;
      data.Cores[i] = (float)v;
      sum += v;
    }
    data.Cpu = sum / data.Cores.Length;
    data.CpuHistory.Add((float)data.Cpu);

    var render = new FakeApp { Name = "render", Count = 8, Gpu = 0, Ram = (0.4 + 63 * ramLoad) * GB, Cpu = data.Cpu * 0.97,
      Vram = (0.2 + 21.6 * vramLoad) * GB, GpuUtil = 97 * gpuLoad };
    var programs = new List<FakeApp> {
      new FakeApp { Name = "modeler", Count = 1, Ram = 9.6 * GB, Cpu = 2.1 - 1.8 * cpuLoad, Vram = 4.1 * GB, GpuUtil = 3 },
      new FakeApp { Name = "compositor", Count = 1, Ram = 6.8 * GB, Cpu = 0.6, Vram = 1.2 * GB },
      new FakeApp { Name = "browser", Count = 42, Ram = 4.3 * GB, Cpu = 1.4, Vram = 0.7 * GB, GpuUtil = 1 },
      new FakeApp { Name = "dwm", Count = 1, Ram = 0.21 * GB, Cpu = 0.9, Vram = 0.9 * GB, GpuUtil = 4 },
      new FakeApp { Name = "player", Count = 1, Ram = 0.62 * GB, Cpu = 0.8, Vram = 0.25 * GB, GpuUtil = 2 },
      new FakeApp { Name = "explorer", Count = 1, Ram = 0.31 * GB, Cpu = 0.2, Vram = 0.18 * GB }
    };
    for (int i = 0; i < programs.Count; i++) {
      var p = programs[i];
      p.Ram *= 1 + 0.02 * (Noise(t, 300 + i) - 0.5);
      p.Cpu = Math.Max(0, p.Cpu * (0.6 + 0.8 * Noise(t, 400 + i)));
    }
    if (cpuLoad + ramLoad > 0.03) programs.Add(render);

    data.RamTotal = 128 * GB;
    data.RamUsed = 26.4 * GB + render.Ram + 0.15 * GB * Noise(t, 500);
    data.RamAvail = data.RamTotal - data.RamUsed - 0.4 * GB;
    data.Commit = data.RamUsed * 1.3;
    data.CommitLimit = 146 * GB;
    data.RamHistory.Add((float)data.RamPercent);

    var g0 = data.Gpus[0];
    g0.Util = Math.Round(gpuLoad * (95 + 4 * Noise(t, 600)));
    g0.Used = 0.35 * GB + render.Vram;
    g0.Temp = (int)Math.Round(36 + 35 * heat);
    g0.Watts = 23 + 318 * gpuLoad * (0.94 + 0.06 * Noise(t, 601));
    var g1 = data.Gpus[1];
    g1.Util = Math.Round(3 + 5 * Noise(t, 700));
    g1.Used = 0.6 * GB;
    foreach (var p in programs) if (p.Gpu == 1) g1.Used += p.Vram;
    g1.Temp = 41;
    g1.Watts = 38 + 6 * Noise(t, 701);
    foreach (var g in data.Gpus) {
      g.Free = g.Total - g.Used - g.Reserved;
      g.UtilHistory.Add((float)g.Util);
    }

    // The app refreshes the top lists every 2 s, i.e. every other sample.
    if (t % 2 != 0) return;
    data.TopRam.Clear();
    data.TopVram.Clear();
    int pid = 1000;
    var rows = new List<AppRow>();
    foreach (var p in programs) {
      var row = new AppRow(p.Name, @"C:\Demo\" + p.Name + ".exe", data.Gpus.Count) { Ram = p.Ram, Cpu = p.Cpu, Vram = p.Vram, GpuUtil = p.GpuUtil, Gpu = p.Gpu };
      row.VramOn[p.Gpu] = p.Vram;
      row.UtilOn[p.Gpu] = p.GpuUtil;
      for (int i = 0; i < p.Count; i++) row.Pids.Add(pid += 4);
      rows.Add(row);
    }
    rows.Sort((a, b) => b.Ram.CompareTo(a.Ram));
    data.TopRam.AddRange(rows.GetRange(0, Math.Min(5, rows.Count)));
    rows.Sort((a, b) => b.Vram.CompareTo(a.Vram));
    data.TopVram.AddRange(rows.GetRange(0, Math.Min(5, rows.Count)));
  }

  static double Clamp(double x) { return Math.Max(0, Math.Min(1, x)); }

  // 0 before a, 1 between b and c, 0 again after d, with smooth steps.
  static double Pulse(double t, double a, double b, double c, double d) {
    return Step((t - a) / (b - a)) * (1 - Step((t - c) / (d - c)));
  }

  static double Step(double x) {
    x = Clamp(x);
    return x * x * (3 - 2 * x);
  }

  // Repeatable noise in [0, 1) for sample t and channel ch.
  static double Noise(int t, int ch) {
    uint h = (uint)(t * 73856093) ^ (uint)(ch * 19349663) ^ 0x9E3779B9u;
    h ^= h >> 16;
    h *= 0x7FEB352Du;
    h ^= h >> 15;
    h *= 0x846CA68Bu;
    h ^= h >> 16;
    return h / 4294967296.0;
  }
}
