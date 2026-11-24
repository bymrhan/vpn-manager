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
    static readonly HashSet<string> pins = new HashSet<string>();

    // name, host, user, kind (o = OpenVPN, f = FortiClient), minutes since last connection (0 = never), reachable
    static readonly object[][] Rows = new object[][]
    {
        new object[] { "Acme Lojistik",           "vpn.acme-lojistik.example",   "ayse.demir",     'o', 35,      true  },
        new object[] { "Contoso Enerji",          "vpn.contoso-enerji.example",  "mehmet.kaya",    'o', 190,     true  },
        new object[] { "Northwind Gıda",          "gw.northwind-gida.example",   "elif.yildiz",    'o', 1500,    true  },
        new object[] { "Fabrikam Yazılım",        "vpn.fabrikam.example:10443",  "can.ozturk",     'f', 2900,    true  },
        new object[] { "Globex Otomotiv",         "vpn.globex-otomotiv.example", "zeynep.arslan",  'o', 5200,    true  },
        new object[] { "Initech Danışmanlık",     "vpn.initech.example",         "burak.sahin",    'o', 0,       true  },
        new object[] { "Umbrella Sağlık",         "uzak.umbrella-saglik.example","selin.acar",     'o', 0,       false },
        new object[] { "Wayne Makine",            "vpn.wayne-makine.example:8443","ali.cetin",     'f', 0,       true  },
        new object[] { "Stark Elektrik",          "vpn.stark-elektrik.example",  "deniz.polat",    'o', 0,       true  },
        new object[] { "Tailspin Turizm",         "vpn.tailspin.example",        "ece.korkmaz",    'o', 0,       true  },
        new object[] { "Wingtip Tekstil",         "gw.wingtip-tekstil.example",  "emre.aydin",     'o', 0,       true  },
        new object[] { "Adventure Works İnşaat",  "vpn.adventure-works.example", "gizem.kurt",     'o', 0,       false },
        new object[] { "Litware Gayrimenkul",     "vpn.litware.example",         "oguz.erdem",     'o', 0,       true  },
        new object[] { "Proseware Eğitim",        "vpn.proseware.example:4443",  "pinar.ates",     'f', 0,       true  },
        new object[] { "Woodgrove Finans",        "vpn.woodgrove.example",       "kerem.bulut",    'o', 0,       true  },
        new object[] { "Blue Yonder Havacılık",   "vpn.blue-yonder.example",     "naz.tuna",       'o', 0,       true  },
    };

    public static List<VpnProfile> Profiles()
    {
        var now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        var list = new List<VpnProfile>();
        foreach (var r in Rows)
        {
            var name = (string)r[0]; var host = (string)r[1]; var forti = (char)r[3] == 'f';
            var slug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            var p = new VpnProfile
            {
                Id = (forti ? "forti-ssl:" : "demo-") + slug,
                Name = name, Host = host, Username = (string)r[2],
                Source = forti ? "" : slug + ".ovpn",
                LastConnected = (int)r[4] > 0 ? now - (long)(int)r[4] * 60000L : 0
            };
            p.Initials = ProfileStore.MakeInitials(p.Name);
            p.Keys = new[] { ProfileStore.SearchKey(p.Name), ProfileStore.SearchKey(p.Host), ProfileStore.SearchKey(p.Username), ProfileStore.SearchKey(p.Source) };
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
        foreach (var r in Rows) if (id.EndsWith(Slug((string)r[0]), StringComparison.Ordinal)) return (bool)r[5] ? ReachState.Up : ReachState.Down;
        return ReachState.Unknown;
    }

    static string Slug(string name) { return new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()); }
}
