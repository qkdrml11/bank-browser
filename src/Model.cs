using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;

namespace BankBrowser
{
    /// <summary>관리 대상 하나 (서비스 / 시작프로그램 / 시작폴더 바로가기)</summary>
    [DataContract]
    public class Item
    {
        [DataMember] public string Type;             // Service, Run, StartupFolder
        [DataMember] public string Name;
        [DataMember] public string Display;
        [DataMember] public string Command;
        [DataMember] public string ExePath;
        [DataMember] public string Location;         // Run: "HKLM|경로", StartupFolder: 폴더 경로
        [DataMember] public string Approved;         // "HKLM|StartupApproved 경로"
        [DataMember] public int OriginalStart;       // 서비스 원래 시작 유형 (2 자동, 3 수동, 4 사용 안 함)
        [DataMember] public bool OriginalDelayed;
        [DataMember] public string OriginalApproved; // 원래 StartupApproved 값 (base64, 없으면 null)
        [DataMember] public bool Managed;            // 평소엔 끄고 은행 브라우저 때만 켜기
        [DataMember] public bool Protected;          // 제조사 자체 보호로 변경 불가
        [DataMember] public bool AutoLearned;        // 은행 브라우저 사용 중 설치되어 자동 등록됨
        [DataMember] public bool SkipOnLaunch;       // 한 번 켜지면 재부팅 전엔 못 끄는 서비스: 은행 브라우저를 켤 때 같이 켜지 않음

        public bool Present;      // 실행 시점에 실제로 존재하는지 (저장 안 함)
        public string MatchText;  // 탐지용 텍스트 (저장 안 함)

        public string Key { get { return Type + "|" + (Location ?? "") + "|" + Name; } }
    }

    [DataContract]
    public class AppState
    {
        [DataMember] public bool Installed;
        [DataMember] public bool UseChrome;   // 1.0 호환용
        [DataMember] public bool UseEdge;     // 1.0 호환용
        [DataMember] public bool AutoManageNew;
        [DataMember] public List<Item> Items;
        [DataMember] public List<string> Browsers;
        [DataMember] public List<BrowserDef> CustomBrowsers;

        public static AppState Load()
        {
            AppState s = null;
            try
            {
                if (File.Exists(Paths.StateFile))
                {
                    using (var fs = File.OpenRead(Paths.StateFile))
                        s = (AppState)new DataContractJsonSerializer(typeof(AppState)).ReadObject(fs);
                }
            }
            catch (Exception ex) { Log.Write("상태 파일 읽기 실패: " + ex.Message); }
            if (s == null) s = new AppState { AutoManageNew = true };
            if (s.Items == null) s.Items = new List<Item>();
            if (s.CustomBrowsers == null) s.CustomBrowsers = new List<BrowserDef>();
            if (s.Browsers == null)
            {
                s.Browsers = new List<string>();
                if (s.UseChrome) s.Browsers.Add("chrome");
                if (s.UseEdge) s.Browsers.Add("edge");
            }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Paths.DataDir);
            string tmp = Paths.StateFile + ".tmp";
            using (var fs = File.Create(tmp))
                new DataContractJsonSerializer(typeof(AppState)).WriteObject(fs, this);
            if (File.Exists(Paths.StateFile)) File.Delete(Paths.StateFile);
            File.Move(tmp, Paths.StateFile);
        }
    }

    public static class Paths
    {
        public const string AppName = "은행 전용 브라우저";
        public const string Version = "1.1.1";

        public static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BankBrowser"); } }
        public static string InstalledExe { get { return Path.Combine(InstallDir, "BankBrowser.exe"); } }
        public static string DataDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BankBrowser"); } }
        public static string StateFile { get { return Path.Combine(DataDir, "state.json"); } }
        public static string OldScript { get { return Path.Combine(DataDir, "BankBrowser.ps1"); } }
        public static string UserDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BankBrowser"); } }
        public static string SnapshotFile { get { return Path.Combine(DataDir, "session_snapshot.txt"); } }

        public static string StartMenuDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName); } }
        public static string Desktop { get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); } }

        public static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    public static class Log
    {
        static readonly object Lock = new object();
        public static void Write(string msg)
        {
            try
            {
                string dir = Paths.IsAdmin() ? Paths.DataDir : Paths.UserDir;
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "log.txt");
                lock (Lock)
                {
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > 512 * 1024) File.Delete(file);
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
