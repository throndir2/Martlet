using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

public partial class SetupWindow : Window
{
    private readonly SetupService service;
    private AppSettings? draft;
    private string? revision;
    private bool rendering;
    private bool busy;
    private bool routeDirty;
    private SetupRole Role => RoleChoice.SelectedItem is SetupRole role ? role : SetupRole.Stt;

    public SetupWindow(SetupService service)
    {
        this.service = service;
        InitializeComponent();
        rendering = true;
        RoleChoice.ItemsSource = Enum.GetValues<SetupRole>();
        RoleChoice.SelectedIndex = 0;
        DisclosureText.Text = OpenAiSetup.Disclosure;
        rendering = false;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        KeyInput.Clear();
        busy = true;
        EditorPanel.IsEnabled = ReloadButton.IsEnabled = false;
        try
        {
            var loaded = await service.LoadAsync();
            revision = loaded.Revision;
            draft = loaded.Error is null ? SetupSettings.Begin(loaded.Settings) : null;
            ResultText.Text = loaded.Error?.Summary ?? (loaded.Settings?.SchemaVersion == 1
                ? "Version 1 loaded unchanged. Explicit Save migrates it and atomically snapshots the original; profile ID and legacy references are preserved."
                : "Checkpoint loaded. No secret lookup, device or network action was performed.");
            if (draft is not null)
            {
                rendering = true;
                FixtureChoice.IsChecked = draft.Profile.Kind is ProfileKind.Fixture or ProfileKind.NotConfigured;
                ApiChoice.IsChecked = draft.Profile.Kind == ProfileKind.Api;
                Steps.SelectedIndex = (int)draft.Setup!.Checkpoint;
                rendering = false;
                RenderRole();
            }
            RenderStatus();
        }
        finally
        {
            busy = false;
            EditorPanel.IsEnabled = draft is not null;
            ReloadButton.IsEnabled = true;
        }
    }

    private void RenderStatus()
    {
        SetupStatus.Text = draft is null ? "Setup blocked. Original settings preserved; see the remedy below." : SetupSettings.Describe(draft);
        CredentialScope.Text = $"Selected role: {Role}; internal alias: {OpenAiSetup.Alias(Role)}; origin: {OpenAiSetup.Origin}. Select another role on Destinations.";
        RemovalChoice.ItemsSource = draft?.Setup?.PendingRemovals;
        RemovalChoice.SelectedIndex = 0;
    }

    private void RenderRole()
    {
        rendering = true;
        var route = draft?.Setup?.Routes.SingleOrDefault(r => r.Role == Role);
        BoundaryText.Text = $"{OpenAiSetup.Boundary(Role)} Internal alias: {OpenAiSetup.Alias(Role)}. Destination: {OpenAiSetup.Origin}.";
        ModelId.Text = route?.ModelId ?? "";
        VoiceId.Text = route?.VoiceId ?? "";
        VoiceId.IsEnabled = Role == SetupRole.Tts;
        ConsentChoice.IsChecked = route?.Consent is not null;
        KeyInput.Clear();
        routeDirty = false;
        rendering = false;
        RenderStatus();
    }

    private void Choice_Changed(object sender, RoutedEventArgs e)
    {
        if (rendering || draft is null) return;
        draft = draft with { Profile = draft.Profile with { Kind = ApiChoice.IsChecked == true ? ProfileKind.Api : ProfileKind.Fixture } };
        RenderStatus();
    }

