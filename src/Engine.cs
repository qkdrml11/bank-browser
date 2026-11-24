using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace BankBrowser
{
    public static class Engine
    {
        // ------------------------------------------------------------ 탐지 규칙
        static readonly string[] Include = {
            "Safe Transaction", @"\bASTx\b", "AhnLab Online Security",
            "TouchEn", "nxKey", "RaonSecure", "nProtect",
            "IPinside", "Interezen",
            "Veraport", "Wizvera", "Delfino",
            "MagicLine", "DreamSecurity",
            "INISAFE", "Initech", "CrossWeb", "CrossEX",
            "XecureWeb", "SoftForum", "AnySign",
            "SignKorea", @"\bKICA\b", "Yettiesoft", "KeySharp",
            "MarkAny", "ePageSAFER", @"\bKSign", "SecuKit", "K-Defense", "UbiKey"
        };
        static readonly string[] Exclude = { "AhnLab V3", "V3 Lite", "V3Lite", "GameGuard", "BankBrowser" };
        static readonly Regex[] IncludeRx = Include.Select(p => new Regex(p, RegexOptions.IgnoreCase)).ToArray();
        static readonly Regex[] ExcludeRx = Exclude.Select(p => new Regex(p, RegexOptions.IgnoreCase)).ToArray();

        public static bool IsMatch(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var r in ExcludeRx) if (r.IsMatch(text)) return false;
            foreach (var r in IncludeRx) if (r.IsMatch(text)) return true;
            return false;
        }

        // ------------------------------------------------------------ 레지스트리
        const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string Run32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedBase = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        static readonly string[][] RunLocations = {
            new[] { "HKLM|" + RunPath,   "HKLM|" + ApprovedBase + "Run" },
            new[] { "HKLM|" + Run32Path, "HKLM|" + ApprovedBase + "Run32" },
            new[] { "HKCU|" + RunPath,   "HKCU|" + ApprovedBase + "Run" },
        };

        static RegistryKey OpenKey(string loc, bool write, bool create)
        {
            int i = loc.IndexOf('|');
            var hive = loc.Substring(0, i) == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            string path = loc.Substring(i + 1);
            var b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            if (write && create) return b.CreateSubKey(path);
            return b.OpenSubKey(path, write);
        }

        static byte[] GetApprovedBytes(string approved, string name)
        {
            try { using (var k = OpenKey(approved, false, false)) return k == null ? null : k.GetValue(name) as byte[]; }
            catch { return null; }
        }

        public static bool IsApprovedDisabled(string approved, string name)
        {
            var v = GetApprovedBytes(approved, name);
            return v != null && v.Length > 0 && (v[0] % 2) == 1;
        }

        static void SetApprovedDisabled(string approved, string name)
        {
            var bytes = new byte[12];
            bytes[0] = 3;
            Array.Copy(BitConverter.GetBytes(DateTime.Now.ToFileTime()), 0, bytes, 4, 8);
            using (var k = OpenKey(approved, true, true)) k.SetValue(name, bytes, RegistryValueKind.Binary);
        }

        static void RestoreApproved(Item it)
        {
            using (var k = OpenKey(it.Approved, true, true))
            {
                if (string.IsNullOrEmpty(it.OriginalApproved)) k.DeleteValue(it.Name, false);
                else k.SetValue(it.Name, Convert.FromBase64String(it.OriginalApproved), RegistryValueKind.Binary);
            }
        }

        // ------------------------------------------------------------ 서비스 설정
        const uint SC_MANAGER_CONNECT = 0x0001, SERVICE_QUERY_CONFIG = 0x0001, SERVICE_CHANGE_CONFIG = 0x0002;
        const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        struct DelayedInfo { public int fDelayedAutostart; }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenSCManager(string machine, string db, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenService(IntPtr scm, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ChangeServiceConfig(IntPtr svc, uint type, uint start, uint error, string bin, string group,
            IntPtr tag, string deps, string user, string pwd, string display);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ChangeServiceConfig2(IntPtr svc, uint level, ref DelayedInfo info);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool CloseServiceHandle(IntPtr h);

        /// <returns>시작 유형 (2 자동, 3 수동, 4 사용 안 함), 서비스가 없으면 -1</returns>
        public static int GetServiceStart(string name, out bool delayed)
        {
            delayed = false;
            try
            {
                using (var k = OpenKey(@"HKLM|SYSTEM\CurrentControlSet\Services\" + name, false, false))
                {
                    if (k == null) return -1;
                    delayed = Convert.ToInt32(k.GetValue("DelayedAutostart", 0)) == 1;
                    return Convert.ToInt32(k.GetValue("Start", -1));
                }
            }
            catch { return -1; }
        }

        static bool SetServiceStart(string name, int start, bool delayed)
        {
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return false;
            try
            {
                IntPtr svc = OpenService(scm, name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);
                if (svc == IntPtr.Zero) { Log.Write("서비스 열기 실패 " + name + ": " + new Win32Exception().Message); return false; }
                try
                {
                    if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, (uint)start, SERVICE_NO_CHANGE,
                            null, null, IntPtr.Zero, null, null, null, null))
                    {
                        Log.Write("서비스 변경 실패 " + name + ": " + new Win32Exception().Message);
                        return false;
                    }
                    if (start == 2)
                    {
                        var info = new DelayedInfo { fDelayedAutostart = delayed ? 1 : 0 };
                        ChangeServiceConfig2(svc, 3 /* SERVICE_CONFIG_DELAYED_AUTO_START_INFO */, ref info);
                    }
                    return true;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        // ------------------------------------------------------------ 공통 도우미
        static List<ManagementObject> Wmi(string query)
        {
            var r = new List<ManagementObject>();
            try
            {
                using (var s = new ManagementObjectSearcher(query))
                    foreach (ManagementObject o in s.Get()) r.Add(o);
            }
            catch (Exception ex) { Log.Write("WMI 실패: " + ex.Message); }
            return r;
        }

        static string Str(ManagementObject o, string prop)
        {
            try { var v = o[prop]; return v == null ? "" : v.ToString(); } catch { return ""; }
        }

        public static void SplitCommand(string cmd, out string exe, out string args)
        {
            cmd = Environment.ExpandEnvironmentVariables((cmd ?? "").Trim());
            exe = cmd; args = "";
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0) { exe = cmd.Substring(1, end - 1); args = cmd.Substring(end + 1).Trim(); return; }
            }
            var m = Regex.Match(cmd, @"^(.+?\.exe)(\s+.*)?$", RegexOptions.IgnoreCase);
            if (m.Success) { exe = m.Groups[1].Value; args = m.Groups[2].Value.Trim(); }
        }

        static string ExeOf(string cmd)
        {
            string exe, args;
            SplitCommand(cmd, out exe, out args);
            return exe;
        }

        static string ShortcutTarget(string lnk)
        {
            try
            {
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                return (string)sh.CreateShortcut(lnk).TargetPath;
            }
            catch { return ""; }
        }

        // ------------------------------------------------------------ 검사
        /// <summary>보안 프로그램만 (탐지 규칙에 맞거나, 이미 등록된 항목)</summary>
        public static List<Item> Scan(AppState st)
        {
            var known = new HashSet<string>(st.Items.Select(i => i.Key));
            return ScanAll().Where(i => IsMatch(i.MatchText) || known.Contains(i.Key)).ToList();
        }

        /// <summary>컴퓨터의 모든 서비스·시작프로그램·시작폴더 항목</summary>
        public static List<Item> ScanAll()
        {
            var list = new List<Item>();

            foreach (var o in Wmi("SELECT Name, DisplayName, PathName FROM Win32_Service"))
            {
                string name = Str(o, "Name"), disp = Str(o, "DisplayName"), path = Str(o, "PathName");
                if (name == "") continue;
                list.Add(new Item { Type = "Service", Name = name, Display = disp == "" ? name : disp,
                                    Command = path, ExePath = ExeOf(path), Location = "", Approved = "",
                                    MatchText = name + " " + disp + " " + path });
            }

            foreach (var loc in RunLocations)
            {
                try
                {
                    using (var k = OpenKey(loc[0], false, false))
                    {
                        if (k == null) continue;
                        foreach (var vn in k.GetValueNames())
                        {
                            if (string.IsNullOrEmpty(vn)) continue;
                            string cmd = Convert.ToString(k.GetValue(vn));
                            list.Add(new Item { Type = "Run", Name = vn, Display = vn, Command = cmd,
                                                ExePath = ExeOf(cmd), Location = loc[0], Approved = loc[1],
                                                MatchText = vn + " " + cmd });
                        }
                    }
                }
                catch (Exception ex) { Log.Write("시작프로그램 읽기 실패: " + ex.Message); }
            }

            var folders = new[] {
                new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "HKLM|" + ApprovedBase + "StartupFolder" },
                new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup),       "HKCU|" + ApprovedBase + "StartupFolder" },
            };
            foreach (var f in folders)
            {
                try
                {
                    if (string.IsNullOrEmpty(f[0]) || !Directory.Exists(f[0])) continue;
                    foreach (var file in Directory.GetFiles(f[0]))
                    {
                        string fn = Path.GetFileName(file);
                        if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        string target = fn.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) : file;
                        list.Add(new Item { Type = "StartupFolder", Name = fn, Display = Path.GetFileNameWithoutExtension(fn),
                                            Command = file, ExePath = target, Location = f[0], Approved = f[1],
                                            MatchText = fn + " " + target });
                    }
                }
                catch (Exception ex) { Log.Write("시작폴더 읽기 실패: " + ex.Message); }
            }
            return list;
        }

        /// <summary>검사 결과를 저장된 목록과 합친다. 새 항목은 원래 상태를 기록해 둔다.</summary>
        public static List<Item> Merge(AppState st, List<Item> scanned, out int added)
        {
            added = 0;
            foreach (var it in st.Items) it.Present = false;
            foreach (var s in scanned)
            {
                var ex = st.Items.FirstOrDefault(i => i.Key == s.Key);
                if (ex != null)
                {
                    ex.Present = true;
                    ex.Display = s.Display; ex.Command = s.Command; ex.ExePath = s.ExePath;
                    continue;
                }
                if (s.Type == "Service")
                {
                    bool delayed;
                    s.OriginalStart = GetServiceStart(s.Name, out delayed);
                    s.OriginalDelayed = delayed;
                }
                else
                {
                    var b = GetApprovedBytes(s.Approved, s.Name);
                    s.OriginalApproved = b == null ? null : Convert.ToBase64String(b);
                }
                s.Managed = st.Installed ? st.AutoManageNew : true;
                s.Present = true;
                st.Items.Add(s);
                added++;
            }
            return st.Items.Where(i => i.Present).ToList();
        }

        /// <summary>항목 하나를 설정대로 맞춘다 (관리 = 자동 실행 끔, 관리 안 함 = 원래대로)</summary>
        public static void Apply(Item it)
        {
            try
            {
                if (it.Type == "Service")
                {
                    bool delayed;
                    int cur = GetServiceStart(it.Name, out delayed);
                    if (cur < 0) return;
                    if (it.Managed)
                    {
                        if (cur == 2) it.Protected = !SetServiceStart(it.Name, 3, false);
                        else it.Protected = false;
                    }
                    else
                    {
                        if (it.OriginalStart == 2 && cur != 2) SetServiceStart(it.Name, 2, it.OriginalDelayed);
                        it.Protected = false;
                    }
                }
                else
                {
                    if (it.Managed) { if (!IsApprovedDisabled(it.Approved, it.Name)) SetApprovedDisabled(it.Approved, it.Name); }
                    else RestoreApproved(it);
                }
            }
            catch (Exception ex) { Log.Write("적용 실패 " + it.Display + ": " + ex.Message); }
        }

        public static string StatusText(Item it)
        {
            if (it.Type == "Service")
            {
                bool d;
                int cur = GetServiceStart(it.Name, out d);
                if (it.Managed && it.Protected) return "제조사 보호 (끌 수 없음)";
                switch (cur)
                {
                    case 2: return "켜짐 (컴퓨터 켤 때 자동 실행)";
                    case 3: return it.Managed ? "평소 꺼짐 (관리 중)" : "필요할 때 실행";
                    case 4: return "사용 안 함";
                    default: return "-";
                }
            }
            if (IsApprovedDisabled(it.Approved, it.Name)) return it.Managed ? "평소 꺼짐 (관리 중)" : "꺼짐";
            return "켜짐 (컴퓨터 켤 때 자동 실행)";
        }

        public static string TypeText(Item it)
        {
            string t = it.Type == "Service" ? "서비스" : it.Type == "Run" ? "시작프로그램" : "시작폴더";
            if (it.SkipOnLaunch) t += " (켤 때 건너뜀)";
            return it.AutoLearned ? t + " (자동 추가)" : t;
        }

        // ------------------------------------------------------------ 켜기 / 끄기
        public static void StartServices(AppState st)
        {
            var started = new List<ServiceController>();
            foreach (var it in st.Items.Where(i => i.Managed && i.Type == "Service"))
            {
                try
                {
                    if (it.SkipOnLaunch) continue;          // 켜면 재부팅 전엔 못 끄는 서비스: 필요한 사이트가 직접 켜게 둔다
                    bool d;
                    if (GetServiceStart(it.Name, out d) == 4) continue;
                    var sc = new ServiceController(it.Name);
                    if (sc.Status == ServiceControllerStatus.Stopped) { sc.Start(); started.Add(sc); }
                }
                catch (Exception ex) { Log.Write("서비스 시작 실패 " + it.Display + ": " + ex.Message); }
            }
            var until = DateTime.Now.AddSeconds(15);
            foreach (var sc in started)
            {
                try
                {
                    var left = until - DateTime.Now;
                    if (left > TimeSpan.Zero) sc.WaitForStatus(ServiceControllerStatus.Running, left);
                }
                catch { }
            }
        }

        public static void StartUserItems(AppState st)
        {
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in Wmi("SELECT ExecutablePath FROM Win32_Process"))
            {
                string p = Str(o, "ExecutablePath");
                if (p != "") running.Add(p);
            }
            foreach (var it in st.Items.Where(i => i.Managed && i.Type != "Service"))
            {
                try
                {
                    if (it.Type == "StartupFolder")
                    {
                        if (File.Exists(it.Command) && !running.Contains(it.ExePath ?? ""))
                            Process.Start(new ProcessStartInfo(it.Command) { UseShellExecute = true });
                        continue;
                    }
                    string exe, args;
                    SplitCommand(it.Command, out exe, out args);
                    if (!File.Exists(exe) || running.Contains(exe)) continue;
                    Process.Start(new ProcessStartInfo(exe, args)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(exe)
                    });
                }
                catch (Exception ex) { Log.Write("실행 실패 " + it.Display + ": " + ex.Message); }
            }
        }

        public static List<string> RunningSecurityProcesses()
        {
            var r = new List<string>();
            foreach (var o in Wmi("SELECT Name, ExecutablePath FROM Win32_Process"))
            {
                string n = Str(o, "Name"), p = Str(o, "ExecutablePath");
                if (IsMatch(n + " " + p)) r.Add(n);
            }
            return r;
        }

        public static void KillSecurityProcesses(AppState st, bool elevated)
        {
            var keepDirs = st.Items.Where(i => !i.Managed && !string.IsNullOrEmpty(i.ExePath))
                .Select(i => { try { return Path.GetDirectoryName(i.ExePath).TrimEnd('\\') + "\\"; } catch { return null; } })
                .Where(d => !string.IsNullOrEmpty(d) && d.Length > 4).ToList();
            int me = Process.GetCurrentProcess().Id;

            foreach (var o in Wmi("SELECT ProcessId, Name, ExecutablePath FROM Win32_Process"))
            {
                string n = Str(o, "Name"), p = Str(o, "ExecutablePath");
                int pid;
                if (!int.TryParse(Str(o, "ProcessId"), out pid) || pid == me) continue;
                if (!IsMatch(n + " " + p)) continue;
                if (p == "" && !elevated) continue;               // 경로가 안 보이면 관리자 단계에서 처리
                if (p != "" && keepDirs.Any(d => p.StartsWith(d, StringComparison.OrdinalIgnoreCase))) continue;
                try { Process.GetProcessById(pid).Kill(); Log.Write("종료: " + n); }
                catch (Exception ex) { Log.Write("종료 실패 " + n + ": " + ex.Message); }
            }
        }

        public static void StopAll(AppState st)
        {
            var stopping = new List<ServiceController>();
            foreach (var it in st.Items.Where(i => i.Managed && i.Type == "Service"))
            {
                try
                {
                    var sc = new ServiceController(it.Name);
                    if (sc.Status == ServiceControllerStatus.Running && !sc.CanStop && !it.Protected && !it.SkipOnLaunch)
                    {
                        // 중지를 받지 않는 서비스(예: MarkAny WebDRM): 재부팅 전엔 못 끄므로 다음부터 은행 브라우저를 켤 때 같이 켜지 않는다
                        it.SkipOnLaunch = true;
                        Log.Write("중지 불가 서비스 " + it.Display + ": 다음부터 은행 브라우저를 켤 때 켜지 않음 (재부팅하면 꺼짐)");
                        continue;
                    }
                    if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                    { sc.Stop(); stopping.Add(sc); }
                }
                catch (Exception ex) { Log.Write("서비스 종료 실패 " + it.Display + ": " + ex.Message); }
            }
            var until = DateTime.Now.AddSeconds(10);
            foreach (var sc in stopping)
            {
                try { var left = until - DateTime.Now; if (left > TimeSpan.Zero) sc.WaitForStatus(ServiceControllerStatus.Stopped, left); }
                catch { }
            }
            KillSecurityProcesses(st, true);
        }

        /// <summary>관리자 단계: 새로 깔린 보안 프로그램 등록 + 업데이트로 다시 켜진 자동 실행을 다시 끈다</summary>
        public static int Tidy(AppState st)
        {
            int added;
            Merge(st, Scan(st), out added);
            foreach (var it in st.Items.Where(i => i.Present && i.Managed)) Apply(it);
            return added;
        }

        // ------------------------------------------------------------ 브라우저
        public static List<int> ProfileProcesses(string exeName, string profileDir)
        {
            var r = new List<int>();
            foreach (var o in Wmi("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='" + exeName + "'"))
            {
                string cl = Str(o, "CommandLine");
                int pid;
                if (cl.IndexOf(profileDir, StringComparison.OrdinalIgnoreCase) >= 0 && int.TryParse(Str(o, "ProcessId"), out pid))
                    r.Add(pid);
            }
            return r;
        }

        public static bool AnyBankBrowserRunning(AppState st)
        {
            foreach (var b in Browsers.Detect(st))
                if (ProfileProcesses(Path.GetFileName(b.Path), Browsers.ProfileDir(b.Key)).Count > 0) return true;
            return false;
        }

        // ------------------------------------------------------------ 자동 학습
        // 은행 브라우저를 켤 때 컴퓨터의 시작 항목 목록을 기록해 두고, 닫을 때 비교해서
        // 그 사이에 새로 생긴 항목(= 은행·보험사 사이트가 설치한 보안 프로그램)을 등록한다.
        static readonly Regex NotLearnable = new Regex(
            @"GoogleUpdate|gupdate|GoogleChromeElevation|edgeupdate|MicrosoftEdge|Brave|Whale|Naver|Vivaldi|Mozilla|Firefox|" +
            @"OneDrive|Teams|BankBrowser|\\Windows\\|TrustedInstaller|svchost|WindowsApps|Microsoft\.NET",
            RegexOptions.IgnoreCase);

        public static void SaveSnapshot()
        {
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.WriteAllLines(Paths.SnapshotFile, ScanAll().Select(i => i.Key), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Write("스냅샷 저장 실패: " + ex.Message); }
        }

        /// <returns>새로 등록한 항목 수</returns>
        public static int LearnFromSnapshot(AppState st)
        {
            if (!File.Exists(Paths.SnapshotFile)) return 0;
            int added = 0;
            try
            {
                var before = new HashSet<string>(File.ReadAllLines(Paths.SnapshotFile, Encoding.UTF8));
                var known = new HashSet<string>(st.Items.Select(i => i.Key));
                foreach (var it in ScanAll())
                {
                    if (before.Contains(it.Key) || known.Contains(it.Key)) continue;
                    if (NotLearnable.IsMatch(it.MatchText ?? "")) continue;
                    if (it.Type == "Service")
                    {
                        bool d;
                        it.OriginalStart = GetServiceStart(it.Name, out d);
                        it.OriginalDelayed = d;
                        if (it.OriginalStart != 2 && it.OriginalStart != 3) continue; // 드라이버·사용 안 함은 제외
                    }
                    else
                    {
                        var b = GetApprovedBytes(it.Approved, it.Name);
                        it.OriginalApproved = b == null ? null : Convert.ToBase64String(b);
                    }
                    it.AutoLearned = true;
                    it.Managed = st.AutoManageNew;
                    it.Present = true;
                    st.Items.Add(it);
                    added++;
                    Log.Write("자동 등록: " + it.Display);
                }
            }
            catch (Exception ex) { Log.Write("자동 학습 실패: " + ex.Message); }
            try { File.Delete(Paths.SnapshotFile); } catch { }
            return added;
        }

        // ------------------------------------------------------------ 예약 작업 (UAC 창 없이 관리자 단계 실행)
        static int RunHidden(string file, string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });
                if (!p.WaitForExit(30000)) return -1;
                return p.ExitCode;
            }
            catch (Exception ex) { Log.Write(file + " 실행 실패: " + ex.Message); return -1; }
        }

        static string TaskXml(string exe, string arg)
        {
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>은행 전용 브라우저 보조 작업 (보안 프로그램 켜기/끄기)</Description></RegistrationInfo>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId>" +
                "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><ExecutionTimeLimit>PT10M</ExecutionTimeLimit>" +
                "<Priority>5</Priority></Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) + "</Command>" +
                "<Arguments>" + arg + "</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";
        }

        public static bool RegisterTasks(string exe)
        {
            bool ok = true;
            foreach (var t in new[] { new[] { "SvcStart", "--svcstart" }, new[] { "SvcStop", "--svcstop" } })
            {
                string xml = Path.Combine(Path.GetTempPath(), "BankBrowser_" + t[0] + ".xml");
                File.WriteAllText(xml, TaskXml(exe, t[1]), Encoding.Unicode);
                int rc = RunHidden("schtasks.exe", "/Create /F /TN \"\\BankBrowser\\" + t[0] + "\" /XML \"" + xml + "\"");
                try { File.Delete(xml); } catch { }
                if (rc != 0) { ok = false; Log.Write("예약 작업 등록 실패 " + t[0] + " (" + rc + ")"); }
            }
            return ok;
        }

        public static void DeleteTasks()
        {
            RunHidden("schtasks.exe", "/Delete /F /TN \"\\BankBrowser\\SvcStart\"");
            RunHidden("schtasks.exe", "/Delete /F /TN \"\\BankBrowser\\SvcStop\"");
        }

        public static string MarkerFile(string task) { return Path.Combine(Paths.UserDir, task + ".done"); }

        /// <summary>관리자 예약 작업을 실행하고 끝날 때까지 기다린다</summary>
        public static bool RunHelperTask(string task)
        {
            string marker = MarkerFile(task);
            try { Directory.CreateDirectory(Paths.UserDir); if (File.Exists(marker)) File.Delete(marker); } catch { }
            if (RunHidden("schtasks.exe", "/Run /TN \"\\BankBrowser\\" + task + "\"") != 0) return false;
            var until = DateTime.Now.AddSeconds(90);
            while (DateTime.Now < until)
            {
                if (File.Exists(marker)) return true;
                Thread.Sleep(400);
            }
            Log.Write("예약 작업 대기 시간 초과: " + task);
            return true;
        }

        // ------------------------------------------------------------ 바로가기 / 설치 정보
        public static void CreateShortcut(string lnkPath, string target, string args, string icon, string desc)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath));
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic l = sh.CreateShortcut(lnkPath);
            l.TargetPath = target;
            l.Arguments = args;
            l.WorkingDirectory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(icon)) l.IconLocation = icon + ",0";
            l.Description = desc;
            l.Save();
        }

        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BankBrowser";

        public static void RegisterUninstall(string exe)
        {
            using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = b.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Paths.AppName);
                k.SetValue("DisplayVersion", Paths.Version);
                k.SetValue("Publisher", Paths.AppName);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", Path.GetDirectoryName(exe));
                k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        public static void RemoveUninstall()
        {
            try
            {
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    b.DeleteSubKeyTree(UninstallKey, false);
            }
            catch { }
        }
    }
}
