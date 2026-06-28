using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>
/// Best-effort lookup of the device's primary unicast IP, used to pre-fill the interactive
/// <see cref="Phases.ConfigPhase"/> "Public host(s)" row with a sensible Enter-to-accept
/// default on a fresh self-host bootstrap. Operator override always wins; non-interactive
/// (<c>--non-interactive</c>) flows do <b>not</b> consult this detector — the fail-fast
/// contract on <see cref="DeploymentSection.Hosts"/> stays intact, so a JSON file that
/// forgot <c>hosts</c> still produces a precise validation error rather than silently
/// minting a cert for whatever address the bootstrap host happened to have at the moment.
///
/// <para>
/// The detector enumerates <see cref="NetworkInterface.GetAllNetworkInterfaces"/> and picks
/// the first unicast address that survives a chain of "almost certainly not the operator's
/// intended host IP" filters. The filtering is split into a pure <see cref="Pick"/> step
/// (over fabricated <see cref="NicProbe"/> records, for unit testing) and a thin live
/// wrapper that does the NIC-enumeration adapter work and swallows enumeration faults so
/// any platform quirk degrades to "no default" rather than crashing the bootstrap.
/// </para>
/// </summary>
internal static class LocalAddressDetector
{
    /// <summary>
    /// Case-insensitive name prefixes for interfaces we want to skip even when their
    /// <see cref="NetworkInterface.NetworkInterfaceType"/> reports a "normal" type. On Linux
    /// the runtime returns <see cref="NetworkInterfaceType.Ethernet"/> for Docker bridges,
    /// KVM bridges, podman CNI veth pairs, k8s flannel/cilium overlays, etc.; on Windows
    /// Hyper-V switches appear as <c>vEthernet (...)</c>; on macOS Parallels / VMware show
    /// up as <c>vmnet*</c>. Without a name-prefix guard the picker would happily latch onto
    /// a Docker-bridge 172.17.0.1 on a dev laptop — which is almost never what the operator
    /// wants in a leaf-cert SAN.
    /// </summary>
    private static readonly string[] VirtualNamePrefixes =
    [
        "docker",   // Linux: docker0 + docker_gwbridge
        "br-",      // Linux: docker user-defined bridge networks (br-<hash>)
        "veth",     // Linux: container veth pairs visible on the host side
        "tap",      // Linux/macOS: TAP devices (VPN clients, qemu)
        "tun",      // Linux/macOS: TUN devices (OpenVPN, WireGuard userspace)
        "vEthernet (", // Windows: Hyper-V virtual switches expose vEthernet (Name)
        "vmnet",    // macOS: VMware Fusion / Parallels host-only adapters
        "virbr",    // Linux: libvirt-managed bridges (virbr0)
        "cni",      // Linux: container networking interface plugins
        "flannel",  // Kubernetes flannel overlay (flannel.1, etc.)
        "cilium_",  // Kubernetes cilium overlay
        "kube-",    // Misc Kubernetes-managed interfaces
        "zt",       // ZeroTier virtual interfaces
        "wg",       // WireGuard tunnels (wg0, wg1, ...)
        "utun",     // macOS userspace TUN (Tailscale, Cloudflare WARP)
        "tailscale", // Tailscale (Linux side: tailscale0)
    ];

