using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Serilog;

namespace VoiceWink.Services.Licensing;

/// <summary>
/// Three-component hardware fingerprint used to bind a license activation to a machine.
/// Components: MachineGuid (HKLM registry), SMBIOS UUID (WMI), primary NIC MAC.
/// Validation requires 2 of 3 components to match the stored fingerprint so that common
/// failure modes — VM clones sharing MachineGuid, disk restores regenerating it, USB NICs
/// being added/removed — do not lock paying users out of their own machines.
/// </summary>
public readonly record struct HardwareFingerprint(string MachineGuidHash, string SmbiosUuidHash, string MacHash)
{
    private const char Separator = '|';
    private static ILogger Logger => Log.ForContext(typeof(HardwareFingerprint));

    /// <summary>
    /// Number of non-empty components in this fingerprint. Used to decide whether the
    /// current environment is rich enough to support 2-of-3 matching or whether the
    /// caller should fall back to strict equality.
    /// </summary>
    public int NonEmptyComponentCount =>
        (string.IsNullOrEmpty(MachineGuidHash) ? 0 : 1)
        + (string.IsNullOrEmpty(SmbiosUuidHash) ? 0 : 1)
        + (string.IsNullOrEmpty(MacHash) ? 0 : 1);

    /// <summary>
    /// Count how many non-empty components match the other fingerprint. Empty components
    /// never count as a match — two unknowns are not "the same unknown".
    /// </summary>
    public int MatchingComponents(HardwareFingerprint other)
    {
        var count = 0;
        if (!string.IsNullOrEmpty(MachineGuidHash) && MachineGuidHash == other.MachineGuidHash) count++;
        if (!string.IsNullOrEmpty(SmbiosUuidHash) && SmbiosUuidHash == other.SmbiosUuidHash) count++;
        if (!string.IsNullOrEmpty(MacHash) && MacHash == other.MacHash) count++;
        return count;
    }

    public string Serialize() => $"{MachineGuidHash}{Separator}{SmbiosUuidHash}{Separator}{MacHash}";

    public static HardwareFingerprint Parse(string serialized)
    {
        if (string.IsNullOrEmpty(serialized))
            return default;
        var parts = serialized.Split(Separator);
        return new HardwareFingerprint(
            parts.Length > 0 ? parts[0] : "",
            parts.Length > 1 ? parts[1] : "",
            parts.Length > 2 ? parts[2] : "");
    }

    /// <summary>
    /// Read the three hardware components on this machine, hash each independently, and
    /// return the composite fingerprint. Any component that can't be read becomes an
    /// empty string; the caller decides how to handle degraded environments.
    /// </summary>
    public static HardwareFingerprint Compute()
    {
        return new HardwareFingerprint(
            HashOrEmpty(ReadMachineGuid()),
            HashOrEmpty(ReadSmbiosUuid()),
            HashOrEmpty(ReadPrimaryMac()));
    }

    private static string HashOrEmpty(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var bytes = Encoding.UTF8.GetBytes(raw.Trim().ToUpperInvariant());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string ReadMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string ?? "";
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to read MachineGuid");
            return "";
        }
    }

    private static string ReadSmbiosUuid()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
            foreach (var mo in searcher.Get())
            {
                var uuid = mo["UUID"] as string;
                if (!string.IsNullOrEmpty(uuid) && uuid != "00000000-0000-0000-0000-000000000000")
                    return uuid;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to read SMBIOS UUID via WMI");
        }
        return "";
    }

    private static string ReadPrimaryMac()
    {
        try
        {
            // Pick the first up, non-loopback, non-tunnel, non-virtual-vendor NIC.
            // Hypervisor OUIs we want to skip so VM-default NICs don't all collide to a common MAC.
            var virtualOuis = new[] { "005056", "000569", "000C29", "001C14", "005056", "0003FF", "00155D" };
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up
                                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                     && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel))
            {
                var mac = nic.GetPhysicalAddress().ToString();
                if (mac.Length < 12) continue;
                if (virtualOuis.Any(oui => mac.StartsWith(oui, StringComparison.OrdinalIgnoreCase)))
                    continue;
                return mac;
            }

            // Fallback — accept any non-empty MAC even from a virtualized NIC, so we still have
            // a component in VM-only environments instead of degrading to 2 components total.
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up
                                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                var mac = nic.GetPhysicalAddress().ToString();
                if (!string.IsNullOrEmpty(mac)) return mac;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to enumerate network interfaces for MAC fingerprint");
        }
        return "";
    }
}
