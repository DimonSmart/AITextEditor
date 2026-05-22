using System.Text.Json.Serialization;
using AiTextEditor.Core.Model;
using Microsoft.Extensions.AI;

namespace AiTextEditor.Agent;

public interface ICursorAgentModelClient
{
    Task<AgentCommand> GetNextCommandAsync(
        CursorAgentModelRequest request,
        CancellationToken cancellationToken = default);

    Task<FinalizerResponse> FinalizeAsync(
        CursorAgentFinalizerModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CursorAgentModelRequest(
    string AgentSystemPrompt,
    string TaskDefinitionPrompt,
    string EvidenceSnapshot,
    string BatchMessage);

public sealed record CursorAgentFinalizerModelRequest(
    string FinalizerSystemPrompt,
    string FinalizerUserMessage);

public sealed record AgentCommand(
    [property: JsonRequired] string Action,
    [property: JsonRequired] bool BatchFound,
    [property: JsonRequired] IReadOnlyList<EvidenceItem>? NewEvidence,
    [property: JsonRequired] string? Progress,
    [property: JsonRequired] bool NeedMoreContext);

public sealed record FinalizerResponse(
    [property: JsonRequired] string Decision,
    [property: JsonRequired] string? SemanticPointerFrom,
    [property: JsonRequired] string? Excerpt,
    [property: JsonRequired] string? WhyThis,
    [property: JsonRequired] string? Markdown,
    [property: JsonRequired] string? Summary);

public sealed class AgenticCursorAgentModelClient(IAgenticModelClient modelClient) : ICursorAgentModelClient
{
    private readonly IAgenticModelClient modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));

    public Task<AgentCommand> GetNextCommandAsync(
        CursorAgentModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetValidatedCommandAsync(request, cancellationToken);
    }

    private async Task<AgentCommand> GetValidatedCommandAsync(
        CursorAgentModelRequest request,
        CancellationToken cancellationToken)
    {
        var command = await modelClient.RunAsync<AgentCommand>(
            new AgenticModelRequest(
                [
                    new ChatMessage(ChatRole.System, request.AgentSystemPrompt),
                    new ChatMessage(ChatRole.User, request.TaskDefinitionPrompt),
                    new ChatMessage(ChatRole.User, request.EvidenceSnapshot),
                    new ChatMessage(ChatRole.User, request.BatchMessage)
                ],
                InvalidContractError: "cursor_agent_response_contract_invalid"),
            cancellationToken).ConfigureAwait(false);

        if (!IsValidCommand(command))
        {
            throw new InvalidOperationException("cursor_agent_response_contract_invalid");
        }

        return command;
    }

    public Task<FinalizerResponse> FinalizeAsync(
        CursorAgentFinalizerModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetValidatedFinalizerResponseAsync(request, cancellationToken);
    }

    private async Task<FinalizerResponse> GetValidatedFinalizerResponseAsync(
        CursorAgentFinalizerModelRequest request,
        CancellationToken cancellationToken)
    {
        var response = await modelClient.RunAsync<FinalizerResponse>(
            new AgenticModelRequest(
                [
                    new ChatMessage(ChatRole.System, request.FinalizerSystemPrompt),
                    new ChatMessage(ChatRole.User, request.FinalizerUserMessage)
                ],
                InvalidContractError: "cursor_agent_finalizer_response_contract_invalid"),
            cancellationToken).ConfigureAwait(false);

        if (!IsValidFinalizerResponse(response))
        {
            throw new InvalidOperationException("cursor_agent_finalizer_response_contract_invalid");
        }

        return response;
    }

    private static bool IsValidCommand(AgentCommand command)
    {
        if (!string.Equals(command.Action, "continue", StringComparison.Ordinal)
            && !string.Equals(command.Action, "stop", StringComparison.Ordinal))
        {
            return false;
        }

        if (command.NewEvidence is null)
        {
            return false;
        }

        return command.NewEvidence.All(evidence => !string.IsNullOrWhiteSpace(evidence.Pointer));
    }

    private static bool IsValidFinalizerResponse(FinalizerResponse response)
    {
        return string.Equals(response.Decision, "success", StringComparison.Ordinal)
               || string.Equals(response.Decision, "not_found", StringComparison.Ordinal);
    }
}
