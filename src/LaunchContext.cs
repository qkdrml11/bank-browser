using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BankBrowser
{
    /// <summary>은행 브라우저 실행 모드: 트레이 아이콘으로 상태를 보여주고, 창이 모두 닫히면 보안 프로그램을 끈다</summary>
    public class LaunchContext : ApplicationContext
    {
        readonly string browser;
        readonly NotifyIcon tray;
        readonly Control ui;
        volatile bool stopRequested;
        string browserExe, profileDir;
        BrowserDef def;

        public LaunchContext(string browser)
        {
            this.browser = browser;
            ui = new Control();
            ui.CreateControl();
            var unused = ui.Handle;

            var menu = new ContextMenuStrip();
            menu.Items.Add("설정 열기", null, delegate { Program.RunElevated("--settings", false); });
            menu.Items.Add("은행 업무 끝내기 (브라우저 닫기)", null, delegate { stopRequested = true; });

            tray = new NotifyIcon
            {
                Icon = Program.AppIcon,
                Text = "은행 브라우저",
                ContextMenuStrip = menu,
                Visible = true
            };

            new Thread(Work) { IsBackground = true }.Start();
        }

        void Notify(string text, ToolTipIcon icon = ToolTipIcon.Info)
        {
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
                    tray.ShowBalloonTip(4000, Paths.AppName, text, icon);
                });
            }
            catch { }
        }

        void Work()
        {
            try { Run(); }
            catch (Exception ex)
            {
                Log.Write("실행 오류: " + ex);
                Notify("오류가 발생했습니다: " + ex.Message, ToolTipIcon.Error);
                Thread.Sleep(4000);
            }
            finally
            {
                try
                {
                    ui.BeginInvoke((MethodInvoker)delegate
                    {
                        tray.Visible = false;
                        tray.Dispose();
                        ExitThread();
                    });
                }
                catch { }
            }
        }

        void Run()
        {
            var st0 = AppState.Load();
            def = Browsers.Find(st0, browser);
            if (def == null)
            {
                Notify("브라우저를 찾을 수 없습니다. 설정에서 다른 브라우저를 골라 주세요.", ToolTipIcon.Error);
                Thread.Sleep(5000);
                return;
            }
            browserExe = def.Path;
            string exeName = Path.GetFileName(browserExe);
            profileDir = Browsers.ProfileDir(def.Key);
            Directory.CreateDirectory(profileDir);
            string startPage = StartPage.Write();
            string args = Browsers.BuildArgs(def, profileDir, startPage);

            // 이미 열려 있으면 새 창만 띄운다 (관리는 먼저 실행된 쪽이 함)
            if (Engine.ProfileProcesses(exeName, profileDir).Count > 0)
            {
                Process.Start(new ProcessStartInfo(browserExe, args) { UseShellExecute = false });
                return;
            }

            var st = AppState.Load();
            if (Engine.AnyBankBrowserRunning(st))
            {
                // 다른 브라우저로 은행 모드가 이미 켜져 있음 → 보안 프로그램은 이미 실행 중
                Log.Write("다른 은행 브라우저 실행 중 → 보안 프로그램 켜기 생략");
            }
            else
            {
                Notify("보안 프로그램을 켜는 중입니다...");
                if (!Engine.RunHelperTask("SvcStart"))
                {
                    Log.Write("예약 작업 실패 → 관리자 권한으로 직접 실행");
                    Program.RunElevated("--svcstart", true);
                }
                Engine.StartUserItems(st);
                Thread.Sleep(1500);
            }

            Process.Start(new ProcessStartInfo(browserExe, args) { UseShellExecute = false });
            Notify("은행 모드 실행 중 — 브라우저 창을 모두 닫으면 보안 프로그램이 자동으로 꺼집니다.");

            WaitUntilClosed(exeName);

            Notify("브라우저가 닫혔습니다. 보안 프로그램을 끄는 중...");
            foreach (int pid in Engine.ProfileProcesses(exeName, profileDir))
                try { Process.GetProcessById(pid).Kill(); } catch { }
            Thread.Sleep(500);

            if (Engine.AnyBankBrowserRunning(AppState.Load()))
            {
                Notify("다른 은행 브라우저가 아직 열려 있어서 보안 프로그램은 그대로 둡니다.");
            }
            else
            {
                st = AppState.Load();
                Engine.KillSecurityProcesses(st, false);
                if (!Engine.RunHelperTask("SvcStop")) Program.RunElevated("--svcstop", true);
                Notify("보안 프로그램을 모두 껐습니다. 수고하셨습니다!");
            }
            Thread.Sleep(4000);
        }

        void WaitUntilClosed(string exeName)
        {
            DateTime began = DateTime.Now;
            bool sawWindow = false;
            int noWindow = 0;
            while (true)
            {
                Thread.Sleep(2000);
                if (stopRequested) return;
                var pids = Engine.ProfileProcesses(exeName, profileDir);
                double secs = (DateTime.Now - began).TotalSeconds;
                if (pids.Count == 0)
                {
                    if (sawWindow || secs > 30) return;
                    continue;
                }
                if (HasWindow(pids)) { sawWindow = true; noWindow = 0; continue; }
                if (!sawWindow && secs < 30) continue;
                if (++noWindow >= 3) return;   // 창 없이 백그라운드만 남은 상태가 6초 지속
            }
        }

        static bool HasWindow(List<int> pids)
        {
            foreach (int pid in pids)
            {
                try
                {
                    using (var p = Process.GetProcessById(pid))
                        if (p.MainWindowHandle != IntPtr.Zero) return true;
                }
                catch { }
            }
            return false;
        }
    }

    static class StartPage
    {
        public static string Write()
        {
            string file = Path.Combine(Paths.UserDir, "start.html");
            try
            {
                Directory.CreateDirectory(Paths.UserDir);
                File.WriteAllText(file, Html, new UTF8Encoding(false));
                return new Uri(file).AbsoluteUri;
            }
            catch { return "https://www.gov.kr"; }
        }

        const string Html = @"<!DOCTYPE html>
<html lang=""ko""><head><meta charset=""utf-8""><title>은행 전용 브라우저</title>
<style>
body{font-family:'Malgun Gothic',sans-serif;background:#f4f6f8;margin:0;padding:40px;color:#222}
h1{font-size:22px;margin:0 0 6px}p{color:#666;margin:0 0 24px;font-size:14px}
h2{font-size:15px;color:#555;margin:24px 0 10px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(170px,1fr));gap:10px;max-width:900px}
a{display:block;background:#fff;border:1px solid #dde2e7;border-radius:10px;padding:16px;text-decoration:none;color:#222;font-weight:bold}
a:hover{border-color:#3b7ddd;color:#3b7ddd}
</style></head><body>
<h1>은행 전용 브라우저</h1>
<p>보안 프로그램이 켜져 있습니다. 이 창을 모두 닫으면 보안 프로그램도 함께 꺼집니다.<br>
여기 없는 사이트도 그대로 쓰시면 됩니다. 보안 프로그램 설치를 요구하면 설치하세요. 창을 닫을 때 자동으로 관리 목록에 추가됩니다.<br>
자주 가는 사이트는 즐겨찾기(Ctrl+D)에 추가해 두세요.</p>
<h2>관공서</h2><div class=""grid"">
<a href=""https://www.gov.kr"">정부24</a><a href=""https://www.hometax.go.kr"">홈택스</a><a href=""https://www.wetax.go.kr"">위택스</a>
<a href=""https://www.nhis.or.kr"">국민건강보험</a><a href=""https://www.nps.or.kr"">국민연금</a></div>
<h2>은행</h2><div class=""grid"">
<a href=""https://www.kbstar.com"">KB국민은행</a><a href=""https://www.shinhan.com"">신한은행</a><a href=""https://www.wooribank.com"">우리은행</a>
<a href=""https://www.kebhana.com"">하나은행</a><a href=""https://banking.nonghyup.com"">NH농협은행</a><a href=""https://www.ibk.co.kr"">IBK기업은행</a>
<a href=""https://www.busanbank.co.kr"">BNK부산은행</a><a href=""https://www.knbank.co.kr"">BNK경남은행</a><a href=""https://www.kjbank.com"">광주은행</a><a href=""https://www.kfcc.co.kr"">새마을금고</a><a href=""https://www.cu.co.kr"">신협</a>
<a href=""https://www.epostbank.go.kr"">우체국</a></div>
<h2>보험</h2><div class=""grid"">
<a href=""https://cont.insure.or.kr"">내보험찾아줌</a><a href=""https://www.samsunglife.com"">삼성생명</a><a href=""https://www.hanwhalife.com"">한화생명</a>
<a href=""https://www.kyobo.com"">교보생명</a><a href=""https://www.shinhanlife.co.kr"">신한라이프</a><a href=""https://www.nhlife.co.kr"">NH농협생명</a>
<a href=""https://www.samsungfire.com"">삼성화재</a><a href=""https://www.hi.co.kr"">현대해상</a><a href=""https://www.idbins.com"">DB손해보험</a>
<a href=""https://www.kbinsure.co.kr"">KB손해보험</a><a href=""https://www.meritzfire.com"">메리츠화재</a></div>
</body></html>";
    }
}
