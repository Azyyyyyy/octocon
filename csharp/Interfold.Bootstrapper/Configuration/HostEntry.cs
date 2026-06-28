using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>
/// Discrete shape of one <see cref="DeploymentSection.Hosts"/> entry. The kind determines how
/// the entry flows through downstream consumers:
/// <list type="bullet">
///   <item><b>Dns</b>: emitted as a <c>dNSName</c> in the leaf SAN and in the root-CA Name
///         Constraints permittedSubtrees. Wildcards (<c>*.example.com</c>) are preserved
///         on the SAN side; <see cref="Phases.CertificatePhase.StripWildcardPrefix"/>
///         collapses them for the Name Constraints subtree (where wildcards are illegal).</item>
///   <item><b>Ipv4 / Ipv6</b>: emitted as an <c>iPAddress</c> in the leaf SAN, and as an
///         <c>iPAddress</c> Name Constraints subtree with an all-ones mask (<c>/32</c> /
///         <c>/128</c>) per RFC 5280 §4.2.1.10.</item>
///   <item><b>Ipv4Cidr / Ipv6Cidr</b>: emitted only as an <c>iPAddress</c> Name Constraints
///         subtree with the operator-supplied prefix length. CIDR entries are deliberately
///         excluded from the leaf SAN (a leaf cert can only serve a single host) and from
///         derived URL primary-host selection (no canonical URL form for a network range).</item>
/// </list>
/// </summary>
internal enum HostKind
{
    Dns,
    Ipv4,
    Ipv6,
    Ipv4Cidr,
    Ipv6Cidr,
}

/// <summary>
/// Parsed representation of one <see cref="DeploymentSection.Hosts"/> entry. <see cref="Raw"/>
/// preserves the operator's original input for error messages and round-trip display;
/// <see cref="DnsName"/> is set for <see cref="HostKind.Dns"/> entries; <see cref="Ip"/> is set
/// for every IP-shaped kind. <see cref="PrefixLength"/> is the address bit-length (32 or 128) for
/// single-IP kinds and the operator-supplied prefix for CIDR kinds.
/// </summary>
internal sealed record HostEntry(
    string Raw,
    HostKind Kind,
    string? DnsName,
    IPAddress? Ip,
    int PrefixLength)
{
    public bool IsCidr => HostParser.IsCidr(Kind);
    public bool IsLeafEligible => HostParser.IsLeafEligible(Kind);
}

/// <summary>
/// Classifier + parser for <see cref="DeploymentSection.Hosts"/> entries. The single
/// <see cref="Parse"/> entry point is the authoritative validation surface; downstream consumers
/// (<see cref="Phases.CertificatePhase"/>, <see cref="Phases.ConfigPhase"/>,
/// <see cref="Phases.PublishPhase"/>) pattern-match on <see cref="HostKind"/> rather than
/// re-parsing the raw string.
/// </summary>
internal static class HostParser
{
    public const int Ipv4Bits = 32;
    public const int Ipv6Bits = 128;

    public static bool IsCidr(HostKind k) => k is HostKind.Ipv4Cidr or HostKind.Ipv6Cidr;

    /// <summary>
    /// Whether this kind contributes a leaf-cert SAN entry. CIDR entries never do — a leaf cert
    /// can only serve a specific host, and a CIDR is a network range. CIDR entries are also
    /// ineligible to be the URL "primary host" used by <see cref="Phases.ConfigPhase.ResolveDerivedDefaults"/>.
    /// </summary>
    public static bool IsLeafEligible(HostKind k) => k is HostKind.Dns or HostKind.Ipv4 or HostKind.Ipv6;

