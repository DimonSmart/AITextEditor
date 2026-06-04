using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible;

namespace AiTextEditor.Web.Services;

public sealed class ProgramSettings
{
    public const string DefaultEmbeddingModelName = "bge-m3:latest";
    public const string DefaultCharacterBibleDossierLanguage = "Russian";

    public List<AiServerSettings> AiServers { get; set; } = [];

    public string LastBookPath { get; set; } = string.Empty;

    public string SelectedAiServerName { get; set; } = string.Empty;

    public string SelectedAiModelName { get; set; } = string.Empty;

    public string SelectedEmbeddingModelName { get; set; } = DefaultEmbeddingModelName;

    public CharacterBibleExtractionSettings CharacterBibleExtraction { get; set; } = new();

    public string CharacterBibleDossierLanguage { get; set; } = DefaultCharacterBibleDossierLanguage;

    public int LlmRetryCount { get; set; } = 5;

    public ProgramSettings Clone()
    {
        return new ProgramSettings
        {
            AiServers = [.. AiServers.Select(server => server.Clone())],
            LastBookPath = LastBookPath ?? string.Empty,
            SelectedAiServerName = SelectedAiServerName,
            SelectedAiModelName = SelectedAiModelName,
            SelectedEmbeddingModelName = string.IsNullOrWhiteSpace(SelectedEmbeddingModelName)
                ? DefaultEmbeddingModelName
                : SelectedEmbeddingModelName,
            CharacterBibleExtraction = CharacterBibleExtraction.Clone(),
            CharacterBibleDossierLanguage = string.IsNullOrWhiteSpace(CharacterBibleDossierLanguage)
                ? DefaultCharacterBibleDossierLanguage
                : CharacterBibleDossierLanguage.Trim(),
            LlmRetryCount = LlmRetryCount
        };
    }

    public static ProgramSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var defaultServer = new AiServerSettings
        {
            Name = "Default",
            BaseUrl = configuration["LLM_BASE_URL"] ?? string.Empty,
            ApiKey = configuration["LLM_API_KEY"] ?? string.Empty,
            Username = configuration["LLM_USERNAME"] ?? string.Empty,
            Password = configuration["LLM_PASSWORD"] ?? string.Empty,
            IgnoreSslErrors = IsTrue(configuration["LLM_IGNORE_SSL_ERRORS"]),
            LogRequestBody = IsTrue(configuration["LLM_LOG_BODY"]),
            TimeoutMinutes = ReadPositiveInt(configuration["LLM_TIMEOUT_MINUTES"], 20)
        };

        return new ProgramSettings
        {
            AiServers = HasServerConfiguration(defaultServer) ? [defaultServer] : [],
            SelectedAiServerName = HasServerConfiguration(defaultServer) ? defaultServer.Name : string.Empty,
            SelectedAiModelName = configuration["LLM_MODEL"] ?? string.Empty,
            SelectedEmbeddingModelName = ResolveEmbeddingModelName(configuration["EMBEDDING_MODEL"]),
            CharacterBibleDossierLanguage = ResolveCharacterBibleDossierLanguage(configuration["CHARACTER_BIBLE_DOSSIER_LANGUAGE"])
        };
    }

    private static bool HasServerConfiguration(AiServerSettings server)
    {
        return !string.IsNullOrWhiteSpace(server.BaseUrl) ||
            !string.IsNullOrWhiteSpace(server.ApiKey) ||
            !string.IsNullOrWhiteSpace(server.Username) ||
            !string.IsNullOrWhiteSpace(server.Password);
    }

    private static bool IsTrue(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadPositiveInt(string? value, int defaultValue)
    {
        if (int.TryParse(value, out var result) && result > 0)
        {
            return result;
        }

        return defaultValue;
    }

    private static string ResolveEmbeddingModelName(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? DefaultEmbeddingModelName
            : value.Trim();
    }

    private static string ResolveCharacterBibleDossierLanguage(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? DefaultCharacterBibleDossierLanguage
            : value.Trim();
    }
}

public sealed class AiServerSettings
{
    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool IgnoreSslErrors { get; set; }

    public bool LogRequestBody { get; set; }

    public int TimeoutMinutes { get; set; } = 20;

    public AiServerSettings Clone()
    {
        return new AiServerSettings
        {
            Name = Name,
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            Username = Username,
            Password = Password,
            IgnoreSslErrors = IgnoreSslErrors,
            LogRequestBody = LogRequestBody,
            TimeoutMinutes = TimeoutMinutes
        };
    }
}

public sealed class CharacterBibleExtractionSettings
{
    public int MaxParagraphsPerBatch { get; set; } = 20;

    public int MaxBatchBytes { get; set; } = 8000;

