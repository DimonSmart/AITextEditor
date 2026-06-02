using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Agent.CharacterBible.Patching;
using AiTextEditor.Agent.CharacterBible.Resolution;
using AiTextEditor.Agent.CharacterBible.VectorSearch;
using System.Net.Http.Headers;
using System.Text;

namespace AiTextEditor.Web.Services;

public sealed class CharacterBibleWorkflowClient : ICharacterBibleWorkflowClient
{
    private readonly ILoggerFactory loggerFactory;
    private readonly IProgramSettingsStore settingsStore;
    private readonly ICharacterVectorSearchTool characterVectorSearchTool;

    public CharacterBibleWorkflowClient(
        IProgramSettingsStore settingsStore,
        ILoggerFactory loggerFactory,
        ICharacterVectorSearchTool characterVectorSearchTool)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        this.characterVectorSearchTool = characterVectorSearchTool ?? throw new ArgumentNullException(nameof(characterVectorSearchTool));
    }

    public async Task<CharacterBibleWorkflowOutput> RunAsync(
        EditorWorkspaceState workspace,
        CharacterBibleWorkflowInput request,
        IProgress<CharacterBibleWorkflowProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);

        var settings = await settingsStore.LoadAsync(cancellationToken);
        var modelSettings = ProgramSettingsValidation.ValidateForWorkflow(settings);
        using var httpClient = CreateHttpClient(modelSettings);
        var modelClient = AgenticFrameworkModelClient.CreateOpenAiCompatible(
            httpClient,
            modelSettings.ModelName,
            modelSettings.Endpoint,
            modelSettings.ApiKey,
            loggerFactory,
            modelSettings.LlmRetryCount);

        var extractionClient = new AgenticCharacterExtractionModelClient(
            modelClient,
            loggerFactory.CreateLogger<AgenticCharacterExtractionModelClient>());
        var patchClient = new AgenticCharacterProfilePatchModelClient(
            modelClient,
            loggerFactory.CreateLogger<AgenticCharacterProfilePatchModelClient>());
        var identityResolverClient = new AgenticCharacterIdentityResolutionModelClient(
            modelClient,
            loggerFactory.CreateLogger<AgenticCharacterIdentityResolutionModelClient>());
        var splitCandidateClient = new AgenticSplitCandidateModelClient(
            modelClient,
            loggerFactory.CreateLogger<AgenticSplitCandidateModelClient>());
        var documentContext = new DocumentContext(workspace.CurrentDocument, workspace.CharacterDossiers);
        var generator = new CharacterDossiersGenerator(
            documentContext,
            workspace.CharacterDossiers,
            modelSettings.CharacterBibleLimits,
            loggerFactory.CreateLogger<CharacterDossiersGenerator>(),
            extractionClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new DossierPatchPromptBuilder(),
            identityResolverClient,
            characterVectorSearchTool,
            loggerFactory,
            new CharacterIdentityResolutionPromptBuilder(),
            splitCandidateClient,
            new SplitCandidatePromptBuilder());
        var runner = new CharacterBibleWorkflowRunner(generator, loggerFactory);

        return await runner.RunAsync(request, progress, cancellationToken);
    }

    private HttpClient CreateHttpClient(ValidatedAiConnectionSettings settings)
    {
        var handler = new HttpClientHandler();
        if (settings.IgnoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
        }

        HttpMessageHandler finalHandler = handler;
        if (!string.IsNullOrEmpty(settings.Password))
        {
            finalHandler = new BasicAuthHandler(settings.Username, settings.Password, finalHandler);
        }

        finalHandler = new LlmRequestLoggingHandler(
            loggerFactory.CreateLogger<LlmRequestLoggingHandler>(),
            finalHandler,
            settings.LogRequestBody);

        return new HttpClient(finalHandler)
        {
            Timeout = settings.Timeout
        };
    }

    private sealed class BasicAuthHandler : DelegatingHandler
    {
        private readonly string headerValue;

        public BasicAuthHandler(string user, string password, HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
            var bytes = Encoding.UTF8.GetBytes($"{user}:{password}");
            headerValue = Convert.ToBase64String(bytes);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", headerValue);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
