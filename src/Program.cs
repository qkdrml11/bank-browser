using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BankBrowser
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        public static Icon AppIcon;
        public static string ExePath { get { return Application.ExecutablePath; } }

        [STAThread]
        static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            // 예약 작업에서 호출되는 관리자 단계 (화면 없음)
            if (mode == "--svcstart" || mode == "--svcstop")
            {
                RunElevatedStep(mode == "--svcstart");
                return 0;
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { AppIcon = Icon.ExtractAssociatedIcon(ExePath); } catch { }
            if (AppIcon == null) AppIcon = SystemIcons.Shield;

            if (mode == "--launch")
            {
                string browser = args.Length > 1 ? args[1].ToLowerInvariant() : "chrome";
                if (!AppState.Load().Installed)
                {
                    MessageBox.Show("아직 설치가 완료되지 않았습니다. 설정 화면을 엽니다.", Paths.AppName);
                    RunElevated("--settings", false);
                    return 0;
                }
                Application.Run(new LaunchContext(browser));
                return 0;
            }

            // 설정/설치/제거 화면은 관리자 권한 필요
            if (!Paths.IsAdmin())
            {
                RunElevated(mode == "--uninstall" ? "--uninstall" : "--settings", false);
                return 0;
            }

            var form = new SettingsForm();
            if (mode == "--uninstall") form.StartUninstall = true;
            Application.Run(form);
            return 0;
        }

        static void RunElevatedStep(bool start)
        {
            string task = start ? "SvcStart" : "SvcStop";
            try
            {
                if (!Paths.IsAdmin()) { Log.Write(task + ": 관리자 권한이 아님"); return; }
                var st = AppState.Load();
                if (start)
                {
                    Engine.StartServices(st);
                    Engine.SaveSnapshot();   // 은행 브라우저 사용 중 새로 설치되는 프로그램을 찾기 위한 기록
                    Log.Write("보안 프로그램 서비스 켬");
                }
                else
                {
                    Engine.StopAll(st);
                    // 은행 브라우저 사용 중 새로 설치된 프로그램 자동 등록 (어느 사이트든)
                    int learned = Engine.LearnFromSnapshot(st);
                    // 탐지 규칙에 맞는 새 보안 프로그램 등록 + 업데이트로 다시 켜진 자동 실행 끄기
                    int added = Engine.Tidy(st) + learned;
                    if (added > 0 && st.AutoManageNew) { Log.Write("새 보안 프로그램 " + added + "개 자동 등록"); Engine.StopAll(st); }
                    st.Save();
                    Log.Write("보안 프로그램 끔");
                }
            }
            catch (Exception ex) { Log.Write(task + " 오류: " + ex); }
            finally
            {
                try
                {
                    Directory.CreateDirectory(Paths.UserDir);
                    File.WriteAllText(Engine.MarkerFile(task), DateTime.Now.ToString("o"));
                }
                catch { }
            }
        }

        /// <summary>관리자 권한으로 자기 자신을 실행. wait=true 면 끝날 때까지 기다림</summary>
        public static bool RunElevated(string args, bool wait)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(ExePath, args) { UseShellExecute = true, Verb = "runas" });
                if (wait && p != null) p.WaitForExit();
                return true;
            }
            catch (Win32Exception) { return false; } // 사용자가 UAC 창에서 '아니요'
        }
    }
}
