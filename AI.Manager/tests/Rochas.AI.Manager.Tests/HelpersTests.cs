using System.Text.Json;
using System.Text.Json.Nodes;
using Rochas.AI.Manager.Application.Helpers;
using Xunit;

namespace Rochas.AI.Manager.Tests;

public class ToolCallCoercerTests
{
    [Fact]
    public void ExtractAngleCall()
    {
        var content = "<{\"name\":\"foo\",\"arguments\":{\"x\":1}}>";
        var calls = ToolCallCoercer.ExtractTextToolCalls(content);
        Assert.Single(calls);
        Assert.Equal("foo", calls[0].Name);
    }

    [Fact]
    public void ExtractAssistantToolCall()
    {
        var content = "[Assistant tool call]: bar({\"y\":2})";
        var calls = ToolCallCoercer.ExtractTextToolCalls(content);
        Assert.Single(calls);
        Assert.Equal("bar", calls[0].Name);
    }

    [Fact]
    public void CoerceChatCalls_SetsToolCalls()
    {
        var json = "{\"choices\":[{\"message\":{\"content\":\"<\\\"name\\\":\\\"baz\\\",\\\"arguments\\\":{}}>\",\"role\":\"assistant\"},\"finish_reason\":\"stop\"}]}";
        // build proper
        var root = new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["message"] = new JsonObject { ["content"] = "<{\"name\":\"baz\",\"arguments\":{}}>", ["role"] = "assistant" },
                    ["finish_reason"] = "stop"
                }
            }
        };
        var n = ToolCallCoercer.CoerceChatCalls(root);
        Assert.Equal(1, n);
        Assert.NotNull(root["choices"]![0]!["message"]!["tool_calls"]);
    }

    [Fact]
    public void EmptyContent_ReturnsEmpty()
    {
        var calls = ToolCallCoercer.ExtractTextToolCalls("");
        Assert.Empty(calls);
    }

    [Fact]
    public void KnownFilter()
    {
        var content = "<{\"name\":\"foo\",\"arguments\":{}}>";
        var calls = ToolCallCoercer.ExtractTextToolCalls(content, new List<string> { "bar" });
        Assert.Empty(calls);
    }
}

public class ReasoningSanitizerTests
{
    [Fact]
    public void RemovesReasoningContent()
    {
        var json = "{\"choices\":[{\"message\":{\"content\":\"hi\",\"reasoning_content\":\"think\"}}]}";
        var out_ = ReasoningSanitizer.Sanitize(json);
        Assert.DoesNotContain("reasoning_content", out_);
        Assert.Contains("hi", out_);
    }

    [Fact]
    public void InvalidJson_ReturnsOriginal()
    {
        var text = "not json";
        Assert.Equal(text, ReasoningSanitizer.Sanitize(text));
    }
}