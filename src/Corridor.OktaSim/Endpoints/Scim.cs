using System.Text.Json;
using Corridor.OktaSim.Models;
using Corridor.OktaSim.Stores;

namespace Corridor.OktaSim.Endpoints;

/// <summary>
/// SCIM 2.0 provisioning surface on /scim/v2/Users with application/scim+json
/// bodies and RFC 7644 error shapes. Demo bearer token is the contract constant
/// corridor-scim-token (documented demo-only; swap for a real grant in production).
/// Supports: list (filter userName eq), create, get, put (replace), patch
/// (replace ops on active and groups only).
/// </summary>
public static class ScimEndpoints
{
    public const string BearerToken = "corridor-scim-token";
    public const string ContentType = "application/scim+json";

    private const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    private const string ErrorSchema = "urn:ietf:params:scim:api:messages:2.0:Error";
    private const string RoleExtension = "urn:corridor:scim:1.0:User";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapScimEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/scim/v2/Users");
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPut("/{id}", PutAsync);
        group.MapPatch("/{id}", PatchAsync);
        return app;
    }

    private static bool Authorized(HttpRequest request) =>
        request.Headers.Authorization.ToString().Equals($"Bearer {BearerToken}", StringComparison.Ordinal);

    private static IResult Unauthorized() =>
        ScimJson(new Dictionary<string, object>
        {
            ["schemas"] = new[] { ErrorSchema },
            ["status"] = "401",
            ["detail"] = "Provide Authorization: Bearer corridor-scim-token (demo constant, documented demo-only).",
        }, 401);

    private static IResult ScimJson(object body, int status = 200) =>
        Results.Json(body, JsonOptions, contentType: ContentType, statusCode: status);

    private static IResult ScimError(int status, string detail) =>
        ScimJson(new Dictionary<string, object>
        {
            ["schemas"] = new[] { ErrorSchema },
            ["status"] = status.ToString(),
            ["detail"] = detail,
        }, status);

    private static async Task<IResult> ListAsync(HttpRequest request, IUserStore users)
    {
        if (!Authorized(request))
        {
            return Unauthorized();
        }

        var all = await users.ListAsync();
        var filter = request.Query["filter"].ToString().Trim();
        if (filter.Length > 0)
        {
            // Only "userName eq \"value\"" is supported; anything else is a 400 per RFC 7644.
            var match = System.Text.RegularExpressions.Regex.Match(
                filter,
                "^userName\\s+eq\\s+\"(?<value>[^\"]*)\"$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return ScimError(400, "Only the filter form 'userName eq \"value\"' is supported.");
            }
            var wanted = match.Groups["value"].Value;
            all = all.Where(u => string.Equals(u.UserName, wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        var startIndex = ParsePositiveInt(request.Query["startIndex"].ToString(), 1);
        var count = Math.Clamp(ParsePositiveInt(request.Query["count"].ToString(), 100), 1, 200);
        var page = all.Skip(startIndex - 1).Take(count).ToArray();

        return ScimJson(new Dictionary<string, object>
        {
            ["schemas"] = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
            ["totalResults"] = all.Count,
            ["startIndex"] = startIndex,
            ["itemsPerPage"] = page.Length,
            ["Resources"] = page.Select(ToResource).ToArray(),
        });
    }

    private static int ParsePositiveInt(string value, int fallback)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static async Task<IResult> CreateAsync(HttpRequest request, IUserStore users, ILoggerFactory loggerFactory)
    {
        if (!Authorized(request))
        {
            return Unauthorized();
        }
        JsonDocument? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonDocument>(request.Body);
        }
        catch (JsonException)
        {
            return ScimError(400, "Request body is not valid SCIM JSON.");
        }
        if (body is null)
        {
            return ScimError(400, "Request body is required.");
        }

        var userName = GetString(body, "userName");
        if (string.IsNullOrWhiteSpace(userName) || !userName.Contains('@'))
        {
            return ScimError(400, "userName is required and must be an email-style upn.");
        }
        if (await users.FindByUserNameAsync(userName) is not null)
        {
            return ScimError(400, $"userName {userName} is already provisioned; userName must be unique.");
        }

        var role = GetRole(body) ?? DirectoryRoles.User;
        var created = await users.CreateAsync(new DirectoryUser(
            Id: string.Empty,
            UserName: userName,
            DisplayName: GetString(body, "displayName") is { Length: > 0 } displayName ? displayName : userName,
            Role: role,
            Active: GetBool(body, "active") ?? true,
            Groups: GetGroups(body),
            PasswordHash: DirectoryUser.HashDemoPassword(InMemoryUserStore.DemoPassword)));
        if (created is null)
        {
            return ScimError(400, $"userName {userName} is already provisioned; userName must be unique.");
        }

        loggerFactory.CreateLogger("Scim.Create").LogInformation(
            "SCIM user created: {Upn}, role {Role}, id {Id}", created.UserName, created.Role, created.Id);
        request.HttpContext.Response.Headers.Location = $"/scim/v2/Users/{created.Id}";
        return ScimJson(ToResource(created), 201);
    }

    private static async Task<IResult> GetAsync(HttpRequest request, string id, IUserStore users)
    {
        if (!Authorized(request))
        {
            return Unauthorized();
        }
        var user = await users.FindByIdAsync(id);
        return user is null
            ? ScimError(404, $"User {id} not found.")
            : ScimJson(ToResource(user));
    }

    private static async Task<IResult> PutAsync(HttpRequest request, string id, IUserStore users, ILoggerFactory loggerFactory)
    {
        if (!Authorized(request))
        {
            return Unauthorized();
        }
        JsonDocument? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonDocument>(request.Body);
        }
        catch (JsonException)
        {
            return ScimError(400, "Request body is not valid SCIM JSON.");
        }
        if (body is null)
        {
            return ScimError(400, "Request body is required.");
        }

        var existing = await users.FindByIdAsync(id);
        if (existing is null)
        {
            return ScimError(404, $"User {id} not found.");
        }
        var userName = GetString(body, "userName");
        if (string.IsNullOrWhiteSpace(userName) || !userName.Contains('@'))
        {
            return ScimError(400, "userName is required and must be an email-style upn.");
        }

        var replaced = await users.ReplaceAsync(existing with
        {
            UserName = userName,
            DisplayName = GetString(body, "displayName") is { Length: > 0 } displayName ? displayName : userName,
            Role = GetRole(body) ?? existing.Role,
            Active = GetBool(body, "active") ?? existing.Active,
            Groups = GetGroups(body),
        });
        if (replaced is null)
        {
            return ScimError(400, $"userName {userName} is already provisioned; userName must be unique.");
        }

        loggerFactory.CreateLogger("Scim.Put").LogInformation(
            "SCIM user replaced: {Upn}, active {Active}", replaced.UserName, replaced.Active);
        return ScimJson(ToResource(replaced));
    }

    private static async Task<IResult> PatchAsync(HttpRequest request, string id, IUserStore users, ILoggerFactory loggerFactory)
    {
        if (!Authorized(request))
        {
            return Unauthorized();
        }
        JsonDocument? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonDocument>(request.Body);
        }
        catch (JsonException)
        {
            return ScimError(400, "Request body is not valid SCIM JSON.");
        }
        if (body is null)
        {
            return ScimError(400, "Request body is required.");
        }

        var existing = await users.FindByIdAsync(id);
        if (existing is null)
        {
            return ScimError(404, $"User {id} not found.");
        }

        bool? active = existing.Active;
        var groups = existing.Groups.ToList();

        if (!body.RootElement.TryGetProperty("Operations", out var operations)
            || operations.ValueKind != JsonValueKind.Array)
        {
            return ScimError(400, "Operations array is required.");
        }

        foreach (var op in operations.EnumerateArray())
        {
            if (op.ValueKind != JsonValueKind.Object
                || !op.TryGetProperty("op", out var opName)
                || !string.Equals(opName.GetString(), "replace", StringComparison.OrdinalIgnoreCase))
            {
                return ScimError(400, "Only 'replace' operations are supported by this directory.");
            }
            var path = op.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;

            if (string.Equals(path, "active", StringComparison.OrdinalIgnoreCase))
            {
                if (!op.TryGetProperty("value", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return ScimError(400, "replace active requires a boolean value.");
                }
                active = value.GetBoolean();
            }
            else if (string.Equals(path, "groups", StringComparison.OrdinalIgnoreCase))
            {
                if (!op.TryGetProperty("value", out var value)
                    || (value.ValueKind is not JsonValueKind.Array && value.ValueKind is not JsonValueKind.Object))
                {
                    return ScimError(400, "replace groups requires an array of {\"display\": name} objects.");
                }
                groups = ExtractGroupNames(value).ToList();
            }
            else if (path is not null
                && path.StartsWith("groups[display eq", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("]", StringComparison.Ordinal))
            {
                // Single-group replace: groups[display eq "name"] with value true/false
                // (set or clear that membership) or a display name string.
                var filterMatch = System.Text.RegularExpressions.Regex.Match(
                    path, "groups\\[display eq \"(?<name>[^\"]+)\"\\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!filterMatch.Success)
                {
                    return ScimError(400, "Unsupported groups filter path; expected groups[display eq \"name\"].");
                }
                var groupName = filterMatch.Groups["name"].Value;
                bool include;
                if (op.TryGetProperty("value", out var value) && value.ValueKind is JsonValueKind.True)
                {
                    include = true;
                }
                else if (value.ValueKind is JsonValueKind.False)
                {
                    include = false;
                }
                else if (value.ValueKind is JsonValueKind.String)
                {
                    include = string.Equals(value.GetString(), groupName, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    return ScimError(400, "Unsupported value for a groups[display eq ...] replace.");
                }
                groups.RemoveAll(g => string.Equals(g, groupName, StringComparison.OrdinalIgnoreCase));
                if (include)
                {
                    groups.Add(groupName);
                }
            }
            else
            {
                return ScimError(400, $"Unsupported patch path '{path}': only replace on active and groups is supported.");
            }
        }

        var updated = await users.ReplaceAsync(existing with { Active = active ?? existing.Active, Groups = groups });
        if (updated is null)
        {
            return ScimError(400, "Patch could not be applied to the directory.");
        }
        loggerFactory.CreateLogger("Scim.Patch").LogInformation(
            "SCIM user patched: {Upn}, active {Active}, groups {Groups}",
            updated.UserName, updated.Active, string.Join(",", updated.Groups));
        return ScimJson(ToResource(updated));
    }

    private static Dictionary<string, object> ToResource(DirectoryUser user) => new()
    {
        ["schemas"] = new[] { UserSchema, RoleExtension },
        ["id"] = user.Id,
        ["userName"] = user.UserName,
        ["displayName"] = user.DisplayName,
        ["active"] = user.Active,
        ["name"] = new Dictionary<string, object> { ["formatted"] = user.DisplayName },
        ["emails"] = new[] { new Dictionary<string, object> { ["value"] = user.Email, ["primary"] = true } },
        ["groups"] = user.Groups.Select(g => new Dictionary<string, object> { ["display"] = g }).ToArray(),
        [RoleExtension] = new Dictionary<string, object> { ["role"] = user.Role },
        ["meta"] = new Dictionary<string, object>
        {
            ["resourceType"] = "User",
            ["location"] = $"/scim/v2/Users/{user.Id}",
        },
    };

    private static string? GetString(JsonDocument doc, string property)
    {
        return doc.RootElement.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? GetBool(JsonDocument doc, string property)
    {
        return doc.RootElement.TryGetProperty(property, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static string? GetRole(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty(RoleExtension, out var extension)
            && extension.ValueKind == JsonValueKind.Object
            && extension.TryGetProperty("role", out var role)
            && role.ValueKind == JsonValueKind.String)
        {
            return role.GetString();
        }
        return null;
    }

    private static List<string> GetGroups(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            return ExtractGroupNames(groups).ToList();
        }
        return [];
    }

    private static IEnumerable<string> ExtractGroupNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            element = JsonSerializer.SerializeToElement(new[] { element });
        }
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("display", out var display)
                && display.ValueKind == JsonValueKind.String)
            {
                var name = display.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    yield return name;
                }
            }
        }
    }
}
