using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using Microsoft.Win32;

namespace BankBrowser
{
    [DataContract]
    public class BrowserDef
    {
        [DataMember] public string Key;
        [DataMember] public string Label;
        [DataMember] public string Path;
        [DataMember] public string Kind;   // chromium, firefox
    }

    public static class Browsers
    {
        // key, 이름, 실행 파일, 종류, 기본 설치 위치들
        static readonly string[][] Known = {
            new[] { "chrome",  "크롬",       "chrome.exe",  "chromium", @"%ProgramFiles%\Google\Chrome\Application\chrome.exe", @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe", @"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe" },
            new[] { "edge",    "엣지",       "msedge.exe",  "chromium", @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe", @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe" },
            new[] { "whale",   "웨일",       "whale.exe",   "chromium", @"%ProgramFiles%\Naver\Naver Whale\Application\whale.exe", @"%ProgramFiles(x86)%\Naver\Naver Whale\Application\whale.exe", @"%LOCALAPPDATA%\Naver\Naver Whale\Application\whale.exe" },
            new[] { "brave",   "브레이브",   "brave.exe",   "chromium", @"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe", @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\Application\brave.exe" },
            new[] { "vivaldi", "비발디",     "vivaldi.exe", "chromium", @"%LOCALAPPDATA%\Vivaldi\Application\vivaldi.exe", @"%ProgramFiles%\Vivaldi\Application\vivaldi.exe" },
            new[] { "firefox", "파이어폭스", "firefox.exe", "firefox",  @"%ProgramFiles%\Mozilla Firefox\firefox.exe", @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe" },
        };

        static string FromAppPaths(string exe)
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                    using (var k = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                    {
                        var p = k == null ? null : k.GetValue("") as string;
                        if (!string.IsNullOrEmpty(p)) { p = p.Trim(new[] { '"' }); if (File.Exists(p)) return p; }
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>이 컴퓨터에서 쓸 수 있는 브라우저 (기본 + 직접 추가한 것)</summary>
        public static List<BrowserDef> Detect(AppState st)
        {
            var list = new List<BrowserDef>();
            foreach (var k in Known)
            {
                string path = FromAppPaths(k[2]);
                if (path == null)
                    path = k.Skip(4).Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists);
                if (path != null) list.Add(new BrowserDef { Key = k[0], Label = k[1], Path = path, Kind = k[3] });
            }
            if (st != null && st.CustomBrowsers != null)
                foreach (var c in st.CustomBrowsers)
                    if (c != null && File.Exists(c.Path) && !list.Any(b => b.Key == c.Key)) list.Add(c);
            return list;
        }

        public static BrowserDef Find(AppState st, string key)
        {
            return Detect(st).FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        public static BrowserDef MakeCustom(AppState st, string exePath)
        {
            string label = null;
            try { label = FileVersionInfo.GetVersionInfo(exePath).ProductName; } catch { }
            if (string.IsNullOrWhiteSpace(label)) label = System.IO.Path.GetFileNameWithoutExtension(exePath);
            label = label.Trim();
            string name = System.IO.Path.GetFileName(exePath).ToLowerInvariant();
            string kind = (name.Contains("firefox") || name.Contains("librewolf") || name.Contains("waterfox") || name.Contains("floorp"))
                ? "firefox" : "chromium";
            int n = 1;
            while (st.CustomBrowsers.Any(c => c.Key == "custom" + n)) n++;
            return new BrowserDef { Key = "custom" + n, Label = label, Path = exePath, Kind = kind };
        }

        public static string ProfileDir(string key)
        {
            // 크롬·엣지는 이전 버전과 같은 폴더를 써서 즐겨찾기를 이어서 사용
            string name = key == "chrome" ? "Profile" : key == "edge" ? "Profile-Edge" : "Profile-" + key;
            return System.IO.Path.Combine(Paths.UserDir, name);
        }

        public static string BuildArgs(BrowserDef b, string profileDir, string url)
        {
            if (b.Kind == "firefox")
                return "-no-remote -profile \"" + profileDir + "\" \"" + url + "\"";
            return "--user-data-dir=\"" + profileDir + "\" --no-first-run --no-default-browser-check \"" + url + "\"";
        }

        public static string ShortcutName(string label) { return "은행 브라우저 (" + label + ").lnk"; }
    }
}
