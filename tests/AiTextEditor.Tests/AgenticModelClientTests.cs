using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Agent.CharacterBible.Extraction;
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
            new AgenticModelRequest<CharacterExtractionResponse>(
                [new ChatMessage(ChatRole.User, "extract")],
                InvalidContractError: "invalid_contract"));

        Assert.Empty(result.Characters);
        Assert.NotNull(chatClient.LastOptions);
        var responseFormat = Assert.IsType<ChatResponseFormatJson>(chatClient.LastOptions.ResponseFormat);
        Assert.NotNull(responseFormat.Schema);
        Assert.Equal(nameof(CharacterExtractionResponse), responseFormat.SchemaName);
    }

    [Fact]
    public async Task AgenticFrameworkModelClient_RetriesWhenStructuredOutputIsWrappedInMarkdown()
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
            { "characters": [] }
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
        var diagnostics = new List<AgenticModelDiagnostic>();

        var result = await client.RunAsync<CharacterExtractionResponse>(
            new AgenticModelRequest<CharacterExtractionResponse>(
                [new ChatMessage(ChatRole.User, "extract")],
                InvalidContractError: "invalid_contract",
                Diagnostics: new ListProgress<AgenticModelDiagnostic>(diagnostics)));

        Assert.Empty(result.Characters);
        Assert.Equal(2, chatClient.CallCount);
        var malformed = Assert.Single(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.MalformedResponse);
        Assert.Equal("Agent Framework could not produce the typed response. Raw response is available for copying.", malformed.Message);
        Assert.Contains(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.Retry);
        Assert.Contains(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.RetrySucceeded);
    }

    [Fact]
    public async Task AgenticFrameworkModelClient_RetriesWhenMalformedRecoveryFails()
    {
        var chatClient = new CapturingChatClient(
            """
            { "characters": [
            """,
            "There is no valid JSON here.",
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
        var diagnostics = new List<AgenticModelDiagnostic>();

        var result = await client.RunAsync<CharacterExtractionResponse>(
            new AgenticModelRequest<CharacterExtractionResponse>(
                [new ChatMessage(ChatRole.User, "extract")],
                InvalidContractError: "invalid_contract",
                Diagnostics: new ListProgress<AgenticModelDiagnostic>(diagnostics)));

        Assert.Empty(result.Characters);
        Assert.Equal(3, chatClient.CallCount);
        Assert.Contains(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.MalformedResponse);
        Assert.Contains(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.Retry);
        Assert.Contains(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.RetrySucceeded);
        Assert.Contains(
            chatClient.LastMessages,
            message => message.Role == ChatRole.System
                       && message.Text?.Contains("previous response was invalid", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task AgenticFrameworkModelClient_RetriesWhenRawModelRequestFails()
    {
        var chatClient = new CapturingChatClient(
            new InvalidOperationException("temporary model error"),
            """
            { "characters": [] }
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
        var diagnostics = new List<AgenticModelDiagnostic>();

        var result = await client.RunAsync<CharacterExtractionResponse>(
            new AgenticModelRequest<CharacterExtractionResponse>(
                [new ChatMessage(ChatRole.User, "extract")],
                InvalidContractError: "invalid_contract",
                Diagnostics: new ListProgress<AgenticModelDiagnostic>(diagnostics)));

        Assert.Empty(result.Characters);
        Assert.Equal(2, chatClient.CallCount);
        Assert.Contains(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.Retry);
        Assert.Contains(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.RetrySucceeded);
    }

    [Theory]
    [InlineData("The profile has been updated.")]
    [InlineData("")]
    public async Task AgenticFrameworkModelClient_ToolOnlyCall_IgnoresFinalText(string finalText)
    {
        var chatClient = new CapturingChatClient(finalText);
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
        var diagnostics = new List<AgenticModelDiagnostic>();

        var result = await client.RunToolOnlyAsync(
            new AgenticToolOnlyModelRequest(
                [new ChatMessage(ChatRole.User, "update profile")],
                OperationName: "CharacterProfileUpdate",
                ModelCallError: "model_call_failed",
                Diagnostics: new ListProgress<AgenticModelDiagnostic>(diagnostics)));

        Assert.Equal(finalText, result.Text);
        Assert.Equal(1, chatClient.CallCount);
        Assert.DoesNotContain(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.MalformedResponse);
        Assert.DoesNotContain(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.Retry);
    }

    [Fact]
    public async Task AgenticFrameworkModelClient_ToolOnlyCall_RetriesWhenModelCallFails()
    {
        var chatClient = new CapturingChatClient(
            new InvalidOperationException("temporary model error"),
            "No updates needed.");
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
        var diagnostics = new List<AgenticModelDiagnostic>();

        var result = await client.RunToolOnlyAsync(
            new AgenticToolOnlyModelRequest(
                [new ChatMessage(ChatRole.User, "update profile")],
                OperationName: "CharacterProfileUpdate",
                ModelCallError: "model_call_failed",
                Diagnostics: new ListProgress<AgenticModelDiagnostic>(diagnostics)));

        Assert.Equal("No updates needed.", result.Text);
        Assert.Equal(2, chatClient.CallCount);
        Assert.Contains(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.ModelCallFailed);
        Assert.Contains(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.Retry);
        Assert.Contains(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.RetrySucceeded);
    }

    [Fact]
    public async Task AgenticFrameworkModelClient_RetriesWhenResponseContractIsInvalid()
    {
        var chatClient = new CapturingChatClient(
            """
            { "characters": null }
            """,
            """
            { "characters": [] }
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
        var diagnostics = new List<AgenticModelDiagnostic>();

        var result = await client.RunAsync<CharacterExtractionResponse>(
            new AgenticModelRequest<CharacterExtractionResponse>(
                [new ChatMessage(ChatRole.User, "extract")],
                InvalidContractError: "invalid_contract",
                ValidateResponse: response => response.Characters is null
                    ? AgenticModelValidationResult.Invalid("characters is required.")
                    : AgenticModelValidationResult.Valid,
                Diagnostics: new ListProgress<AgenticModelDiagnostic>(diagnostics)));

        Assert.Empty(result.Characters);
        Assert.Equal(2, chatClient.CallCount);
        Assert.Contains(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.InvalidContract);
        Assert.Contains(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.Retry);
        Assert.Contains(diagnostics, item => item.Kind == AgenticModelDiagnosticKind.RetrySucceeded);
    }

    [Fact]
    public async Task AgenticFrameworkModelClient_WhenResponseContractStaysInvalid_ThrowsContractError()
    {
        var chatClient = new CapturingChatClient(
            """
            { "characters": null }
            """,
            """
            { "characters": null }
            """,
            """
            { "characters": null }
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RunAsync<CharacterExtractionResponse>(
                new AgenticModelRequest<CharacterExtractionResponse>(
                    [new ChatMessage(ChatRole.User, "extract")],
                    InvalidContractError: "invalid_contract",
                    ValidateResponse: _ => AgenticModelValidationResult.Invalid("characters is required."))));

        Assert.Equal("invalid_contract", exception.Message);
        Assert.Equal(3, chatClient.CallCount);
    }

    [Fact]
    public async Task AgenticFrameworkModelClient_DeserializesCompactCharacterExtraction()
    {
        var chatClient = new CapturingChatClient(
            """
            {
              "characters": [
                {
                  "name": "John",
                  "gender": "unknown",
                  "aliases": [ "Johnny" ],
                  "pointers": [ "p1" ]
                }
              ]
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
            new AgenticModelRequest<CharacterExtractionResponse>(
                [new ChatMessage(ChatRole.User, "extract")],
                InvalidContractError: "invalid_contract"));

        var character = Assert.Single(result.Characters);
        Assert.Equal("John", character.Name);
        Assert.Equal("Johnny", Assert.Single(character.Aliases!));
        Assert.Equal("p1", Assert.Single(character.Pointers!));
    }


    [Fact]
    public async Task CharacterExtractionClient_WhenPointerIsMissing_FailsContractValidation()
    {
        var response = new CharacterExtractionResponse(
        [
            new ExtractedLocalCharacter("John", "unknown", ["Johnny"], [])
        ]);
        var client = new AgenticCharacterExtractionModelClient(
            new StubAgenticModelClient(response),
            NullLogger<AgenticCharacterExtractionModelClient>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExtractCharactersAsync(new CharacterExtractionModelRequest("system", "user")));

        Assert.Equal("character_extraction_response_contract_invalid", exception.Message);
    }

    [Fact]
    public async Task CharacterExtractionClient_WhenPointersAreValid_PassesContractValidation()
    {
        var response = new CharacterExtractionResponse(
        [
            new ExtractedLocalCharacter("John", "unknown", ["Johnny"], ["p1"])
        ]);
        var client = new AgenticCharacterExtractionModelClient(
            new StubAgenticModelClient(response),
            NullLogger<AgenticCharacterExtractionModelClient>.Instance);

        var result = await client.ExtractCharactersAsync(new CharacterExtractionModelRequest("system", "user"));

        var character = Assert.Single(result.Characters);
        Assert.Equal("p1", Assert.Single(character.Pointers!));
    }

    [Fact]
    public async Task CharacterExtractionClient_WhenPointersAreMissing_FailsContractValidation()
    {
        var response = new CharacterExtractionResponse(
        [
            new ExtractedLocalCharacter("John", "unknown", [], null)
        ]);
        var client = new AgenticCharacterExtractionModelClient(
            new StubAgenticModelClient(response),
            NullLogger<AgenticCharacterExtractionModelClient>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExtractCharactersAsync(new CharacterExtractionModelRequest("system", "user")));

        Assert.Equal("character_extraction_response_contract_invalid", exception.Message);
    }

    [Fact]
    public async Task CharacterExtractionClient_WhenPointerIsNull_FailsContractValidation()
    {
        var response = new CharacterExtractionResponse(
        [
            new ExtractedLocalCharacter("John", "unknown", [], [null!])
        ]);
        var client = new AgenticCharacterExtractionModelClient(
            new StubAgenticModelClient(response),
            NullLogger<AgenticCharacterExtractionModelClient>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExtractCharactersAsync(new CharacterExtractionModelRequest("system", "user")));

        Assert.Equal("character_extraction_response_contract_invalid", exception.Message);
    }

    private sealed class CapturingChatClient(params object[] responseScripts) : IChatClient
    {
        private readonly Queue<object> responses = new(responseScripts);

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
            var responseScript = responses.Count > 0
                ? responses.Dequeue()
                : throw new InvalidOperationException("No scripted chat response is available.");
            if (responseScript is Exception exception)
            {
                throw exception;
            }

            var responseText = Assert.IsType<string>(responseScript);
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
        public Task<AgenticModelCompletion> RunToolOnlyAsync(
            AgenticToolOnlyModelRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgenticModelCompletion(string.Empty));
        }

        public Task<TResponse> RunAsync<TResponse>(
            AgenticModelRequest<TResponse> request,
            CancellationToken cancellationToken = default)
            where TResponse : class
        {
            var typedResponse = (TResponse)(object)response;
            var validation = request.ValidateResponse?.Invoke(typedResponse) ?? AgenticModelValidationResult.Valid;
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(request.InvalidContractError);
            }

            return Task.FromResult(typedResponse);
        }
    }

    private sealed class ListProgress<T>(List<T> items) : IProgress<T>
    {
        public void Report(T value)
        {
            items.Add(value);
        }
    }
}

