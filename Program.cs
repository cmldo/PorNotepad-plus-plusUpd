using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using SharpCompress.Archives;
using SharpCompress.Common;

// NuGet: SharpCompress (for .7z support)
// dotnet add package SharpCompress

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

            btnCheck  = new Button { Text = "Check version", Left = 20, Top = 20, Width = 160 };
            btnUpdate = new Button { Text = "Update Notepad++", Left = 200, Top = 20, Width = 140, Enabled = false };
            progress  = new ProgressBar { Left = 20, Top = 60, Width = 580 };
            log       = new TextBox   { Left = 20, Top = 95, Width = 580, Height = 260,
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

        Version GetInstalledVersion()
        {
            if (!File.Exists(nppExe))
            {
                Log("Notepad++ not found. Will download latest version...");
                return Version.Parse("0.0");
            }

            var info = FileVersionInfo.GetVersionInfo(nppExe);

            // FileVersionInfo.ProductVersion can contain commit hashes like "8.7.6.0 (64-bit)"
            // so we strip everything after the first space and take only numeric segments.
            var raw = (info.ProductVersion ?? info.FileVersion ?? "0.0")
                          .Split(' ')[0]          // drop any suffix like "(64-bit)"
                          .Split('-')[0];         // drop any pre-release tag

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

                // Find the portable .7z asset for x64 (skip arm64 variants)
                var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
                    .FirstOrDefault(a =>
                    {
                        var name = a.GetProperty("name").GetString() ?? "";
                        return name.EndsWith(".portable.7z", StringComparison.OrdinalIgnoreCase)
                            && !name.Contains("arm64", StringComparison.OrdinalIgnoreCase);
                    });

                if (asset.ValueKind == JsonValueKind.Undefined)
                    HardFail("Could not find a portable .7z asset for x64.");

                assetUrl = asset.GetProperty("browser_download_url").GetString();

                Log($"Latest version : {latestVersion}");
                Log($"Asset          : {Path.GetFileName(assetUrl)}");
                progress.Value = 100;

                if (installedVersion < latestVersion)
                {
                    Log("Update available 🚀");
                    btnUpdate.Enabled = true;
                }
                else
                {
                    Log("Notepad++ is up to date ✔");
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                HardFail(ex.Message);
            }
        }

        void CreateDesktopShortcut()
        {
            try
            {
                string shortcutPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "Notepad++ Portable.lnk");

                Type shellType = Type.GetTypeFromProgID("WScript.Shell")!;
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
            if (assetUrl == null || latestVersion == null)
                HardFail("No update info. Run 'Check version' first.");

            try
            {
                btnUpdate.Enabled = false;
                progress.Value    = 0;

                string archiveName = Path.GetFileName(assetUrl!);
                string archivePath = Path.Combine(root, archiveName);

                // ── 1. Download ──────────────────────────────────────────
                if (File.Exists(archivePath))
                {
                    Log($"Removing stale download: {archiveName}");
                    File.Delete(archivePath);
                }

                Log($"Downloading {archiveName}…");
                progress.Value = 10;

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("NppPortableUpdater");

                    // Stream the download so we can report progress
                    using var response = await client.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    long total   = response.Content.Headers.ContentLength ?? -1;
                    long written = 0;

                    await using var src  = await response.Content.ReadAsStreamAsync();
                    await using var dest = File.Create(archivePath);

                    var buffer = new byte[81_920];
                    int read;
                    while ((read = await src.ReadAsync(buffer)) > 0)
                    {
                        await dest.WriteAsync(buffer.AsMemory(0, read));
                        written += read;
                        if (total > 0)
                            progress.Value = 10 + (int)(written * 40 / total); // 10 → 50 %
                    }
                }

                Log("Download complete.");

                // ── 2. Extract (SharpCompress handles .7z natively) ──────
                Log("Extracting…");
                progress.Value = 55;

                if (Directory.Exists(nppDir))
                {
                    Log("Removing old installation…");
                    Directory.Delete(nppDir, recursive: true);
                }
                Directory.CreateDirectory(nppDir);

                using (var archive = ArchiveFactory.Open(archivePath))
                {
                    var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                    int total   = entries.Count;
                    int done    = 0;

                    foreach (var entry in entries)
                    {
                        entry.WriteToDirectory(nppDir,
                            new ExtractionOptions { ExtractFullPath = true, Overwrite = true });

                        done++;
                        progress.Value = 55 + (int)(done * 30 / total); // 55 → 85 %
                    }
                }

                Log("Extraction complete.");

                // ── 3. Clean up archive ──────────────────────────────────
                Log($"Deleting {archiveName}…");
                File.Delete(archivePath);
                progress.Value = 90;

                // ── 4. Shortcut ──────────────────────────────────────────
                CreateDesktopShortcut();
                progress.Value = 100;

                Log("Notepad++ updated successfully ✔");
                MessageBox.Show(
                    $"Notepad++ {latestVersion} is ready!\n\nShortcut placed on your Desktop.",
                    "Update complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                HardFail("Update failed: " + ex.Message);
            }
        }
    }
}
