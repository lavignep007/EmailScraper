using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailScraper.EmailProviders.Microsoft365;

internal sealed class MicrosoftIdentityClient
{
    private static readonly string[] Scopes = ["offline_access", "Mail.Read", "User.Read"];
    private readonly HttpClient httpClient;
    private readonly EmailSourceConfig source;
    private readonly string tokenPath;
    private TokenCache? cache;

    public MicrosoftIdentityClient(HttpClient httpClient, EmailSourceConfig source)
    {
        this.httpClient = httpClient;
        this.source = source;
        tokenPath = string.IsNullOrWhiteSpace(source.TokenPath)
            ? Path.Combine(AppContext.BaseDirectory, "token", source.Id, "microsoft365.json")
            : ResolveRuntimePath(source.TokenPath);
    }

    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false)
    {
        cache ??= await LoadAsync();

        if (!forceRefresh && cache is not null &&
            cache.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(5) &&
            !string.IsNullOrWhiteSpace(cache.AccessToken))
            return cache.AccessToken;

        if (!string.IsNullOrWhiteSpace(cache?.RefreshToken))
        {
            var refreshed = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["client_id"] = source.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = cache.RefreshToken,
                ["scope"] = string.Join(' ', Scopes)
            });

            if (refreshed is not null)
            {
                await SaveAsync(refreshed);
                return refreshed.AccessToken;
            }
        }

        return await AcquireByDeviceCodeAsync();
    }

    private async Task<string> AcquireByDeviceCodeAsync()
    {
        var authority = BuildAuthority();
        using var response = await httpClient.PostAsync(
            $"{authority}/oauth2/v2.0/devicecode",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = source.ClientId,
                ["scope"] = string.Join(' ', Scopes)
            }));
        response.EnsureSuccessStatusCode();
        var device = await response.Content.ReadFromJsonAsync<DeviceCodeResponse>()
            ?? throw new InvalidOperationException("Microsoft returned an empty device-code response.");

        Console.WriteLine();
        Console.WriteLine(device.Message ??
            $"Open {device.VerificationUri} and enter code {device.UserCode}.");
        Console.WriteLine();

        var interval = Math.Max(device.Interval, 5);
        var expires = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);

        while (DateTimeOffset.UtcNow < expires)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval));
            using var tokenResponse = await httpClient.PostAsync(
                $"{authority}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = source.ClientId,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = device.DeviceCode
                }));
            var json = await tokenResponse.Content.ReadAsStringAsync();

            if (tokenResponse.IsSuccessStatusCode)
            {
                var token = JsonSerializer.Deserialize<TokenResponse>(json)
                    ?? throw new InvalidOperationException("Microsoft returned an empty token response.");
                await SaveAsync(token);
                return token.AccessToken;
            }

            var error = JsonSerializer.Deserialize<OAuthError>(json);
            if (error?.Error == "authorization_pending") continue;
            if (error?.Error == "slow_down")
            {
                interval += 5;
                continue;
            }

            throw new InvalidOperationException(
                $"Microsoft authentication failed: {error?.ErrorDescription ?? json}");
        }

        throw new TimeoutException("Microsoft device authorization expired before sign-in completed.");
    }

    private async Task<TokenResponse?> RequestTokenAsync(Dictionary<string, string> form)
    {
        using var response = await httpClient.PostAsync(
            $"{BuildAuthority()}/oauth2/v2.0/token",
            new FormUrlEncodedContent(form));
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<TokenResponse>();
    }

    private async Task<TokenCache?> LoadAsync()
    {
        if (!File.Exists(tokenPath)) return null;

        try
        {
            return JsonSerializer.Deserialize<TokenCache>(await File.ReadAllTextAsync(tokenPath));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SaveAsync(TokenResponse token)
    {
        cache = new TokenCache
        {
            AccessToken = token.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken)
                ? cache?.RefreshToken ?? ""
                : token.RefreshToken,
            ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn)
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(tokenPath))!);
        await File.WriteAllTextAsync(
            tokenPath,
            JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
    }

    private string BuildAuthority() =>
        $"https://login.microsoftonline.com/{Uri.EscapeDataString(source.TenantId) }";

    private static string ResolveRuntimePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    private sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")]
        public string DeviceCode { get; set; } = "";

        [JsonPropertyName("user_code")]
        public string UserCode { get; set; } = "";

        [JsonPropertyName("verification_uri")]
        public string VerificationUri { get; set; } = "";

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = "";

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private sealed class OAuthError
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }

    private sealed class TokenCache
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTimeOffset ExpiresUtc { get; set; }
    }
}
