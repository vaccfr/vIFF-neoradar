using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Viff.NeoRadar.Tests.Fakes;
using Viff.NeoRadar.Viff;
using Xunit;

namespace Viff.NeoRadar.Tests.Viff;

public sealed class ViffApiClientTests
{
    [Fact]
    public async Task Writes_FlagARejectedKeyAndTheNewKeyClearsIt()
    {
        StubHandler handler = new(request =>
            request.Headers.GetValues("x-api-key").Single() == "REAL"
                ? Reply(HttpStatusCode.OK, "true")
                : Reply(HttpStatusCode.Unauthorized, """{"message":"Invalid API key","statusCode":401}"""));
        FakeLogger logger = new();
        using ViffApiClient api = new(new Uri("https://viff.test/"), "TEST", "tests", logger, handler);

        Assert.False(await api.SetMasterAsync("LFPG", "LFPG_DEL", CancellationToken.None));
        Assert.True(api.KeyRejected);

        api.SetApiKey("REAL");
        Assert.False(api.KeyRejected);
        Assert.True(await api.SetMasterAsync("LFPG", "LFPG_DEL", CancellationToken.None));
        Assert.False(api.KeyRejected);
        Assert.Equal(new[] { "TEST", "REAL" }, handler.Keys);
        Assert.DoesNotContain(logger.Lines, l => l.Contains("REAL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reads_DoNotTouchTheKeyFlag()
    {
        StubHandler handler = new(_ => Reply(HttpStatusCode.Unauthorized, "{}"));
        using ViffApiClient api = new(new Uri("https://viff.test/"), "TEST", "tests", new FakeLogger(), handler);

        Assert.Null(await api.GetMastersAsync(CancellationToken.None));
        Assert.False(api.KeyRejected);
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply)
        {
            _reply = reply;
        }

        public List<string> Keys { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Keys.Add(request.Headers.GetValues("x-api-key").Single());
            return Task.FromResult(_reply(request));
        }
    }
}
