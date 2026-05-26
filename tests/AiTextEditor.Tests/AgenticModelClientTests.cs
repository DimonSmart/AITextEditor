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
        Assert.Contains("```json", malformed.RawResponse, StringComparison.Ordinal);
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
                       && message.Text?.Contains("previous response was malformed", StringComparison.OrdinalIgnoreCase) == true);
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
    public async Task AgenticFrameworkModelClient_DeserializesCharacterExtractionEvidence()
    {
        var chatClient = new CapturingChatClient(
            """
            {
              "characters": [
                {
                  "canonicalName": "John",
                  "gender": "unknown",
                  "aliases": [
                    {
                      "form": "Johnny",
                      "evidence": {
                        "pointer": "p1",
                        "excerpt": "Johnny entered."
                      }
                    }
                  ],
                  "evidence": [
                    {
                      "pointer": "p1",
                      "excerpt": "Johnny entered."
                    }
                  ]
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
        Assert.Equal("John", character.CanonicalName);
        var evidence = Assert.Single(character.Evidence!);
        Assert.Equal("p1", evidence.Pointer);
        Assert.Equal("Johnny entered.", evidence.Excerpt);
    }


    [Fact]
    public async Task CharacterExtractionClient_WhenAliasEvidenceExcerptIsMissing_FailsContractValidation()
    {
        var response = new CharacterExtractionResponse
        {
            Characters =
            [
                new CharacterExtractionCharacter(
                    "John",
                    "unknown",
                    [new CharacterExtractionAlias("Johnny", new CharacterExtractionEvidence("p1", ""))],
                    [new CharacterExtractionEvidence("p1", "Johnny entered.")])
            ]
        };
        var client = new AgenticCharacterExtractionModelClient(
            new StubAgenticModelClient(response),
            NullLogger<AgenticCharacterExtractionModelClient>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExtractCharactersAsync(new CharacterExtractionModelRequest("system", "user")));

        Assert.Equal("character_extraction_response_contract_invalid", exception.Message);
    }

    [Fact]
    public async Task CharacterExtractionClient_WhenEvidenceIsValid_PassesContractValidation()
    {
        var response = new CharacterExtractionResponse
        {
            Characters =
            [
                new CharacterExtractionCharacter(
                    "John",
                    "unknown",
                    [new CharacterExtractionAlias("Johnny", new CharacterExtractionEvidence("p1", "Johnny entered."))],
                    [new CharacterExtractionEvidence("p1", "Johnny entered.")])
            ]
        };
        var client = new AgenticCharacterExtractionModelClient(
            new StubAgenticModelClient(response),
            NullLogger<AgenticCharacterExtractionModelClient>.Instance);

        var result = await client.ExtractCharactersAsync(new CharacterExtractionModelRequest("system", "user"));

        var character = Assert.Single(result.Characters);
        Assert.NotNull(character.Evidence);
        Assert.Equal("p1", Assert.Single(character.Evidence).Pointer);
    }

    [Fact]
    public async Task CharacterExtractionClient_WhenEvidenceIsMissing_FailsContractValidation()
    {
        var response = new CharacterExtractionResponse
        {
            Characters =
            [
                new CharacterExtractionCharacter(
                    "John",
                    "unknown",
                    [],
                    null)
            ]
        };
        var client = new AgenticCharacterExtractionModelClient(
            new StubAgenticModelClient(response),
            NullLogger<AgenticCharacterExtractionModelClient>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExtractCharactersAsync(new CharacterExtractionModelRequest("system", "user")));

        Assert.Equal("character_extraction_response_contract_invalid", exception.Message);
    }

    [Fact]
    public async Task CharacterExtractionClient_WhenEvidencePointerIsNull_FailsContractValidation()
    {
        var response = new CharacterExtractionResponse
        {
            Characters =
            [
                new CharacterExtractionCharacter(
                    "John",
                    "unknown",
                    [],
                    [new CharacterExtractionEvidence(null, "John entered.")])
            ]
        };
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
        public Task<TResponse> RunAsync<TResponse>(
            AgenticModelRequest<TResponse> request,
            CancellationToken cancellationToken = default)
            where TResponse : class
        {
            return Task.FromResult((TResponse)(object)response);
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

