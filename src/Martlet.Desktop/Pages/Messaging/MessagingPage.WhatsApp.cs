using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Messaging;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Messaging's WhatsApp card: the Meta access token and app secret, the optional IDs, the public address
/// (a Cloudflare quick tunnel by default) and Connect.</summary>
internal sealed partial class MessagingPage
{
    private Border WhatsAppCard(MessagingPageModel model, WhatsAppPreferences saved)
    {
        var connected = saved.Connected;
        var busy = services.WhatsAppBusy;
        var draft = services.WhatsAppDraft ?? ("", "", saved.PhoneNumberId, saved.BusinessAccountId, saved.PublicAddress);
        var token = new PasswordBox { MaxLength = 700, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Password = draft.Token };
        AutomationProperties.SetName(token, "WhatsApp access token");
        AutomationProperties.SetAutomationId(token, "MessagingWhatsAppToken");
        var secret = new PasswordBox { MaxLength = 64, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Password = draft.Secret };
        AutomationProperties.SetName(secret, "Meta app secret");
        AutomationProperties.SetAutomationId(secret, "MessagingWhatsAppSecret");
        TextBox Field(string value, string name, string id)
        {
            var box = new TextBox { Text = value, MaxLength = 200, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(box, name);
            AutomationProperties.SetAutomationId(box, id);
            return box;
        }
        var phone = Field(draft.PhoneId, "Phone number ID", "MessagingWhatsAppPhoneId");
        var account = Field(draft.AccountId, "WhatsApp Business account ID", "MessagingWhatsAppAccountId");
        var address = Field(draft.Address, "Public address", "MessagingWhatsAppAddress");
        // Once connected, the "saved" marks fill the empty secret boxes instead.
        if (!connected)
        {
            InputHint.SetText(token, "Paste your Meta access token (EA…)");
            InputHint.SetText(secret, "Paste the App secret from App settings › Basic");
        }
        InputHint.SetText(phone, "106540352242922");
        InputHint.SetText(account, "102290129340398");
        InputHint.SetText(address, "https://martlet.example.com");
        void Keep() => services.WhatsAppDraft = (token.Password, secret.Password, phone.Text, account.Text, address.Text);
        token.PasswordChanged += (_, _) => Keep();
        secret.PasswordChanged += (_, _) => Keep();
        phone.TextChanged += (_, _) => Keep();
        account.TextChanged += (_, _) => Keep();
        address.TextChanged += (_, _) => Keep();

        var connect = PageButton(busy ? "Working..." : connected ? "Save and reconnect" : "Connect",
            () => services.ConnectWhatsApp(token, secret, phone.Text, account.Text, address.Text), primary: !connected, id: "MessagingWhatsAppConnect");
        connect.IsEnabled = !busy;
        var disconnect = connected ? PageButton("Disconnect", () => services.Disconnect(MessagingApp.WhatsApp), id: "MessagingWhatsAppDisconnect") : null;

        var children = new List<UIElement>
        {
            Heading("WhatsApp"),
            Status(model.StatusLine(MessagingApp.WhatsApp), "MessagingWhatsAppStatus", 15)
        };
        if (services.WhatsAppNote is { } note) children.Add(Status(note, "MessagingWhatsAppNote"));
        if (!connected)
        {
            children.Add(HelpTip.Explain(MessagingPageModel.WhatsAppSteps, new Thickness(0, 8, 0, 0), "WhatsAppSetup", "setting up WhatsApp",
                wiki: "Telegram#set-up-whatsapp"));
            children.Add(Row(
                PageButton("Create a Meta app", () => services.OpenInBrowser(MetaNewApp), link: true, id: "MessagingWhatsAppOpenMeta"),
                PageButton("Your Meta apps", () => services.OpenInBrowser(MetaApps), link: true, id: "MessagingWhatsAppOpenApps"),
                PageButton("System users", () => services.OpenInBrowser(MetaSystemUsers), link: true, id: "MessagingWhatsAppOpenSystemUsers"),
                PageButton("Meta's WhatsApp guide", () => services.OpenInBrowser(WhatsAppGuide), link: true, id: "MessagingWhatsAppOpenGuide")));
        }
        children.Add(new Label { Content = "_Access token", Target = token, Padding = new Thickness(0, 10, 0, 4) });
        children.Add(SecretField(token, connected, "Token saved (type to replace)"));
        children.Add(new Label { Content = "App _secret", Target = secret, Padding = new Thickness(0, 10, 0, 4) });
        children.Add(SecretField(secret, connected, "Secret saved (type to replace)"));
        children.Add(new Label { Content = "_Phone number ID (optional)", Target = phone, Padding = new Thickness(0, 10, 0, 4) });
        children.Add(phone);
        children.Add(new Label { Content = "WhatsApp _Business account ID (optional)", Target = account, Padding = new Thickness(0, 10, 0, 4) });
        children.Add(account);
        children.Add(Note("Leave the IDs empty and Martlet finds them from the token (the first number it reaches).", new Thickness(0, 4, 0, 0)));

        var cloudflared = services.Cloudflared;
        children.Add(new Label { Content = "Public a_ddress (optional)", Target = address, Padding = new Thickness(0, 10, 0, 4) });
        children.Add(address);
        children.Add(Status(MessagingPageModel.WhatsAppAddressText(saved, cloudflared, services.OwnCloudflared), "MessagingWhatsAppTunnel"));
        children.Add(Row(
            cloudflared is null ? PageButton(busy ? "Downloading..." : "Get cloudflared", services.GetCloudflared, id: "MessagingWhatsAppGetCloudflared") : null,
            PageButton("About quick tunnels", () => services.OpenInBrowser(CloudflareQuickTunnel.About), link: true, id: "MessagingWhatsAppOpenTunnelHelp")));
        children.Add(HelpTip.Explain("Martlet checks the token and secret with Meta, keeps them in Windows Credential Manager on this PC and sends them only " +
            "to Meta. Messages Meta delivers are checked against the app secret, so nobody else can post to Martlet's address.", new Thickness(0, 4, 0, 0),
            "WhatsAppSecrets", "the WhatsApp secrets", wiki: "Telegram#set-up-whatsapp"));
        children.Add(Row(connect, disconnect));
        children.Add(SpeakRepliesCheck(MessagingApp.WhatsApp, saved, "MessagingWhatsApp"));
        return Card([.. children]);
    }
}
