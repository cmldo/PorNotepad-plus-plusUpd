using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NppPortableUpdater
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }

    public sealed class MainForm : Form
    {
        Button btnCheck, btnUpdate;
        ProgressBar progress;
        TextBox log;

        readonly string root;
        readonly string nppDir;
        readonly string nppExe;
        string? assetUrl;

        Version? installedVersion;
        Version? latestVersion;

        const string API =
            "https://api.github.com/repos/notepad-plus-plus/notepad-plus-plus/releases/latest";

        public MainForm()
        {
            Text = "Notepad++ Portable Updater";
            Width = 640;
            Height = 420;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            btnCheck  = new Button { Text = "Check version",    Left = 20,  Top = 20, Width = 160 };
            btnUpdate = new Button { Text = "Update Notepad++", Left = 200, Top = 20, Width = 140, Enabled = false };
            progress  = new ProgressBar { Left = 20, Top = 60, Width = 580 };
            log       = new TextBox { Left = 20, Top = 95, Width = 580, Height = 260,
                                      Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };

            Controls.AddRange(new Control[] { btnCheck, btnUpdate, progress, log });

            btnCheck.Click  += async (_, __) => await CheckLatestAsync();
            btnUpdate.Click += async (_, __) => await UpdateNppAsync();

            root   = AppDomain.CurrentDomain.BaseDirectory;
            nppDir = Path.Combine(root, "notepad-plus-plus");
            nppExe = Path.Combine(nppDir, "notepad++.exe");
        }

        void Log(string msg)
        {
            log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
            log.SelectionStart = log.Text.Length;
            log.ScrollToCaret();
            Application.DoEvents();
        }

        void HardFail(string msg)
        {
            Log("FATAL: " + msg);
            MessageBox.Show(msg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            throw new InvalidOperationException(msg);
        }

        // Looks for 7z.exe next to the updater first, then common install paths
        string Find7Zip()
        {
            var candidates = new[]
            {
                Path.Combine(root, "7z.exe"),
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe"
            };

            var found = candidates.FirstOrDefault(File.Exists);
            if (found == null)
                HardFail("7z.exe not found. Install 7-Zip or place 7z.exe next to this updater.");

            return found!;
        }

        void Extract7z(string archivePath, string outputDir)
        {
            string sevenZip = Find7Zip();
            Log($"Using: {sevenZip}");

            if (Directory.Exists(outputDir))
            {
                Log("Removing old installation...");
                Directory.Delete(outputDir, recursive: true);
            }
            Directory.CreateDirectory(outputDir);

            var psi = new ProcessStartInfo
            {
                FileName               = sevenZip,
                Arguments              = $"x \"{archivePath}\" -o\"{outputDir}\" -y",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };

            using var p = Process.Start(psi)!;

            // Forward 7z output to our log
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) Log("ERR: " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            p.WaitForExit();

            if (p.ExitCode != 0)
                HardFail($"7-Zip exited with code {p.ExitCode}.");
        }

        Version GetInstalledVersion()
        {
            if (!File.Exists(nppExe))
            {
                Log("Notepad++ not found. Will download latest version...");
                return Version.Parse("0.0");
            }

            var info = FileVersionInfo.GetVersionInfo(nppExe);
            var raw  = (info.ProductVersion ?? info.FileVersion ?? "0.0")
                           .Split(' ')[0]
                           .Split('-')[0];

            return Version.Parse(raw);
        }

        async Task CheckLatestAsync()
        {
            try
            {
                progress.Value = 10;
                Log("Checking installed Notepad++ version...");
                installedVersion = GetInstalledVersion();
                Log($"Installed version: {installedVersion}");

                progress.Value = 30;
                Log("Checking latest GitHub release...");

                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("NppPortableUpdater");
                var json = await client.GetStringAsync(API);
                using var doc = JsonDocument.Parse(json);

                var tag = doc.RootElement.GetProperty("tag_name").GetString()!;
                latestVersion = Version.Parse(tag.TrimStart('v'));

                // portable .7z for x64, skip arm64
                var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
                    .FirstOrDefault(a =>
                    {
                        var name = a.GetProperty("name").GetString() ?? "";
                        return name.EndsWith(".portable.7z", StringComparison.OrdinalIgnoreCase)
                            && !name.Contains("arm64", StringComparison.OrdinalIgnoreCase);
                    });

                if (asset.ValueKind == JsonValueKind.Undefined)
                    HardFail("Could not find portable .7z asset for x64.");

                assetUrl = asset.GetProperty("browser_download_url").GetString();

                Log($"Latest version: {latestVersion}");
                Log($"Asset: {Path.GetFileName(assetUrl)}");
                progress.Value = 100;

                if (installedVersion < latestVersion)
                {
                    Log("Update available 🚀");
                    btnUpdate.Enabled = true;
                }
                else Log("Notepad++ is up to date ✔");
            }
            catch (Exception ex)
            {
                HardFail(ex.Message);
            }
        }

        void CreateDesktopShortcut()
        {
            try
            {
                string desktopPath  = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string shortcutPath = Path.Combine(desktopPath, "Notepad++ Portable.lnk");

                Type shellType   = Type.GetTypeFromProgID("WScript.Shell")!;
                dynamic shell    = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);

                shortcut.TargetPath       = nppExe;
                shortcut.WorkingDirectory = nppDir;
                shortcut.Description      = "Launch Notepad++ in Portable Mode";
                shortcut.Save();

                Log($"Shortcut created: {shortcutPath}");
            }
            catch (Exception ex)
            {
                Log($"Warning: could not create shortcut — {ex.Message}");
            }
        }

        async Task UpdateNppAsync()
        {
            if (assetUrl == null || latestVersion == null) HardFail("No update info.");

            try
            {
                btnUpdate.Enabled = false;
                progress.Value    = 0;

                string archiveName = Path.GetFileName(assetUrl!);
                string archivePath = Path.Combine(root, archiveName);

                if (File.Exists(archivePath))
                {
                    Log($"Deleting existing {archiveName}...");
                    File.Delete(archivePath);
                }

                Log($"Downloading {archiveName}...");
                progress.Value = 20;

                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("NppPortableUpdater");
                var data = await client.GetByteArrayAsync(assetUrl);
                await File.WriteAllBytesAsync(archivePath, data);

                Log("Extracting...");
                progress.Value = 50;
                Extract7z(archivePath, nppDir);

                progress.Value = 80;
                Log($"Deleting {archiveName}...");
                File.Delete(archivePath);

                progress.Value = 90;
                CreateDesktopShortcut();

                progress.Value = 100;
                Log("Notepad++ updated successfully ✔");
                MessageBox.Show("Update complete!", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                HardFail("Update failed: " + ex.Message);
            }
        }
    }
}
