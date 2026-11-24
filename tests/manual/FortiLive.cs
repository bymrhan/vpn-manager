using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Accessibility;

// Manual checks against the running FortiClient window. NOT part of the automated tests: some
// commands open or close a real VPN tunnel. Build and run with tests\manual\run-forti-live.ps1.
//
//   probe   <tunnel>          what FortiGui.TryConnect would do; never presses anything
//   select  <tunnel>          FortiStore.Open(): selects the tunnel in FortiClient (may restart its window)
//   fill                      writes dummy text into Username/Password, reads it back; no Connect
//   clear   [username]        empties the password and sets the username (restore after "fill")
//   connect <tunnel>          REAL CONNECTION: FortiGui.TryConnect with press = true
//   disconnect                REAL DISCONNECT: presses Disconnect
static class FortiLive
{
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr h, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible o);
    [DllImport("oleacc.dll")] static extern int AccessibleChildren(IAccessible p, int s, int n, [Out] object[] k, out int got);

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0) { Console.WriteLine("kullanım: probe|select|fill|clear|connect|disconnect [tünel]"); return 2; }
        var arg = args.Length > 1 ? args[1] : "";
        string detail;
        switch (args[0])
        {
            case "probe":
                Console.WriteLine(FortiGui.TryConnect(arg, false, out detail) + " (" + detail + ")");
                return 0;
            case "connect":
                Console.WriteLine(FortiGui.TryConnect(arg, true, out detail) + " (" + detail + ")");
                return 0;
            case "select":
                Console.WriteLine("Open: " + (FortiStore.Open(new VpnProfile { Id = FortiStore.IdPrefixSsl + arg, Name = arg }) ?? "ok"));
                var combo = WaitFor(46, "VPN Name", 25);
                Console.WriteLine("seçili: " + (combo == null ? "(form yok)" : Val(combo)));
                return 0;
            case "disconnect":
                var b = WaitFor(43, "Disconnect", 10);
                if (b == null) { Console.WriteLine("Disconnect düğmesi yok"); return 1; }
                b.accDoDefaultAction(0);
                Console.WriteLine("Disconnect basıldı");
                return 0;
            case "fill":
            case "clear":
                var u = WaitFor(42, "Username", 10); var pw = WaitFor(42, "Password", 10);
                if (u == null || pw == null) { Console.WriteLine("alan yok"); return 1; }
                Console.WriteLine("önce  kullanıcı='" + Val(u) + "' parola uzunluğu=" + Val(pw).Length);
                u.set_accValue(0, args[0] == "fill" ? "deneme-kullanici" : arg);
                pw.set_accValue(0, args[0] == "fill" ? "deneme-parola" : "");
                Thread.Sleep(500);
                u = Find(42, "Username"); pw = Find(42, "Password");
                Console.WriteLine("sonra kullanıcı='" + Val(u) + "' parola uzunluğu=" + Val(pw).Length);
                return 0;
        }
        return 2;
    }

    static IAccessible Find(int role, string name)
    {
        var h = FortiStore.FindMainWindow(); if (h == IntPtr.Zero) return null;
        var g = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71"); IAccessible root;
        if (AccessibleObjectFromWindow(h, 0xFFFFFFFC, ref g, out root) != 0) return null;
        return Walk(root, role, name, 0);
    }

    static IAccessible Walk(IAccessible a, int role, string name, int depth)
    {
        if (depth > 30) return null;
        int n; try { n = a.accChildCount; } catch { return null; }
        if (n <= 0) return null;
        var kids = new object[n]; int got;
        if (AccessibleChildren(a, 0, n, kids, out got) != 0) return null;
        for (var i = 0; i < got; i++)
        {
            var c = kids[i] as IAccessible; if (c == null) continue;
            int r = 0; string nm = "";
            try { r = Convert.ToInt32(c.get_accRole(0)); nm = (c.get_accName(0) ?? "").Trim(); } catch { }
            if (r == role && nm == name) return c;
            var f = Walk(c, role, name, depth + 1); if (f != null) return f;
        }
        return null;
    }

    static IAccessible WaitFor(int role, string name, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { var a = Find(role, name); if (a != null) return a; Thread.Sleep(500); }
        return null;
    }

    static string Val(IAccessible a) { try { return a.get_accValue(0) ?? ""; } catch { return ""; } }
}
