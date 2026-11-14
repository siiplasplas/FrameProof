using Microsoft.Win32;

namespace FrameProof.Core.Util;

public static class Reg
{
    public static RegistryKey Root(string hive) => hive switch
    {
        "HKCU" => Registry.CurrentUser,
        "HKLM" => Registry.LocalMachine,
        _ => throw new ArgumentException("Bilinmeyen hive: " + hive),
    };

    public static object? Get(string hive, string key, string name)
    {
        try
        {
            using var k = Root(hive).OpenSubKey(key);
            return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch { return null; }
    }

    public static int? GetInt(string hive, string key, string name)
    {
        var v = Get(hive, key, name);
        return v switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var p) => p,
            _ => null,
        };
    }

    public static string? GetString(string hive, string key, string name) => Get(hive, key, name)?.ToString();
}
