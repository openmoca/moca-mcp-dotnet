using System.IO.Pipelines;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenMoca.Reader;
using Xunit;

namespace OpenMoca.Mcp.Tests;

/// <summary>The library's extension points, used in-process as a host would.</summary>
public sealed class ExtensionTests
{
    /// <summary>A source that is not a folder: packages by name, from a table the host keeps.</summary>
    private sealed class TableSource : IPackageSource
    {
        private readonly Dictionary<string, string> _table = new()
        {
            ["kb"] = Path.Combine(MocaSpec.Root, "examples", "support-kb"),
            ["acme"] = Path.Combine(MocaSpec.Root, "examples", "acme-support"),
        };

        public int Opened { get; private set; }

        public event EventHandler<PackagesChangedEventArgs>? Changed;

        public ValueTask<IReadOnlyList<PackageDescriptor>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<PackageDescriptor>>(
        [
            new("kb", "https://example.com/moca/support-kb", "4.2.0", "Support KB", null, null),
            new("acme", "https://example.com/moca/acme-support", "1.0.0", "Acme", null, null),
        ]);

        public ValueTask<PackageTarget> OpenAsync(PackageDescriptor package, CancellationToken cancellationToken)
        {
            Opened++;
            return ValueTask.FromResult(PackageTarget.FromPath(_table[package.Location]));
        }

        public void Raise() => Changed?.Invoke(this, new PackagesChangedEventArgs(["https://example.com/moca/acme-support@1.0.0"]));
    }

    private static async Task<(McpClient Client, McpServer Server)> ConnectAsync(MocaMcpServer moca)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), moca.ServerOptions);
        moca.Attach(server);
        _ = server.RunAsync(TestContext.Current.CancellationToken);
        var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
            cancellationToken: TestContext.Current.CancellationToken);
        return (client, server);
    }

    [Fact]
    public async Task A_custom_source_serves_packages_and_opens_them_only_when_used()
    {
        var source = new TableSource();
        var moca = await MocaMcpServer.CreateAsync(new MocaMcpOptions(), source, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, moca.Packages.Count);
        Assert.Equal(0, source.Opened);

        var (client, _) = await ConnectAsync(moca);
        await using var _client = client;
        var result = await client.CallToolAsync("moca_search", new Dictionary<string, object?> { ["query"] = "refund", ["packages"] = new[] { "https://example.com/moca/acme-support" } },
            cancellationToken: TestContext.Current.CancellationToken);
        var json = JsonNode.Parse(((TextContentBlock)result.Content[0]).Text)!;

        Assert.Equal(1, source.Opened);
        Assert.NotEmpty(json["results"]!.AsArray());
        Assert.All(json["results"]!.AsArray(), r => Assert.StartsWith("https://example.com/moca/acme-support@", (string)r!["ref"]!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_source_change_unloads_the_package_and_tells_the_client()
    {
        var source = new TableSource();
        var moca = await MocaMcpServer.CreateAsync(new MocaMcpOptions { Preload = true }, source, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, source.Opened);
        var (client, _) = await ConnectAsync(moca);
        await using var _client = client;
        var told = new TaskCompletionSource();
        await using var registration = client.RegisterNotificationHandler(NotificationMethods.ResourceListChangedNotification, (_, _) =>
        {
            told.TrySetResult();
            return ValueTask.CompletedTask;
        });

        source.Raise();
        await told.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await client.CallToolAsync("moca_search", new Dictionary<string, object?> { ["query"] = "refund", ["packages"] = new[] { "https://example.com/moca/acme-support" } },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, source.Opened);
    }

    [Fact]
    public async Task Dependency_injection_takes_a_custom_source()
    {
        var services = new ServiceCollection();
        services.AddMocaMcp(new MocaMcpOptions { Audiences = ["public"] }).WithPackageSource<TableSource>();
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<TableSource>(provider.GetRequiredService<IPackageSource>());
        var moca = await provider.GetRequiredService<MocaMcpServerFactory>().CreateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["https://example.com/moca/support-kb@4.2.0", "https://example.com/moca/acme-support@1.0.0"], moca.Packages.Select(p => p.Key));
    }
}
