using AwesomeAssertions;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.Contracts.RatelDesk;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class RatelDeskManualProfileProbeTests
{
    private static readonly Guid Source = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Receiver = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid SourceNamespace = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private const string Api = "https://receiver.example/helpdesk";
    private const string Bearer = "rdk_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static RatelDeskConnectorState Connector => new(Guid.Parse("44444444-4444-4444-8444-444444444444"), 1, 1, 1, "owner",
        new("manual", Api, "org", "customer", null, Array.Empty<Guid>(), new(), true), "ciphertext", 1);

    [Fact]
    public async Task Concrete_probe_uses_only_authenticated_capability_and_exact_read_only_target_route()
    {
        var f = new HttpFixture(); f.Handler.Responses.Enqueue(JsonReply(Capability())); f.Handler.Responses.Enqueue(JsonReply(Target()));
        var result = await f.Probe.CaptureAsync(Connector, Source, Bearer, default);
        (result.Peer.SourceInstanceId).Should().Be(Source); (result.Peer.SourceNamespaceId).Should().Be(SourceNamespace);
        (f.Handler.Requests.Count).Should().Be(2);
        ((f.Handler.Requests[0].Method, f.Handler.Requests[0].Uri)).Should().Be(("GET", Api + ReceiverWireValidation.CapabilitiesPath));
        ((f.Handler.Requests[1].Method, f.Handler.Requests[1].Uri)).Should().Be(("POST", Api + ReceiverWireValidation.TargetsPath));
        (f.Handler.Requests).Should().AllSatisfy(request =>
        {
            (request.Scheme).Should().Be("Bearer"); (request.Credential).Should().Be(Bearer);
            (request.Source).Should().Be(Source.ToString("D")); (request.IdempotencyKey).Should().BeNull();
        });
        (f.Handler.Requests[0].Body).Should().Be("");
        using var target = JsonDocument.Parse(f.Handler.Requests[1].Body);
        (target.RootElement.GetProperty("organizationId").GetString()).Should().Be("org");
        (target.RootElement.GetProperty("customerId").GetString()).Should().Be("customer");
        (f.Handler.Requests).Should().NotContain(request => request.Uri == Api + ReceiverWireValidation.CreatePath);
        (f.Factory.Names).Should().AllSatisfy(name => (name).Should().Be(RatelDeskReceiverHttpPipeline.ManualClient));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Denied_or_unavailable_capability_does_not_send_target_or_guess_identity(int status)
    {
        var f = new HttpFixture(); f.Handler.Responses.Enqueue(JsonReply(new { code = "source-not-authorized" }, status));
        var error = (await ((Func<Task>)(() => f.Probe.CaptureAsync(Connector, Source, Bearer, default))).Should().ThrowAsync<RatelDeskReceiverReadException>()).Which;
        (error.HttpStatus).Should().Be(status); (f.Handler.Requests).Should().ContainSingle();
    }

    [Theory]
    [InlineData("receiverInstanceId")]
    [InlineData("sourceInstanceId")]
    [InlineData("sourceNamespaceId")]
    [InlineData("organizationId")]
    [InlineData("customerId")]
    [InlineData("categoryIds")]
    public async Task Target_validation_cannot_capture_a_different_authorized_namespace_or_mapping(string field)
    {
        var f = new HttpFixture(); var target = Target();
        if (field is "organizationId" or "customerId" or "categoryIds")
            ((Dictionary<string, object?>)target["mapping"]!)[field] = field == "categoryIds" ? new[] { Guid.NewGuid().ToString("D") } : "another-target";
        else target[field] = Guid.NewGuid().ToString("D");
        f.Handler.Responses.Enqueue(JsonReply(Capability())); f.Handler.Responses.Enqueue(JsonReply(target));
        await ((Func<Task>)(() => f.Probe.CaptureAsync(Connector, Source, Bearer, default))).Should().ThrowAsync<InvalidDataException>();
        (f.Handler.Requests.Count).Should().Be(2);
    }

    [Fact]
    public async Task Missing_no_store_and_duplicate_identity_fields_fail_before_target_probe()
    {
        var f = new HttpFixture(); var reply = JsonReply(Capability()); reply.Headers.CacheControl = null;
        f.Handler.Responses.Enqueue(reply);
        await ((Func<Task>)(() => f.Probe.CaptureAsync(Connector, Source, Bearer, default))).Should().ThrowAsync<InvalidDataException>();
        (f.Handler.Requests).Should().ContainSingle();
        var duplicate = JsonSerializer.Serialize(Capability()).Replace("{", "{\"receiverInstanceId\":\"" + Receiver.ToString("D") + "\",", StringComparison.Ordinal);
        // Capabilities has no nested object, so this inserts exactly one duplicate identity field.
        ((Action)(() => ReceiverWireValidation.CaptureManual(Encoding.UTF8.GetBytes(duplicate), Connector, Api, Source, DateTimeOffset.UnixEpoch))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Bounded_HTTP_pipeline_clamps_rate_diagnosis_and_retains_manual_client_selection()
    {
        var f = new HttpFixture(); var response = JsonReply(new { code = "rate-limited" }, 429);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(999)); f.Handler.Responses.Enqueue(response);
        var reply = await f.Pipeline.ReadAsync(RatelDeskAuthenticationMode.ManualApiBearer, 1, Connector.Id, Api, Source,
            Bearer, HttpMethod.Get, Api + ReceiverWireValidation.CapabilitiesPath, null, default);
        (reply.Status).Should().Be(429); (reply.RetryAfter).Should().Be(TimeSpan.FromSeconds(300));
        (f.Handler.Requests).Should().ContainSingle(); (f.Factory.Names).Should().ContainSingle();
    }

    [Fact]
    public async Task Deployment_source_is_applied_before_concrete_continuity_can_seed_an_absent_Flow_identity()
    {
        var order = new List<string>(); Guid? appliedSource = null;
        var identity = new IdentityReader(() =>
        {
            order.Add("installation"); appliedSource = Source;
            return new(Receiver.ToString("D"), Source.ToString("D"), 1);
        });
        var flow = new FlowIdentity(() =>
        {
            order.Add("flow"); (appliedSource).Should().Be(Source); return appliedSource!.Value;
        });
        await new RatelDeskProducerContinuity(flow, identity).RequireCurrentAsync(Source, default);
        (order).Should().Equal(new[] { "installation", "flow", "installation" });
    }

    [Fact]
    public async Task Existing_distinct_producer_ids_fail_without_adopting_replacing_or_deleting_either()
    {
        var other = Guid.Parse("55555555-5555-4555-8555-555555555555");
        var identity = new IdentityReader(() => new(Receiver.ToString("D"), other.ToString("D"), 7));
        var flow = new FlowIdentity(() => Source);
        var error = (await ((Func<Task>)(() => new RatelDeskProducerContinuity(flow, identity).RequireCurrentAsync(Source, default))).Should().ThrowAsync<UnauthorizedAccessException>()).Which;
        (error.Message).Should().Be("explicit-source-mapping-and-reapproval-required");
        (identity.Reads).Should().Be(2); (flow.Reads).Should().Be(1);
        ((await identity.GetAsync(default)).SourceInstanceId).Should().Be(other.ToString("D"));
    }

    [Fact]
    public void Managed_private_base_restriction_does_not_require_manual_private_permission()
    {
        var options = new Monitor<RatelDeskReceiverOptions>(new() { AllowedApiBases = ["http://127.0.0.1:9000/helpdesk"] });
        var network = new RatelDeskReceiverNetworkPolicy(options, new Monitor<ServiceLinkOptions>(new() { AllowPrivateHttp = true }),
            new Monitor<ServiceIdentityOptions>(new()));
        (new RatelDeskReceiverOptionsValidator().Validate(null, options.CurrentValue).Succeeded).Should().BeTrue();
        (network.ValidateApprovedApiBase(RatelDeskAuthenticationMode.ManagedServiceLink, "http://127.0.0.1:9000/helpdesk")).Should().Be("http://127.0.0.1:9000/helpdesk");
        ((Action)(() => network.ValidateApprovedApiBase(RatelDeskAuthenticationMode.ManualApiBearer, "http://127.0.0.1:9000/helpdesk"))).Should().Throw<ArgumentException>();
    }

    private static Dictionary<string, object> Capability() => ManualRatelDeskBindingResolverTests.Capability();
    private static Dictionary<string, object?> Target() => new()
    {
        ["contractVersion"] = ReceiverWireValidation.Contract, ["receiverInstanceId"] = Receiver.ToString("D"),
        ["sourceInstanceId"] = Source.ToString("D"), ["sourceNamespaceId"] = SourceNamespace.ToString("D"), ["valid"] = true,
        ["mapping"] = new Dictionary<string, object?> { ["organizationId"] = "org", ["customerId"] = "customer", ["assignedToId"] = null, ["categoryIds"] = Array.Empty<string>() }
    };
    private static HttpResponseMessage JsonReply(object value, int status = 200)
    {
        var result = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        result.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true }; return result;
    }
    private sealed class HttpFixture
    {
        public Handler Handler { get; } = new();
        public Factory Factory { get; }
        public RatelDeskReceiverHttpPipeline Pipeline { get; }
        public RatelDeskManualProfileProbe Probe { get; }
        public HttpFixture()
        {
            Factory = new(Handler);
            var network = new RatelDeskReceiverNetworkPolicy(new Monitor<RatelDeskReceiverOptions>(new()),
                new Monitor<ServiceLinkOptions>(new()), new Monitor<ServiceIdentityOptions>(new()));
            Pipeline = new(Factory, network, TimeProvider.System, new RatelDeskTransportLimiter());
            Probe = new(Pipeline, network, TimeProvider.System);
        }
    }
    private sealed record Request(string Method, string Uri, string? Scheme, string? Credential, string Source, string? IdempotencyKey, string Body);
    private sealed class Handler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Responses { get; } = new();
        public List<Request> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new(request.Method.Method, request.RequestUri!.OriginalString, request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter, request.Headers.GetValues("X-NetRatel-Source-Instance").Single(),
                request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return Responses.Dequeue();
        }
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public List<string> Names { get; } = new();
        public HttpClient CreateClient(string name) { Names.Add(name); return new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }; }
    }
    private sealed class IdentityReader(Func<ServiceLinkIdentityDto> get) : IRatelDeskInstallationIdentityReader
    {
        public int Reads { get; private set; }
        public Task<ServiceLinkIdentityDto> GetAsync(CancellationToken ct) { Reads++; return Task.FromResult(get()); }
    }
    private sealed class FlowIdentity(Func<Guid> get) : IFlowSourceIdentityResolver
    {
        public int Reads { get; private set; }
        public Task<Guid> EnsureAsync(CancellationToken ct) { Reads++; return Task.FromResult(get()); }
    }
    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
