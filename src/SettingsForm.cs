using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Windows.Forms;

namespace BankBrowser
{
    public class SettingsForm : Form
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeleteFile(string path);

        public bool StartUninstall;

        AppState st;
        ListView lv;
        CheckBox cbAuto;
        FlowLayoutPanel flowB;
        Button btnAddBrowser;
        List<BrowserDef> browsers = new List<BrowserDef>();
        Button btnApply, btnRescan, btnRemove, btnClose;
        Label lblStatus;
        float scale = 1f;

        public SettingsForm()
        {
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            Text = Paths.AppName + " 설정";
            Icon = Program.AppIcon;
            Font = new Font("Malgun Gothic", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(760), S(600));
            MinimumSize = new Size(S(620), S(480));

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(S(14)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var title = new Label { Text = Paths.AppName, AutoSize = true, Font = new Font("Malgun Gothic", 15F, FontStyle.Bold), Margin = new Padding(0, 0, 0, S(2)) };
            var sub = new Label
            {
                Text = "은행·관공서 보안 프로그램을 평소에는 꺼 두고, 은행 브라우저를 켤 때만 실행합니다. 브라우저를 닫으면 다시 꺼집니다.",
                AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(S(720), 0), Margin = new Padding(0, 0, 0, S(10))
            };

            var grpBrowser = new GroupBox { Text = "사용할 브라우저 (바탕화면에 아이콘이 생깁니다)", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(S(8)) };
            flowB = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            btnAddBrowser = new Button { Text = "다른 브라우저 추가...", AutoSize = true, Margin = new Padding(S(4), S(1), S(4), S(1)) };
            btnAddBrowser.Click += delegate { AddBrowser(); };
            grpBrowser.Controls.Add(flowB);

            var lblList = new Label
            {
                Text = "관리할 보안 프로그램 — 체크한 것만 평소에 꺼 두고, 은행 브라우저를 쓸 때만 켭니다.",
                AutoSize = true, Font = new Font("Malgun Gothic", 9F, FontStyle.Bold), Margin = new Padding(0, S(12), 0, S(4))
            };

            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false };
            lv.Columns.Add("보안 프로그램", S(300));
            lv.Columns.Add("종류", S(160));
            lv.Columns.Add("현재 상태", S(200));

            cbAuto = new CheckBox { Text = "은행 브라우저 사용 중 새로 설치된 보안 프로그램도 자동으로 관리 (은행·보험사 등 어느 사이트든, 추천)", AutoSize = true, Margin = new Padding(0, S(8), 0, S(4)) };

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, S(6), 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            var right = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            btnRescan = MakeButton("다시 검사");
            btnRemove = MakeButton("원래대로 되돌리고 제거");
            btnClose = MakeButton("닫기");
            btnApply = MakeButton("설치하기");
            btnApply.Font = new Font("Malgun Gothic", 9F, FontStyle.Bold);
            left.Controls.Add(btnRescan);
            left.Controls.Add(btnRemove);
            right.Controls.Add(btnClose);
            right.Controls.Add(btnApply);
            bottom.Controls.Add(left, 0, 0);
            bottom.Controls.Add(right, 1, 0);

            lblStatus = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, S(6), 0, 0) };

            root.Controls.Add(title);
            root.Controls.Add(sub);
            root.Controls.Add(grpBrowser);
            root.Controls.Add(lblList);
            root.Controls.Add(lv);
            root.Controls.Add(cbAuto);
            root.Controls.Add(bottom);
            root.Controls.Add(lblStatus);
            for (int i = 0; i < 8; i++) root.RowStyles.Add(new RowStyle(i == 4 ? SizeType.Percent : SizeType.AutoSize, 100));
            Controls.Add(root);

            btnRescan.Click += delegate { Busy(() => Reload()); };
            btnApply.Click += delegate { Busy(() => DoApply()); };
            btnRemove.Click += delegate { DoUninstall(); };
            btnClose.Click += delegate { Close(); };
        }

        int S(int v) { return (int)Math.Round(v * scale); }

        Button MakeButton(string text)
        {
            return new Button { Text = text, AutoSize = true, MinimumSize = new Size(S(96), S(32)), Padding = new Padding(S(8), 0, S(8), 0) };
        }

        void Busy(Action a)
        {
            Cursor = Cursors.WaitCursor;
            Enabled = false;
            try { a(); }
            catch (Exception ex) { Log.Write("오류: " + ex); MessageBox.Show(this, "오류가 발생했습니다.\n\n" + ex.Message, Paths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { Enabled = true; Cursor = Cursors.Default; }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!MigrateOldVersion()) { Close(); return; }
            if (StartUninstall) { DoUninstall(); return; }
            Busy(() => Reload());
        }

        // ------------------------------------------------------------ 이전 스크립트 버전 정리
        bool MigrateOldVersion()
        {
            if (!File.Exists(Paths.OldScript)) return true;
            var r = MessageBox.Show(this,
                "이전 버전(스크립트 방식)이 설치되어 있습니다.\n새 버전을 쓰려면 먼저 이전 설정을 원래대로 되돌려야 합니다.\n\n" +
                "지금 되돌릴까요?\n(검은 창이 뜨면 Y 를 입력하고 엔터, 끝나면 엔터를 한 번 더 누르세요)",
                Paths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r != DialogResult.Yes) return false;
            try
            {
                var p = Process.Start(new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + Paths.OldScript + "\" -Mode Restore") { UseShellExecute = true });
                p.WaitForExit();
            }
            catch (Exception ex) { MessageBox.Show(this, "이전 버전 되돌리기 실패: " + ex.Message, Paths.AppName); return false; }

            if (File.Exists(Paths.StateFile))
            {
                MessageBox.Show(this, "이전 버전 되돌리기가 완료되지 않았습니다. 다시 실행해 주세요.", Paths.AppName);
                return false;
            }
            foreach (var f in new[] { "BankBrowser.ps1", "config.json", "start.html" })
                try { File.Delete(Path.Combine(Paths.DataDir, f)); } catch { }
            return true;
        }

        // ------------------------------------------------------------ 목록
        void Reload()
        {
            st = AppState.Load();
            int added;
            var items = Engine.Merge(st, Engine.Scan(st), out added)
                .OrderBy(i => i.Type == "Service" ? 0 : 1).ThenBy(i => i.Display).ToList();

            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var it in items)
            {
                var lvi = new ListViewItem(it.Display) { Checked = it.Managed, Tag = it };
                lvi.SubItems.Add(Engine.TypeText(it));
                lvi.SubItems.Add(Engine.StatusText(it));
                if (it.Managed && it.Protected) lvi.ForeColor = Color.Gray;
                lv.Items.Add(lvi);
            }
            lv.EndUpdate();

            FillBrowsers(null);
            cbAuto.Checked = st.AutoManageNew;

            btnApply.Text = st.Installed ? "변경 내용 적용" : "설치하기";
            btnRemove.Enabled = st.Installed;

            int running = Engine.RunningSecurityProcesses().Count;
            lblStatus.Text = string.Format("찾은 보안 프로그램 {0}개 · 지금 실행 중인 보안 프로세스 {1}개 · 버전 {2}",
                items.Count, running, Paths.Version);
        }

        void FillBrowsers(string extraChecked)
        {
            // 화면에서 바꾼 체크 상태는 유지
            var checkedKeys = new HashSet<string>(flowB.Controls.OfType<CheckBox>().Where(c => c.Checked).Select(c => (string)c.Tag));
            bool first = flowB.Controls.OfType<CheckBox>().Count() == 0;
            browsers = Browsers.Detect(st);
            flowB.SuspendLayout();
            flowB.Controls.Clear();
            for (int i = 0; i < browsers.Count; i++)
            {
                var b = browsers[i];
                bool on;
                if (!first) on = checkedKeys.Contains(b.Key);
                else if (st.Installed) on = st.Browsers.Contains(b.Key);
                else on = i == 0; // 처음 설치: 첫 번째 브라우저만 기본 선택
                if (b.Key == extraChecked) on = true;
                flowB.Controls.Add(new CheckBox { Text = b.Label, Tag = b.Key, Checked = on, AutoSize = true, Margin = new Padding(S(4), S(4), S(18), S(4)) });
            }
            if (browsers.Count == 0) flowB.Controls.Add(new Label { Text = "설치된 브라우저를 찾지 못했습니다.", AutoSize = true, Margin = new Padding(S(4), S(6), S(12), S(4)) });
            flowB.Controls.Add(btnAddBrowser);
            flowB.ResumeLayout();
        }

        void AddBrowser()
        {
            using (var dlg = new OpenFileDialog { Title = "브라우저 실행 파일(.exe)을 골라 주세요", Filter = "실행 파일 (*.exe)|*.exe" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (browsers.Any(b => string.Equals(b.Path, dlg.FileName, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show(this, "이미 목록에 있는 브라우저입니다.", Paths.AppName);
                    return;
                }
                var def = Browsers.MakeCustom(st, dlg.FileName);
                st.CustomBrowsers.Add(def);
                FillBrowsers(def.Key);
                MessageBox.Show(this, "[" + def.Label + "] 을(를) 추가했습니다. '변경 내용 적용' 또는 '설치하기'를 누르면 바탕화면 아이콘이 생깁니다.\n\n" +
                    "참고: 크롬 계열(웨일, 브레이브 등)과 파이어폭스 계열 브라우저를 지원합니다.", Paths.AppName);
            }
        }

        // ------------------------------------------------------------ 설치 / 적용
        string InstallExe()
        {
            string target = Paths.InstalledExe;
            if (string.Equals(Path.GetFullPath(Program.ExePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                return target;
            Directory.CreateDirectory(Paths.InstallDir);
            try { File.Copy(Program.ExePath, target, true); }
            catch (IOException)
            {
                MessageBox.Show(this, "은행 브라우저가 실행 중이라 설치 파일을 바꿀 수 없습니다.\n은행 브라우저를 모두 닫고 다시 시도해 주세요.", Paths.AppName);
                return null;
            }
            DeleteFile(target + ":Zone.Identifier"); // '인터넷에서 받은 파일' 표시 제거 (실행할 때마다 경고 방지)
            return target;
        }

        void DoApply()
        {
            var selected = flowB.Controls.OfType<CheckBox>().Where(c => c.Checked).Select(c => (string)c.Tag).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "사용할 브라우저를 하나 이상 골라 주세요.", Paths.AppName);
                return;
            }
            bool firstInstall = !st.Installed;
            foreach (ListViewItem lvi in lv.Items) ((Item)lvi.Tag).Managed = lvi.Checked;
            st.Browsers = selected;
            st.UseChrome = selected.Contains("chrome");
            st.UseEdge = selected.Contains("edge");
            st.AutoManageNew = cbAuto.Checked;

            string exe = InstallExe();
            if (exe == null) return;

            foreach (var it in st.Items.Where(i => i.Present)) Engine.Apply(it);
            st.Installed = true;
            st.Save();

            bool tasksOk = Engine.RegisterTasks(exe);

            foreach (var b in browsers)
            {
                bool use = st.Browsers.Contains(b.Key);
                string desk = Path.Combine(Paths.Desktop, Browsers.ShortcutName(b.Label));
                string menu = Path.Combine(Paths.StartMenuDir, Browsers.ShortcutName(b.Label));
                if (use)
                {
                    string desc = "보안 프로그램을 켜고 " + b.Label + " 은행 브라우저를 엽니다";
                    Engine.CreateShortcut(desk, exe, "--launch " + b.Key, b.Path, desc);
                    Engine.CreateShortcut(menu, exe, "--launch " + b.Key, b.Path, desc);
                }
                else
                {
                    try { File.Delete(desk); } catch { }
                    try { File.Delete(menu); } catch { }
                }
            }
            Engine.CreateShortcut(Path.Combine(Paths.StartMenuDir, Paths.AppName + " 설정.lnk"), exe, "--settings", exe, Paths.AppName + " 설정");
            Engine.RegisterUninstall(exe);

            if (!Engine.AnyBankBrowserRunning(st)) Engine.StopAll(st);
            st.Save();
            Reload();

            var msg = (firstInstall ? "설치가 끝났습니다!" : "적용했습니다.") + "\n\n" +
                      "앞으로 은행·보험·정부24 업무는 바탕화면의 [은행 브라우저] 아이콘으로 하세요.\n" +
                      "어느 사이트든 보안 프로그램 설치를 요구하면 그냥 설치하세요. 창을 닫을 때 자동으로 관리 목록에 추가됩니다.\n" +
                      "평소에는 체크한 보안 프로그램이 실행되지 않습니다.";
            var prot = st.Items.Where(i => i.Present && i.Managed && i.Protected).Select(i => "  - " + i.Display).ToList();
            if (prot.Count > 0)
                msg += "\n\n아래 항목은 제조사가 스스로를 보호하고 있어 자동 실행을 끌 수 없었습니다:\n" + string.Join("\n", prot);
            if (!tasksOk)
                msg += "\n\n(참고: 보조 작업 등록에 실패해서, 은행 브라우저를 켤 때마다 관리자 확인 창이 뜰 수 있습니다.)";
            if (firstInstall) msg += "\n\n확실하게 적용하려면 컴퓨터를 한 번 다시 시작하는 것을 권장합니다.";
            MessageBox.Show(this, msg, Paths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ------------------------------------------------------------ 제거
        void DoUninstall()
        {
            var r = MessageBox.Show(this,
                "보안 프로그램들을 원래대로(컴퓨터 켤 때 자동 실행) 되돌리고 은행 전용 브라우저를 제거할까요?",
                Paths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) { if (StartUninstall) Close(); return; }

            Busy(() =>
            {
                var s = AppState.Load();
                int added;
                Engine.Merge(s, Engine.Scan(s), out added);
                foreach (var it in s.Items)
                {
                    if (!it.Present) continue;
                    it.Managed = false;
                    Engine.Apply(it);
                    if (it.Type == "Service" && it.OriginalStart == 2)
                        try { var sc = new ServiceController(it.Name); if (sc.Status == ServiceControllerStatus.Stopped) sc.Start(); } catch { }
                }
                Engine.DeleteTasks();
                try { foreach (var f in Directory.GetFiles(Paths.Desktop, "은행 브라우저 (*).lnk")) File.Delete(f); } catch { }
                try { if (Directory.Exists(Paths.StartMenuDir)) Directory.Delete(Paths.StartMenuDir, true); } catch { }
                Engine.RemoveUninstall();
                try { File.Delete(Paths.StateFile); } catch { }
            });

            MessageBox.Show(this,
                "제거했습니다. 보안 프로그램은 원래대로 컴퓨터를 켤 때 자동 실행됩니다.\n\n" +
                "은행 브라우저의 즐겨찾기·설정은 아래 폴더에 남아 있습니다. 필요 없으면 지우셔도 됩니다.\n" + Paths.UserDir,
                Paths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);

            // 프로그램 폴더는 이 창이 닫힌 뒤에 지운다
            if (Directory.Exists(Paths.InstallDir))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("cmd.exe",
                        "/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"" + Paths.InstallDir + "\"")
                    { UseShellExecute = false, CreateNoWindow = true });
                }
                catch { }
            }
            Close();
        }
    }
}
