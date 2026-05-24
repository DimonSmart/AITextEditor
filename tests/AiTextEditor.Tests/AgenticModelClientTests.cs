using AiTextEditor.Agent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class AgenticModelClientTests
{
    [Fact]
    public async Task AgenticFrameworkModelClient_UsesGenericStructuredOutput()
    {
        var chatClient = new CapturingChatClient(
            """
            {
              "characters": []
            }
            """);
        var agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "test_agent",
                UseProvidedChatClientAsIs = true
            },
            NullLoggerFactory.Instance);
        var client = new AgenticFrameworkModelClient(
            agent,
            NullLogger<AgenticFrameworkModelClient>.Instance);

        var result = await client.RunAsync<CharacterExtractionResponse>(
            new AgenticModelRequest(
                [new ChatMessage(ChatRole.User, "extract")],
                InvalidContractError: "invalid_contract"));

        Assert.Empty(result.Characters);
        Assert.NotNull(chatClient.LastOptions);
        var responseFormat = Assert.IsType<ChatResponseFormatJson>(chatClient.LastOptions.ResponseFormat);
        Assert.NotNull(responseFormat.Schema);
        Assert.Equal(nameof(CharacterExtractionResponse), responseFormat.SchemaName);
    }

    [Fact]
    public async Task AgenticFrameworkModelClient_RetriesMalformedStructuredOutput()
    {
        var chatClient = new CapturingChatClient(
            """
            ```json
            {
              "characters": []
            }
            ```
            """,
            """
            {
              "characters": []
            }
            """);
        var agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "test_agent",
                UseProvidedChatClientAsIs = true
            },
            NullLoggerFactory.Instance);
        var client = new AgenticFrameworkModelClient(
            agent,
            NullLogger<AgenticFrameworkModelClient>.Instance);

        var result = await client.RunAsync<CharacterExtractionResponse>(
            new AgenticModelRequest(
                [new ChatMessage(ChatRole.User, "extract")],
                InvalidContractError: "invalid_contract"));

        Assert.Empty(result.Characters);
        Assert.Equal(2, chatClient.CallCount);
        Assert.Contains(
            chatClient.LastMessages,
            message => message.Role == ChatRole.System
                       && message.Text?.Contains("previous response was malformed", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task CharacterExtractionClient_WhenAliasExampleIsMissing_FailsContractValidation()
    {
        var response = new CharacterExtractionResponse
        {
            Characters =
            [
                new CharacterExtractionCharacter(
                    "John",
                    "unknown",
                    [new CharacterExtractionAlias("Johnny", "")],
                    "")
            ]
        };
        var client = new AgenticCharacterExtractionModelClient(
            new StubAgenticModelClient(response),
            NullLogger<AgenticCharacterExtractionModelClient>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExtractCharactersAsync(new CharacterExtractionModelRequest("system", "user")));

        Assert.Equal("character_extraction_response_contract_invalid", exception.Message);
    }

    private sealed class CapturingChatClient(params string[] responseTexts) : IChatClient
    {
        private readonly Queue<string> responses = new(responseTexts);

        public int CallCount { get; private set; }

        public ChatOptions? LastOptions { get; private set; }

        public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastMessages = messages.ToArray();
            LastOptions = options;
            var responseText = responses.Count > 0
                ? responses.Dequeue()
                : throw new InvalidOperationException("No scripted chat response is available.");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubAgenticModelClient(CharacterExtractionResponse response) : IAgenticModelClient
    {
        public Task<TResponse> RunAsync<TResponse>(
            AgenticModelRequest request,
            CancellationToken cancellationToken = default)
            where TResponse : class
        {
            return Task.FromResult((TResponse)(object)response);
        }
    }
}
