using Avalonia.Controls;
using Avalonia.Interactivity;
using Capture.Storage;

namespace Capture.App.Views;

/// <summary>What to back up, chosen in <see cref="BackupWindow"/>.</summary>
public sealed record BackupChoice(BackupParts Parts, bool IncludeCredentials);

/// <summary>Chooses which parts of Capture's configuration to back up, or which parts of a backup to
/// restore. One window for both, since the choices are the same.</summary>
public partial class BackupWindow : Window
{
    private readonly bool _restoring;
    private readonly bool _backupHasScripts;

    public BackupChoice? Result { get; private set; }

    public BackupWindow()
    {
        InitializeComponent();
    }

    /// <summary>Back up mode: everything available is ticked; empty parts are greyed out.</summary>
    public BackupWindow(BackupInventory inventory) : this()
    {
        Title = "Back up";
        HeadingText.Text = "Choose what to back up";
        ConfirmButton.Content = "Back up…";
        ProfilesText.Text = Count(inventory.CaptureProfileCount, "Capture profile");
        RedactionSetsText.Text = Count(inventory.RedactionSetCount, "Custom redaction set");
        Offer(ProfilesCheckBox, inventory.CaptureProfileCount > 0);
        Offer(RedactionSetsCheckBox, inventory.RedactionSetCount > 0);
        Offer(AiCatalogCheckBox, inventory.HasAiFieldCatalog);
        NotesText.Text = "Saved as one .zip file. Documents aren't included. Only saved settings are backed up, so press Save first if you've just changed something.";
        UpdateConfirm();
    }

    /// <summary>Restore mode: only the parts the backup holds are shown.</summary>
    public BackupWindow(CaptureBackup backup) : this()
    {
        _restoring = true;
        _backupHasScripts = backup.HasScripts;
        Title = "Restore";
        HeadingText.Text = "Choose what to restore";
        ConfirmButton.Content = "Restore";
        var made = backup.Manifest.CreatedUtc.ToLocalTime().ToString("d MMM yyyy, h:mm tt");
        SourceText.Text = backup.Manifest.AppVersion is { Length: > 0 } version
            ? $"Backup made {made} by Capture {version}"
            : $"Saved {made}";
        SourceText.IsVisible = true;

        ProfilesText.Text = Count(backup.CaptureProfiles.Count, "Capture profile");
        RedactionSetsText.Text = Count(backup.RedactionSets.Count, "Custom redaction set");
        SettingsCheckBox.IsVisible = backup.Parts.HasFlag(BackupParts.Settings);
        ProfilesCheckBox.IsVisible = backup.Parts.HasFlag(BackupParts.CaptureProfiles);
        RedactionSetsCheckBox.IsVisible = backup.Parts.HasFlag(BackupParts.RedactionSets);
        AiCatalogCheckBox.IsVisible = backup.Parts.HasFlag(BackupParts.AiFieldCatalog);

        CredentialsDivider.IsVisible = false;
        CredentialsCheckBox.IsVisible = false;
        var notes = "Settings replace your current ones. Capture profiles and redaction sets replace any matching ones already here; others are kept. Documents aren't affected.";
        if (!backup.Manifest.IncludesCredentials)
            notes += " This backup has no passwords or API keys, so the ones you have now are kept.";
        NotesText.Text = notes;
        UpdateConfirm();
    }

    private static string Count(int count, string noun) => count == 1 ? $"{noun} (1)" : $"{noun}s ({count})";

    private static void Offer(CheckBox box, bool available)
    {
        box.IsEnabled = available;
        box.IsChecked = available;
    }

    private BackupParts SelectedParts()
    {
        var parts = BackupParts.None;
        if (SettingsCheckBox is { IsVisible: true, IsChecked: true }) parts |= BackupParts.Settings;
        if (ProfilesCheckBox is { IsVisible: true, IsEnabled: true, IsChecked: true }) parts |= BackupParts.CaptureProfiles;
        if (RedactionSetsCheckBox is { IsVisible: true, IsEnabled: true, IsChecked: true }) parts |= BackupParts.RedactionSets;
        if (AiCatalogCheckBox is { IsVisible: true, IsEnabled: true, IsChecked: true }) parts |= BackupParts.AiFieldCatalog;
        return parts;
    }

    private void UpdateConfirm()
    {
        var parts = SelectedParts();
        ConfirmButton.IsEnabled = parts != BackupParts.None;
        ScriptsWarning.IsVisible = _restoring && _backupHasScripts && parts.HasFlag(BackupParts.CaptureProfiles);
    }

    private void OnPartChanged(object? sender, RoutedEventArgs e)
    {
        if (ConfirmButton is not null)
            UpdateConfirm();
    }

    private void OnCredentialsChanged(object? sender, RoutedEventArgs e) =>
        CredentialsWarning.IsVisible = CredentialsCheckBox.IsChecked == true;

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        Result = new BackupChoice(SelectedParts(), !_restoring && CredentialsCheckBox.IsChecked == true);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
