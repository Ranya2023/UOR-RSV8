using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace Remco;

/// <summary>
/// Turns the laptop's Windows "Mobile hotspot" on/off from inside Remco
/// (same thing as Settings → Network &amp; internet → Mobile hotspot).
/// </summary>
internal static class Hotspot
{
    public sealed record Info(bool On, string Ssid, string Pass, int Clients, string Error);

    /// <summary>Remco turned the hotspot on (so it turns it off again on exit).</summary>
    public static bool StartedByRemco { get; private set; }

    private static NetworkOperatorTetheringManager? Manager(out string error)
    {
        error = "";
        var profiles = new List<ConnectionProfile>();
        try
        {
            var internet = NetworkInformation.GetInternetConnectionProfile();
            if (internet != null) profiles.Add(internet);
            profiles.AddRange(NetworkInformation.GetConnectionProfiles());
        }
        catch (Exception ex) { error = ex.Message; }

        foreach (var p in profiles)
        {
            try
            {
                var cap = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(p);
                if (cap != TetheringCapability.Enabled) { error = "Hotspot not available: " + cap; continue; }
                return NetworkOperatorTetheringManager.CreateFromConnectionProfile(p);
            }
            catch (Exception ex) { error = ex.Message; }
        }
        if (error.Length == 0)
            error = "Windows needs a network (Wi-Fi or cable) to share before it can start a hotspot.";
        return null;
    }

    public static Info Status()
    {
        var m = Manager(out string err);
        if (m == null) return new Info(false, "", "", 0, err);
        try
        {
            var cfg = m.GetCurrentAccessPointConfiguration();
            bool on = m.TetheringOperationalState == TetheringOperationalState.On;
            return new Info(on, cfg.Ssid, cfg.Passphrase, on ? (int)m.ClientCount : 0, "");
        }
        catch (Exception ex) { return new Info(false, "", "", 0, ex.Message); }
    }

    public static async Task<Info> StartAsync()
    {
        var m = Manager(out string err);
        if (m == null) return new Info(false, "", "", 0, err);
        try
        {
            if (m.TetheringOperationalState != TetheringOperationalState.On)
            {
                var r = await m.StartTetheringAsync();
                if (r.Status != TetheringOperationStatus.Success)
                    return new Info(false, "", "", 0, $"{r.Status} {r.AdditionalErrorMessage}".Trim());
                StartedByRemco = true;
            }
            var cfg = m.GetCurrentAccessPointConfiguration();
            return new Info(true, cfg.Ssid, cfg.Passphrase, (int)m.ClientCount, "");
        }
        catch (Exception ex) { return new Info(false, "", "", 0, ex.Message); }
    }

    public static async Task StopAsync()
    {
        var m = Manager(out _);
        if (m == null) return;
        try
        {
            if (m.TetheringOperationalState == TetheringOperationalState.On) await m.StopTetheringAsync();
        }
        catch { }
        StartedByRemco = false;
    }

    /// <summary>Text for a QR code that phones' cameras understand ("join this Wi-Fi").</summary>
    public static string WifiQr(string ssid, string pass)
    {
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace(":", "\\:").Replace("\"", "\\\"");
        return $"WIFI:T:WPA;S:{Esc(ssid)};P:{Esc(pass)};;";
    }
}
