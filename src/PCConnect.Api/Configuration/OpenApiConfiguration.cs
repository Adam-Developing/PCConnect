using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace PCConnect.Api.Configuration;

/// <summary>
/// The document metadata and security scheme, so the generated contract is a
/// usable artefact rather than a bare path list.
///
/// Everything below is generated from the code that serves the requests — the
/// document is output, not prose. `api/api_spec.md` documented endpoints that no
/// implementation provided, and that is how three incompatible API surfaces came
/// to exist for one product (04 §1).
/// </summary>
public static class OpenApiConfiguration
{
    public static IServiceCollection AddPcConnectOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "PCConnect API",
                    Version = "2.0.0",
                    Description = Description,
                    License = new OpenApiLicense
                    {
                        Name = "GPL-3.0-or-later",
                        Url = new Uri("https://www.gnu.org/licenses/gpl-3.0.en.html"),
                    },
                };

                // The document describes the shape of the API, not where one
                // instance happens to be running: a client resolves its backend
                // at runtime from configuration and discovery (06 §1, S3-08).
                document.Servers?.Clear();

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
                document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description =
                        "An ES256-signed access token, 15 minutes, carrying scope claims. " +
                        "There is no cookie authentication on this API, which makes CSRF " +
                        "structurally impossible on it.",
                };

                document.Security ??= [];
                document.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("bearer", document)] = [],
                });

                document.Tags = new HashSet<OpenApiTag>(
                [
                    new OpenApiTag { Name = "Auth", Description = "Passwords, tokens, passkeys and step-up confirmation." },
                    new OpenApiTag { Name = "Passkeys", Description = "WebAuthn registration and assertion." },
                    new OpenApiTag { Name = "Step-up", Description = "Confirming a destructive command." },
                    new OpenApiTag { Name = "Devices", Description = "Pairing, presence and the device registry." },
                    new OpenApiTag { Name = "Commands", Description = "Issue, deliver, acknowledge, expire." },
                    new OpenApiTag { Name = "Reminders", Description = "Encrypted reminders and recurrence." },
                    new OpenApiTag { Name = "Account", Description = "Profile, sessions, export and deletion." },
                    new OpenApiTag { Name = "Meta", Description = "Discovery, health and server time." },
                ]);

                return Task.CompletedTask;
            });
        });

    private const string Description = """
        Remote PC control and reminders.

        This document is the **contract**, and it is generated from the request and response
        types the handlers use. A handler that changes shape changes this document, and CI
        fails when the committed copy no longer matches (C-5).

        ## Authentication

        Bearer tokens only. Two token flavours share one format:

        | Token | `did` claim | Scopes |
        |---|---|---|
        | User access token | absent | `reminder:*`, `device:read`, `device:manage`, `command:issue`, `account:manage` |
        | Device access token | the device's id | `command:receive`, `command:ack` |

        `command:issue` and `command:receive` are never held by the same token: a stolen
        phone token can ask for a shutdown and can never receive or execute one.

        ## Destructive commands

        `shutdown`, `restart`, `signout` and `hibernate` additionally require a `stepUpToken`
        from `POST /v2/auth/step-up/verify`. It is single-use, expires in five minutes, and
        is bound to the account. A valid session is enough to lock a screen; it is not enough
        to power a machine off.

        ## Idempotency

        Command issue is idempotent by construction: the client generates the command `id`
        before sending, so a retry — or an offline queue replaying on reconnect — returns the
        existing command rather than issuing a second one. `POST` responds 201 when it
        created the command and 200 when it did not.

        ## Time

        Every instant on the wire is RFC 3339 UTC. Local rendering is a client concern,
        driven by the IANA timezone on the user record.

        ## Errors

        One envelope, always:

        ```json
        { "error": { "code": "device.not_paired", "message": "...", "requestId": "01JZ8K…" } }
        ```

        Clients switch on `code`, never on `message`. 404 is returned for both "does not
        exist" and "not visible to you", deliberately, so the API is not an existence oracle.

        ## What is not here

        The `/api/*` legacy compatibility surface is excluded on purpose. It reproduces the
        v1 wire format for clients that are already installed, it carries `Deprecation` and
        `Sunset` headers, and it is not something a new client should be generated from.
        """;
}
