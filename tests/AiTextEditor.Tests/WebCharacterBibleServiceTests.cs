using AiTextEditor.Agent;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using AiTextEditor.Web.Services;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class WebCharacterBibleServiceTests
{
    [Fact]
    public void EditorWorkspaceState_LoadMarkdown_AllowsEmptyText()
    {
        var workspace = new EditorWorkspaceState();

        var document = workspace.LoadMarkdown(string.Empty);

        Assert.Empty(workspace.CurrentMarkdown);
        Assert.Empty(document.Items);
        Assert.Empty(workspace.CurrentDocument.Items);
    }

    [Fact]
    public void EditorWorkspaceState_LoadUploadedBook_LoadsContentWithoutDiskPath()
    {
        var workspace = new EditorWorkspaceState();

        var document = workspace.LoadUploadedBook("# Uploaded\n\nText.", "uploaded.md");

        Assert.Equal("# Uploaded\n\nText.", workspace.CurrentMarkdown);
        Assert.Equal("uploaded", document.Id);
        Assert.Equal("uploaded", workspace.CurrentDocument.Id);
        Assert.Null(workspace.CurrentBookPath);
        Assert.Null(workspace.CurrentCharacterBiblePath);
        Assert.False(workspace.CurrentCharacterBibleLoadedFromFile);
    }

    [Fact]
    public void MarkdownPreviewHtmlRenderer_ReturnsEmptyHtmlForEmptyMarkdown()
    {
        var html = MarkdownPreviewHtmlRenderer.Render(string.Empty);

        Assert.Empty(html);
    }

    [Fact]
    public void MarkdownPreviewHtmlRenderer_RendersNeznaykaSample()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownBooks", "neznayka_sample.md");
        var markdown = File.ReadAllText(path);

        var html = MarkdownPreviewHtmlRenderer.Render(markdown);

        Assert.Contains("<h1", html, StringComparison.Ordinal);
        Assert.Contains("Часть I", html, StringComparison.Ordinal);
        Assert.Contains("Как Знайка победил профессора Звёздочкина", html, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterBibleMarkdownRenderer_RendersDossierProjection()
    {
        var renderer = new CharacterBibleMarkdownRenderer();
        var dossiers = new CharacterDossiers(
            "d1",
            1,
            [
                new CharacterDossier(
                    "c1",
                    "Alice",
                    "Keeps careful notes.",
                    ["Al"],
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Al"] = "Al opened the notebook."
                    },
                    [new CharacterFact("role", "editor", "Alice revised the chapter.")],
                    "female")
            ]);

        var markdown = renderer.Render(dossiers);

        Assert.Contains("# Character Bible", markdown, StringComparison.Ordinal);
        Assert.Contains("## Alice", markdown, StringComparison.Ordinal);
        Assert.Contains("**Gender:** female", markdown, StringComparison.Ordinal);
        Assert.Contains("- Al: Al opened the notebook.", markdown, StringComparison.Ordinal);
        Assert.Contains("- role: editor", markdown, StringComparison.Ordinal);
        Assert.Contains("Evidence: Alice revised the chapter.", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Generate character bible")]
    [InlineData("Create character bible")]
    [InlineData("Создай каталог досье персонажей книги")]
    [InlineData("Составь библию персонажей")]
    public void CommandParser_MapsGenerateCommandsToFullRun(string command)
    {
        var result = new CharacterBibleCommandParser().Parse(command);

        Assert.True(result.Success);
        Assert.NotNull(result.Request);
        Assert.Null(result.Request!.ChangedPointers);
    }

    [Fact]
    public void CommandParser_MapsRefreshPointers()
    {
        var result = new CharacterBibleCommandParser().Parse("Refresh character bible for 1.2.p4, 1.2p5");

        Assert.True(result.Success);
        Assert.NotNull(result.Request);
        Assert.Equal(["1.2.p4", "1.2.p5"], result.Request!.ChangedPointers);
    }

    [Fact]
    public void CommandParser_RejectsUnsupportedCommand()
    {
        var result = new CharacterBibleCommandParser().Parse("Tell me about Alice");

        Assert.False(result.Success);
        Assert.Null(result.Request);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void CharacterBibleFileStore_GetCompanionPath_UsesJsonExtension()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AiTextEditorTests", Guid.NewGuid().ToString("N"));
        var bookPath = Path.Combine(directory, "novel.md");
        var store = new CharacterBibleFileStore();

        var characterBiblePath = store.GetCompanionPath(bookPath);

        Assert.Equal(Path.Combine(directory, "novel-character-bible.json"), characterBiblePath);
    }

    [Fact]
    public async Task CharacterBibleFileStore_SavesAndLoadsCompanionJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AiTextEditorTests", Guid.NewGuid().ToString("N"));
        var bookPath = Path.Combine(directory, "novel.md");
        var store = new CharacterBibleFileStore();
        var characterBiblePath = store.GetCompanionPath(bookPath);
        var source = new CharacterDossierService("d1");
        source.UpsertDossier(new CharacterDossier(
            "c1",
            "Alice",
            "Keeps careful notes.",
            ["Al"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Al"] = "Al opened the notebook."
            },
            [new CharacterFact("role", "editor", "Alice revised the chapter.")],
            "female"));

        await store.SaveAsync(characterBiblePath, source, CancellationToken.None);
        var target = new CharacterDossierService("empty");
        var loaded = await store.LoadAsync(characterBiblePath, target, CancellationToken.None);

        Assert.True(loaded);
        Assert.Equal(Path.Combine(directory, "novel-character-bible.json"), characterBiblePath);
        Assert.Contains("\"dossiersId\":\"d1\"", await File.ReadAllTextAsync(characterBiblePath));
        var dossier = Assert.Single(target.GetDossiers().Characters);
        Assert.Equal("Alice", dossier.Name);
        Assert.Equal("female", dossier.Gender);
        Assert.Equal(["Al"], dossier.Aliases);
        Assert.Equal("Al opened the notebook.", dossier.AliasExamples["Al"]);
        var fact = Assert.Single(dossier.Facts);
        Assert.Equal("role", fact.Key);
        Assert.Equal("editor", fact.Value);
        Assert.Equal("Alice revised the chapter.", fact.Example);
    }

    [Fact]
    public async Task CharacterBibleFileStore_LoadsLegacyMarkdownYamlCompanionWhenJsonMissing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AiTextEditorTests", Guid.NewGuid().ToString("N"));
        var bookPath = Path.Combine(directory, "novel.md");
        var store = new CharacterBibleFileStore();
        var characterBiblePath = store.GetCompanionPath(bookPath);
        var legacyCharacterBiblePath = Path.Combine(directory, "novel-character-bible.md");
        var source = new CharacterDossierService("d1");
        source.UpsertDossier(new CharacterDossier(
            "c1",
            "Alice",
            "Keeps careful notes.",
            ["Al"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Al"] = "Al opened the notebook."
            },
            [new CharacterFact("role", "editor", "Alice revised the chapter.")],
            "female"));

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            legacyCharacterBiblePath,
            $"""
            # Character Bible

            <!-- ai-text-editor-character-dossiers:start -->
            ```yaml
            {source.SaveToYaml().TrimEnd()}
            ```
            <!-- ai-text-editor-character-dossiers:end -->

            ## Markdown projection
            """);

        var target = new CharacterDossierService("empty");
        var loaded = await store.LoadAsync(characterBiblePath, target, CancellationToken.None);

        Assert.True(loaded);
        Assert.False(File.Exists(characterBiblePath));
        var dossier = Assert.Single(target.GetDossiers().Characters);
        Assert.Equal("Alice", dossier.Name);
        Assert.Equal("female", dossier.Gender);
        Assert.Equal(["Al"], dossier.Aliases);
    }

    [Fact]
    public void CharacterDossierService_SaveToJson_LoadFromJson_RoundTrips()
    {
        var source = new CharacterDossierService("d1");
        source.UpsertDossier(new CharacterDossier(
            "c1",
            "Alice",
            "Keeps careful notes.",
            ["unused"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Al"] = "Al opened the notebook.",
                ["alice"] = "alice checked the facts."
            },
            [
                new CharacterFact("role", "editor", "Alice revised the chapter."),
                new CharacterFact("role", "Editor", "Duplicate by key and value should normalize away."),
                new CharacterFact("empty", "", "ignored")
            ],
            "Female"));

        var json = source.SaveToJson();
        var target = new CharacterDossierService("empty");
        target.LoadFromJson(json);

        var dossier = Assert.Single(target.GetDossiers().Characters);
        Assert.Equal("Alice", dossier.Name);
        Assert.Equal("female", dossier.Gender);
        Assert.Equal(["Al", "alice"], dossier.Aliases);
        Assert.Equal("Al opened the notebook.", dossier.AliasExamples["Al"]);
        Assert.Equal("alice checked the facts.", dossier.AliasExamples["alice"]);
        var fact = Assert.Single(dossier.Facts);
        Assert.Equal("role", fact.Key);
        Assert.Equal("editor", fact.Value);
        Assert.Equal("Alice revised the chapter.", fact.Example);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("NOTE KEEPER")]
    [InlineData("EDITOR")]
    public void CharacterDossierSearch_FiltersByNameAliasAndFactValueCaseInsensitively(string query)
    {
        var dossiers = new[]
        {
            new CharacterDossier(
                "c1",
                "Alice",
                "Keeps careful notes.",
                ["Note Keeper"],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Note Keeper"] = "Al opened the notebook."
                },
                [new CharacterFact("role", "editor", "Alice revised the chapter.")],
                "female"),
            new CharacterDossier(
                "c2",
                "Bob",
                "Tracks deadlines.",
                [],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                [new CharacterFact("role", "planner", "Bob updated the outline.")],
                "male")
        };

        var result = CharacterDossierSearch.Filter(dossiers, query);

        var dossier = Assert.Single(result);
        Assert.Equal("Alice", dossier.Name);
    }

    [Fact]
    public async Task ProgramSettingsStore_LoadsInitialSettingsWhenFileIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), "AiTextEditorTests", Guid.NewGuid().ToString("N"), "settings.json");
        var initialSettings = new ProgramSettings
        {
            AiServers =
            [
                new AiServerSettings
                {
                    Name = "Local",
                    BaseUrl = "http://localhost:11434",
                    ApiKey = "ollama",
                    TimeoutMinutes = 15
                }
            ],
            SelectedAiServerName = "Local",
            SelectedAiModelName = "qwen3:latest"
        };
        var store = new FileProgramSettingsStore(path, initialSettings);

        var settings = await store.LoadAsync(CancellationToken.None);

        var server = Assert.Single(settings.AiServers);
        Assert.Equal("Local", server.Name);
        Assert.Equal("http://localhost:11434", server.BaseUrl);
        Assert.Equal("ollama", server.ApiKey);
        Assert.Equal(15, server.TimeoutMinutes);
        Assert.Equal("Local", settings.SelectedAiServerName);
        Assert.Equal("qwen3:latest", settings.SelectedAiModelName);
    }

    [Fact]
    public async Task ProgramSettingsStore_SavesAndLoadsSettingsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "AiTextEditorTests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new FileProgramSettingsStore(path, new ProgramSettings());
        var savedSettings = new ProgramSettings
        {
            AiServers =
            [
                new AiServerSettings
                {
                    Name = "Remote",
                    BaseUrl = "https://example.test",
                    ApiKey = "token",
                    Username = "user",
                    Password = "password",
                    IgnoreSslErrors = true,
                    LogRequestBody = true,
                    TimeoutMinutes = 7
                }
            ],
            SelectedAiServerName = "Remote",
            SelectedAiModelName = "model-a"
        };

        await store.SaveAsync(savedSettings, CancellationToken.None);
        var loadedSettings = await store.LoadAsync(CancellationToken.None);

        var server = Assert.Single(loadedSettings.AiServers);
        Assert.Equal("Remote", server.Name);
        Assert.Equal("https://example.test", server.BaseUrl);
        Assert.Equal("token", server.ApiKey);
        Assert.Equal("user", server.Username);
        Assert.Equal("password", server.Password);
        Assert.True(server.IgnoreSslErrors);
        Assert.True(server.LogRequestBody);
        Assert.Equal(7, server.TimeoutMinutes);
        Assert.Equal("Remote", loadedSettings.SelectedAiServerName);
        Assert.Equal("model-a", loadedSettings.SelectedAiModelName);
    }

    [Fact]
    public void ProgramSettingsValidation_NormalizesOpenAiEndpoint()
    {
        var settings = new ProgramSettings
        {
            AiServers =
            [
                new AiServerSettings
                {
                    Name = "Local",
                    BaseUrl = "http://localhost:11434",
                    ApiKey = "ollama",
                    TimeoutMinutes = 30
                }
            ],
            SelectedAiServerName = "Local",
            SelectedAiModelName = "qwen3:latest"
        };

        var validated = ProgramSettingsValidation.ValidateForWorkflow(settings);

        Assert.Equal(new Uri("http://localhost:11434/v1"), validated.Endpoint);
        Assert.Equal("qwen3:latest", validated.ModelName);
        Assert.Equal(TimeSpan.FromMinutes(30), validated.Timeout);
    }

    [Fact]
    public void ProgramSettingsValidation_ReportsMissingWorkflowSettings()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ProgramSettingsValidation.ValidateForWorkflow(new ProgramSettings()));

        Assert.Equal("Missing AI configuration: selected AI server, AI model.", exception.Message);
    }

    [Fact]
    public void ProgramSettingsValidation_ReportsMissingSelectedServer()
    {
        var settings = new ProgramSettings
        {
            AiServers =
            [
                new AiServerSettings
                {
                    Name = "Local",
                    BaseUrl = "http://localhost:11434",
                    ApiKey = "ollama"
                }
            ],
            SelectedAiServerName = "Remote",
            SelectedAiModelName = "qwen3:latest"
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProgramSettingsValidation.ValidateForWorkflow(settings));

        Assert.Equal("Selected AI server 'Remote' does not exist.", exception.Message);
    }

    [Fact]
    public async Task OperationRunner_EmitsStartedAndCompleted()
    {
        var output = CreateOutput("generated");
        var workspace = new EditorWorkspaceState();
        var runner = new CharacterBibleOperationRunner(
            workspace,
            new FakeWorkflowClient(output));

        var events = await CollectAsync(runner.RunAsync(
            new CharacterBibleOperationRequest("Generate character bible", null),
            CancellationToken.None));

        Assert.Equal(CharacterBibleOperationEventType.Started, events[0].Type);
        Assert.Contains(events, item => item.Type == CharacterBibleOperationEventType.Completed && ReferenceEquals(output, item.Output));
        var dossier = Assert.Single(workspace.CharacterDossiers.GetDossiers().Characters);
        Assert.Equal("Alice", dossier.Name);
    }

    [Fact]
    public async Task OperationRunner_ForwardsWorkflowProgressEvents()
    {
        var output = CreateOutput("generated");
        var runner = new CharacterBibleOperationRunner(
            new EditorWorkspaceState(),
            new FakeWorkflowClient(
                output,
                [
                    new CharacterBibleWorkflowProgress("collect", "Collected 25 paragraphs for character bible processing."),
                    new CharacterBibleWorkflowProgress("extract", "Batch 1 produced 13 character candidates."),
                    new CharacterBibleWorkflowProgress("commit", "Character bible generated: 11 dossiers, version 2.")
                ]));

        var events = await CollectAsync(runner.RunAsync(
            new CharacterBibleOperationRequest("Generate character bible", null),
            CancellationToken.None));

        var progressMessages = events
            .Where(item => item.Type == CharacterBibleOperationEventType.Progress)
            .Select(item => item.Message)
            .ToArray();

        Assert.Contains(progressMessages, message => message.StartsWith("Current document loaded:", StringComparison.Ordinal));
        Assert.Contains("Collected 25 paragraphs for character bible processing.", progressMessages);
        Assert.Contains("Batch 1 produced 13 character candidates.", progressMessages);
        Assert.Contains("Character bible generated: 11 dossiers, version 2.", progressMessages);
    }

    [Fact]
    public async Task OperationRunner_EmitsFailedForWorkflowException()
    {
        var runner = new CharacterBibleOperationRunner(
            new EditorWorkspaceState(),
            new FakeWorkflowClient(new InvalidOperationException("missing config")));

        var events = await CollectAsync(runner.RunAsync(
            new CharacterBibleOperationRequest("Generate character bible", null),
            CancellationToken.None));

        var failed = Assert.Single(events, item => item.Type == CharacterBibleOperationEventType.Failed);
        Assert.Equal("missing config", failed.Message);
        Assert.IsType<InvalidOperationException>(failed.Error);
    }

    private static CharacterBibleWorkflowOutput CreateOutput(string status)
    {
        var dossiers = new CharacterDossiers(
            "d1",
            1,
            [
                new CharacterDossier(
                    "c1",
                    "Alice",
                    "Keeps careful notes.",
                    ["Al"],
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Al"] = "Al opened the notebook."
                    },
                    [],
                    "female")
            ]);
        return new CharacterBibleWorkflowOutput(dossiers, status, 0, 1, 0, 0, 0, []);
    }

    private static async Task<List<CharacterBibleOperationEvent>> CollectAsync(
        IAsyncEnumerable<CharacterBibleOperationEvent> events)
    {
        var items = new List<CharacterBibleOperationEvent>();
        await foreach (var item in events)
        {
            items.Add(item);
        }

        return items;
    }

    private sealed class FakeWorkflowClient : ICharacterBibleWorkflowClient
    {
        private readonly CharacterBibleWorkflowOutput? output;
        private readonly Exception? exception;
        private readonly IReadOnlyList<CharacterBibleWorkflowProgress> progressEvents;

        public FakeWorkflowClient(
            CharacterBibleWorkflowOutput output,
            IReadOnlyList<CharacterBibleWorkflowProgress>? progressEvents = null)
        {
            this.output = output;
            this.progressEvents = progressEvents ?? [];
        }

        public FakeWorkflowClient(Exception exception)
        {
            this.exception = exception;
            progressEvents = [];
        }

        public Task<CharacterBibleWorkflowOutput> RunAsync(
            EditorWorkspaceState workspace,
            CharacterBibleWorkflowInput request,
            IProgress<CharacterBibleWorkflowProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (exception is not null)
            {
                throw exception;
            }

            foreach (var progressEvent in progressEvents)
            {
                progress?.Report(progressEvent);
            }

            return Task.FromResult(output ?? throw new InvalidOperationException("missing fake output"));
        }
    }
}
