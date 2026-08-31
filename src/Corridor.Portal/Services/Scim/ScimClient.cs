using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Corridor.Portal.Services.Scim;

/// <summary>
/// Real provisioning client against okta-sim's /scim/v2/Users endpoint: create
/// (capturing the returned id), put (userName, displayName, active, plus the role
/// through the urn:corridor:scim:1.0:User extension the sim supports), and the
/// deactivate patch. Runs on the named "scim" HttpClient (5 second cap, bearer
/// from Portal:ScimToken) wired in Program.cs; every failure mode surfaces as a
/// ScimProvisioningException so the dashboard can show it inline.
/// </summary>
public sealed class ScimClient : IScimProvisioner
{
    public const string ClientName = "scim";

    private const string CoreSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    private const string RoleExtension = "urn:corridor:scim:1.0:User";
    private const string ScimContentType = "application/scim+json";

    private readonly HttpClient _http;

    public ScimClient(HttpClient http, string bearerToken)
    {
        _http = http;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public async Task<string> CreateAsync(ScimUser user, CancellationToken ct = default)
    {
        var response = await SendAsync(
            token => _http.PostAsync("/scim/v2/Users", Body(ReplaceBody(user, active: true)), token),
            $"create {user.UserName}",
            ct);
        await EnsureSuccessAsync(response, $"create {user.UserName}", ct);
        using var document = await ReadDocumentAsync(response, ct);
        if (document.RootElement.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } externalId)
        {
            return externalId;
        }
        throw new ScimProvisioningException($"SCIM create for {user.UserName} returned no user id.");
    }

    public async Task ReplaceAsync(string externalId, ScimUser user, CancellationToken ct = default)
    {
        var response = await SendAsync(
            token => _http.PutAsync($"/scim/v2/Users/{externalId}", Body(ReplaceBody(user, active: true)), token),
            $"update {user.UserName}",
            ct);
        await EnsureSuccessAsync(response, $"update {user.UserName}", ct);
    }

    public async Task DeactivateAsync(string externalId, CancellationToken ct = default)
    {
        var response = await SendAsync(
            token => _http.PatchAsync(
                $"/scim/v2/Users/{externalId}",
                Body(new Dictionary<string, object>
                {
                    ["schemas"] = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
                    ["Operations"] = new[]
                    {
                        new Dictionary<string, object> { ["op"] = "replace", ["path"] = "active", ["value"] = false }
                    }
                }),
                token),
            $"deactivate {externalId}",
            ct);
        await EnsureSuccessAsync(response, $"deactivate {externalId}", ct);
    }

    private static Dictionary<string, object> ReplaceBody(ScimUser user, bool active) => new()
    {
        ["schemas"] = new[] { CoreSchema, RoleExtension },
        ["userName"] = user.UserName,
        ["displayName"] = user.DisplayName,
        ["active"] = active,
        [RoleExtension] = new Dictionary<string, object> { ["role"] = user.Role }
    };

    private static StringContent Body(Dictionary<string, object> payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, ScimContentType);

    /// <summary>Wraps transport failures (unreachable, timeout) into ScimProvisioningException.</summary>
    private static async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send, string operation, CancellationToken ct)
    {
        try
        {
            return await send(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ScimProvisioningException(
                $"SCIM {operation} could not reach the provisioning endpoint: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The client's timeout cancels the call without the caller's token being pulled.
            throw new ScimProvisioningException($"SCIM {operation} timed out.", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var detail = await response.Content.ReadAsStringAsync(ct);
        if (detail.Length > 300)
        {
            detail = detail[..300];
        }
        throw new ScimProvisioningException(
            $"SCIM {operation} failed with {(int)response.StatusCode} {response.StatusCode}: {detail}");
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new ScimProvisioningException($"SCIM response was not valid JSON: {ex.Message}", ex);
        }
    }
}
