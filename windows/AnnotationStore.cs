using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Remco;

/// <summary>
/// Number badges and text labels, per slide, per presentation. Saved to
/// Remco-data\annotations (next to Remco.exe) so they are still there next
/// lesson (like the web app keeping them "until you remove this lesson").
/// Keyed by PowerPoint's SlideID, so reordering slides doesn't mix them up.
/// </summary>
internal sealed class AnnotationStore
{
    private static string Dir => Path.Combine(Settings.DataDir, "annotations");

    private string _file = "";
    private Dictionary<string, List<Ann>> _data = new();

    public static readonly string[] NumberColors =
        { "#f87171", "#fb923c", "#fbbf24", "#a3e635", "#34d399", "#22d3ee", "#60a5fa", "#a78bfa", "#f472b6", "#fb7185" };

    public void Open(string presentationKey)
    {
        Directory.CreateDirectory(Dir);
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(presentationKey.ToLowerInvariant())))[..16];
        string file = Path.Combine(Dir, hash + ".json");
        if (file == _file) return;
        _file = file;
        _data = new();
        try
        {
            if (File.Exists(file))
                _data = JsonSerializer.Deserialize<Dictionary<string, List<Ann>>>(File.ReadAllText(file)) ?? new();
        }
        catch { _data = new(); }
    }

    public IEnumerable<string> Keys => _data.Keys.ToList();

    public List<Ann> For(string slideKey)
    {
        if (!_data.TryGetValue(slideKey, out var list)) { list = new List<Ann>(); _data[slideKey] = list; }
        return list;
    }

    public void Save()
    {
        if (_file.Length == 0) return;
        try
        {
            foreach (var k in _data.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) _data.Remove(k);
            // drawings on photos / videos / the phone screen are only for this session
            var keep = _data.Where(kv => !kv.Key.StartsWith("media:") && kv.Key != "phone").ToDictionary(kv => kv.Key, kv => kv.Value);
            File.WriteAllText(_file, JsonSerializer.Serialize(keep));
        }
        catch { }
    }
}
