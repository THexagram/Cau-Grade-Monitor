using System.Text.Json;
using CauGradeMonitor.Desktop.Models;

namespace CauGradeMonitor.Desktop.Services;

public static class MonitorConfigWriter
{
    public static void Write(AppSettings settings, string monitorRoot)
    {
        AppPaths.EnsureCreated();
        var extensionDirectory = Path.Combine(monitorRoot, "edge-cau-proxy-extension");
        var config = new
        {
            manualLogin = true,
            loginUrl = settings.LoginUrl,
            gradeUrl = settings.GradeUrl,
            listUrl = settings.ListUrl,
            term = "auto",
            intervalSeconds = settings.PollIntervalSeconds,
            initialLoginRetrySeconds = 10,
            errorNotifyCooldownSeconds = 1800,
            emptyResultNotifyAfter = 3,
            emptyResultRetrySeconds = settings.PollIntervalSeconds,
            requestTimeoutMs = 30000,
            logging = new
            {
                noChangeEverySeconds = 1800,
                loginNeededEverySeconds = 300
            },
            gpa = new
            {
                scope = settings.GpaScope,
                useSelectedTypes = settings.GpaSelectedTypesConfigured,
                selectedTypes = settings.GpaSelectedTypes,
                includedCourseKeys = settings.GpaIncludedCourseKeys,
                excludedCourseKeys = settings.GpaExcludedCourseKeys
            },
            stateFile = AppPaths.MonitorStateFile,
            guiSnapshotFile = AppPaths.MonitorGuiSnapshotFile,
            proxy = new
            {
                enabled = true,
                server = $"socks5://{settings.SocksBind}",
                browser = false,
                bypass = "localhost,127.0.0.1,one.cau.edu.cn,onecas.cau.edu.cn,wep.cau.edu.cn,jx.cau.edu.cn,imrobot.cau.edu.cn"
            },
            browser = new
            {
                channel = "msedge",
                executablePath = "",
                lowResourceMode = true,
                closeExtraPages = true,
                maxScanPages = 6,
                blockResourcesAfterFirstSuccess = true,
                blockResourceTypes = new[] { "image", "media", "font" },
                headless = false,
                userDataDir = AppPaths.BrowserProfileDirectory,
                proxyPacFile = "",
                loadExtensionDir = Directory.Exists(extensionDirectory) ? extensionDirectory : "",
                args = new[]
                {
                    "--host-resolver-rules=MAP newjw.cau.edu.cn 10.200.36.235,EXCLUDE localhost",
                    "--disable-background-networking",
                    "--disable-component-update",
                    "--disable-default-apps",
                    "--disable-domain-reliability",
                    "--disable-client-side-phishing-detection",
                    "--disable-sync",
                    "--metrics-recording-only",
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--safebrowsing-disable-auto-update"
                },
                viewport = new { width = 1100, height = 720 }
            },
            feishu = new
            {
                webhookEnv = "FEISHU_WEBHOOK_URL",
                secretEnv = "FEISHU_BOT_SECRET",
                atAll = settings.FeishuAtAll,
                notifyOnStart = settings.NotifyOnStart
            }
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var temporaryFile = AppPaths.MonitorConfigFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(config, options));
        File.Move(temporaryFile, AppPaths.MonitorConfigFile, true);
    }
}
