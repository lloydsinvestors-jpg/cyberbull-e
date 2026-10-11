// CyberBull-E Launcher (BullyLink's launcher; public name since 2026-10-11, display only: the DLL, the MelonLoader
// name and UserData\BullyLink keep "BullyLink"). Built 2026-10-10, Austin: "now make a mod launcher that installs launches the mod weve built").
// v0.23 bring your own Bully (2026-10-11, Austin: "build the bring your own bully launcher for nexus"): the public download
// carries nothing from Bully. The launcher finds the player's own Bully: Scholarship Edition, runs the bundled exporter
// (payload\exporter\BullyLinkExporter.exe, tools\byob_export.py packed with PyInstaller) to build Jimmy, the adults, the
// clothes, anims, sounds and icons from it into Schedule I\UserData\BullyLink, installs the mod DLL into Mods\, launches the
// game through Steam and watches MelonLoader's log until the mod is loaded.
// Where the mod comes from: a "payload" folder next to the launcher (BullyLink.dll, exporter\, export_version.txt), or, on
// the dev machine, the BullyLink project the launcher sits in (found by walking up to BullyLink.csproj).
using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BullyLinkLauncher
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new LauncherForm());
        }
    }

    class Paths
    {
        public const string AppId = "3164500";
        public string Game, Bully, SrcDll, Exporter, SourceName;
        public int ExportVersion = 1;
        public string ModsDir => Path.Combine(Game, "Mods");
        public string Root => Path.Combine(Game, "UserData", "BullyLink");
        public string DataDir => Path.Combine(Root, "data");
        public string Manifest => Path.Combine(DataDir, "export.txt");
        public string DstDll => Path.Combine(ModsDir, "BullyLink.dll");
        public string MelonLog => Path.Combine(Game, "MelonLoader", "Latest.log");
        public string Trace => Path.Combine(Game, "UserData", "BullyLink.trace.txt");
        public bool MelonInstalled => Game != null && Directory.Exists(Path.Combine(Game, "MelonLoader")) && File.Exists(Path.Combine(Game, "version.dll"));
        static string Settings => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BullyLink", "launcher.txt");

        public static Paths Find()
        {
            var libs = SteamLibraries();
            var p = new Paths { Game = FindIn(libs, "Schedule I", d => File.Exists(Path.Combine(d, "Schedule I.exe"))) };
            p.Bully = Saved("bully") is string b && IsBully(b) ? b : FindBully(libs);
            var payload = Path.Combine(AppContext.BaseDirectory, "payload");
            if (File.Exists(Path.Combine(payload, "BullyLink.dll")))
            {
                p.SrcDll = Path.Combine(payload, "BullyLink.dll");
                p.Exporter = Path.Combine(payload, "exporter", "CyberBull-E Exporter.exe");
                p.SourceName = "payload folder";
                var v = Path.Combine(payload, "export_version.txt");
                if (File.Exists(v) && int.TryParse(File.ReadAllText(v).Trim(), out var n)) p.ExportVersion = n;
            }
            else if (ProjectRoot() is string proj)
            {
                p.SrcDll = Path.Combine(proj, "bin", "Release", "net6.0", "BullyLink.dll");
                p.Exporter = Path.Combine(proj, "tools", "dist", "CyberBull-E Exporter", "CyberBull-E Exporter.exe");
                p.SourceName = "BullyLink project build";
                var m = Regex.Match(File.ReadAllText(Path.Combine(proj, "tools", "byob_export.py")), @"^VERSION = (\d+)", RegexOptions.Multiline);
                if (m.Success) p.ExportVersion = int.Parse(m.Groups[1].Value);
            }
            return p;
        }

        /// <summary>Dev machine: the launcher lives in the project's launcher\publish; walk up to BullyLink.csproj.</summary>
        static string ProjectRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "BullyLink.csproj"))) return d.FullName;
            return null;
        }

        /// <summary>Same test as the exporter's paths.is_bully: the archive, the effects textures and the speech bank.</summary>
        public static bool IsBully(string d) => d != null &&
            new[] { @"Stream\World.img", @"Stream\World.dir", @"TXD\effects.nft", @"Audio\PLAYLIST\Speech.bin" }.All(f => File.Exists(Path.Combine(d, f)));

        static string FindBully(List<string> libs)
        {
            var hit = FindIn(libs, "Bully Scholarship Edition", IsBully);
            if (hit != null) return hit;
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                var d = Path.Combine(pf, "Rockstar Games", "Bully Scholarship Edition");
                if (IsBully(d)) return d;
            }
            return null;
        }

        static string FindIn(List<string> libs, string folder, Func<string, bool> ok)
        {
            foreach (var lib in libs)
            {
                var dir = Path.Combine(lib, "steamapps", "common", folder);
                if (ok(dir)) return dir;
            }
            return null;
        }

        /// <summary>Every Steam library: Steam's own folder + each "path" in libraryfolders.vdf.</summary>
        static List<string> SteamLibraries()
        {
            var libs = new List<string>();
            try
            {
                var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (steam != null)
                {
                    libs.Add(steam.Replace('/', '\\'));
                    var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                            libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
            }
            catch { }
            return libs;
        }

        static string Saved(string key)
        {
            try
            {
                if (File.Exists(Settings))
                    foreach (var line in File.ReadAllLines(Settings))
                        if (line.StartsWith(key + "\t")) return line.Substring(key.Length + 1).Trim();
            }
            catch { }
            return null;
        }

        public static void Save(string key, string value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Settings));
                var lines = File.Exists(Settings) ? File.ReadAllLines(Settings).Where(l => !l.StartsWith(key + "\t")).ToList() : new List<string>();
                lines.Add($"{key}\t{value}");
                File.WriteAllLines(Settings, lines);
            }
            catch { }
        }

        /// <summary>The export manifest: (exporter version, Bully folder it read), or null if no finished export.</summary>
        public (int version, string bully)? Exported()
        {
            try
            {
                if (Game == null || !File.Exists(Manifest)) return null;
                var lines = File.ReadAllLines(Manifest);
                var m = Regex.Match(lines.FirstOrDefault() ?? "", @"BullyLink export v(\d+)");
                if (!m.Success) return null;
                var b = lines.FirstOrDefault(l => l.StartsWith("bully\t"))?.Substring(6).Trim();
                return (int.Parse(m.Groups[1].Value), b);
            }
            catch { return null; }
        }

        public bool ExportCurrent => Exported() is { } e && e.version == ExportVersion && string.Equals(e.bully?.TrimEnd('\\'), Bully?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    class LauncherForm : Form
    {
        static readonly Color Bg = Color.FromArgb(22, 22, 24), Panel = Color.FromArgb(34, 34, 38), Text_ = Color.FromArgb(232, 232, 232),
            Dim = Color.FromArgb(150, 150, 155), Green = Color.FromArgb(120, 200, 80), Red = Color.FromArgb(230, 90, 80), Amber = Color.FromArgb(235, 180, 70);

        readonly Paths _p = Paths.Find();
        readonly Label _game = new(), _melon = new(), _bully = new(), _files = new(), _mod = new(), _running = new();
        readonly TextBox _log = new();
        readonly Button _go = new(), _install = new(), _launch = new(), _findBully = new(), _uninstall = new(), _folder = new();
        readonly System.Windows.Forms.Timer _tick = new() { Interval = 1500 };
        bool _busy;

        public LauncherForm()
        {
            Text = "CyberBull-E Launcher";
            ClientSize = new Size(620, 580);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg; ForeColor = Text_; Font = new Font("Segoe UI", 10f);
            try { Icon = new Icon(typeof(LauncherForm).Assembly.GetManifestResourceStream("app.ico")); } catch { }

            // header: the mod's public name (2026-10-11, Austin: "CyberBull-E is the name"), a tagline under it
            var head = new Panel { Bounds = new Rectangle(0, 0, 620, 96), BackColor = Color.Black };
            var name = new Label { Text = "CYBERBULL", Font = new Font("Segoe UI Black", 26f, FontStyle.Bold), ForeColor = Color.White, AutoSize = true, Location = new Point(20, 12), BackColor = Color.Black };
            var e = new Label { Text = "-E", Font = new Font("Segoe UI Black", 26f, FontStyle.Bold), ForeColor = Green, AutoSize = true, BackColor = Color.Black };
            var tag = new Label { Text = "Jimmy Hopkins in Schedule I. Bring your own Bully.", Font = new Font("Segoe UI", 10f), ForeColor = Dim, AutoSize = true, Location = new Point(24, 62), BackColor = Color.Black };
            head.Controls.AddRange(new Control[] { name, e, tag });
            head.Layout += (_, _) => e.Location = new Point(name.Right - 6, 12);
            Controls.Add(head);

            var box = new Panel { Bounds = new Rectangle(16, 110, 588, 177), BackColor = Panel };
            int y = 10;
            foreach (var (title, l) in new[] { ("Schedule I", _game), ("MelonLoader", _melon), ("Bully: Scholarship Ed.", _bully), ("Bully files (yours)", _files), ("Mod", _mod), ("Game", _running) })
            {
                box.Controls.Add(new Label { Text = title, ForeColor = Dim, AutoSize = false, Bounds = new Rectangle(12, y, 170, 24), BackColor = Panel });
                l.AutoSize = false; l.Bounds = new Rectangle(186, y, 392, 24); l.BackColor = Panel; l.AutoEllipsis = true;
                box.Controls.Add(l);
                y += 27;
            }
            Controls.Add(box);

            Style(_go, "INSTALL + PLAY", new Rectangle(16, 300, 588, 52), Green, Color.Black, 13f);
            Style(_install, "Install / update", new Rectangle(16, 360, 112, 36), Panel, Text_, 9.5f);
            Style(_launch, "Play", new Rectangle(135, 360, 112, 36), Panel, Text_, 9.5f);
            Style(_findBully, "Find Bully...", new Rectangle(254, 360, 112, 36), Panel, Text_, 9.5f);
            Style(_uninstall, "Uninstall", new Rectangle(373, 360, 112, 36), Panel, Text_, 9.5f);
            Style(_folder, "Game folder", new Rectangle(492, 360, 112, 36), Panel, Text_, 9.5f);
            _go.Click += async (_, _) => await Run(async () => { if (await Install()) await Launch(); });
            _install.Click += async (_, _) => await Run(Install);
            _launch.Click += async (_, _) => await Run(Launch);
            _findBully.Click += async (_, _) => await Run(() => { PickBully(); return Task.CompletedTask; });
            _uninstall.Click += async (_, _) => await Run(() => { Uninstall(); return Task.CompletedTask; });
            _folder.Click += (_, _) => { if (_p.Game != null) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_p.Game}\"")); };

            _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Vertical;
            _log.Bounds = new Rectangle(16, 408, 588, 156); _log.BackColor = Color.FromArgb(14, 14, 16); _log.ForeColor = Dim;
            _log.BorderStyle = BorderStyle.None; _log.Font = new Font("Consolas", 9f);
            Controls.Add(_log);

            _tick.Tick += (_, _) => { if (!_busy) Refresh_(); };
            _tick.Start();
            Shown += (_, _) =>
            {
                Log($"Mod source: {_p.SourceName ?? "none found"}");
                Log("CyberBull-E ships nothing from Bully: it builds Jimmy and the rest from YOUR copy of Bully: Scholarship Edition.");
                Refresh_();
            };
        }

        void Style(Button b, string text, Rectangle r, Color bg, Color fg, float size)
        {
            b.Text = text; b.Bounds = r; b.BackColor = bg; b.ForeColor = fg; b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 76); b.Font = new Font("Segoe UI Semibold", size); b.Cursor = Cursors.Hand;
            Controls.Add(b);
        }

        void Log(string s)
        {
            if (InvokeRequired) { BeginInvoke(() => Log(s)); return; }
            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\r\n");
        }

        static void Set(Label l, string text, Color c) { l.Text = text; l.ForeColor = c; }
        static bool GameRunning() => Process.GetProcessesByName("Schedule I").Length > 0;

        void Refresh_()
        {
            var buttons = new[] { _go, _install, _launch, _uninstall };
            if (_p.Game == null)
            {
                Set(_game, "not found in any Steam library", Red);
                foreach (var b in buttons) b.Enabled = false;
                return;
            }
            Set(_game, _p.Game, Text_);
            Set(_melon, _p.MelonInstalled ? "installed" : "not installed (needed to load mods)", _p.MelonInstalled ? Green : Red);
            Set(_bully, _p.Bully ?? "not found: click Find Bully... (you need your own copy)", _p.Bully != null ? Text_ : Red);
            var ex = _p.Exported();
            if (ex == null) Set(_files, "not built yet (Install builds them from your Bully)", Amber);
            else if (_p.ExportCurrent) Set(_files, "built from your Bully", Green);
            else if (ex.Value.version != _p.ExportVersion) Set(_files, "built by an older exporter: Install rebuilds them", Amber);
            else Set(_files, "built from a different Bully folder: Install rebuilds them", Amber);
            string src = File.Exists(_p.SrcDll) ? Hash(_p.SrcDll) : null, dst = File.Exists(_p.DstDll) ? Hash(_p.DstDll) : null;
            if (src == null) Set(_mod, "no copy of the mod next to the launcher", Red);
            else if (dst == null) Set(_mod, "not installed", Amber);
            else if (src == dst) Set(_mod, "installed, up to date", Green);
            else Set(_mod, "update available", Amber);
            bool run = GameRunning();
            Set(_running, run ? "running" : "not running", run ? Green : Dim);
            _uninstall.Enabled = dst != null || ex != null;
        }

        static string Hash(string f) { using var s = File.OpenRead(f); return Convert.ToHexString(SHA256.HashData(s)); }

        /// <summary>One action at a time, locked BEFORE anything else happens. v0.23 fix: the buttons used to lock only after
        /// Install's first checks, and a second click in that gap ran two installs side by side (two exporters writing the
        /// same files, two launches; seen in the release test).</summary>
        async Task Run(Func<Task> job)
        {
            if (_busy) return;
            Busy(true);
            try { await job(); }
            finally { Busy(false); Refresh_(); }
        }

        void Busy(bool on)
        {
            _busy = on;
            foreach (var b in new[] { _go, _install, _launch, _uninstall, _findBully }) b.Enabled = !on;
            UseWaitCursor = on;
        }

        void PickBully()
        {
            using var dlg = new FolderBrowserDialog { Description = "Pick your Bully: Scholarship Edition folder (the one with Bully.exe)", UseDescriptionForTitle = true };
            if (_p.Bully != null) dlg.SelectedPath = _p.Bully;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (!Paths.IsBully(dlg.SelectedPath))
            {
                Log($"{dlg.SelectedPath} isn't a Bully: Scholarship Edition install (no Stream\\World.img, TXD\\effects.nft or Audio\\PLAYLIST\\Speech.bin).");
                return;
            }
            _p.Bully = dlg.SelectedPath;
            Paths.Save("bully", _p.Bully);
            Log($"Bully: {_p.Bully}");
            Refresh_();
        }

        /// <summary>Close the game if it's running (asked first): files can't be replaced under a running game.</summary>
        async Task<bool> GameClosed(string why)
        {
            if (!GameRunning()) return true;
            if (MessageBox.Show(this, $"Schedule I is running. Close it to {why}?", "CyberBull-E", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            foreach (var pr in Process.GetProcessesByName("Schedule I")) { try { pr.Kill(); } catch { } }
            for (int i = 0; i < 40 && GameRunning(); i++) await Task.Delay(250);
            await Task.Delay(1500);                          // let it let go of its files
            Log("Closed Schedule I.");
            return !GameRunning();
        }

        /// <summary>Run the exporter (hidden), streaming its progress into the log. True when it finished and wrote its manifest.</summary>
        async Task<bool> Export()
        {
            if (!File.Exists(_p.Exporter)) { Log($"The exporter is missing ({_p.Exporter}). Re-download CyberBull-E."); return false; }
            Log("Building the Bully files from your copy of Bully (takes a few minutes the first time)...");
            var psi = new ProcessStartInfo(_p.Exporter) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--bully"); psi.ArgumentList.Add(_p.Bully);
            psi.ArgumentList.Add("--schedule"); psi.ArgumentList.Add(_p.Game);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            var full = new List<string>();
            using var pr = new Process { StartInfo = psi };
            pr.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (full) full.Add(e.Data);
                if (e.Data.StartsWith("[") || e.Data.StartsWith("EXPORT") || e.Data.StartsWith("ERROR")) Log(e.Data);
            };
            pr.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (full) full.Add(e.Data); };
            pr.Start();
            pr.BeginOutputReadLine(); pr.BeginErrorReadLine();
            await pr.WaitForExitAsync();
            try { Directory.CreateDirectory(_p.Root); File.WriteAllLines(Path.Combine(_p.Root, "export.log"), full); } catch { }
            bool ok = pr.ExitCode == 0 && _p.Exported() != null;
            Log(ok ? "Bully files built." : $"The export failed (exit {pr.ExitCode}). Details: UserData\\BullyLink\\export.log");
            return ok;
        }

        async Task<bool> Install()
        {
            if (_p.Game == null) return false;
            if (!_p.MelonInstalled)
            {
                if (MessageBox.Show(this, "MelonLoader isn't installed in Schedule I, and the mod can't load without it.\n\nOpen the MelonLoader download page?", "CyberBull-E", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo("https://github.com/LavaGang/MelonLoader/releases") { UseShellExecute = true });
                return false;
            }
            if (_p.Bully == null)
            {
                Log("CyberBull-E needs your own copy of Bully: Scholarship Edition. Click Find Bully... and pick its folder.");
                PickBully();
                if (_p.Bully == null) return false;
            }
            if (!File.Exists(_p.SrcDll)) { Log($"No copy of the mod at {_p.SrcDll}."); return false; }
            bool needDll = !File.Exists(_p.DstDll) || Hash(_p.SrcDll) != Hash(_p.DstDll);
            bool needExport = !_p.ExportCurrent;
            if (!needDll && !needExport) { Log("Everything's already installed and up to date."); return true; }
            if (!await GameClosed("install")) { Log("Install cancelled: the game is still running."); return false; }
            try
            {
                if (needExport && !await Export()) return false;
                if (needDll)
                {
                    await Task.Run(() => { Directory.CreateDirectory(_p.ModsDir); File.Copy(_p.SrcDll, _p.DstDll, true); });
                    Log("Installed BullyLink.dll into Mods.");
                }
                bool ok = Hash(_p.SrcDll) == Hash(_p.DstDll) && _p.ExportCurrent;
                Log(ok ? "Install verified." : "Install check FAILED: the installed mod or the Bully files don't match.");
                return ok;
            }
            catch (Exception e) { Log($"Install failed: {e.Message}"); return false; }
        }

        async Task Launch()
        {
            if (_p.Game == null) return;
            if (GameRunning())
            {
                if (MessageBox.Show(this, "Schedule I is already running. Restart it?", "CyberBull-E", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                if (!await GameClosed("restart it")) return;
            }
            if (!_p.ExportCurrent) Log("Heads up: the Bully files aren't built yet, so the game starts without Jimmy. Use Install first.");
            try
            {
                var started = DateTime.Now;
                // v0.23 fix: the trace is appended across runs, so an OLD "past startup" line used to count as proof
                // (it claimed "running" 63 s before the mod got there). Only what's written after this launch counts.
                long traceFrom = File.Exists(_p.Trace) ? new FileInfo(_p.Trace).Length : 0;
                Process.Start(new ProcessStartInfo($"steam://rungameid/{Paths.AppId}") { UseShellExecute = true });
                Log("Launching Schedule I through Steam...");
                // proof, not hope: wait for the game process, then MelonLoader's log naming BullyLink, then the mod's own
                // trace saying it got past startup
                bool proc = false, loaded = false, past = false;
                for (int i = 0; i < 120 && !past; i++)
                {
                    await Task.Delay(1000);
                    if (!proc && GameRunning()) { proc = true; Log("Game process is up."); }
                    if (proc && !GameRunning() && i > 15) { Log("The game closed during startup. Check MelonLoader\\Latest.log."); return; }
                    if (!loaded && Fresh(_p.MelonLog, started))
                    {
                        var m = Regex.Match(ReadShared(_p.MelonLog), @"BullyLink v([\d.]+)");
                        if (m.Success) { loaded = true; Log($"MelonLoader loaded the mod (BullyLink.dll v{m.Groups[1].Value})."); }
                    }
                    if (loaded && Fresh(_p.Trace, started) && ReadShared(_p.Trace, traceFrom).Contains("past startup")) past = true;
                }
                Log(past ? "CyberBull-E is running. Have fun, and watch out for Russell." : loaded ? "Mod loaded; still starting up." : "Couldn't confirm the mod loaded within 2 minutes.");
            }
            catch (Exception e) { Log($"Launch failed: {e.Message}"); }
        }

        static bool Fresh(string f, DateTime since) => File.Exists(f) && File.GetLastWriteTime(f) >= since;

        /// <summary>A log another process is writing, from byte <paramref name="from"/> on (or its last 4 MB).</summary>
        static string ReadShared(string f, long from = 0)
        {
            try
            {
                using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (from > 0 && from <= s.Length) s.Seek(from, SeekOrigin.Begin);
                if (s.Length - s.Position > 4_000_000) s.Seek(-4_000_000, SeekOrigin.End);
                return new StreamReader(s).ReadToEnd();
            }
            catch { return ""; }
        }

        void Uninstall()
        {
            if (GameRunning()) { Log("Close the game first."); return; }
            var ans = MessageBox.Show(this, "Remove CyberBull-E from Mods?\n\nYes: remove the mod AND the Bully files it built (your saved outfits stay).\nNo: remove just the mod (reinstalling is quick).",
                "CyberBull-E", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (ans == DialogResult.Cancel) return;
            try
            {
                if (File.Exists(_p.DstDll)) { File.Delete(_p.DstDll); Log("CyberBull-E removed from Mods. Schedule I will start without it."); }
                if (ans == DialogResult.Yes)
                {
                    foreach (var d in new[] { "data", "peds", "clothes", "cache" })
                    {
                        var dir = Path.Combine(_p.Root, d);
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    }
                    Log("Removed the Bully files built from your install (saved outfits kept).");
                }
            }
            catch (Exception e) { Log($"Uninstall failed: {e.Message}"); }
            Refresh_();
        }
    }
}
