using System;
using System.Collections.Generic;
using System.Linq;

// =====================================================================
// Demo mode (--demo): fictional sample profiles for screenshots and trying the UI out.
// Nothing is read from or written to OpenVPN Connect, FortiClient or the pin list, and no
// connection is ever started: "connecting" only changes the on-screen state.
// =====================================================================

internal static class Demo
{
    public static bool On;
    public static string ConnectedName;                 // --demo-connected=<name part>
    public static string Query;                         // --demo-search=<text>
    public static int SettingsPage = -1;                // --demo-settings=<page 0..3>
    public static bool Empty;                           // --demo-empty: no profiles, "unknown layout" empty state
    static readonly HashSet<string> pins = new HashSet<string>();

    // name, host, user, kind (o = OpenVPN, f = FortiClient, s = Sophos Connect, w = WatchGuard SSL VPN),
    // minutes since last connection (0 = never), reachable, fictional sessions in the last 60 days
    // (Demo.History; 3+ puts a profile in "SIK KULLANILANLAR" unless it is pinned or recent)
    static readonly object[][] Rows = new object[][]
    {
        new object[] { "Acme Lojistik",           "vpn.acme-lojistik.example",   "ayse.demir",     'o', 35,      true,  14 },
        new object[] { "Contoso Enerji",          "vpn.contoso-enerji.example",  "mehmet.kaya",    'o', 190,     true,  6  },
        new object[] { "Northwind Gıda",          "gw.northwind-gida.example",   "elif.yildiz",    'o', 1500,    true,  9  },
        new object[] { "Fabrikam Yazılım",        "vpn.fabrikam.example:10443",  "can.ozturk",     'f', 2900,    true,  2  },
        new object[] { "Globex Otomotiv",         "vpn.globex-otomotiv.example", "zeynep.arslan",  'o', 5200,    true,  3  },
        new object[] { "Initech Danışmanlık",     "vpn.initech.example",         "burak.sahin",    'o', 8600,    true,  12 },
        new object[] { "Umbrella Sağlık",         "uzak.umbrella-saglik.example","selin.acar",     'o', 0,       false, 0  },
        new object[] { "Wayne Makine",            "vpn.wayne-makine.example:8443","ali.cetin",     'f', 9500,    true,  5  },
        new object[] { "Stark Elektrik",          "vpn.stark-elektrik.example",  "deniz.polat",    'o', 11000,   true,  7  },
        new object[] { "Tailspin Turizm",         "vpn.tailspin.example",        "ece.korkmaz",    'o', 20000,   true,  2  },
        new object[] { "Wingtip Tekstil",         "gw.wingtip-tekstil.example",  "emre.aydin",     'o', 0,       true,  0  },
        new object[] { "Adventure Works İnşaat",  "vpn.adventure-works.example", "gizem.kurt",     'o', 0,       false, 0  },
        new object[] { "Litware Gayrimenkul",     "vpn.litware.example",         "oguz.erdem",     'o', 0,       true,  0  },
        new object[] { "Proseware Eğitim",        "vpn.proseware.example:4443",  "pinar.ates",     'f', 0,       true,  0  },
        new object[] { "Woodgrove Finans",        "vpn.woodgrove.example",       "kerem.bulut",    'o', 13000,   true,  4  },
        new object[] { "Blue Yonder Havacılık",   "vpn.blue-yonder.example",     "naz.tuna",       'o', 0,       true,  0  },
        new object[] { "Fourth Coffee Gıda",      "gw.fourth-coffee.example",    "hakan.yilmaz",   's', 720,     true,  1  },
        new object[] { "Alpine Ski Turizm",       "sslvpn.alpine-ski.example:443","irem.koc",      's', 0,       false, 0  },
        new object[] { "Margie Seyahat",          "ssl.margie-seyahat.example:443","tolga.sen",    'w', 4300,    true,  2  },
        new object[] { "Lucerne Yayıncılık",      "vpn.lucerne.example:4100",    "derya.uslu",     'w', 15000,   true,  3  },
    };

    // Fictional connection history (never read from or written to history.csv): per row as many
    // 40-minute sessions as its count, the newest at its last connection, then every 2.5 days back.
    static List<HistoryEntry> history;
    public static List<HistoryEntry> History()
    {
        if (history != null) return history;
        var now = DateTime.UtcNow; var list = new List<HistoryEntry>();
        foreach (var r in Rows)
        {
            var count = (int)r[6]; var minutes = (int)r[4];
            if (count <= 0 || minutes <= 0) continue;
            for (var i = 0; i < count; i++)
            {
                var start = now.AddMinutes(-minutes).AddHours(-60 * i);
                list.Add(new HistoryEntry { ProfileId = IdFor(r), ProfileName = (string)r[0], StartUtc = start, EndUtc = start.AddMinutes(40) });
            }
        }
        history = list;
        return list;
    }

    // Ids use the real provider prefixes (forti-ssl:, sophos:, watchguard-ssl:) so the app routes
    // the rows like real ones; in demo mode Connect never reaches a client.
    static string IdFor(object[] r)
    {
        var slug = Slug((string)r[0]);
        switch ((char)r[3])
        {
            case 'f': return "forti-ssl:" + slug;
            case 's': return SophosStore.IdPrefix + slug;
            case 'w': return WatchGuardStore.IdPrefix + (string)r[1];
            default: return "demo-" + slug;
        }
    }

    public static List<VpnProfile> Profiles()
    {
        var now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        var list = new List<VpnProfile>();
        foreach (var r in Rows)
        {
            var name = (string)r[0]; var host = (string)r[1]; var ovpn = (char)r[3] == 'o';
            var slug = Slug(name);
            var p = new VpnProfile
            {
                Id = IdFor(r),
                Name = name, Host = host, Username = (string)r[2],
                Source = ovpn ? slug + ".ovpn" : "",
                LastConnected = (int)r[4] > 0 ? now - (long)(int)r[4] * 60000L : 0
            };
            p.Initials = ProfileStore.MakeInitials(p.Name);
            p.Keys = new[] { ProfileStore.SearchKey(p.Name), ProfileStore.SearchKey(p.Host), ProfileStore.SearchKey(p.Username), ProfileStore.SearchKey(p.Source), ProfileStore.SearchKey(VpnProviders.Label(VpnProviders.KindOf(p))) };
            list.Add(p);
        }
        return list;
    }

    public static HashSet<string> Pins()
    {
        if (pins.Count == 0) { pins.Add("demo-acme-lojistik"); pins.Add("demo-contoso-enerji"); }
        return new HashSet<string>(pins);
    }

    public static ReachState Reach(string id)
    {
        foreach (var r in Rows) if (id == IdFor(r)) return (bool)r[5] ? ReachState.Up : ReachState.Down;
        return ReachState.Unknown;
    }

    static string Slug(string name) { return new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()); }
}