    public int OverlapParagraphs { get; set; } = 1;

    public int OverlapMaxBytes { get; set; }

    public int FullScanMaxItems { get; set; } = 100;

    public CharacterBibleExtractionSettings Clone()
    {
        return new CharacterBibleExtractionSettings
        {
            MaxParagraphsPerBatch = MaxParagraphsPerBatch,
            MaxBatchBytes = MaxBatchBytes,
            OverlapParagraphs = OverlapParagraphs,
            OverlapMaxBytes = OverlapMaxBytes,
            FullScanMaxItems = FullScanMaxItems
        };
    }

    public CharacterBibleExtractionLimits ToCharacterBibleExtractionLimits()
    {
        return new CharacterBibleExtractionLimits
        {
            MaxParagraphsPerBatch = MaxParagraphsPerBatch,
            MaxBatchBytes = MaxBatchBytes,
            OverlapParagraphs = OverlapParagraphs,
            OverlapMaxBytes = OverlapMaxBytes,
            FullScanMaxItems = FullScanMaxItems
        };
    }
}

public interface IProgramSettingsStore
{
    Task<ProgramSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ProgramSettings settings, CancellationToken cancellationToken);
}

public sealed class FileProgramSettingsStore : IProgramSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ProgramSettings initialSettings;
    private readonly string settingsPath;

    public FileProgramSettingsStore(IConfiguration configuration)
        : this(CreateDefaultSettingsPath(), ProgramSettings.FromConfiguration(configuration))
    {
    }

    public FileProgramSettingsStore(string settingsPath, ProgramSettings initialSettings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentNullException.ThrowIfNull(initialSettings);

        this.settingsPath = settingsPath;
        this.initialSettings = initialSettings.Clone();
    }

    public async Task<ProgramSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(settingsPath))
        {
            return initialSettings.Clone();
        }

        await using var stream = File.OpenRead(settingsPath);
        var settings = await JsonSerializer.DeserializeAsync<ProgramSettings>(
            stream,
            JsonOptions,
            cancellationToken);

        return settings ?? throw new InvalidOperationException($"Settings file '{settingsPath}' is empty.");
    }

    public async Task SaveAsync(ProgramSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"Settings path '{settingsPath}' has no directory.");
        }

        Directory.CreateDirectory(directory);
        await using var stream = File.Create(settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }

    private static string CreateDefaultSettingsPath()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("Local application data folder is unavailable.");
        }

        return Path.Combine(localApplicationData, "AITextEditor", "settings.json");
    }
}

public static class ProgramSettingsValidation
{
    private static readonly string[] SupportedCharacterBibleDossierLanguages =
    [
        "Russian",
        "English",
        "Spanish"
    ];

