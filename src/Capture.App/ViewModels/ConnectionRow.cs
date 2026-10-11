using Capture.Core.Watch;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public sealed record ConnectionAuthChoice(ConnectionAuthMethod Value, string Label);

/// <summary>One Settings ▸ Connections entry, edited as a copy in <see cref="ConnectionEditorViewModel"/>
/// and written back to <see cref="WatchSettings.Connections"/> when Settings is saved.</summary>
public sealed partial class ConnectionRow : ObservableObject
{
    // Whether BaseUrl still holds the address built from the tenant name, so typing a tenant keeps
    // updating it until someone types their own (e.g. an on-premise server).
    private bool _baseUrlIsAutoFilled;
    private bool _settingBaseUrl;

    public ConnectionRow(ConnectionDefinition connection)
    {
        Id = connection.Id;
        Type = connection.Type;
        _name = connection.Name;
        _baseUrl = connection.BaseUrl ?? string.Empty;
        _tenantName = connection.TenantName ?? string.Empty;
        _username = connection.Username ?? string.Empty;
        _password = connection.Password ?? string.Empty;
        _token = connection.Token ?? string.Empty;
        _apiKeyHeader = string.IsNullOrWhiteSpace(connection.ApiKeyHeader) ? "X-API-Key" : connection.ApiKeyHeader;
        AuthChoices = ConnectionDefinition.AuthMethodsFor(Type).Select(method => new ConnectionAuthChoice(method, Label(method))).ToList();
        _selectedAuth = AuthChoices.FirstOrDefault(choice => choice.Value == connection.AuthMethod) ?? AuthChoices[0];
        _baseUrlIsAutoFilled = string.IsNullOrWhiteSpace(_baseUrl);
    }

    public Guid Id { get; }

    public ConnectionType Type { get; }

    public bool IsTherefore => Type == ConnectionType.Therefore;

    public bool IsRest => Type == ConnectionType.Rest;

    public string TypeLabel => IsTherefore ? "Therefore" : "REST";

    public IReadOnlyList<ConnectionAuthChoice> AuthChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScriptUsage))]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name.Trim();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _baseUrl;

    partial void OnBaseUrlChanged(string value)
    {
        if (!_settingBaseUrl)
            _baseUrlIsAutoFilled = string.IsNullOrWhiteSpace(value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _tenantName;

    partial void OnTenantNameChanged(string value)
    {
        if (!IsTherefore || !_baseUrlIsAutoFilled || string.IsNullOrWhiteSpace(value))
            return;

        _settingBaseUrl = true;
        BaseUrl = $"https://{value.Trim()}.thereforeonline.com";
        _settingBaseUrl = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesUsername))]
    [NotifyPropertyChangedFor(nameof(UsesToken))]
    [NotifyPropertyChangedFor(nameof(UsesApiKeyHeader))]
    [NotifyPropertyChangedFor(nameof(TokenLabel))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private ConnectionAuthChoice _selectedAuth;

    public bool UsesUsername => SelectedAuth.Value == ConnectionAuthMethod.Basic;

    public bool UsesToken => SelectedAuth.Value is ConnectionAuthMethod.Bearer or ConnectionAuthMethod.ApiKey;

    public bool UsesApiKeyHeader => SelectedAuth.Value == ConnectionAuthMethod.ApiKey;

    public string TokenLabel => SelectedAuth.Value == ConnectionAuthMethod.ApiKey ? "API key" : "Bearer token";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _username;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _password;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _token;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _apiKeyHeader;

    public string ScriptUsage => $"Connections[\"{DisplayName}\"]";

    /// <summary>The list line under the name: its address, or what's still missing.</summary>
    public string Summary
    {
        get
        {
            var model = ToModel();
            if (model.ServiceUrl is null)
                return IsTherefore ? "Not set up — no tenant or server address" : "Not set up — no address";
            var address = IsTherefore
                ? string.IsNullOrWhiteSpace(BaseUrl) ? $"https://{TenantName.Trim()}.thereforeonline.com" : BaseUrl.Trim()
                : BaseUrl.Trim();
            return model.IsConfigured ? address : $"{address} — sign-in details missing";
        }
    }

    public ConnectionDefinition ToModel() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        Type = Type,
        BaseUrl = Blank(BaseUrl),
        TenantName = IsTherefore ? Blank(TenantName) : null,
        AuthMethod = SelectedAuth.Value,
        // Only what the chosen sign-in uses, so a switched-away password or token isn't kept around.
        Username = UsesUsername ? Blank(Username) : null,
        Password = UsesUsername && Password.Length > 0 ? Password : null,
        Token = UsesToken ? Blank(Token) : null,
        ApiKeyHeader = UsesApiKeyHeader ? Blank(ApiKeyHeader) : null
    };

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Label(ConnectionAuthMethod method) => method switch
    {
        ConnectionAuthMethod.None => "None",
        ConnectionAuthMethod.Basic => "Username and password",
        ConnectionAuthMethod.Bearer => "Bearer token",
        ConnectionAuthMethod.ApiKey => "API key header",
        _ => method.ToString()
    };
}

/// <summary>The add/edit connection dialog: works on a copy, so Cancel leaves the list untouched.</summary>
public sealed partial class ConnectionEditorViewModel : ObservableObject
{
    private readonly HttpClient _http;
    private readonly IReadOnlyCollection<string> _otherNames;

    public ConnectionEditorViewModel(ConnectionRow row, bool isNew, IReadOnlyCollection<string> otherNames, HttpClient http)
    {
        Row = row;
        IsNew = isNew;
        _otherNames = otherNames;
        _http = http;
    }

    public ConnectionRow Row { get; }

    public bool IsNew { get; }

    public string Title => $"{(IsNew ? "Add" : "Edit")} {Row.TypeLabel} connection";

    public bool Accepted { get; private set; }

    public Action? Close { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _statusIsError;

    public bool HasStatus => StatusText.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private bool _isTesting;

    private bool CanTest() => !IsTesting;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        IsTesting = true;
        StatusIsError = false;
        StatusText = "Testing…";
        try
        {
            var (ok, message) = await ConnectionRequests.TestAsync(_http, Row.ToModel()).ConfigureAwait(true);
            StatusText = message;
            StatusIsError = !ok;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void Done()
    {
        var name = Row.Name.Trim();
        var error = name.Length == 0
            ? "Give the connection a name — scripts use it to find the connection."
            : _otherNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? $"There's already a connection called \"{name}\"."
                : null;
        if (error is not null)
        {
            StatusText = error;
            StatusIsError = true;
            return;
        }

        Accepted = true;
        Close?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => Close?.Invoke();
}
