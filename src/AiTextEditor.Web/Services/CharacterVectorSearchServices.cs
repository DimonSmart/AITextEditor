using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible.VectorSearch;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Web.Services;

internal sealed class ConfiguredCharacterVectorSearchTool : ICharacterVectorSearchTool, IDisposable
{
    private readonly IProgramSettingsStore settingsStore;
    private readonly ILoggerFactory loggerFactory;
    private readonly SemaphoreSlim configurationLock = new(1, 1);
    private readonly List<IDisposable> ownedEmbeddingClients = [];
    private CharacterVectorSearchTool? currentTool;
    private string currentConfigurationKey = string.Empty;
    private bool disposed;

    public ConfiguredCharacterVectorSearchTool(
        IProgramSettingsStore settingsStore,
        ILoggerFactory loggerFactory)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public async Task<IReadOnlyList<CharacterVectorSearchHit>> SearchAsync(
        CharacterDossiers dossiers,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dossiers);
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }

        var tool = await GetCurrentToolAsync(cancellationToken).ConfigureAwait(false);
        return await tool.SearchAsync(dossiers, query, limit, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        foreach (var embeddingClient in ownedEmbeddingClients)
        {
            embeddingClient.Dispose();
        }

        configurationLock.Dispose();
        disposed = true;
    }

    private async Task<CharacterVectorSearchTool> GetCurrentToolAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var embeddingSettings = ProgramSettingsValidation.ValidateForCharacterVectorSearch(settings);
        var configurationKey = BuildConfigurationKey(embeddingSettings);

        if (currentTool is not null &&
            string.Equals(currentConfigurationKey, configurationKey, StringComparison.Ordinal))
        {
            return currentTool;
        }

        await configurationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (currentTool is not null &&
                string.Equals(currentConfigurationKey, configurationKey, StringComparison.Ordinal))
            {
                return currentTool;
            }

            var embeddingClient = new OllamaCharacterVectorEmbeddingClient(
                embeddingSettings,
                loggerFactory.CreateLogger<OllamaCharacterVectorEmbeddingClient>(),
                loggerFactory.CreateLogger<LlmRequestLoggingHandler>());

            var nextTool = new CharacterVectorSearchTool(
                embeddingClient,
                CharacterVectorSearchOptions.CreateDefault(embeddingSettings.EmbeddingModelName));

            ownedEmbeddingClients.Add(embeddingClient);
            currentTool = nextTool;
            currentConfigurationKey = configurationKey;

            return currentTool;
        }
        finally
        {
            configurationLock.Release();
        }
    }

    private static string BuildConfigurationKey(ValidatedCharacterVectorEmbeddingSettings settings)
    {
        return string.Join(
            "\n",
            settings.Endpoint,
            settings.EmbeddingModelName,
            settings.Username,
            settings.Password,
            settings.ApiKey,
            settings.IgnoreSslErrors,
            settings.LogRequestBody,
            settings.Timeout);
    }
}

internal sealed class OllamaCharacterVectorEmbeddingClient : ICharacterVectorEmbeddingClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient;
    private readonly string embeddingModelName;
    private readonly ILogger<OllamaCharacterVectorEmbeddingClient> logger;
    private bool disposed;

    public OllamaCharacterVectorEmbeddingClient(
        ValidatedCharacterVectorEmbeddingSettings settings,
        ILogger<OllamaCharacterVectorEmbeddingClient> logger,
        ILogger<LlmRequestLoggingHandler> requestLogger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(requestLogger);

        embeddingModelName = settings.EmbeddingModelName;

        var handler = new HttpClientHandler();
        if (settings.IgnoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        httpClient = new HttpClient(
            new LlmRequestLoggingHandler(requestLogger, handler, settings.LogRequestBody))
        {
            BaseAddress = settings.Endpoint,
            Timeout = settings.Timeout
        };

        if (!string.IsNullOrEmpty(settings.Password))
        {
            var user = settings.Username ?? string.Empty;
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{settings.Password}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", value);
        }
        else if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }
    }

    public async Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var payload = new OllamaEmbedRequest(embeddingModelName, [text]);
        using var response = await httpClient.PostAsJsonAsync(
            "api/embed",
            payload,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var message = $"Ollama embedding request failed with status {(int)response.StatusCode}.";
            logger.LogError(
                "Ollama embedding request failed for model {EmbeddingModel}: {StatusCode}",
                embeddingModelName,
                response.StatusCode);
            throw new InvalidOperationException(message);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ReadFirstEmbedding(document.RootElement);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        httpClient.Dispose();
        disposed = true;
    }

    private static ReadOnlyMemory<float> ReadFirstEmbedding(JsonElement root)
    {
        if (!root.TryGetProperty("embeddings", out var embeddings) ||
            embeddings.ValueKind != JsonValueKind.Array ||
            embeddings.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Ollama embedding response does not contain embeddings.");
        }

        var first = embeddings[0];
        if (first.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Ollama embedding response contains an invalid embedding vector.");
        }

        var vector = new float[first.GetArrayLength()];
        var index = 0;
        foreach (var value in first.EnumerateArray())
        {
            vector[index] = value.GetSingle();
            index++;
        }

        return vector;
    }

    private sealed record OllamaEmbedRequest(
        string Model,
        IReadOnlyList<string> Input);
}
