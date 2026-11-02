// Minimal QR code encoder (ISO/IEC 18004) for the donate window: byte mode, error correction level M, versions 1-10
// (up to 213 bytes, plenty for any wallet address). Follows the structure of Project Nayuki's reference encoder.
using System;
using System.Collections.Generic;
using System.Text;

static class Qr {
  // Level M, indexed by version: error correction codewords per block, and number of blocks.
  static readonly int[] EccPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
  static readonly int[] Blocks = { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };
  static readonly bool[] FinderLike = { true, false, true, true, true, false, true, false, false, false, false };

  // Dark modules as [row, column], without the quiet zone.
  public static bool[,] Encode(string text) {
    byte[] bytes = Encoding.UTF8.GetBytes(text);
    int ver = 1;
    while (4 + (ver < 10 ? 8 : 16) + bytes.Length * 8 > DataCodewords(ver) * 8) {
      if (++ver > 10) throw new ArgumentException("too long for a QR code: " + bytes.Length + " bytes");
    }

    // Mode, length, data, terminator, then padding to the version's capacity.
    var bits = new List<bool>();
    Append(bits, 4, 4);
    Append(bits, bytes.Length, ver < 10 ? 8 : 16);
    foreach (byte b in bytes) Append(bits, b, 8);
    int capacity = DataCodewords(ver) * 8;
    Append(bits, 0, Math.Min(4, capacity - bits.Count));
    Append(bits, 0, (8 - bits.Count % 8) % 8);
    for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(bits, pad, 8);
    var data = new byte[bits.Count / 8];
    for (int i = 0; i < bits.Count; i++) {
      if (bits[i]) data[i >> 3] |= (byte)(0x80 >> (i & 7));
    }

    var m = new Matrix(ver);
    m.DrawFunctionPatterns();
    m.DrawCodewords(AddEccAndInterleave(data, ver));
    int best = 0, bestPenalty = int.MaxValue;
    for (int mask = 0; mask < 8; mask++) {
      m.ApplyMask(mask);
      m.DrawFormatBits(mask);
      int p = m.Penalty();
      if (p < bestPenalty) {
        best = mask;
        bestPenalty = p;
      }
      m.ApplyMask(mask); // XOR again to undo
    }
    m.ApplyMask(best);
    m.DrawFormatBits(best);
    return m.Dark;
  }

  static void Append(List<bool> bits, int value, int length) {
    for (int i = length - 1; i >= 0; i--) bits.Add((value >> i & 1) != 0);
  }

  static bool Bit(int x, int i) { return (x >> i & 1) != 0; }

  static int RawDataModules(int ver) {
    int r = (16 * ver + 128) * ver + 64;
    if (ver >= 2) {
      int n = ver / 7 + 2;
      r -= (25 * n - 10) * n - 55;
      if (ver >= 7) r -= 36;
    }
    return r;
  }

  static int DataCodewords(int ver) { return RawDataModules(ver) / 8 - EccPerBlock[ver] * Blocks[ver]; }

  // Splits the data into blocks, appends Reed-Solomon codewords to each and interleaves them.
  static byte[] AddEccAndInterleave(byte[] data, int ver) {
    int numBlocks = Blocks[ver], eccLen = EccPerBlock[ver], raw = RawDataModules(ver) / 8;
    int numShort = numBlocks - raw % numBlocks, shortLen = raw / numBlocks;
    byte[] divisor = RsDivisor(eccLen);
    var blocks = new byte[numBlocks][];
    for (int i = 0, k = 0; i < numBlocks; i++) {
      int datLen = shortLen - eccLen + (i < numShort ? 0 : 1);
      var block = new byte[shortLen + 1];
      Array.Copy(data, k, block, 0, datLen);
      Array.Copy(RsRemainder(data, k, datLen, divisor), 0, block, shortLen + 1 - eccLen, eccLen);
      k += datLen;
      blocks[i] = block;
    }
    var result = new byte[raw];
    int n = 0;
    for (int i = 0; i <= shortLen; i++) {
      for (int j = 0; j < numBlocks; j++) {
        if (i != shortLen - eccLen || j >= numShort) result[n++] = blocks[j][i];
      }
    }
    return result;
  }

  static byte[] RsDivisor(int degree) {
    var r = new byte[degree];
    r[degree - 1] = 1;
    int root = 1;
    for (int i = 0; i < degree; i++) {
      for (int j = 0; j < degree; j++) {
        r[j] = Mul(r[j], root);
        if (j + 1 < degree) r[j] ^= r[j + 1];
      }
      root = Mul(root, 2);
    }
    return r;
  }

  static byte[] RsRemainder(byte[] data, int offset, int length, byte[] divisor) {
    var r = new byte[divisor.Length];
    for (int i = offset; i < offset + length; i++) {
      int factor = data[i] ^ r[0];
      Array.Copy(r, 1, r, 0, r.Length - 1);
      r[r.Length - 1] = 0;
      for (int j = 0; j < r.Length; j++) r[j] ^= Mul(divisor[j], factor);
    }
    return r;
  }

  // Multiplication in GF(2^8) modulo x^8 + x^4 + x^3 + x^2 + 1.
  static byte Mul(int x, int y) {
    int z = 0;
    for (int i = 7; i >= 0; i--) {
      z = (z << 1) ^ ((z >> 7) * 0x11D);
      z ^= (y >> i & 1) * x;
    }
    return (byte)z;
  }

  sealed class Matrix {
    readonly int ver, size;
    public readonly bool[,] Dark, Fn; // Fn: finder, timing, alignment and format modules, which data and masks skip

    public Matrix(int ver) {
      this.ver = ver;
      size = ver * 4 + 17;
      Dark = new bool[size, size];
      Fn = new bool[size, size];
    }

