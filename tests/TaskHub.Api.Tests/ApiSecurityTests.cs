using System.Net;
using System.Text.Json;
using TaskHub.Abstractions.Contracts;

namespace TaskHub.Api.Tests;

public class ApiSecurityTests
{
    [Fact]
    public async Task Requests_without_a_token_are_rejected()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client(token: null);

        var response = await http.GetAsync("/v1/tasks");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("token", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_token_is_rejected()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client(token: "not-the-token-not-the-token-not-the-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/v1/status")).StatusCode);
    }

    [Fact]
    public async Task Valid_token_is_accepted()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        var response = await http.GetAsync("/v1/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("evil.example.com")]       // DNS rebinding: attacker's name resolving to 127.0.0.1
    [InlineData("127.0.0.1")]              // missing port
    [InlineData("127.0.0.1:1")]            // wrong port
    [InlineData("localhost.evil.com:{port}")]
    public async Task Foreign_host_header_is_rejected_even_with_a_valid_token(string host)
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/status");
        request.Headers.Host = host.Replace("{port}", api.Server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
    }

    [Fact]
    public async Task Localhost_host_header_is_allowed()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/status");
        request.Headers.Host = $"localhost:{api.Server.Port}";
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("null")]                      // sandboxed iframes / file:// pages
    [InlineData("http://127.0.0.1:3000")]     // even other local web apps
    public async Task Browser_origins_are_rejected_even_with_a_valid_token(string origin)
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/tasks")
        {
            Content = new StringContent("{\"text\":\"pwned\"}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", origin);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await api.Core.Query()); // nothing was created
    }

    [Fact]
    public async Task Server_binds_to_loopback_only()
    {
        await using var api = await ApiHarness.CreateAsync();
        Assert.StartsWith("http://127.0.0.1:", api.BaseUrl, StringComparison.Ordinal);
        Assert.True(api.Server.Port > 0);
    }

    [Fact]
    public async Task Discovery_file_and_token_file_are_written_and_cleaned_up()
    {
        var api = await ApiHarness.CreateAsync();
        var discovery = ApiDiscovery.TryRead(api.DiscoveryFile);
        Assert.NotNull(discovery);
        Assert.Equal(api.Server.Port, discovery.Port);
        Assert.Equal(api.Token, discovery.ReadToken());
        Assert.True(api.Token.Length >= 40);

        await api.Server.StopAsync(CancellationToken.None);
        Assert.False(File.Exists(api.DiscoveryFile));
        await api.Core.DisposeAsync();
    }

    [Fact]
    public async Task Token_survives_restart_and_rotation_invalidates_old_token()
    {
        await using var api = await ApiHarness.CreateAsync();
        var original = api.Token;
        Assert.Equal(original, new Security.ApiTokenStore(api.Core.Paths.ApiTokenFile).Token);

        api.Server.Tokens!.Rotate();
        using var old = api.Client(token: original);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/v1/status")).StatusCode);
        using var fresh = api.Client();
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/v1/status")).StatusCode);
    }

    [Fact]
    public async Task Error_bodies_are_json()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client(token: null);
        var body = await (await http.GetAsync("/v1/tasks")).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }
}