    private void Step_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || draft is null || e.Source != Steps) return;
        draft = draft with { Setup = draft.Setup! with { Checkpoint = (SetupStep)Steps.SelectedIndex } };
        RenderStatus();
    }

    private void Role_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!rendering && draft is not null)
        {
            if (routeDirty) ResultText.Text = "Unapplied route fields were discarded when changing role. Apply the selected role before leaving it.";
            RenderRole();
        }
    }

    private void Route_Changed(object sender, TextChangedEventArgs e)
    {
        if (rendering || draft is null) return;
        ConsentChoice.IsChecked = false;
        routeDirty = true;
        ResultText.Text = "Route fields changed. Apply the route and explicitly review its destination consent again before saving.";
    }

    private void ApplyRoute_Click(object sender, RoutedEventArgs e)
    {
        if (draft is null) return;
        try
        {
            var updated = SetupSettings.SelectRoute(draft, Role, ModelId.Text, Role == SetupRole.Tts ? VoiceId.Text : null);
            var route = updated.Setup!.Routes.Single(r => r.Role == Role);
            draft = SetupSettings.ReplaceRoute(updated, route with { Consent = ConsentChoice.IsChecked == true ? route.Selection() : null });
            routeDirty = false;
            ResultText.Text = "Route applied to the working checkpoint. Save to persist it. Model availability, credential validity, cost and quota remain unknown.";
            RenderStatus();
        }
        catch (ContractException ex) { ResultText.Text = ex.Message; }
    }

    private void Consent_Changed(object sender, RoutedEventArgs e)
    {
        if (!rendering && draft is not null) routeDirty = true;
    }

    private AppSettings Checkpoint()
    {
        if (draft is null || routeDirty)
            throw new ContractException(ErrorCode.InvalidContract, "Apply edited route fields before saving, or reload the saved checkpoint.");
        if (draft.Profile.Kind == ProfileKind.NotConfigured)
            draft = draft with { Profile = draft.Profile with { Kind = ProfileKind.Fixture } };
        draft.Validate();
        return draft;
    }

    private async Task<bool> PerformAsync(Func<Task<SetupSaveResult>> action)
    {
        if (busy) return false;
        busy = true;
        EditorPanel.IsEnabled = ReloadButton.IsEnabled = false;
        try
        {
            var result = await action();
            ResultText.Text = result.Summary;
            if (result.Save.Saved)
            {
                draft = result.Settings;
                revision = result.Save.Revision;
                RenderRole();
            }
            RenderStatus();
            return result.Save.Saved;
        }
        catch (ContractException ex) { ResultText.Text = ex.Message; return false; }
        finally { busy = false; EditorPanel.IsEnabled = draft is not null; ReloadButton.IsEnabled = true; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) =>
        await PerformAsync(() => service.SaveAsync(Checkpoint(), revision));

    private async void SaveExit_Click(object sender, RoutedEventArgs e)
    {
        if (await PerformAsync(() => service.SaveAsync(Checkpoint(), revision))) Close();
    }

    private bool Confirm(string text) => MessageBox.Show(this, text, "Explicit scoped action",
        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async void StoreKey_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm($"Store a new key for this profile's {Role} route at {OpenAiSetup.Origin} and save this checkpoint? This invalidates that role's consent. Any previous key is detached, not deleted; remove it explicitly below. No provider access will be tested.")) return;
        await PerformAsync(async () =>
        {
            var settings = Checkpoint();
            using var secure = KeyInput.SecurePassword;
            var chars = new char[secure.Length];
            var pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
            try
            {
                Marshal.Copy(pointer, chars, 0, chars.Length);
                using var secret = new SecretLease(chars);
                return await service.ReplaceCredentialAsync(settings, revision, Role, secret);
            }
            finally
            {
                KeyInput.Clear();
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
                Marshal.ZeroFreeGlobalAllocUnicode(pointer);
            }
        });
    }

    private void ReadKey_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm($"Read only this profile's selected {Role} key from Windows to check local presence? It will be immediately discarded, never revealed or sent to a provider.")) return;
        try { ResultText.Text = CredentialMessages.Describe(service.CheckCredential(Checkpoint(), Role)); }
        catch (ContractException ex) { ResultText.Text = ex.Message; }
    }

    private async void DetachKey_Click(object sender, RoutedEventArgs e)
    {
        if (Confirm($"Detach only this profile's {Role} credential and save? Consent is invalidated. The OS key remains listed for explicit removal; other roles and credentials are unchanged."))
            await PerformAsync(() => service.DetachCredentialAsync(Checkpoint(), revision, Role));
    }

    private async void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (RemovalChoice.SelectedItem is not PendingCredentialRemoval removal)
        {
            ResultText.Text = "No detached credential is selected. Detach a role first; unrelated Windows credentials cannot be listed or removed.";
            return;
        }
        if (Confirm($"Permanently remove detached {removal.Role} reference {removal.CredentialId} for this profile only? An old settings snapshot cannot restore this key."))
            await PerformAsync(() => service.RemoveDetachedAsync(Checkpoint(), revision, removal));
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (!busy && Confirm("Discard unsaved configuration edits and reload the saved checkpoint? No credential is read or deleted.")) await LoadAsync();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Steps.SelectedIndex = Math.Max(0, Steps.SelectedIndex - 1);
    private void Next_Click(object sender, RoutedEventArgs e) => Steps.SelectedIndex = Math.Min(3, Steps.SelectedIndex + 1);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; return; }
        KeyInput.Clear();
    }

    private void Link_Click(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.AbsoluteUri is not (OpenAiSetup.Pricing or OpenAiSetup.Retention)) return;
        if (Confirm("Open the official OpenAI page in your browser? This contacts the website, not the inference API."))
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Win32Exception) { ResultText.Text = "The browser could not open. Use the displayed official link manually; no provider call was made."; }
        }
        e.Handled = true;
    }
}
