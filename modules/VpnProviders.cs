using System;
using System.Collections.Generic;

// =====================================================================
// Provider routing: which VPN client a profile belongs to, the filter bar segments, row labels and
// icons, and the small decisions the app makes for Sophos Connect and WatchGuard SSL VPN (which
// profile a poll result means, when to poll). Pure helpers: no client is touched here.
// =====================================================================

internal enum VpnKind { OpenVpn, Forti, Sophos, WatchGuard }

internal static class VpnProviders
{
    // sccli spawns one process per connection on every status check: never more often than this.
    public const double SophosPollSeconds = 10;
    // WatchGuard's check is cheap (process lookup, adapter, log tail): fast while a command runs.
    public const double WatchGuardBusyPollSeconds = 2, WatchGuardIdlePollSeconds = 10;
    // Connect attempts that never show up as connected end after this; disconnects after DisconnectGiveUpSeconds.
    public const double ConnectGiveUpSeconds = 120, DisconnectGiveUpSeconds = 60;

    public static VpnKind KindOf(VpnProfile p)
    {
        if (p == null || p.Id == null) return VpnKind.OpenVpn;
        if (FortiStore.IsForti(p)) return VpnKind.Forti;
        if (SophosStore.IsSophos(p)) return VpnKind.Sophos;
        if (WatchGuardStore.IsWatchGuard(p)) return VpnKind.WatchGuard;
        return VpnKind.OpenVpn;
    }

    public static bool IsOpenVpn(VpnProfile p) { return KindOf(p) == VpnKind.OpenVpn; }

    // Sophos and WatchGuard: driven through ConnectExt / DisconnectExt / PollExt in the app.
    public static bool IsExternal(VpnKind k) { return k == VpnKind.Sophos || k == VpnKind.WatchGuard; }

    public static string SegmentName(VpnKind k)
    {
        switch (k)
        {
            case VpnKind.Forti: return "FortiClient";
            case VpnKind.Sophos: return "Sophos";
            case VpnKind.WatchGuard: return "WatchGuard";
            default: return "OpenVPN";
        }
    }

    // Name used in status messages and menus.
    public static string Label(VpnKind k)
    {
        switch (k)
        {
            case VpnKind.Forti: return "FortiClient";
            case VpnKind.Sophos: return "Sophos Connect";
            case VpnKind.WatchGuard: return "WatchGuard";
            default: return "OpenVPN";
        }
    }

    // Group header in the unfiltered list.
    public static string GroupTitle(VpnKind k)
    {
        switch (k)
        {
            case VpnKind.Forti: return "FORTICLIENT VPN";
            case VpnKind.Sophos: return "SOPHOS CONNECT";
            case VpnKind.WatchGuard: return "WATCHGUARD SSL VPN";
            default: return "OPENVPN PROFİLLERİ";
        }
    }

    // Icon (assets\*.svg) for the row avatar badge.
    public static string Asset(VpnKind k)
    {
        switch (k)
        {
            case VpnKind.Forti: return "provider-forti";
            case VpnKind.Sophos: return "provider-sophos";
            case VpnKind.WatchGuard: return "provider-watchguard";
            default: return "provider-openvpn";
        }
    }

    // First part of a row's second line: the provider for non-OpenVPN rows (Forti keeps its
    // Source text, e.g. "FortiClient SSL"), null for OpenVPN rows (they show the host only).
    public static string RowLabel(VpnProfile p)
    {
        var k = KindOf(p);
        if (k == VpnKind.OpenVpn) return null;
        if (!string.IsNullOrEmpty(p.Source)) return p.Source;
        return k == VpnKind.Sophos ? "Sophos Connect" : k == VpnKind.WatchGuard ? "WatchGuard SSL" : SegmentName(k);
    }

    // Filter bar segments: "Tümü" (null), OpenVPN, then each enabled provider in a fixed order.
    public static List<VpnKind?> Segments(bool forti, bool sophos, bool watchGuard)
    {
        var s = new List<VpnKind?> { null, VpnKind.OpenVpn };
        if (forti) s.Add(VpnKind.Forti);
        if (sophos) s.Add(VpnKind.Sophos);
        if (watchGuard) s.Add(VpnKind.WatchGuard);
        return s;
    }

    public static string[] SegmentNames(IList<VpnKind?> segments)
    {
        var names = new string[segments.Count];
        for (var i = 0; i < names.Length; i++) names[i] = segments[i].HasValue ? SegmentName(segments[i].Value) : "Tümü";
        return names;
    }

