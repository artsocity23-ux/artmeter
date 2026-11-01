// P/Invoke for ArtMeter: PDH performance counters, NVML (the library nvidia-smi uses), D3DKMT adapter lookup,
// NtQuerySystemInformation process snapshot and a few window helpers. 64-bit only (struct offsets assume x64).
using System;
using System.Runtime.InteropServices;
using System.Text;

static unsafe class Native {
  // ---------- memory / processes ----------

  [StructLayout(LayoutKind.Sequential)]
  public struct MEMORYSTATUSEX {
    public uint dwLength, dwMemoryLoad;
    public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
  }

  [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);
  public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

  [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int size, out int needed);
  public const int SystemProcessInformation = 5;
  public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

  // SYSTEM_PROCESS_INFORMATION field offsets on x64.
  public const int SpiNext = 0x00, SpiPrivateWorkingSet = 0x08, SpiCreateTime = 0x20, SpiUserTime = 0x28, SpiKernelTime = 0x30,
    SpiNameLength = 0x38, SpiNameBuffer = 0x40, SpiPid = 0x50;

  // ---------- PDH ----------

  [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhOpenQuery(string source, IntPtr user, out IntPtr query);
  [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
  [DllImport("pdh.dll")] public static extern int PdhCollectQueryData(IntPtr query);
  [DllImport("pdh.dll")] public static extern int PdhCloseQuery(IntPtr query);
  [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhGetFormattedCounterArray(IntPtr counter, int format, ref int size, out int count, IntPtr items);
  [DllImport("pdh.dll")] public static extern int PdhGetFormattedCounterValue(IntPtr counter, int format, out int type, out PdhValue value);
  public const int PDH_FMT_DOUBLE = 0x200, PDH_FMT_LARGE = 0x400;
  public const int PDH_MORE_DATA = unchecked((int)0x800007D2);

  [StructLayout(LayoutKind.Explicit)]
  public struct PdhValue {
    [FieldOffset(0)] public int Status;
    [FieldOffset(8)] public double Double;
    [FieldOffset(8)] public long Large;
    public bool Ok { get { return (uint)Status <= 1; } } // PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA
  }

  [StructLayout(LayoutKind.Explicit)]
  public struct PdhItem {
    [FieldOffset(0)] public char* Name;
    [FieldOffset(8)] public int Status;
    [FieldOffset(16)] public double Double;
    [FieldOffset(16)] public long Large;
    public bool Ok { get { return (uint)Status <= 1; } }
  }

  // ---------- NVML ----------

  [StructLayout(LayoutKind.Sequential)] public struct NvmlUtil { public uint gpu, memory; }
  [StructLayout(LayoutKind.Sequential)] public struct NvmlMem { public ulong total, free, used; }
  [StructLayout(LayoutKind.Sequential)] public struct NvmlMem2 { public uint version; public ulong total, reserved, free, used; }
  [StructLayout(LayoutKind.Sequential)]
  public struct NvmlPci {
    public fixed byte busIdLegacy[16];
    public uint domain, bus, device, pciDeviceId, pciSubSystemId;
    public fixed byte busId[32];
  }

  [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
  [DllImport("nvml.dll")] public static extern int nvmlShutdown();
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetCount_v2(out uint count);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
  [DllImport("nvml.dll", CharSet = CharSet.Ansi)] public static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
  [DllImport("nvml.dll", CharSet = CharSet.Ansi)] public static extern int nvmlSystemGetDriverVersion(StringBuilder version, uint length);
  [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPciInfo_v3")] public static extern int nvmlDeviceGetPciInfo(IntPtr device, ref NvmlPci pci);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtil util);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMem mem);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetMemoryInfo_v2(IntPtr device, ref NvmlMem2 mem);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetDisplayActive(IntPtr device, out int active);

  // ---------- D3DKMT: which PCI bus a WDDM adapter LUID belongs to ----------

  [StructLayout(LayoutKind.Sequential)] public struct D3DKMT_OPENADAPTERFROMLUID { public uint LuidLow; public int LuidHigh; public uint hAdapter; }
  [StructLayout(LayoutKind.Sequential)] public struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr Data; public uint DataSize; }
  [StructLayout(LayoutKind.Sequential)] public struct D3DKMT_ADAPTERADDRESS { public uint Bus, Device, Function; }
  [DllImport("gdi32.dll")] public static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID open);
  [DllImport("gdi32.dll")] public static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO query);
  [DllImport("gdi32.dll")] public static extern int D3DKMTCloseAdapter(ref uint hAdapter);
  public const int KMTQAITYPE_ADAPTERADDRESS = 6;

  public static int BusOfLuid(ulong luid) {
    var open = new D3DKMT_OPENADAPTERFROMLUID { LuidLow = (uint)luid, LuidHigh = (int)(luid >> 32) };
    if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return -1;
    var addr = new D3DKMT_ADAPTERADDRESS();
    var q = new D3DKMT_QUERYADAPTERINFO {
      hAdapter = open.hAdapter, Type = KMTQAITYPE_ADAPTERADDRESS, Data = (IntPtr)(&addr), DataSize = (uint)sizeof(D3DKMT_ADAPTERADDRESS),
    };
    int r = D3DKMTQueryAdapterInfo(ref q);
    uint h = open.hAdapter;
    D3DKMTCloseAdapter(ref h);
    return r == 0 ? (int)addr.Bus : -1;
  }

  // ---------- windows ----------

  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
  [DllImport("user32.dll")] public static extern bool ReleaseCapture();
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
  [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
  public const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2, WM_MOVING = 0x216, WM_EXITSIZEMOVE = 0x232;
  public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;

  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  // ---------- GDI text ----------

  [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct TEXTMETRIC {
    public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth, tmMaxCharWidth, tmWeight,
      tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
    public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
    public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
  }

  [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
  [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
  [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
  [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
  [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
  [DllImport("gdi32.dll")] public static extern int SetTextColor(IntPtr hdc, int color);
  [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRIC tm);
  [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern bool GetTextExtentPoint32W(IntPtr hdc, string s, int count, out SIZE size);
  [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
  public static extern bool GetTextExtentExPointW(IntPtr hdc, string s, int count, int maxExtent, out int fit, int[] dx, out SIZE size);
  [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
  public static extern bool ExtTextOutW(IntPtr hdc, int x, int y, uint options, IntPtr rect, string s, uint count, IntPtr dx);

  // ---------- parsing PDH instance names without allocating ----------

  public static int ReadInt(ref char* p) {
    int v = 0;
    while (*p >= '0' && *p <= '9') v = v * 10 + (*p++ - '0');
    return v;
  }

  static uint ReadHex(ref char* p) {
    uint v = 0;
    for (;;) {
      char c = *p;
      int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
      if (d < 0) return v;
      v = v << 4 | (uint)d;
      p++;
    }
  }

  static bool Skip(ref char* p, string s) {
    for (int i = 0; i < s.Length; i++) if (p[i] != s[i]) return false;
    p += s.Length;
    return true;
  }

  // "pid_3048_luid_0x00000000_0x0008509b_phys_0[_eng_0_engtype_3d]" -> pid, luid
  public static bool ParseGpuInstance(char* p, out int pid, out ulong luid) {
    pid = 0;
    luid = 0;
    if (!Skip(ref p, "pid_")) return false;
    pid = ReadInt(ref p);
    if (!Skip(ref p, "_luid_0x")) return false;
    uint hi = ReadHex(ref p);
    if (!Skip(ref p, "_0x")) return false;
    uint lo = ReadHex(ref p);
    luid = (ulong)hi << 32 | lo;
    return true;
  }
}

// One PDH query; counters added to it are sampled together by Collect().
sealed class PdhQuery : IDisposable {
  IntPtr query;

  public PdhQuery() {
    int r = Native.PdhOpenQuery(null, IntPtr.Zero, out query);
    if (r != 0) Log.Write("PdhOpenQuery failed 0x" + r.ToString("X8"));
  }

  public IntPtr Add(string path) {
    IntPtr counter;
    int r = Native.PdhAddEnglishCounter(query, path, IntPtr.Zero, out counter);
    if (r == 0) return counter;
    Log.Write("counter unavailable " + path + " 0x" + r.ToString("X8"));
    return IntPtr.Zero;
  }

  public bool Collect() {
    return query != IntPtr.Zero && Native.PdhCollectQueryData(query) == 0;
  }

  public void Dispose() {
    if (query != IntPtr.Zero) Native.PdhCloseQuery(query);
    query = IntPtr.Zero;
  }
}

// Formatted values of a wildcard counter, read into a buffer that is reused between samples.
sealed unsafe class PdhArray : IDisposable {
  readonly IntPtr counter;
  readonly int format;
  IntPtr buffer;
  int capacity;
  public int Count;

  public PdhArray(IntPtr counter, int format) {
    this.counter = counter;
    this.format = format;
  }

  public Native.PdhItem* Items { get { return (Native.PdhItem*)buffer; } }

  public bool Read() {
    Count = 0;
    if (counter == IntPtr.Zero) return false;
    for (int attempt = 0; attempt < 3; attempt++) {
      int size = capacity, count;
      int r = Native.PdhGetFormattedCounterArray(counter, format, ref size, out count, buffer);
      if (r == 0) {
        Count = count;
        return true;
      }
      if (r != Native.PDH_MORE_DATA) return false;
      if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
      capacity = size + 16384;
      buffer = Marshal.AllocHGlobal(capacity);
    }
    return false;
  }

  public void Dispose() {
    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
    buffer = IntPtr.Zero;
  }
}
