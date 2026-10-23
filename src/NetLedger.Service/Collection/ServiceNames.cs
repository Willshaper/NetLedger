using System.Text.RegularExpressions;

namespace NetLedger.Service.Collection;

/// <summary>
/// English names for Windows services that commonly use the network. Windows reports service display names in its
/// display language, but NetLedger's interface is English. Services not listed here keep the name Windows gives them.
/// </summary>
internal static partial class ServiceNames
{
    private static readonly Dictionary<string, string> English = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AppXSvc"] = "AppX Deployment Service",
        ["BFE"] = "Base Filtering Engine",
        ["BITS"] = "Background Intelligent Transfer Service",
        ["CDPSvc"] = "Connected Devices Platform Service",
        ["CDPUserSvc"] = "Connected Devices Platform User Service",
        ["ClipSVC"] = "Client License Service",
        ["CryptSvc"] = "Cryptographic Services",
        ["DcomLaunch"] = "DCOM Server Process Launcher",
        ["Dhcp"] = "DHCP Client",
        ["DiagTrack"] = "Connected User Experiences and Telemetry",
        ["Dnscache"] = "DNS Client",
        ["DoSvc"] = "Delivery Optimization",
        ["DusmSvc"] = "Data Usage",
        ["EventSystem"] = "COM+ Event System",
        ["fdPHost"] = "Function Discovery Provider Host",
        ["FDResPub"] = "Function Discovery Resource Publication",
        ["InstallService"] = "Microsoft Store Install Service",
        ["iphlpsvc"] = "IP Helper",
        ["LanmanServer"] = "Server",
        ["LanmanWorkstation"] = "Workstation",
        ["LicenseManager"] = "Windows License Manager Service",
        ["lfsvc"] = "Geolocation Service",
        ["MapsBroker"] = "Downloaded Maps Manager",
        ["mpssvc"] = "Windows Defender Firewall",
        ["NcbService"] = "Network Connection Broker",
        ["Netman"] = "Network Connections",
        ["netprofm"] = "Network List Service",
        ["NlaSvc"] = "Network Location Awareness",
        ["nsi"] = "Network Store Interface Service",
        ["OneSyncSvc"] = "Sync Host",
        ["PeerDistSvc"] = "BranchCache",
        ["PolicyAgent"] = "IPsec Policy Agent",
        ["RasMan"] = "Remote Access Connection Manager",
        ["RpcSs"] = "Remote Procedure Call (RPC)",
        ["Schedule"] = "Task Scheduler",
        ["SENS"] = "System Event Notification Service",
        ["SharedAccess"] = "Internet Connection Sharing (ICS)",
        ["SSDPSRV"] = "SSDP Discovery",
        ["SstpSvc"] = "Secure Socket Tunneling Protocol Service",
        ["TermService"] = "Remote Desktop Services",
        ["TokenBroker"] = "Web Account Manager",
        ["upnphost"] = "UPnP Device Host",
        ["UsoSvc"] = "Update Orchestrator Service",
        ["W32Time"] = "Windows Time",
        ["WaaSMedicSvc"] = "Windows Update Medic Service",
        ["Wcmsvc"] = "Windows Connection Manager",
        ["WinHttpAutoProxySvc"] = "WinHTTP Web Proxy Auto-Discovery Service",
        ["WinRM"] = "Windows Remote Management",
        ["wisvc"] = "Windows Insider Service",
        ["WlanSvc"] = "WLAN AutoConfig",
        ["wlidsvc"] = "Microsoft Account Sign-in Assistant",
        ["WpnService"] = "Windows Push Notifications System Service",
        ["WpnUserService"] = "Windows Push Notifications User Service",
        ["wuauserv"] = "Windows Update",
        ["XblAuthManager"] = "Xbox Live Auth Manager",
        ["XblGameSave"] = "Xbox Live Game Save",
        ["XboxNetApiSvc"] = "Xbox Live Networking Service",
    };

    /// <summary>
    /// A service name without the suffix Windows adds to per-user services (CDPUserSvc_4f3a2 → CDPUserSvc). The
    /// suffix changes at every sign-in, so keeping it would make each sign-in a new app.
    /// </summary>
    public static string BaseName(string serviceName) => PerUserSuffix().Replace(serviceName, "");

    /// <summary>The English name for a service, or <c>null</c> when it isn't in the list.</summary>
    public static string? EnglishName(string serviceName) =>
        English.TryGetValue(BaseName(serviceName), out string? name) ? name : null;

    /// <summary>The name shown for an svchost.exe instance hosting <paramref name="services"/>.</summary>
    public static string Describe(IEnumerable<(string Name, string Display)> services)
    {
        List<string> names = services
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => EnglishName(s.Name) ?? BaseName(s.Display))
            .Distinct()
            .ToList();
        return names.Count == 1 ? names[0] : "Service host: " + string.Join(", ", names);
    }

    /// <summary>
    /// The English name for a stored service app (key "svc:name+name..."), or <c>null</c> when a service in it
    /// isn't in the list, in which case the stored name stays.
    /// </summary>
    public static string? EnglishNameForKey(string key)
    {
        if (!key.StartsWith(AppKeys.ServicePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string[] services = key[AppKeys.ServicePrefix.Length..].Split('+');
        if (services.Any(s => EnglishName(s) is null))
        {
            return null;
        }

        return Describe(services.Select(s => (s, "")));
    }

    // Per-user services get a sign-in-specific hex suffix, e.g. CDPUserSvc_4f3a2. Requiring a digit and 4+ characters
    // keeps ordinary names that happen to end in "_cafe" or similar intact.
    [GeneratedRegex("_(?=[0-9a-f]*[0-9])[0-9a-f]{4,}$", RegexOptions.IgnoreCase)]
    private static partial Regex PerUserSuffix();
}
