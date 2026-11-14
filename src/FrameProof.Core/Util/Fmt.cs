using System.Globalization;

namespace FrameProof.Core.Util;

public static class Fmt
{
    public static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Bytes(long b)
    {
        if (b < 0) return "?";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return v.ToString(i == 0 ? "0" : v < 10 ? "0.0" : "0", Tr) + " " + u[i];
    }

    public static string Num(double v, int digits = 1) => v.ToString("F" + digits, Tr);
    public static string Pct(double v, int digits = 1) => (v >= 0 ? "+" : "") + v.ToString("F" + digits, Tr) + "%";
}