    public static IReadOnlyList<string> ValidateForSave(ProgramSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();
        var serverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in settings.AiServers)
        {
            if (string.IsNullOrWhiteSpace(server.Name))
            {
                errors.Add("AI server name is required.");
            }
            else if (!serverNames.Add(server.Name.Trim()))
            {
                errors.Add($"AI server name '{server.Name.Trim()}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(server.BaseUrl))
            {
                errors.Add($"AI server '{DisplayServerName(server)}' Base URL is required.");
            }
            else if (!Uri.TryCreate(server.BaseUrl, UriKind.Absolute, out _))
            {
                errors.Add($"AI server '{DisplayServerName(server)}' Base URL must be an absolute URI.");
            }

            if (string.IsNullOrWhiteSpace(server.ApiKey))
            {
                errors.Add($"AI server '{DisplayServerName(server)}' API key is required.");
            }

            if (server.TimeoutMinutes <= 0)
            {
                errors.Add($"AI server '{DisplayServerName(server)}' timeout must be greater than zero.");
            }
        }

        var extraction = settings.CharacterBibleExtraction;
        if (extraction.MaxParagraphsPerBatch <= 0)
        {
            errors.Add("Character bible max paragraphs per batch must be greater than zero.");
        }

        if (extraction.MaxBatchBytes <= 0)
        {
            errors.Add("Character bible max batch bytes must be greater than zero.");
        }

        if (extraction.OverlapParagraphs < 0)
        {
            errors.Add("Character bible overlap paragraphs cannot be negative.");
        }

        if (extraction.OverlapMaxBytes < 0)
        {
            errors.Add("Character bible overlap max bytes cannot be negative.");
        }

        if (extraction.FullScanMaxItems <= 0)
        {
            errors.Add("Character bible full-scan item limit must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(settings.CharacterBibleDossierLanguage))
        {
            errors.Add("Character bible dossier language is required.");
        }
        else if (!SupportedCharacterBibleDossierLanguages.Contains(
            settings.CharacterBibleDossierLanguage.Trim(),
            StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"Character bible dossier language must be one of: {string.Join(", ", SupportedCharacterBibleDossierLanguages)}.");
        }

        if (settings.LlmRetryCount <= 0)
        {
            errors.Add("LLM retry count must be greater than zero.");
        }

        if (!string.IsNullOrWhiteSpace(settings.SelectedAiServerName) &&
            !settings.AiServers.Any(server => string.Equals(
                server.Name.Trim(),
                settings.SelectedAiServerName.Trim(),
                StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"Selected AI server '{settings.SelectedAiServerName.Trim()}' does not exist.");
        }

        return errors;
    }

    public static ValidatedAiConnectionSettings ValidateForWorkflow(ProgramSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.SelectedAiServerName))
        {
            missing.Add("selected AI server");
        }

        if (string.IsNullOrWhiteSpace(settings.SelectedAiModelName))
        {
            missing.Add("AI model");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Missing AI configuration: {string.Join(", ", missing)}.");
        }

        var server = settings.AiServers.FirstOrDefault(server => string.Equals(
            server.Name.Trim(),
            settings.SelectedAiServerName.Trim(),
            StringComparison.OrdinalIgnoreCase));
        if (server is null)
        {
            throw new InvalidOperationException($"Selected AI server '{settings.SelectedAiServerName.Trim()}' does not exist.");
        }

        var saveErrors = ValidateForSave(settings);
        if (saveErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", saveErrors));
        }

        return new ValidatedAiConnectionSettings(
            NormalizeOpenAiEndpoint(server.BaseUrl),
            server.ApiKey.Trim(),
            settings.SelectedAiModelName.Trim(),
            server.Username.Trim(),
            server.Password,
            server.IgnoreSslErrors,
            server.LogRequestBody,
            TimeSpan.FromMinutes(server.TimeoutMinutes),
            settings.CharacterBibleExtraction.ToCharacterBibleExtractionLimits(),
            settings.CharacterBibleDossierLanguage.Trim(),
            settings.LlmRetryCount);
    }

    public static ValidatedCharacterVectorEmbeddingSettings ValidateForCharacterVectorSearch(ProgramSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.SelectedAiServerName))
        {
            throw new InvalidOperationException("Missing AI configuration: selected AI server.");
        }

        var server = settings.AiServers.FirstOrDefault(server => string.Equals(
            server.Name.Trim(),
            settings.SelectedAiServerName.Trim(),
            StringComparison.OrdinalIgnoreCase));
        if (server is null)
        {
            throw new InvalidOperationException($"Selected AI server '{settings.SelectedAiServerName.Trim()}' does not exist.");
        }

        var saveErrors = ValidateForSave(settings);
        if (saveErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", saveErrors));
        }

        return new ValidatedCharacterVectorEmbeddingSettings(
            NormalizeOllamaEndpoint(server.BaseUrl),
            server.ApiKey.Trim(),
            string.IsNullOrWhiteSpace(settings.SelectedEmbeddingModelName)
                ? ProgramSettings.DefaultEmbeddingModelName
                : settings.SelectedEmbeddingModelName.Trim(),
            server.Username.Trim(),
            server.Password,
            server.IgnoreSslErrors,
            server.LogRequestBody,
            TimeSpan.FromMinutes(server.TimeoutMinutes));
    }

    private static string DisplayServerName(AiServerSettings server)
    {
        return string.IsNullOrWhiteSpace(server.Name) ? "<unnamed>" : server.Name.Trim();
    }

    private static Uri NormalizeOpenAiEndpoint(string baseUrl)
    {
        var endpoint = baseUrl.Trim().TrimEnd('/');
        if (!endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            endpoint += "/v1";
        }

        return new Uri(endpoint, UriKind.Absolute);
    }

    private static Uri NormalizeOllamaEndpoint(string baseUrl)
    {
        var endpoint = baseUrl.Trim().TrimEnd('/');
        if (endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = endpoint[..^3].TrimEnd('/');
        }

        return new Uri(endpoint, UriKind.Absolute);
    }
}

public sealed record ValidatedAiConnectionSettings(
    Uri Endpoint,
    string ApiKey,
    string ModelName,
    string Username,
    string Password,
    bool IgnoreSslErrors,
    bool LogRequestBody,
    TimeSpan Timeout,
    CharacterBibleExtractionLimits CharacterBibleLimits,
    string CharacterBibleDossierLanguage,
    int LlmRetryCount);

public sealed record ValidatedCharacterVectorEmbeddingSettings(
    Uri Endpoint,
    string ApiKey,
    string EmbeddingModelName,
    string Username,
    string Password,
    bool IgnoreSslErrors,
    bool LogRequestBody,
    TimeSpan Timeout);