    /// <summary>
    /// Live device-IP probe: enumerates NICs and runs them through <see cref="Pick"/>. Any
    /// failure (permissions, mid-reconfigure, unsupported platform) is swallowed and reported
    /// as <c>null</c> — the caller falls back to "no default" and the operator types a host
    /// explicitly. Returns IPv4 in preference to IPv6 when both are available.
    /// </summary>
    public static IPAddress? TryDetectPrimaryIp()
    {
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            var probes = new List<NicProbe>(nics.Length);
            foreach (var nic in nics)
            {
                // GetIPProperties can throw NetworkInformationException on a NIC that's
                // disappeared mid-enumeration (USB unplug, VPN tunnel teardown). Per-NIC
                // try/catch keeps a flaky NIC from poisoning the picker for the whole list.
                IReadOnlyList<IPAddress> addresses;
                try
                {
                    addresses = nic.GetIPProperties().UnicastAddresses
                        .Select(u => u.Address)
                        .ToArray();
                }
                catch (NetworkInformationException)
                {
                    addresses = [];
                }
                probes.Add(new NicProbe(
                    nic.Name,
                    nic.NetworkInterfaceType,
                    nic.OperationalStatus,
                    addresses));
            }
            return Pick(probes);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pure picker over a fabricated list of NIC probes. Filtering pipeline (all conditions
    /// must hold for an address to even be considered):
    /// <list type="bullet">
    ///   <item>NIC <see cref="NicProbe.Status"/> must be <see cref="OperationalStatus.Up"/>.
    ///         Disabled / down / unplugged NICs are skipped — their addresses, if any, won't
    ///         answer.</item>
    ///   <item>NIC <see cref="NicProbe.Type"/> must not be <see cref="NetworkInterfaceType.Loopback"/>
    ///         or <see cref="NetworkInterfaceType.Tunnel"/>. Loopback is never a serving address;
    ///         tunnels are usually transient and operator-specific (VPN endpoints).</item>
    ///   <item>NIC <see cref="NicProbe.Name"/> must not start with any of
    ///         <see cref="VirtualNamePrefixes"/> (case-insensitive). Catches Docker bridges,
    ///         KVM bridges, k8s overlays, Hyper-V virtual switches, VMware host-only adapters,
    ///         WireGuard / Tailscale / ZeroTier tunnels that report as plain Ethernet.</item>
    ///   <item>Address must not be loopback (<see cref="IPAddress.IsLoopback"/>) — defensive
    ///         double-check vs. NICs that report non-loopback type but expose a loopback addr.</item>
    ///   <item>IPv4: must not be APIPA (<c>169.254.0.0/16</c>) — link-local fallback indicates
    ///         no DHCP, no real network reachability.</item>
    ///   <item>IPv6: must not be link-local (<c>fe80::/10</c>), site-local (deprecated
    ///         <c>fec0::/10</c>), or multicast. The remaining v6 candidates are global unicast,
    ///         which are what callers actually want to put in a SAN.</item>
    /// </list>
    /// <para>
    /// The first IPv4 candidate encountered wins; IPv6 is only returned if no usable IPv4
    /// candidate is found across all probes. The IPv4-bias reflects the most common
    /// self-host topology (LAN behind NAT, dual-stack but reached via IPv4); operators on
    /// pure-IPv6 deployments still get a usable default, and operators with strong opinions
    /// override the prompt manually.
    /// </para>
    /// </summary>
    public static IPAddress? Pick(IReadOnlyList<NicProbe> probes)
    {
        IPAddress? firstV4 = null;
        IPAddress? firstV6 = null;

        foreach (var nic in probes)
        {
            if (nic.Status != OperationalStatus.Up) continue;
            if (nic.Type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (IsVirtualName(nic.Name)) continue;

            foreach (var addr in nic.Addresses)
            {
                if (IPAddress.IsLoopback(addr)) continue;

                switch (addr.AddressFamily)
                {
                    case AddressFamily.InterNetwork:
                        var v4Bytes = addr.GetAddressBytes();
                        // APIPA 169.254.0.0/16 indicates a DHCP failure; not a real address.
                        if (v4Bytes[0] == 169 && v4Bytes[1] == 254) continue;
                        firstV4 ??= addr;
                        break;

                    case AddressFamily.InterNetworkV6:
                        if (addr.IsIPv6LinkLocal) continue;
                        if (addr.IsIPv6SiteLocal) continue;
                        if (addr.IsIPv6Multicast) continue;
                        firstV6 ??= addr;
                        break;

                    // Any other family (AppleTalk, IPX, ...) — ignore.
                }
            }
        }

        return firstV4 ?? firstV6;
    }

    /// <summary>
    /// Returns true if <paramref name="name"/> case-insensitively starts with any of
    /// <see cref="VirtualNamePrefixes"/>. Internal for unit-test visibility — the picker's
    /// behaviour over fabricated probes is enough to lock most regressions, but a focused
    /// "this prefix matches" assertion keeps the prefix list itself testable against
    /// real-world NIC name samples we've observed across platforms.
    /// </summary>
    internal static bool IsVirtualName(string name)
    {
        foreach (var prefix in VirtualNamePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Lightweight, test-friendly snapshot of one <see cref="NetworkInterface"/>.
    /// <see cref="NetworkInterface"/> itself is sealed and tightly coupled to the live OS
    /// query path, so the unit tests fabricate <see cref="NicProbe"/> records directly to
    /// drive every branch of <see cref="Pick"/> deterministically.
    /// </summary>
    public readonly record struct NicProbe(
        string Name,
        NetworkInterfaceType Type,
        OperationalStatus Status,
        IReadOnlyList<IPAddress> Addresses);
}
