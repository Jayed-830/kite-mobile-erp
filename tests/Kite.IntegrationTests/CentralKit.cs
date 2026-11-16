using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Kite.IntegrationTests;

/// <summary>Acts as branch servers towards the head office under test: registers them, then talks to the sync port with their certificates.</summary>
public sealed class CentralKit(KiteServerFixture fx)
{
    public sealed record Branch(Guid BranchId, string Code, Guid ServerId, X509Certificate2 Cert);

    public static PushIdentifier I(string imei, short slot = 1) => new("imei", slot, imei);

    public static CentralUnitPush Unit(Guid id, string status, params string[] imeis) =>
        new(id, status, "brand_new", "Branch phone", "هاتف الفرع", DateTimeOffset.UtcNow, imeis.Select((x, i) => I(x, (short)(i + 1))).ToList());

    public async Task<Branch> RegisterBranchAsync(HttpClient owner)
    {
        var code = "C" + PosKit.Suffix()[..5];
        var r = await owner.PostAsJsonAsync("/api/v1/branches", new { code, nameEn = "Branch " + code, nameAr = "فرع " + code });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var branchId = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var tr = await owner.PostAsync($"/api/v1/branches/{branchId}/registration-tokens", null);
        Assert.True(tr.IsSuccessStatusCode, await tr.Content.ReadAsStringAsync());
        var token = (await tr.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var schema = await fx.QueryDbAsync<int>("SELECT max(version) FROM meta.schema_info");
        var (csr, key) = BusinessCa.CreateBranchRequest("PC-" + code);
        using var tls = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var reg = await tls.PostAsJsonAsync("/sync/v1/register", new { token, csrPem = csr, serverName = "PC-" + code, appVersion = "0.6.0", schemaVersion = schema });
        Assert.True(reg.IsSuccessStatusCode, await reg.Content.ReadAsStringAsync());
        var body = await reg.Content.ReadFromJsonAsync<JsonElement>();
        var cert = BusinessCa.WithPrivateKey(BusinessCa.FromPem(body.GetProperty("certPem").GetString()!), key);
        return new Branch(branchId, code, body.GetProperty("serverId").GetGuid(), cert);
    }

    public async Task DeactivateAsync(HttpClient owner, Branch b)
    {
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/v1/servers");
        var row = list.GetProperty("branches").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == b.BranchId);
        var r = await owner.PutAsJsonAsync($"/api/v1/branches/{b.BranchId}", new
        {
            nameEn = row.GetProperty("nameEn").GetString(), nameAr = row.GetProperty("nameAr").GetString(), status = "inactive", version = row.GetProperty("version").GetInt32(),
        });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }

    /// <summary>A request to the sync port through the in-memory server, as if over TLS with this client certificate.</summary>
    public async Task<(int Status, JsonElement Body)> SyncAsync(X509Certificate2 cert, string path, object body)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body, KiteServerFixture.JsonOptions);
        var ctx = await fx.Factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Scheme = "https";
            c.Request.Host = new HostString("localhost");
            c.Request.Path = path;
            c.Request.ContentType = "application/json";
            c.Request.Body = new MemoryStream(json);
            c.Request.ContentLength = json.Length;
            c.Connection.ClientCertificate = cert;
            c.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());
        });
        if (ctx.Response.Body.CanSeek) ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (ctx.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public async Task<JsonElement> RegisterAsync(Branch b, params CentralUnitPush[] units)
    {
        var (status, body) = await SyncAsync(b.Cert, "/sync/v1/imei/register", new CentralRegisterRequest(units.ToList()));
        Assert.True(status == 200, $"register: {status} {body}");
        return body;
    }

    public async Task<JsonElement> CheckAsync(Branch b, params string[] imeis)
    {
        var (status, body) = await SyncAsync(b.Cert, "/sync/v1/imei/check", new CentralCheckRequest(imeis.Select(x => I(x)).ToList()));
        Assert.True(status == 200, $"check: {status} {body}");
        return body.GetProperty("results");
    }

    public async Task<JsonElement> HeartbeatAsync(Branch b, long imeiSeq = 0, long blockedSeq = 0)
    {
        var schema = await fx.QueryDbAsync<int>("SELECT max(version) FROM meta.schema_info");
        var (status, body) = await SyncAsync(b.Cert, "/sync/v1/heartbeat",
            new Kite.Host.Features.HeartbeatRequest("0.6.0", schema, DateTimeOffset.UtcNow, 0, 0, null, null, null, 0, 0, 0, imeiSeq, blockedSeq));
        Assert.True(status == 200, $"heartbeat: {status} {body}");
        return body;
    }

    public static string State(JsonElement registerResult, Guid unitId) =>
        registerResult.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("unitId").GetGuid() == unitId).GetProperty("state").GetString()!;

    private sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
