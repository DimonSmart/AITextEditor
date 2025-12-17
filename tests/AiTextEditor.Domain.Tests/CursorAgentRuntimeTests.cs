using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace AiTextEditor.Domain.Tests;

public class CursorAgentRuntimeTests
{
    [Fact]
    public async Task RunAsync_ResumesAfterPointerWithoutRepeating()
    {
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("# Title\n\nFirst paragraph.\n\nSecond paragraph.");
        var documentContext = new DocumentContext(document);

        var firstPointer = document.Items[0].Pointer.ToCompactString();
        var secondPointer = document.Items[1].Pointer.ToCompactString();

        var firstRunChat = new FakeChatCompletionService(new[]
        {
            $"""{{"decision":"continue","newEvidence":[{{"pointer":"{firstPointer}","excerpt":"First","reason":"step1"}}],"progress":"scanned"}}""",
            $"""{{"decision":"success","semanticPointerFrom":"{firstPointer}","excerpt":"First","whyThis":"first","markdown":"First","summary":"first summary"}}"""
        });

        var runtime = new CursorAgentRuntime(documentContext, documentContext.TargetSetContext, firstRunChat, NullLogger<CursorAgentRuntime>.Instance);
        var request = new CursorAgentRequest(new CursorParameters(1, 4096, true), "Find matches", MaxSteps: 1);

        var firstResult = await runtime.RunAsync(request, cancellationToken: CancellationToken.None);

        Assert.Equal(firstPointer, firstResult.NextAfterPointer);
        Assert.NotNull(firstResult.State);
        Assert.Single(firstResult.State!.Evidence);

        var secondRunChat = new FakeChatCompletionService(new[]
        {
            $"""{{"decision":"done","newEvidence":[{{"pointer":"{secondPointer}","excerpt":"Second","reason":"step2"}}],"progress":"found"}}""",
            $"""{{"decision":"success","semanticPointerFrom":"{secondPointer}","excerpt":"Second","whyThis":"second","markdown":"Second","summary":"second summary"}}"""
        });

        var resumeRuntime = new CursorAgentRuntime(documentContext, documentContext.TargetSetContext, secondRunChat, NullLogger<CursorAgentRuntime>.Instance);
        var resumeRequest = new CursorAgentRequest(
            new CursorParameters(1, 4096, true),
            "Find matches",
            MaxSteps: 2,
            State: firstResult.State,
            StartAfterPointer: firstResult.NextAfterPointer);

        var secondResult = await resumeRuntime.RunAsync(resumeRequest, cancellationToken: CancellationToken.None);

        Assert.Equal(secondPointer, secondResult.NextAfterPointer);
        Assert.True(secondResult.CursorComplete);
        Assert.Equal(2, secondResult.State?.Evidence.Count);
    }

    private sealed class FakeChatCompletionService : IChatCompletionService
    {
        private readonly Queue<string> responses;

        public FakeChatCompletionService(IEnumerable<string> responses)
        {
            this.responses = new Queue<string>(responses);
        }

        public string ModelId => "fake";

        public IReadOnlyDictionary<string, object?> Attributes => new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            if (responses.Count == 0)
            {
                throw new InvalidOperationException("No more fake responses available.");
            }

            var content = responses.Dequeue();
            var message = new ChatMessageContent(AuthorRole.Assistant, content, ModelId, metadata: new Dictionary<string, object?>());
            return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new List<ChatMessageContent> { message });
        }

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            throw new System.NotImplementedException();
        }
    }
}
