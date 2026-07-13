using System.Text.Json.Serialization;

namespace CauGradeMonitor.Desktop.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool RememberSecrets { get; set; } = true;
    public bool AutoStartServices { get; set; }
    public bool MinimizeToTray { get; set; } = true;

    public string VpnServer { get; set; } = "vpn.cau.edu.cn";
    public int VpnPort { get; set; } = 443;
    public string VpnUsername { get; set; } = "";
    public string EncryptedVpnPassword { get; set; } = "";
    public string SocksBind { get; set; } = "127.0.0.1:1080";
    public string ResolveRule { get; set; } = "newjw.cau.edu.cn=10.200.36.235";
    public string EasierConnectPath { get; set; } = "";
    public int VpnRestartMinutes { get; set; } = 120;
    public int EofRestartCount { get; set; } = 4;

    public int PollIntervalSeconds { get; set; } = 60;
    public string LoginUrl { get; set; } = "https://one.cau.edu.cn/tp_up/view?m=up#act=portal/viewhome";
    public string GradeUrl { get; set; } = "https://newjw.cau.edu.cn/jsxsd/kscj/cjcx_frm";
    public string ListUrl { get; set; } = "https://newjw.cau.edu.cn/jsxsd/kscj/cjcx_list";

    public string EncryptedFeishuWebhook { get; set; } = "";
    public string EncryptedFeishuSecret { get; set; } = "";
    public bool FeishuAtAll { get; set; }
    public bool NotifyOnStart { get; set; } = true;

    [JsonIgnore]
    public string VpnPassword { get; set; } = "";

    [JsonIgnore]
    public string FeishuWebhook { get; set; } = "";

    [JsonIgnore]
    public string FeishuSecret { get; set; } = "";
}