    /// <summary>
    /// Parses one raw entry into a <see cref="HostEntry"/>. Throws <see cref="FormatException"/>
    /// with an operator-actionable message on any malformed input. Precedence:
    /// <list type="number">
    ///   <item>Slash-bearing inputs are CIDR; left side must be an IP literal, right side a
    ///         prefix length within the address family's range.</item>
    ///   <item>Slash-free inputs are tried as IP literals first (so a numeric input like
    ///         <c>192.168.1.42</c> never accidentally classifies as a DNS name).</item>
    ///   <item>Anything else is treated as a DNS name and subjected to the lightweight
    ///         RFC 1035 check (<c>length &lt;= 253</c>, no whitespace, optional leading
    ///         <c>*.</c> wildcard).</item>
    /// </list>
    /// URL-style square brackets around a bare IPv6 literal (e.g. <c>[::1]</c>) are tolerated so
    /// operators pasting from a URL don't trip on the bracket form; CIDR inputs must use the
    /// unbracketed canonical form (<c>fe80::/64</c>).
    /// </summary>
    public static HostEntry Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new FormatException("host entry must not be empty or whitespace");
        }
        var trimmed = raw.Trim();

        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex >= 0)
        {
            return ParseCidr(trimmed, slashIndex);
        }

        var addrCandidate = trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']'
            ? trimmed[1..^1]
            : trimmed;
        if (IPAddress.TryParse(addrCandidate, out var ip))
        {
            return ip.AddressFamily switch
            {
                AddressFamily.InterNetwork => new HostEntry(trimmed, HostKind.Ipv4, null, ip, Ipv4Bits),
                AddressFamily.InterNetworkV6 => new HostEntry(trimmed, HostKind.Ipv6, null, ip, Ipv6Bits),
                _ => throw new FormatException($"'{trimmed}' parsed as an IP address of unsupported family '{ip.AddressFamily}'"),
            };
        }

        ValidateDnsName(trimmed);
        return new HostEntry(trimmed, HostKind.Dns, trimmed, null, 0);
    }

    private static HostEntry ParseCidr(string trimmed, int slashIndex)
    {
        var addrText = trimmed[..slashIndex];
        var lenText = trimmed[(slashIndex + 1)..];

        if (!IPAddress.TryParse(addrText, out var ip))
        {
            throw new FormatException($"CIDR '{trimmed}': '{addrText}' is not a valid IP literal");
        }
        if (!int.TryParse(lenText, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
        {
            throw new FormatException($"CIDR '{trimmed}': prefix '{lenText}' is not a non-negative integer");
        }

        var maxPrefix = ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => Ipv4Bits,
            AddressFamily.InterNetworkV6 => Ipv6Bits,
            _ => throw new FormatException($"CIDR '{trimmed}': unsupported IP family '{ip.AddressFamily}'"),
        };
        if (prefix < 0 || prefix > maxPrefix)
        {
            throw new FormatException($"CIDR '{trimmed}': prefix /{prefix} out of range, must be between 0 and {maxPrefix}");
        }

        var addr = ip.GetAddressBytes();
        if (!IsHostBitsZero(addr, prefix))
        {
            // RFC 5280 §4.2.1.10 requires host bits outside the mask to be zero in iPAddress
            // permittedSubtree entries. We surface this as a fix-it rather than silently
            // normalising, because "I typed 192.168.1.42/24" usually means the operator meant
            // a single host (/32) but wrote a network prefix - silently snapping to .0/24 would
            // quietly broaden the Name Constraints scope past what they intended.
            var canonical = new IPAddress(MaskAddress(addr, prefix)).ToString();
            var singleHost = $"{ip}/{maxPrefix}";
            throw new FormatException(
                $"CIDR '{trimmed}' has host bits set beyond the /{prefix} mask. " +
                $"Use '{canonical}/{prefix}' to pin the network, or '{singleHost}' to pin a single host.");
        }

        var kind = ip.AddressFamily == AddressFamily.InterNetwork ? HostKind.Ipv4Cidr : HostKind.Ipv6Cidr;
        return new HostEntry(trimmed, kind, null, ip, prefix);
    }

    private static void ValidateDnsName(string name)
    {
        // Lightweight RFC 1035-shaped check matching the prior Validate() rule: rejects obvious
        // typos (spaces, over-long inputs) without pulling in a full DNS-grammar library. The
        // surrounding flow has already tried IP parsing, so anything reaching here is genuinely
        // a candidate DNS name.
        var probe = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name;
        if (probe.Length == 0)
        {
            throw new FormatException($"'{name}' is not a valid host (DNS name, IP literal, or CIDR)");
        }
        if (name.Length > 253)
        {
            throw new FormatException($"DNS name '{name}' exceeds 253 characters");
        }
        if (name.AsSpan().ContainsAny(' ', '\t'))
        {
            throw new FormatException($"DNS name '{name}' contains whitespace");
        }
    }

    /// <summary>
    /// Returns the netmask for a given prefix length, in network-byte-order, as
    /// <paramref name="byteLength"/> bytes. <paramref name="byteLength"/> is the address-byte
    /// count (4 for IPv4, 16 for IPv6); <paramref name="prefix"/> the bit count to set.
    /// </summary>
    public static byte[] BuildNetmask(int byteLength, int prefix)
    {
        if (prefix < 0 || prefix > byteLength * 8)
        {
            throw new ArgumentOutOfRangeException(nameof(prefix));
        }
        var result = new byte[byteLength];
        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            result[i] = 0xFF;
        }
        var trailingBits = prefix - (fullBytes * 8);
        if (trailingBits > 0 && fullBytes < byteLength)
        {
            result[fullBytes] = (byte)(0xFF << (8 - trailingBits));
        }
        return result;
    }

    /// <summary>
    /// Returns true if every bit beyond the prefix is zero in <paramref name="addr"/>. Encodes
    /// the RFC 5280 §4.2.1.10 "host bits MUST be zero" requirement for iPAddress permittedSubtree
    /// entries.
    /// </summary>
    public static bool IsHostBitsZero(byte[] addr, int prefix)
    {
        var mask = BuildNetmask(addr.Length, prefix);
        for (var i = 0; i < addr.Length; i++)
        {
            if ((addr[i] & (byte)~mask[i]) != 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Address bytes ANDed with the netmask for <paramref name="prefix"/>. Used to render the
    /// canonical network address when surfacing a host-bits-set fix-it to the operator.
    /// </summary>
    public static byte[] MaskAddress(byte[] addr, int prefix)
    {
        var mask = BuildNetmask(addr.Length, prefix);
        var result = new byte[addr.Length];
        for (var i = 0; i < addr.Length; i++)
        {
            result[i] = (byte)(addr[i] & mask[i]);
        }
        return result;
    }

    /// <summary>
    /// Produces the iPAddress GeneralName payload for one NameConstraints permittedSubtree
    /// entry: <c>address || mask</c> in network-byte-order per RFC 5280 §4.2.1.10. 8 octets for
    /// IPv4 (4 addr + 4 mask), 32 octets for IPv6 (16 + 16). Caller must restrict this to
    /// IP-backed entries (Ipv4 / Ipv6 / Ipv4Cidr / Ipv6Cidr); throws otherwise.
    /// </summary>
    public static byte[] ToNameConstraintSubtreeBytes(HostEntry entry)
    {
        if (entry.Ip is null)
        {
            throw new InvalidOperationException(
                $"ToNameConstraintSubtreeBytes requires an IP-backed HostEntry, got Kind={entry.Kind}");
        }
        var addr = entry.Ip.GetAddressBytes();
        var mask = BuildNetmask(addr.Length, entry.PrefixLength);
        var combined = new byte[addr.Length + mask.Length];
        Buffer.BlockCopy(addr, 0, combined, 0, addr.Length);
        Buffer.BlockCopy(mask, 0, combined, addr.Length, mask.Length);
        return combined;
    }

    /// <summary>
    /// Renders a host as the host portion of a URL. IPv6 literals are bracket-wrapped per
    /// RFC 3986 §3.2.2 so the result is a valid URI authority; DNS names and IPv4 literals
    /// are returned verbatim. Throws on CIDR entries — they have no canonical URL form.
    /// </summary>
    public static string ToUrlHost(HostEntry entry) => entry.Kind switch
    {
        HostKind.Dns => entry.DnsName!,
        HostKind.Ipv4 => entry.Ip!.ToString(),
        HostKind.Ipv6 => $"[{entry.Ip}]",
        _ => throw new InvalidOperationException(
            $"HostEntry '{entry.Raw}' ({entry.Kind}) has no URL host representation"),
    };

    /// <summary>
    /// Selects the first leaf-eligible (non-CIDR) entry as the "primary host" used for the
    /// leaf cert subject CN, nginx <c>server_name</c>, and the <c>{scheme}://primary</c> URL
    /// derivations in <see cref="Phases.ConfigPhase.ResolveDerivedDefaults"/>. Returns
    /// <c>null</c> when no leaf-eligible entry exists (all CIDR or empty input).
    /// </summary>
    public static HostEntry? PickPrimary(IEnumerable<HostEntry> entries) =>
        entries.FirstOrDefault(e => e.IsLeafEligible);
}
