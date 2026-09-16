using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capture.Core.Paths;
using Capture.Core.Watch;

namespace Capture.Storage;

/// <summary>Hands a secret (AI API key, Therefore password/bearer token, ...) off to an OS-level
/// secret store instead of writing it to settings.json, keyed by a caller-chosen account name so one
/// store instance can hold several independent secrets. Swappable so tests don't touch the real
/// macOS Keychain / Linux Secret Service — see <see cref="NullOsCredentialStore"/>.</summary>
public interface IOsCredentialStore
{
    bool TryStore(string account, string value);

    string? TryRead(string account);
}

/// <summary>No-op store: never claims a value was stored, so callers keep the plaintext fallback.
/// Used on platforms with no supported store, and by tests.</summary>
public sealed class NullOsCredentialStore : IOsCredentialStore
{
    public bool TryStore(string account, string value) => false;

    public string? TryRead(string account) => null;
}

public sealed class JsonWatchSettingsStore : IWatchSettingsStore
{
    // Written to settings.json in place of the actual value once it's been handed off to the OS
    // credential store (macOS Keychain / Linux Secret Service) — tells LoadAsync to fetch the real
    // value from there instead of trusting the file to hold it. Shared across every secret field:
    // each Protect/Unprotect call already knows which account it's operating on, so there's no
    // collision risk in reusing one sentinel string.
    private const string KeychainSentinel = "::keychain::";

    private readonly IAppPaths _paths;
    private readonly IOsCredentialStore _credentialStore;
    private readonly object _cacheLock = new();
    private WatchSettings? _cached;

    public JsonWatchSettingsStore(IAppPaths paths) : this(paths, ResolveDefaultCredentialStore())
    {
    }

    public JsonWatchSettingsStore(IAppPaths paths, IOsCredentialStore credentialStore)
    {
        _paths = paths;
        _credentialStore = credentialStore;
    }

    private static IOsCredentialStore ResolveDefaultCredentialStore()
    {
        if (OperatingSystem.IsMacOS())
            return new MacKeychainCredentialStore();
        if (OperatingSystem.IsLinux())
            return new LinuxSecretServiceCredentialStore();
        return new NullOsCredentialStore(); // Windows protects the value with DPAPI directly instead.
    }

    // Unprotect() shells out to a native credential store (macOS `security`, Linux `secret-tool`) per
    // secret — a real subprocess spawn, not a cheap call — and LoadAsync used to pay that (up to three
    // times, one per secret) on every single call. IFieldScriptRunner.IsAvailable and IAiExtractor.
    // IsConfigured both call LoadAsync synchronously on every field/document during capture, so an
    // unattended run with AI or script fields could spawn hundreds of these processes for no reason —
    // none of them read a secret's value, they only need a couple of unrelated booleans. Caching the
    // fully-loaded (and already-unprotected) settings here means only the first LoadAsync after startup
    // or after a SaveAsync pays that cost; every later call returns instantly from memory. Each call
    // still gets its own independent clone (a JSON round-trip — cheap relative to what it replaces) so
    // no caller can mutate another caller's copy or the cache by editing the object it got back, the
    // same guarantee a fresh deserialize gave before.
    public async Task<WatchSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        lock (_cacheLock)
        {
            if (_cached is not null)
                return Clone(_cached);
        }

        _paths.EnsureCreated();
        WatchSettings settings;
        if (!File.Exists(_paths.SettingsPath))
        {
            settings = new WatchSettings();
        }
        else
        {
            await using var stream = File.OpenRead(_paths.SettingsPath);
            settings = await JsonSerializer.DeserializeAsync<WatchSettings>(stream, LatticeJson.Options, cancellationToken)
                .ConfigureAwait(false) ?? new WatchSettings();
            settings.AiApiKey = Unprotect("AiApiKey", settings.AiApiKey);
            settings.ThereforePassword = Unprotect("ThereforePassword", settings.ThereforePassword);
            settings.ThereforeBearerToken = Unprotect("ThereforeBearerToken", settings.ThereforeBearerToken);
        }

