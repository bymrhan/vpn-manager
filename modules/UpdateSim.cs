using System;
using System.Windows.Forms;

// =====================================================================
// Update simulation (--demo-update, --demo-update=fail): development only, needs Demo.On.
// Shows the real UpdateNotice for a fake release and plays the stages on timers. No network,
// no file writes, no process start; the app never exits. The real update code is not involved.
// =====================================================================

internal static class UpdateSim
{
    const long Total = 9437184;           // 9 MiB
    const int DownloadMs = 6000, StepMs = 1200, TickMs = 100;
    static bool failRun;
    static UpdateNotice notice;
    static ReleaseInfo release;
    static Timer timer;
    static UpdateStage phase;             // stage being played (Offer = idle)
    static int phaseMs;

    public static void Start(bool fail)
    {
        failRun = fail;
        release = new ReleaseInfo
        {
            Tag = "v9.9.9", Version = "9.9.9",
            HtmlUrl = "https://github.com/bymrhan/vpn-manager/releases",
            Notes = "- İndirme ilerlemesi ve iptal\n- Hata durumunda tekrar deneme\n- Güncelleme sonrası bilgi mesajı",
            ZipUrl = "https://example.invalid/demo.zip", ShaUrl = "https://example.invalid/demo.zip.sha256"   // never fetched
        };
        var delay = new Timer { Interval = 1500 };
        delay.Tick += delegate { delay.Stop(); delay.Dispose(); ShowOffer(); };
        delay.Start();
    }

    static void ShowOffer()
    {
        if (notice != null && !notice.IsDisposed) return;
        var n = new UpdateNotice(release, UpdateLogic.CurrentVersion, true);
        n.UpdateNow += delegate { Play(n); };
        n.Retry += delegate { Play(n); };
        n.Cancel += delegate { Stop(); n.SetStage(UpdateStage.Offer, 0, 0, release.Version); };
        n.Notes += delegate { };   // demo: never opens the browser
        n.FormClosed += delegate { Stop(); if (notice == n) notice = null; n.Dispose(); };
        notice = n;
        n.Show();
        // Test-harness helper (simulation only): press "Şimdi güncelle" by itself after 1 s.
        if (Environment.GetEnvironmentVariable("VPNM_DEMO_UPDATE_AUTOSTART") == "1")
        {
            var auto = new Timer { Interval = 1000 };
            auto.Tick += delegate { auto.Stop(); auto.Dispose(); if (!n.IsDisposed) Play(n); };
            auto.Start();
        }
    }

    static void Stop()
    {
        if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
    }

    static void Play(UpdateNotice n)
    {
        Stop();
        phase = UpdateStage.Downloading; phaseMs = 0;
        n.SetStage(UpdateStage.Downloading, 0, Total, release.Version);
        timer = new Timer { Interval = TickMs };
        timer.Tick += delegate { Tick(n); };
        timer.Start();
    }

    static void Tick(UpdateNotice n)
    {
        if (n.IsDisposed) { Stop(); return; }
        phaseMs += TickMs;
        if (phase == UpdateStage.Downloading)
        {
            var frac = Math.Min(1.0, phaseMs / (double)DownloadMs);
            if (failRun && frac >= 0.6)
            {
                Stop();
                n.SetStage(UpdateStage.Downloading, (long)(Total * 0.6), Total, release.Version);
                n.SetFailed("Bağlantı kesildi (sunucu yanıt vermedi)");
                return;
            }
            n.SetStage(UpdateStage.Downloading, (long)(Total * frac), Total, release.Version);
            if (frac >= 1.0) Next(n, UpdateStage.Verifying);
        }
        else if (phaseMs >= StepMs)
        {
            if (phase == UpdateStage.Verifying) Next(n, UpdateStage.Preparing);
            else if (phase == UpdateStage.Preparing) Next(n, UpdateStage.Restarting);
            else
            {
                Stop();
                n.SetStage(UpdateStage.Offer, 0, 0, release.Version);   // the app does not exit in the simulation
            }
        }
    }

    static void Next(UpdateNotice n, UpdateStage s)
    {
        phase = s; phaseMs = 0;
        n.SetStage(s, 0, 0, release.Version);
    }
}
