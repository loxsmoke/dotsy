using System.Net;
using System.Text;
using System.Text.Json;
using Dotsy.Core.Config;

namespace Dotsy.Mcp.Tests;

[TestClass]
public sealed class McpClientTests
{
    [TestMethod]
    public async Task ConnectAsync_HttpMarksClientConnected()
    {
        using var client = new McpClient(HttpConfig(), new HttpClient(new FakeHandler("{}")));

        await client.ConnectAsync(CancellationToken.None);

        Assert.IsTrue(client.IsConnected);
        Assert.AreEqual("test-mcp", client.ServerName);
    }

    [TestMethod]
    public async Task ListToolsAsync_ParsesToolDefinitionsAndInputSchema()
    {
        var handler = new FakeHandler("""
            {
              "jsonrpc": "2.0",
              "id": 1,
              "result": {
                "tools": [
                  {
                    "name": "lookup",
                    "description": "Lookup a thing",
                    "inputSchema": {
                      "type": "object",
                      "properties": {
                        "query": { "type": "string" }
                      }
                    }
                  }
                ]
              }
            }
            """);
        using var client = new McpClient(HttpConfig(), new HttpClient(handler));

        var tools = await client.ListToolsAsync(CancellationToken.None);

        Assert.AreEqual(1, tools.Count);
        Assert.AreEqual("lookup", tools[0].Name);
        Assert.AreEqual("Lookup a thing", tools[0].Description);
        Assert.AreEqual("test-mcp", tools[0].ServerName);
        Assert.AreEqual("object", tools[0].InputSchema.GetProperty("type").GetString());

        using var posted = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.AreEqual("2.0", posted.RootElement.GetProperty("jsonrpc").GetString());
        Assert.AreEqual("tools/list", posted.RootElement.GetProperty("method").GetString());
    }

    [TestMethod]
    public async Task ListToolsAsync_UsesEmptySchemaWhenToolOmitsInputSchema()
    {
        var handler = new FakeHandler("""
            {
              "jsonrpc": "2.0",
              "id": 1,
              "result": {
                "tools": [
                  { "name": "ping", "description": "Ping" }
                ]
              }
            }
            """);
        using var client = new McpClient(HttpConfig(), new HttpClient(handler));

        var tools = await client.ListToolsAsync(CancellationToken.None);

        Assert.AreEqual(1, tools.Count);
        Assert.AreEqual(JsonValueKind.Object, tools[0].InputSchema.ValueKind);
        Assert.AreEqual(0, tools[0].InputSchema.EnumerateObject().Count());
    }

    [TestMethod]
    public async Task CallToolAsync_ReturnsTextContentFromSuccessfulToolCall()
    {
        var handler = new FakeHandler("""
            {
              "jsonrpc": "2.0",
              "id": 1,
              "result": {
                "content": [
                  { "type": "text", "text": "hello " },
                  { "type": "text", "text": "world" }
                ]
              }
            }
            """);
        using var client = new McpClient(HttpConfig(), new HttpClient(handler));
        using var input = JsonDocument.Parse("""{"query":"dotsy"}""");

        var result = await client.CallToolAsync("lookup", input.RootElement, CancellationToken.None);

        Assert.IsFalse(result.IsError);
        Assert.AreEqual("hello world", result.Content);

        using var posted = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.AreEqual("tools/call", posted.RootElement.GetProperty("method").GetString());
        var args = posted.RootElement.GetProperty("params").GetProperty("arguments");
        Assert.AreEqual("dotsy", args.GetProperty("query").GetString());
    }

    [TestMethod]
    public async Task CallToolAsync_ReturnsErrorContentWhenServerMarksResultAsError()
    {
        var handler = new FakeHandler("""
            {
              "jsonrpc": "2.0",
              "id": 1,
              "result": {
                "isError": true,
                "content": [
                  { "type": "text", "text": "bad input" }
                ]
              }
            }
            """);
        using var client = new McpClient(HttpConfig(), new HttpClient(handler));
        using var input = JsonDocument.Parse("{}");

        var result = await client.CallToolAsync("lookup", input.RootElement, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.AreEqual("bad input", result.Content);
    }

    [TestMethod]
    public async Task CallToolAsync_ReturnsErrorWhenHttpClientThrows()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("network down"));
        using var client = new McpClient(HttpConfig(), new HttpClient(handler));
        using var input = JsonDocument.Parse("{}");

        var result = await client.CallToolAsync("lookup", input.RootElement, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "network down");
    }

    private static McpServerConfig HttpConfig() => new()
    {
        Name = "test-mcp",
        Transport = McpTransport.Http,
        Url = "https://mcp.example.test/rpc"
    };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> responder;

        public List<string> RequestBodies { get; } = [];

        public FakeHandler(string response)
            : this(_ => response)
        {
        }

        public FakeHandler(Func<HttpRequestMessage, string> responder)
        {
            this.responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var response = responder(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
