using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;
using NetRatel.API.Models;
using NetRatel.API.Services;
using NetRatel.Application.Agents;
using System.Text.Json.Nodes;

namespace NetRatel.API.Endpoints;

public static class ClientArtifactsEndpoints
{
    public static IEndpointRouteBuilder MapClientArtifactsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/client-artifacts")
            .WithTags("Client Artifacts");

        group.MapGet(string.Empty, async (
            [FromQuery] string? rid,
            [FromQuery] int? skip,
            [FromQuery] int? take,
            [FromQuery] string? search,
            [FromServices] IClientArtifactsService service,
            CancellationToken ct) =>
        {
            var result = await service.ListAsync(rid, skip ?? 0, take ?? 50, search, ct);
            return Results.Ok(result);
        })
        .WithName("ClientArtifacts_List")
        .Produces<ClientArtifactListDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithOpenApi(op =>
        {
            op ??= new OpenApiOperation();
            op.Summary = "List stored client artifacts.";
            op.Parameters ??= [];
            op.Parameters.Add(new OpenApiParameter
            {
                Name = "rid",
                In = ParameterLocation.Query,
                Required = false,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            });
            op.Parameters.Add(new OpenApiParameter
            {
                Name = "skip",
                In = ParameterLocation.Query,
                Required = false,
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Default = JsonValue.Create(0) }
            });
            op.Parameters.Add(new OpenApiParameter
            {
                Name = "take",
                In = ParameterLocation.Query,
                Required = false,
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Default = JsonValue.Create(50) }
            });
            return op;
        })
        .RequireAuthorization("ClientArtifactsDownload");

        group.MapGet("{rid}/latest", async (
            string rid,
            [FromServices] IClientArtifactsService service,
            CancellationToken ct) =>
        {
            var latest = await service.GetLatestAsync(rid, ct);
            return latest is null ? Results.NotFound() : Results.Ok(latest);
        })
        .RequireAuthorization("ClientArtifactsDownload")
        .WithName("ClientArtifacts_Latest")
        .Produces<ClientArtifactSummaryDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("{rid}/{version}/download", async (
            string rid,
            string version,
            [FromQuery] bool? fallback,
            [FromServices] IClientArtifactsService service,
            CancellationToken ct) =>
        {
            var allowFallback = fallback.GetValueOrDefault();
            var download = await service.DownloadAsync(rid, version, allowFallback, ct);
            return Results.Stream(download.Content, download.ContentType, download.FileName);
        })
        .RequireAuthorization("ClientArtifactsDownload")
        .WithName("ClientArtifacts_Download")
        .Produces<FileContentResult>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithOpenApi(op =>
        {
            op ??= new OpenApiOperation();
            op.Summary = "Download an artifact for a specific RID and version.";
            op.Parameters ??= [];
            op.Parameters.Add(new OpenApiParameter
            {
                Name = "fallback",
                In = ParameterLocation.Query,
                Required = false,
                Description = "Allow legacy fallback scan if the artifact is not stored",
                Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean, Default = JsonValue.Create(false) }
            });
            return op;
        });

        group.MapGet("{rid}/{version}/raw-download", async (
            string rid,
            string version,
            [FromServices] IClientArtifactsService service,
            CancellationToken ct) =>
        {
            var download = await service.DownloadRawAsync(rid, version, ct);
            return Results.Stream(download.Content, download.ContentType, download.FileName);
        })
        .RequireAuthorization("ClientArtifactsDownload")
        .WithName("ClientArtifacts_RawDownload")
        .Produces<FileContentResult>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithOpenApi(op =>
        {
            op ??= new OpenApiOperation();
            op.Summary = "Download the stored client artifact without injecting tokens or enrollment payloads.";
            return op;
        });

        app.MapGet("/api/v2/client-artifacts/{rid}/{version}/onboarding-download", DownloadOnboardingAsync)
            .AllowAnonymous()
            .WithName("ClientArtifacts_V2OnboardingDownload")
            .WithTags("Client Onboarding")
            .Produces<FileContentResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("{rid}/{version}/onboarding-download", DownloadOnboardingAsync)
        .AllowAnonymous()
        .WithName("ClientArtifacts_OnboardingDownload")
        .Produces<FileContentResult>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithOpenApi(op =>
        {
            op ??= new OpenApiOperation();
            op.Summary = "Download a client artifact during onboarding using a tenant-scoped enrollment code.";
            op.Parameters ??= [];
            op.Parameters.Add(new OpenApiParameter
            {
                Name = "X-NetRatel-Tenant-Id",
                In = ParameterLocation.Header,
                Required = true,
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer }
            });
            op.Parameters.Add(new OpenApiParameter
            {
                Name = "X-NetRatel-Enrollment-Code",
                In = ParameterLocation.Header,
                Required = true,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            });
            return op;
        });

        group.MapPost("upload", async (
            HttpContext http,
            [FromForm(Name = "rid")] string rid,
            [FromForm(Name = "version")] string version,
            [FromForm(Name = "notes")] string? notes,
            [FromForm(Name = "file")] IFormFile file,
            [FromServices] IClientArtifactsService service,
            [FromServices] IClientUpdatePublisher updates,
            CancellationToken ct) =>
        {
            try
            {
                var uploadedBy = http.User?.Identity?.Name;
                var result = await service.UploadAsync(file, rid, version, notes, uploadedBy, ct);
                try
                {
                    var manifestJson = await ClientArtifactManifestValidator.ValidateAsync(service, result.Artifact, ct);
                    await updates.PublishArtifactAsync(result.Artifact, manifestJson, uploadedBy, ct);
                }
                catch
                {
                    if (result.Created)
                        await service.DeleteAsync(result.Artifact.Rid, result.Artifact.Version, ct);
                    throw;
                }
                var location = $"/api/v1/client-artifacts/{result.Artifact.Rid}/{result.Artifact.Version}";
                return Results.Created(location, result);
            }
            catch (ClientArtifactConflictException ex)
            {
                return Results.Conflict(new
                {
                    message = ex.Message,
                    rid = ex.Rid,
                    version = ex.Version
                });
            }
        })
        .RequireAuthorization("ClientArtifactsUpload")
        .DisableAntiforgery()
        .Accepts<IFormFile>("multipart/form-data")
        .Produces<ClientArtifactUploadResultDto>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status409Conflict)
        .WithName("ClientArtifacts_Upload")
        .WithOpenApi(op =>
        {
            op ??= new OpenApiOperation();
            op.Summary = "Upload a new client artifact archive.";
            op.RequestBody = new OpenApiRequestBody
            {
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["multipart/form-data"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Object,
                            Properties = new Dictionary<string, IOpenApiSchema>
                            {
                                ["rid"] = new OpenApiSchema { Type = JsonSchemaType.String, Example = JsonValue.Create("win-x64") },
                                ["version"] = new OpenApiSchema { Type = JsonSchemaType.String, Example = JsonValue.Create("0.1.0-rc.1") },
                                ["notes"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
                                ["file"] = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.String,
                                    Format = "binary"
                                }
                            },
                            Required = new HashSet<string> { "rid", "version", "file" }
                        }
                    }
                }
            };
            return op;
        });

        group.MapDelete("{rid}/{version}", async (
            string rid,
            string version,
            [FromServices] IClientArtifactsService service,
            [FromServices] IClientUpdatePublisher updates,
            CancellationToken ct) =>
        {
            if (await updates.IsArtifactPublishedAsync(rid, version, ct))
            {
                return Results.Conflict(new { message = "Published client update artifacts are immutable." });
            }
            await service.DeleteAsync(rid, version, ct);
            return Results.NoContent();
        })
        .RequireAuthorization("ClientArtifactsWrite")
        .DisableAntiforgery()
        .WithName("ClientArtifacts_Delete")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("fallback-scan", async (
            [FromQuery] string rid,
            [FromQuery] string? version,
            [FromServices] IClientArtifactsService service,
            CancellationToken ct) =>
        {
            try
            {
                var result = await service.RunFallbackScanAsync(rid, version, ct);
                return Results.Stream(result.Content, result.ContentType, result.FileName);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        })
        .WithName("ClientArtifacts_FallbackScan")
        .Produces<FileContentResult>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/api/v1/client/download", async (
            ClientDownloadRequest req,
            HttpContext http,
            [FromServices] IClientArtifactsService service,
            CancellationToken ct) =>
        {
            var isWindows = System.Runtime.InteropServices.RuntimeInformation
                .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

            var (_, normalizedRid, normalizedVersion) = ClientDownloadValidation.Validate(req, isWindows);
            req.RuntimeId = normalizedRid;
            req.Version = normalizedVersion;

            var download = await service.DownloadForClientAsync(req, ct);
            if (!string.IsNullOrWhiteSpace(download.EnrollmentCode))
            {
                http.Response.Headers["X-Enrollment-Code"] = download.EnrollmentCode;
                if (download.EnrollmentExpiresUtc.HasValue)
                {
                    http.Response.Headers["X-Enrollment-Expires"] = download.EnrollmentExpiresUtc.Value.UtcDateTime.ToString("O");
                }
            }

            return Results.Stream(download.Content, download.ContentType, download.FileName);
        })
        .RequireAuthorization("ClientArtifactsWrite")
        .WithName("ClientDownload")
        .WithTags("Client Onboarding")
        .Accepts<ClientDownloadRequest>("application/json")
        .Produces<FileContentResult>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithOpenApi(op =>
        {
            op ??= new OpenApiOperation();
            op.Summary = "Download a client package for onboarding.";
            if (op.RequestBody?.Content?.TryGetValue("application/json", out var media) == true)
            {
                media.Examples ??= new Dictionary<string, IOpenApiExample>();
                media.Examples["latestVersion"] = new OpenApiExample
                {
                    Summary = "Download latest artifact",
                    Value = new JsonObject
                    {
                        ["tenantId"] = JsonValue.Create(4098),
                        ["environment"] = JsonValue.Create("Dev"),
                        ["runtimeId"] = JsonValue.Create("win-x64"),
                        ["version"] = JsonValue.Create("latest"),
                        ["injectEnrollment"] = JsonValue.Create(true),
                        ["validForMinutes"] = JsonValue.Create(60),
                        ["maxUses"] = JsonValue.Create(1)
                    }
                };

                media.Examples["specificVersion"] = new OpenApiExample
                {
                    Summary = "Download a specific artifact version",
                    Value = new JsonObject
                    {
                        ["tenantId"] = JsonValue.Create(4098),
                        ["environment"] = JsonValue.Create(1),
                        ["runtimeId"] = JsonValue.Create("win-x64"),
                        ["version"] = JsonValue.Create("0.1.0-rc.1"),
                        ["injectEnrollment"] = JsonValue.Create(false)
                    }
                };
            }

            return op;
        });

        app.MapPost("/api/v1/client/script", async (
            ClientScriptRequest req,
            HttpContext http,
            [FromServices] IClientScriptService service,
            CancellationToken ct) =>
        {
            var isWindows = System.Runtime.InteropServices.RuntimeInformation
                .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

            var (_, normalizedRid, validForMinutes, maxUses) = ClientScriptValidation.Validate(req, isWindows);
            req.RuntimeId = normalizedRid;
            req.ValidForMinutes = validForMinutes;
            req.MaxUses = maxUses;

            var script = await service.GenerateAsync(req, ct);
            if (!string.IsNullOrWhiteSpace(script.EnrollmentCode))
            {
                http.Response.Headers["X-Enrollment-Code"] = script.EnrollmentCode;
                if (script.EnrollmentExpiresUtc.HasValue)
                {
                    http.Response.Headers["X-Enrollment-Expires"] = script.EnrollmentExpiresUtc.Value.UtcDateTime.ToString("O");
                }
            }

            return Results.File(script.Content, script.ContentType, script.FileName);
        })
        .RequireAuthorization("ClientArtifactsWrite")
        .WithName("ClientScript")
        .WithTags("Client Onboarding")
        .Accepts<ClientScriptRequest>("application/json")
        .Produces(StatusCodes.Status200OK, contentType: "text/plain")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithOpenApi(op =>
        {
            op ??= new OpenApiOperation();
            op.Summary = "Generate a deployment script for unattended client rollout.";
            return op;
        });

        return app;
    }

    private static async Task<IResult> DownloadOnboardingAsync(string rid, string version, HttpContext http,
        [FromServices] IClientArtifactsService service, [FromServices] IEnrollmentCodeIssueService enrollmentCodes, CancellationToken ct)
    {
        var tenantHeader = http.Request.Headers["X-NetRatel-Tenant-Id"].FirstOrDefault();
        var enrollmentCode = http.Request.Headers["X-NetRatel-Enrollment-Code"].FirstOrDefault();
        if (!int.TryParse(tenantHeader, out var tenantId) || tenantId <= 0)
        {
            return Results.Unauthorized();
        }

        try
        {
            await enrollmentCodes.ValidateActiveCodeAsync(enrollmentCode ?? string.Empty, tenantId, ct);
            var download = await service.DownloadRawAsync(rid, version, ct);
            return Results.Stream(download.Content, download.ContentType, download.FileName);
        }
        catch (AgentAuthException ex)
        {
            return Results.Problem(statusCode: ex.StatusCode, title: "Enrollment code rejected", detail: ex.Message);
        }
    }
}