    void Set(int x, int y, bool dark) {
      Dark[y, x] = dark;
      Fn[y, x] = true;
    }

    public void DrawFunctionPatterns() {
      for (int i = 0; i < size; i++) {
        Set(6, i, i % 2 == 0);
        Set(i, 6, i % 2 == 0);
      }
      Finder(3, 3);
      Finder(size - 4, 3);
      Finder(3, size - 4);
      int[] align = AlignmentPositions();
      int n = align.Length;
      for (int i = 0; i < n; i++) {
        for (int j = 0; j < n; j++) {
          if (!(i == 0 && j == 0 || i == 0 && j == n - 1 || i == n - 1 && j == 0)) Alignment(align[i], align[j]);
        }
      }
      DrawFormatBits(0); // reserves the area; the real mask is drawn later
      DrawVersion();
    }

    void Finder(int cx, int cy) {
      for (int dy = -4; dy <= 4; dy++) {
        for (int dx = -4; dx <= 4; dx++) {
          int x = cx + dx, y = cy + dy, d = Math.Max(Math.Abs(dx), Math.Abs(dy));
          if (x >= 0 && x < size && y >= 0 && y < size) Set(x, y, d != 2 && d != 4);
        }
      }
    }

    void Alignment(int cx, int cy) {
      for (int dy = -2; dy <= 2; dy++) {
        for (int dx = -2; dx <= 2; dx++) Set(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
      }
    }

    int[] AlignmentPositions() {
      if (ver == 1) return new int[0];
      int n = ver / 7 + 2, step = (ver * 4 + n * 2 + 1) / (n * 2 - 2) * 2;
      var r = new int[n];
      r[0] = 6;
      for (int i = n - 1, pos = size - 7; i >= 1; i--, pos -= step) r[i] = pos;
      return r;
    }

    public void DrawFormatBits(int mask) {
      int data = mask; // level M is 00 in the top two bits
      int rem = data;
      for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
      int bits = (data << 10 | rem) ^ 0x5412;
      for (int i = 0; i <= 5; i++) Set(8, i, Bit(bits, i));
      Set(8, 7, Bit(bits, 6));
      Set(8, 8, Bit(bits, 7));
      Set(7, 8, Bit(bits, 8));
      for (int i = 9; i < 15; i++) Set(14 - i, 8, Bit(bits, i));
      for (int i = 0; i < 8; i++) Set(size - 1 - i, 8, Bit(bits, i));
      for (int i = 8; i < 15; i++) Set(8, size - 15 + i, Bit(bits, i));
      Set(8, size - 8, true);
    }

    void DrawVersion() {
      if (ver < 7) return;
      int rem = ver;
      for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
      int bits = ver << 12 | rem;
      for (int i = 0; i < 18; i++) {
        int a = size - 11 + i % 3, b = i / 3;
        Set(a, b, Bit(bits, i));
        Set(b, a, Bit(bits, i));
      }
    }

    // Zigzag from the bottom-right corner, two columns at a time, skipping the vertical timing column.
    public void DrawCodewords(byte[] data) {
      int i = 0;
      for (int right = size - 1; right >= 1; right -= 2) {
        if (right == 6) right = 5;
        bool upward = ((right + 1) & 2) == 0;
        for (int vert = 0; vert < size; vert++) {
          for (int j = 0; j < 2; j++) {
            int x = right - j, y = upward ? size - 1 - vert : vert;
            if (Fn[y, x] || i >= data.Length * 8) continue;
            Dark[y, x] = Bit(data[i >> 3], 7 - (i & 7));
            i++;
          }
        }
      }
    }

    public void ApplyMask(int mask) {
      for (int y = 0; y < size; y++) {
        for (int x = 0; x < size; x++) {
          bool invert;
          switch (mask) {
            case 0: invert = (x + y) % 2 == 0; break;
            case 1: invert = y % 2 == 0; break;
            case 2: invert = x % 3 == 0; break;
            case 3: invert = (x + y) % 3 == 0; break;
            case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
            case 5: invert = x * y % 2 + x * y % 3 == 0; break;
            case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
            default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
          }
          if (invert && !Fn[y, x]) Dark[y, x] = !Dark[y, x];
        }
      }
    }

    // Lower is easier to scan: long runs, 2x2 blocks, finder-like patterns and dark/light imbalance all cost points.
    public int Penalty() {
      int p = 0, dark = 0;
      for (int a = 0; a < size; a++) p += Line(a, true) + Line(a, false);
      for (int y = 0; y < size - 1; y++) {
        for (int x = 0; x < size - 1; x++) {
          bool c = Dark[y, x];
          if (c == Dark[y, x + 1] && c == Dark[y + 1, x] && c == Dark[y + 1, x + 1]) p += 3;
        }
      }
      foreach (bool d in Dark) {
        if (d) dark++;
      }
      int total = size * size;
      return p + ((Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1) * 10;
    }

    bool At(int a, int b, bool row) { return row ? Dark[a, b] : Dark[b, a]; }

    int Line(int a, bool row) {
      int p = 0, run = 0;
      bool color = false;
      for (int b = 0; b < size; b++) {
        bool c = At(a, b, row);
        if (b > 0 && c == color) {
          run++;
        } else {
          if (run >= 5) p += run - 2;
          color = c;
          run = 1;
        }
      }
      if (run >= 5) p += run - 2;
      for (int b = 0; b + 11 <= size; b++) {
        bool fwd = true, back = true;
        for (int i = 0; i < 11; i++) {
          bool c = At(a, b + i, row);
          if (c != FinderLike[i]) fwd = false;
          if (c != FinderLike[10 - i]) back = false;
        }
        if (fwd) p += 40;
        if (back) p += 40;
      }
      return p;
    }
  }
}
