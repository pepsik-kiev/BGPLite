using System.Net;
using BGPLite.Configuration;
using BGPLite.Providers;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

public class RipeStatProviderTests
{
    private const string TwoPrefixBody =
        """
        {"status":"ok","data":{"resource":"65001","prefixes":{"v4":{"originating":["10.0.0.0/24","192.168.0.0/16"]},"v6":{"originating":[]}}}}
        """;

    /// <summary>Returns a scripted sequence of responses: each step is either an
    /// <see cref="HttpStatusCode"/> (as a 200/5xx body) or an <see cref="Exception"/> to throw.
    /// The last step repeats if there are more calls than steps.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly object[] _steps;
        private int _index;
        public int Calls { get; private set; }

        public ScriptedHandler(params object[] steps) => _steps = steps;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var i = Math.Min(_index, _steps.Length - 1);
            _index++;
            var step = _steps[i];
            return step switch
            {
                HttpStatusCode code => Task.FromResult(new HttpResponseMessage(code)
                {
                    Content = new StringContent(TwoPrefixBody)
                }),
                Exception ex => Task.FromException<HttpResponseMessage>(ex),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(TwoPrefixBody)
                })
            };
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static RipeStatProvider Provider(ScriptedHandler handler, int retries = 2) =>
        new(new StubFactory(handler),
            NullLogger<RipeStatProvider>.Instance,
            new RipeStatConfig { RetryAttempts = retries, RetryDelaySeconds = 0 });

    [Fact]
    public async Task ParsesPrefixes()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK);
        var result = await Provider(handler).GetPrefixesAsync(65001);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RetriesOnTransient5xx_ThenSucceeds()
    {
        var handler = new ScriptedHandler(
            HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.OK);

        var result = await Provider(handler, retries: 2).GetPrefixesAsync(65001);

        Assert.Equal(2, result.Count);
        Assert.Equal(3, handler.Calls); // 1 initial + 2 retries
    }

    [Fact]
    public async Task RetriesOn429_ThenSucceeds()
    {
        var handler = new ScriptedHandler((HttpStatusCode)429, HttpStatusCode.OK);

        var result = await Provider(handler, retries: 2).GetPrefixesAsync(65001);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task RetriesOnTimeout_ThenSucceeds()
    {
        // A client timeout surfaces as TaskCanceledException (a subclass of
        // OperationCanceledException). It must be retried when the caller didn't cancel.
        var handler = new ScriptedHandler(new TaskCanceledException(), HttpStatusCode.OK);

        var result = await Provider(handler, retries: 1).GetPrefixesAsync(65001);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ThrowsAfterExhaustingRetries()
    {
        var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable); // repeats

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider(handler, retries: 2).GetPrefixesAsync(65001));

        Assert.Equal(3, handler.Calls); // 1 initial + 2 retries, then gives up
    }

    [Fact]
    public async Task DoesNotRetry_WhenRetryAttemptsZero()
    {
        var handler = new ScriptedHandler(HttpStatusCode.InternalServerError, HttpStatusCode.OK);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider(handler, retries: 0).GetPrefixesAsync(65001));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DoesNotRetry_NonTransientStatus()
    {
        // 404 is a client error — not transient, so it must not be retried.
        var handler = new ScriptedHandler(HttpStatusCode.NotFound, HttpStatusCode.OK);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider(handler, retries: 2).GetPrefixesAsync(65001));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PropagatesCallerCancellation_WithoutCalling()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Provider(handler).GetPrefixesAsync(65001, cts.Token));

        Assert.Equal(0, handler.Calls); // bails before the first HTTP call
    }
}
