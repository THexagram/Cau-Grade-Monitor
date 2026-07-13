using System.Text.Json;
using CauGradeMonitor.Desktop.Models;

namespace CauGradeMonitor.Desktop.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Load()
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(AppPaths.SettingsFile)) return new AppSettings();

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions)
                ?? new AppSettings();
            if (settings.RememberSecrets)
            {
                settings.VpnPassword = TryUnprotect(settings.EncryptedVpnPassword);
                settings.FeishuWebhook = TryUnprotect(settings.EncryptedFeishuWebhook);
                settings.FeishuSecret = TryUnprotect(settings.EncryptedFeishuSecret);
            }
            return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        AppPaths.EnsureCreated();
        if (settings.RememberSecrets)
        {
            settings.EncryptedVpnPassword = SecretProtector.Protect(settings.VpnPassword);
            settings.EncryptedFeishuWebhook = SecretProtector.Protect(settings.FeishuWebhook);
            settings.EncryptedFeishuSecret = SecretProtector.Protect(settings.FeishuSecret);
        }
        else
        {
            settings.EncryptedVpnPassword = "";
            settings.EncryptedFeishuWebhook = "";
            settings.EncryptedFeishuSecret = "";
        }

        var temporaryFile = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryFile, AppPaths.SettingsFile, true);
    }

    private static string TryUnprotect(string encrypted)
    {
        try
        {
            return SecretProtector.Unprotect(encrypted);
        }
        catch
        {
            return "";
        }
    }
}
