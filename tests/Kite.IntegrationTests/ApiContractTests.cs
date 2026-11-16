using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kite.IntegrationTests;

/// <summary>
/// ADR 18 (revised in Phase 4): the web client is written by hand, so this test reads every call it makes
/// (packages/api-client) and checks that the server has that method and route. A renamed or removed endpoint
/// fails here instead of in a shop.
/// </summary>
[Collection("server")]
public sealed partial class ApiContractTests(KiteServerFixture fx)
{
    [GeneratedRegex(@"\b(get|post|put|del|blob)(?:<[^>]*>)?\(\s*[`'""](/api/v1/[^`'""?]*)")]
    private static partial Regex ClientCall();
    [GeneratedRegex(@"raw\('([A-Z]+)',\s*'(/api/v1/[^']*)'")]
    private static partial Regex RawCall();
    [GeneratedRegex(@"\$\{[^}]*\}")]
    private static partial Regex Template();
    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex RouteParam();

    private static string Norm(string path) => RouteParam().Replace(Template().Replace(path, "{x}"), "{x}").TrimEnd('/');

    [PgFact]
    public void Every_path_the_web_client_calls_exists_on_the_server()
    {
        var root = Directory.GetParent(AppContext.BaseDirectory)!;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        var dir = Path.Combine(root!.FullName, "src", "client", "packages", "api-client", "src");
        var calls = new HashSet<(string Method, string Path)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.ts").Where(f => !f.EndsWith(".test.ts", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in ClientCall().Matches(text))
            {
                var method = m.Groups[1].Value switch { "get" or "blob" => "GET", "post" => "POST", "put" => "PUT", _ => "DELETE" };
                var path = m.Groups[2].Value;
                var query = path.IndexOf("${qs(", StringComparison.Ordinal);
                calls.Add((method, Norm(query >= 0 ? path[..query] : path)));
            }
            foreach (Match m in RawCall().Matches(text)) calls.Add((m.Groups[1].Value, Norm(m.Groups[2].Value)));
        }
        Assert.True(calls.Count > 100, $"only {calls.Count} client calls found");

        var server = fx.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(m => (Method: m, Path: Norm("/" + e.RoutePattern.RawText!.TrimStart('/')))))
            .ToHashSet();
        var missing = calls.Where(c => !server.Contains(c)).Select(c => $"{c.Method} {c.Path}").OrderBy(x => x).ToArray();
        Assert.True(missing.Length == 0, "The web client calls routes the server does not have:\n" + string.Join("\n", missing));
    }
}