        lock (_cacheLock)
            _cached = settings;
        return Clone(settings);
    }

    private static WatchSettings Clone(WatchSettings source) =>
        JsonSerializer.Deserialize<WatchSettings>(JsonSerializer.Serialize(source, LatticeJson.Options), LatticeJson.Options)!;

    public async Task SaveAsync(WatchSettings settings, CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var toSave = new WatchSettings
        {
            WatchFolders = settings.WatchFolders,
            HasCompletedFirstRunSetup = settings.HasCompletedFirstRunSetup,
            StartView = settings.StartView,
            Theme = settings.Theme,
            InboxOrder = settings.InboxOrder,
            DebugMode = settings.DebugMode,
            CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup,
            AllowFieldScripts = settings.AllowFieldScripts,
            AiEndpoint = settings.AiEndpoint,
            AiApiKey = Protect("AiApiKey", settings.AiApiKey),
            AiModel = settings.AiModel,
            AiMaxDocumentChars = settings.AiMaxDocumentChars,
            AiProvider = settings.AiProvider,
            LocalAiMaxDocumentChars = settings.LocalAiMaxDocumentChars,
            LastCaptureProfileId = settings.LastCaptureProfileId,
            AutoDeleteExportedDocuments = settings.AutoDeleteExportedDocuments,
            AutoDeleteExportedDocumentsAfterDays = settings.AutoDeleteExportedDocumentsAfterDays,
            RemoveDocumentsAfterExport = settings.RemoveDocumentsAfterExport,
            TrashRetentionDays = settings.TrashRetentionDays,
            DuplicateImportBehavior = settings.DuplicateImportBehavior,
            ThereforeBaseUrl = settings.ThereforeBaseUrl,
            ThereforeTenantName = settings.ThereforeTenantName,
            ThereforeAuthMethod = settings.ThereforeAuthMethod,
            ThereforeUsername = settings.ThereforeUsername,
            ThereforePassword = Protect("ThereforePassword", settings.ThereforePassword),
            ThereforeBearerToken = Protect("ThereforeBearerToken", settings.ThereforeBearerToken),
            ScanDpi = settings.ScanDpi,
            ScanGrayscale = settings.ScanGrayscale,
            ScanSource = settings.ScanSource,
            ScanDuplex = settings.ScanDuplex,
            ScanPreferredDeviceId = settings.ScanPreferredDeviceId
        };
        await LatticeJson.WriteJsonAsync(_paths.SettingsPath, toSave, LatticeJson.Options, cancellationToken).ConfigureAwait(false);

        // The caller's settings object already holds the current (unprotected) values — toSave only
        // exists to write the protected-for-disk form — so it becomes the new cache directly rather
        // than re-reading the file and re-running Unprotect() against what was just written.
        lock (_cacheLock)
            _cached = Clone(settings);
    }

    private string? Protect(string account, string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        if (OperatingSystem.IsWindows())
        {
            var entropy = Encoding.UTF8.GetBytes("Capture.WatchSettings." + account);
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        // macOS/Linux: hand the real value to the OS credential store and leave only a sentinel in
        // the settings file. If that store isn't available (headless box, tool not installed, etc.),
        // fall back to writing the plaintext value as before rather than losing the key entirely.
        return _credentialStore.TryStore(account, plainText) ? KeychainSentinel : plainText;
    }

    private string? Unprotect(string account, string? storedValue)
    {
        if (string.IsNullOrEmpty(storedValue))
            return storedValue;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var entropy = Encoding.UTF8.GetBytes("Capture.WatchSettings." + account);
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(storedValue), entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                // Pre-existing plaintext value from before encryption was introduced.
                return storedValue;
            }
            catch (CryptographicException)
            {
                // Pre-existing plaintext value from before encryption was introduced.
                return storedValue;
            }
        }

        if (storedValue != KeychainSentinel)
            return storedValue; // pre-existing plaintext value from before this was introduced

        var fromStore = _credentialStore.TryRead(account);
        if (fromStore is null)
            Trace.TraceWarning($"Could not read \"{account}\" back from the OS credential store.");

        return fromStore;
    }
}

public sealed class MacKeychainCredentialStore : IOsCredentialStore
{
    private const string Service = "Capture.WatchSettings";

    public bool TryStore(string account, string value)
    {
        try
        {
            // -U updates the item in place if one already exists, instead of erroring.
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/security")
            {
                ArgumentList = { "add-generic-password", "-U", "-a", account, "-s", Service, "-w", value },
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return false;
            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceWarning($"Could not store \"{account}\" in the macOS Keychain: {ex.Message}");
            return false;
        }
    }

    public string? TryRead(string account)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/security")
            {
                ArgumentList = { "find-generic-password", "-a", account, "-s", Service, "-w" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

// Uses secret-tool (libsecret) — the standard CLI for the Secret Service API on Linux desktops
// (GNOME Keyring, KWallet via a compatible provider, etc). Not present on headless systems with no
// Secret Service running, which Protect()/Unprotect() fall back gracefully around via TryStore/TryRead
// returning false/null.
public sealed class LinuxSecretServiceCredentialStore : IOsCredentialStore
{
    private const string Service = "Capture.WatchSettings";

    public bool TryStore(string account, string value)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("secret-tool")
            {
                ArgumentList = { "store", $"--label=Capture {account}", "service", Service, "account", account },
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return false;
            process.StandardInput.Write(value);
            process.StandardInput.Close();
            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceWarning($"Could not store \"{account}\" in the Linux Secret Service: {ex.Message}");
            return false;
        }
    }

    public string? TryRead(string account)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("secret-tool")
            {
                ArgumentList = { "lookup", "service", Service, "account", account },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