    // Index of a segment after the bar changed; "Tümü" (0) when it is gone.
    public static int IndexOf(IList<VpnKind?> segments, VpnKind? kind)
    {
        if (!kind.HasValue) return 0;
        for (var i = 0; i < segments.Count; i++) if (segments[i] == kind) return i;
        return 0;
    }

    public static VpnKind? SegmentAt(IList<VpnKind?> segments, int index)
    {
        return segments != null && index >= 0 && index < segments.Count ? segments[index] : null;
    }

    public static bool InSegment(VpnProfile p, VpnKind? segment) { return !segment.HasValue || KindOf(p) == segment.Value; }

    // The profile id a status poll means. key: connection name / server from the client, "" when
    // the client is connected but did not say to what, null when nothing is connected.
    // An unnamed connection keeps the profile already shown, else the one being connected, else
    // the only profile of the provider; otherwise it is not attributed to any row.
    public static string ResolveConnectedId(string key, IList<VpnProfile> owned, Func<VpnProfile, string> keyOf, string previousId, string connectingId)
    {
        if (key == null || owned == null || owned.Count == 0) return null;
        if (key.Length > 0)
        {
            foreach (var p in owned) if (string.Equals(keyOf(p), key, StringComparison.OrdinalIgnoreCase)) return p.Id;
            return null;
        }
        foreach (var id in new[] { previousId, connectingId })
            if (id != null) foreach (var p in owned) if (p.Id == id) return id;
        return owned.Count == 1 ? owned[0].Id : null;
    }

    // Whether a status poll is due. busy: a connect or disconnect of this provider is in flight;
    // soon: a command just finished. Sophos never polls faster than SophosPollSeconds.
    public static bool DuePoll(VpnKind k, bool busy, bool soon, double secondsSinceLast)
    {
        if (k == VpnKind.Sophos) return secondsSinceLast >= SophosPollSeconds;
        if (soon) return secondsSinceLast >= 1;
        return secondsSinceLast >= (busy ? WatchGuardBusyPollSeconds : WatchGuardIdlePollSeconds);
    }

    // A shown connection is only dropped after this many "none" polls in a row (one transient
    // miss must not end the session in the history and start a new one on the next poll).
    public const int NoneToDrop = 2;

    public enum PollUse { Apply, Keep }

    // What to do with a status poll. ok: the check itself worked (false = failed / timed out);
    // key: its result (null = none connected); hadConnected: a row is shown connected;
    // expectingNone: a disconnect of this provider is in flight; stale: the poll started before the
    // last connect / disconnect command finished. noneStreak counts consecutive "none" results.
    // Keep = leave the shown state as it is.
    public static PollUse UsePoll(bool ok, string key, bool hadConnected, bool expectingNone, bool stale, ref int noneStreak)
    {
        if (stale || !ok) return PollUse.Keep;
        if (key != null || !hadConnected || expectingNone) { noneStreak = 0; return PollUse.Apply; }
        noneStreak++;
        if (noneStreak < NoneToDrop) return PollUse.Keep;
        noneStreak = 0;
        return PollUse.Apply;
    }

    // Sophos / WatchGuard keep no last-connected time: it comes from the connection history.
    public static void ApplyLastConnected(IEnumerable<VpnProfile> profiles, IDictionary<string, long> lastStartMs)
    {
        if (profiles == null || lastStartMs == null) return;
        foreach (var p in profiles)
        {
            long ms;
            if (IsExternal(KindOf(p)) && lastStartMs.TryGetValue(p.Id, out ms) && ms > p.LastConnected) p.LastConnected = ms;
        }
    }

    // Credential Manager prefix and key (connection name / server entry) for a profile.
    public static string CredentialPrefix(VpnKind k)
    {
        return k == VpnKind.Sophos ? SophosStore.CredentialPrefix : k == VpnKind.WatchGuard ? WatchGuardStore.CredentialPrefix : null;
    }

    public static string CredentialKey(VpnProfile p)
    {
        var k = KindOf(p);
        if (k == VpnKind.Sophos) return SophosStore.ConnectionName(p);
        if (k == VpnKind.WatchGuard) return WatchGuardStore.ServerOf(p);
        if (k == VpnKind.Forti) return FortiStore.TunnelName(p);
        return p == null ? "" : p.Name ?? "";
    }
}
