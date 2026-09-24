using System.Diagnostics;
using System.Security;

namespace Remco;

/// <summary>
/// Joins this laptop to a Wi-Fi (the phone's hotspot) — the phone sends the name
/// and password over Bluetooth, so nobody has to type them. Uses Windows' own
/// "netsh wlan" (no admin rights needed; affects only the current user).
/// </summary>
internal static class WifiJoin
{
    public static string Join(string ssid, string pass, bool wpa3)
    {
        if (string.IsNullOrWhiteSpace(ssid)) return "no network name";
        string auth = wpa3 ? "WPA3SAE" : "WPA2PSK";
        string xml = $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
  <name>{SecurityElement.Escape(ssid)}</name>
  <SSIDConfig><SSID><name>{SecurityElement.Escape(ssid)}</name></SSID></SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>manual</connectionMode>
  <MSM><security>
    <authEncryption><authentication>{auth}</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
    <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(pass)}</keyMaterial></sharedKey>
  </security></MSM>
</WLANProfile>";
        string file = Path.Combine(Path.GetTempPath(), "remco-wifi.xml");
        try
        {
            File.WriteAllText(file, xml);
            string a = Run($"wlan add profile filename=\"{file}\" user=current");
            string b = Run($"wlan connect name=\"{ssid}\" ssid=\"{ssid}\"");
            return (a + " " + b).Trim();
        }
        catch (Exception ex) { return ex.Message; }
        finally { try { File.Delete(file); } catch { } }
    }

    private static string Run(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(8000);
        return o.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
