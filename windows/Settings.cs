using System.Security.Cryptography;
using System.Text.Json;

namespace Remco;

/// <summary>
/// Portable settings: stored in a "UOR-RC-data" folder next to Remco.exe (so the
/// whole thing can live on a USB stick). Falls back to %LOCALAPPDATA%\Remco when
/// the exe's folder is read-only.
/// </summary>
internal sealed class Settings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Pin { get; set; } = RandomNumberGenerator.GetInt32(1000, 10000).ToString();
    public bool AutoHotspot { get; set; }
    public string PhoneIp { get; set; } = "";

    public static string DataDir { get; private set; } = "";
    private static string FilePath => Path.Combine(DataDir, "settings.json");

    public static Settings Load()
    {
        DataDir = PickDataDir();
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        var s = new Settings();
        s.Save();
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    private static string PickDataDir()
    {
        try
        {
            string exeDir = AppContext.BaseDirectory;
            string dir = Path.Combine(exeDir, "UOR-RC-data");
            string old = Path.Combine(exeDir, "Remco-data");          // settings from an older version
            if (!Directory.Exists(dir) && Directory.Exists(old)) dir = old;
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return dir;
        }
        catch
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UOR-RC");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
