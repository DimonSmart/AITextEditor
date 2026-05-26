using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using AiTextEditor.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
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
                    ["Al"],
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Al"] = "Al opened the notebook."
                    },
                    "female",
                    Profile: FullProfile())
            ]);

        var markdown = renderer.Render(dossiers);

        Assert.Contains("# Character Bible", markdown, StringComparison.Ordinal);
        Assert.Contains("## Alice", markdown, StringComparison.Ordinal);
        Assert.Contains("**Gender:** female", markdown, StringComparison.Ordinal);
        Assert.Contains("### Appearance", markdown, StringComparison.Ordinal);
        Assert.Contains("Thin silhouette and silver hair.", markdown, StringComparison.Ordinal);
        Assert.Contains("### Status and competence", markdown, StringComparison.Ordinal);
        Assert.Contains("### Psychological profile", markdown, StringComparison.Ordinal);
        Assert.Contains("### Speech and communication", markdown, StringComparison.Ordinal);
        Assert.Contains("- Al: Al opened the notebook.", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("### Description", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("### Key role bonds", markdown, StringComparison.Ordinal);
        Assert.True(markdown.IndexOf("### Appearance", StringComparison.Ordinal) < markdown.IndexOf("### Status and competence", StringComparison.Ordinal));
        Assert.True(markdown.IndexOf("### Status and competence", StringComparison.Ordinal) < markdown.IndexOf("### Psychological profile", StringComparison.Ordinal));
        Assert.True(markdown.IndexOf("### Psychological profile", StringComparison.Ordinal) < markdown.IndexOf("### Speech and communication", StringComparison.Ordinal));
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
            ["Al"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Al"] = "Al opened the notebook."
            },
            "female"));

        await store.SaveAsync(characterBiblePath, source, CancellationToken.None);
        var target = new CharacterDossierService("empty");
        var loaded = await store.LoadAsync(characterBiblePath, target, CancellationToken.None);

        Assert.True(loaded);
        Assert.Equal(Path.Combine(directory, "novel-character-bible.json"), characterBiblePath);
        Assert.Contains("\"dossiersId\": \"d1\"", await File.ReadAllTextAsync(characterBiblePath));
        var dossier = Assert.Single(target.GetDossiers().Characters);
        Assert.Equal("Alice", dossier.Name);
        Assert.Equal("female", dossier.Gender);
        Assert.Equal(["Al"], dossier.Aliases);
        Assert.Equal("Al opened the notebook.", dossier.AliasExamples["Al"]);
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
            ["Al"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Al"] = "Al opened the notebook."
            },
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
    public async Task EditorWorkspaceState_LoadBookAsync_LoadsBookAndDerivedCharacterBible()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AiTextEditorTests", Guid.NewGuid().ToString("N"));
        var bookPath = Path.Combine(directory, "novel.md");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(bookPath, "# Novel\n\nText.");
        var characterBiblePath = Path.Combine(directory, "novel-character-bible.json");
        var characterDossiers = new CharacterDossierService("d1");
        characterDossiers.UpsertDossier(new CharacterDossier(
            "c1",
            "Alice",
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "female"));
        await File.WriteAllTextAsync(characterBiblePath, characterDossiers.SaveToJson());
        var workspace = new EditorWorkspaceState();

        await workspace.LoadBookAsync(bookPath);

        Assert.Equal("# Novel\n\nText.", workspace.CurrentMarkdown);
        Assert.Equal("novel", workspace.CurrentDocument.Id);
        Assert.Equal(bookPath, workspace.CurrentBookPath);
        Assert.Equal(characterBiblePath, workspace.CurrentCharacterBiblePath);
        Assert.True(workspace.CurrentCharacterBibleLoadedFromFile);
        var dossier = Assert.Single(workspace.CharacterDossiers.GetDossiers().Characters);
        Assert.Equal("Alice", dossier.Name);
    }

    [Fact]
    public async Task EditorWorkspaceState_SaveCharacterBibleAsync_SavesDerivedCompanionPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AiTextEditorTests", Guid.NewGuid().ToString("N"));
        var bookPath = Path.Combine(directory, "novel.md");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(bookPath, "# Novel\n\nText.");
        var workspace = new EditorWorkspaceState();
        await workspace.LoadBookAsync(bookPath);
        workspace.CharacterDossiers.UpsertDossier(new CharacterDossier(
            "c1",
            "Alice",
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "female"));

        await workspace.SaveCharacterBibleAsync();

        var characterBiblePath = Path.Combine(directory, "novel-character-bible.json");
        Assert.Equal(characterBiblePath, workspace.CurrentCharacterBiblePath);
        Assert.True(File.Exists(characterBiblePath));
        Assert.Contains("\"name\": \"Alice\"", await File.ReadAllTextAsync(characterBiblePath));
    }

    [Fact]
    public void EditorWorkspaceState_AutomationLeaseBlocksManualMutations()
    {
        var workspace = new EditorWorkspaceState();
        using var lease = workspace.BeginAutomation();

        Assert.True(workspace.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => workspace.LoadMarkdown("# Updated"));
        Assert.Throws<InvalidOperationException>(() => workspace.UpsertCharacterDossier(new CharacterDossier(
            "c1",
            "Alice",
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "female")));
    }

    [Fact]
    public void CharacterDossierService_SaveToJson_LoadFromJson_RoundTrips()
    {
        var source = new CharacterDossierService("d1");
        source.UpsertDossier(new CharacterDossier(
            "c1",
            "Alice",
            ["unused"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Al"] = "Al opened the notebook.",
                ["alice"] = "alice checked the facts."
            },
            "Female",
            7,
            FullProfile()));

        var json = source.SaveToJson();
        var target = new CharacterDossierService("empty");
        target.LoadFromJson(json);

        Assert.Contains("\"importanceLevel\": 7", json, StringComparison.Ordinal);
        Assert.Contains("\"profile\":", json, StringComparison.Ordinal);
        var dossier = Assert.Single(target.GetDossiers().Characters);
        Assert.Equal("Alice", dossier.Name);
        Assert.Equal("female", dossier.Gender);
        Assert.Equal(7, dossier.ImportanceLevel);
        Assert.NotNull(dossier.Profile);
        Assert.Equal("Thin silhouette and silver hair.", dossier.Profile.Appearance);
        Assert.Equal("Archivist with formal training.", dossier.Profile.StatusAndCompetence);
        Assert.Equal("Careful under pressure.", dossier.Profile.PsychologicalProfile);
        Assert.Equal("Dry, precise questions.", dossier.Profile.SpeechAndCommunication);
        Assert.Equal(["Al", "alice"], dossier.Aliases);
        Assert.Equal("Al opened the notebook.", dossier.AliasExamples["Al"]);
        Assert.Equal("alice checked the facts.", dossier.AliasExamples["alice"]);
    }

    [Fact]
    public void CharacterDossierService_LoadFromJsonWithoutImportanceLevel_SetsNull()
    {
        var service = new CharacterDossierService("empty");

        service.LoadFromJson(
            """
            {
              "dossiersId": "d1",
              "version": 1,
              "characters": [
                {
                  "characterId": "c1",
                  "name": "Alice",
                  "aliases": [],
                  "aliasExamples": {},
                  "facts": [],
                  "gender": "female"
                }
              ]
            }
            """);

        var dossier = Assert.Single(service.GetDossiers().Characters);
        Assert.Null(dossier.ImportanceLevel);
    }

    [Fact]
    public void CharacterDossierService_LoadFromJsonWithoutProfile_SetsEmptyProfile()
    {
        var service = new CharacterDossierService("empty");

        service.LoadFromJson(
            """
            {
              "dossiersId": "d1",
              "version": 1,
              "characters": [
                {
                  "characterId": "c1",
                  "name": "Alice",
                  "aliases": [],
                  "aliasExamples": {},
                  "facts": [],
                  "gender": "female"
                }
              ]
            }
            """);

        var dossier = Assert.Single(service.GetDossiers().Characters);
        Assert.NotNull(dossier.Profile);
        Assert.Equal(CharacterProfile.Empty, dossier.Profile);
    }

    [Fact]
    public void CharacterDossierService_NormalizesProfile()
    {
        var service = new CharacterDossierService("d1");
        service.UpsertDossier(new CharacterDossier(
            "c1",
            "Alice",
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "unknown",
            Profile: new CharacterProfile(
                "  silver hair  ",
                "  archivist  ",
                "  cautious  ",
                "  clipped speech  ")));

        var dossier = Assert.Single(service.GetDossiers().Characters);

        Assert.NotNull(dossier.Profile);
        Assert.Equal("silver hair", dossier.Profile.Appearance);
        Assert.Equal("archivist", dossier.Profile.StatusAndCompetence);
        Assert.Equal("cautious", dossier.Profile.PsychologicalProfile);
        Assert.Equal("clipped speech", dossier.Profile.SpeechAndCommunication);
    }

    [Fact]
    public void CharacterDossierService_LoadFromYamlWithoutImportanceLevel_SetsNull()
    {
        var service = new CharacterDossierService("empty");

        service.LoadFromYaml(
            """
            dossiersId: d1
            version: 1
            characters:
            - characterId: c1
              name: Alice
              aliases: []
              aliasExamples: {}
              gender: female
            """);

        var dossier = Assert.Single(service.GetDossiers().Characters);
        Assert.Null(dossier.ImportanceLevel);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(-1, null)]
    [InlineData(0, null)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(11, 10)]
    public void CharacterDossierService_NormalizesImportanceLevel(int? input, int? expected)
    {
        var service = new CharacterDossierService("d1");
        service.UpsertDossier(new CharacterDossier(
            "c1",
            "Alice",
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "unknown",
            input));

        var dossier = Assert.Single(service.GetDossiers().Characters);
        Assert.Equal(expected, dossier.ImportanceLevel);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("NOTE KEEPER")]
    public void CharacterDossierSearch_FiltersByNameAndAliasCaseInsensitively(string query)
    {
        var dossiers = new[]
        {
            new CharacterDossier(
                "c1",
                "Alice",
                ["Note Keeper"],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Note Keeper"] = "Al opened the notebook."
                },
                "female"),
            new CharacterDossier(
                "c2",
                "Bob",
                [],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                "male")
        };

        var result = CharacterDossierSearch.Filter(dossiers, query);

        var dossier = Assert.Single(result);
        Assert.Equal("Alice", dossier.Name);
    }

    [Theory]
    [InlineData("silver hair")]
    [InlineData("Archivist")]
    [InlineData("CAREFUL UNDER PRESSURE")]
    [InlineData("precise questions")]
    public void CharacterDossierSearch_FiltersByProfile(string query)
    {
        var dossiers = new[]
        {
            new CharacterDossier(
                "c1",
                "Alice",
                [],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                "female",
                Profile: FullProfile()),
            new CharacterDossier(
                "c2",
                "Charlie",
                [],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                "unknown")
        };

        var result = CharacterDossierSearch.Filter(dossiers, query);

        var dossier = Assert.Single(result);
        Assert.Equal("Alice", dossier.Name);
    }

    [Fact]
    public void CharacterDossierSearch_FiltersByGenderAndIncompleteState()
    {
        var dossiers = new[]
        {
            new CharacterDossier(
                "c1",
                "Alice",
                ["Al"],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                "female"),
            new CharacterDossier(
                "c2",
                "Bob",
                [],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                "male")
        };

        var result = CharacterDossierSearch.Filter(dossiers, null, "male", onlyIncomplete: true);

        var dossier = Assert.Single(result);
        Assert.Equal("Bob", dossier.Name);
        Assert.True(CharacterDossierSearch.IsIncomplete(dossier));
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
            LastBookPath = @"C:\Books\novel.md",
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
        Assert.Equal(@"C:\Books\novel.md", settings.LastBookPath);
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
            LastBookPath = @"C:\Books\novel.md",
            SelectedAiServerName = "Remote",
            SelectedAiModelName = "model-a",
            CharacterBibleExtraction = new CharacterBibleExtractionSettings
            {
                MaxParagraphsPerBatch = 12,
                MaxBatchBytes = 4096,
                OverlapParagraphs = 2,
                OverlapMaxBytes = 2048,
                FullScanMaxItems = 250
            }
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
        Assert.Equal(@"C:\Books\novel.md", loadedSettings.LastBookPath);
        Assert.Equal("Remote", loadedSettings.SelectedAiServerName);
        Assert.Equal("model-a", loadedSettings.SelectedAiModelName);
        Assert.Equal(12, loadedSettings.CharacterBibleExtraction.MaxParagraphsPerBatch);
        Assert.Equal(4096, loadedSettings.CharacterBibleExtraction.MaxBatchBytes);
        Assert.Equal(2, loadedSettings.CharacterBibleExtraction.OverlapParagraphs);
        Assert.Equal(2048, loadedSettings.CharacterBibleExtraction.OverlapMaxBytes);
        Assert.Equal(250, loadedSettings.CharacterBibleExtraction.FullScanMaxItems);
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
        Assert.Equal(20, validated.CharacterBibleLimits.MaxParagraphsPerBatch);
        Assert.Equal(8000, validated.CharacterBibleLimits.MaxBatchBytes);
        Assert.Equal(1, validated.CharacterBibleLimits.OverlapParagraphs);
        Assert.Equal(0, validated.CharacterBibleLimits.OverlapMaxBytes);
        Assert.Equal(100, validated.CharacterBibleLimits.FullScanMaxItems);
    }

    [Fact]
    public void ProgramSettingsValidation_RejectsInvalidCharacterBibleExtractionSettings()
    {
        var settings = new ProgramSettings
        {
            CharacterBibleExtraction = new CharacterBibleExtractionSettings
            {
                MaxParagraphsPerBatch = 2,
                MaxBatchBytes = 8000,
                OverlapParagraphs = 2,
                OverlapMaxBytes = -1,
                FullScanMaxItems = 100
            }
        };

        var errors = ProgramSettingsValidation.ValidateForSave(settings);

        Assert.Contains("Character bible overlap max bytes cannot be negative.", errors);
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
    public async Task OperationRunner_SavesGeneratedCharacterBibleWhenBookPathIsKnown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AiTextEditorTests", Guid.NewGuid().ToString("N"));
        var bookPath = Path.Combine(directory, "novel.md");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(bookPath, "# Novel\n\nText.");
        var output = CreateOutput("generated");
        var workspace = new EditorWorkspaceState();
        await workspace.LoadBookAsync(bookPath);
        var runner = new CharacterBibleOperationRunner(
            workspace,
            new FakeWorkflowClient(output));

        var events = await CollectAsync(runner.RunAsync(
            new CharacterBibleOperationRequest("Generate character bible", null),
            CancellationToken.None));

        var characterBiblePath = Path.Combine(directory, "novel-character-bible.json");
        var completed = Assert.Single(events, item => item.Type == CharacterBibleOperationEventType.Completed);
        Assert.Contains(characterBiblePath, completed.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(characterBiblePath));
        Assert.Contains("\"name\": \"Alice\"", await File.ReadAllTextAsync(characterBiblePath));
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
                    new CharacterBibleWorkflowProgress("extract", "Model response parse error.", "raw model response", "Copy raw response", IsError: true),
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
        var diagnosticEvent = Assert.Single(events, item => item.CopyText == "raw model response");
        Assert.Equal("Copy raw response", diagnosticEvent.CopyLabel);
        Assert.True(diagnosticEvent.IsError);
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

    [Fact]
    public async Task OperationState_StartsRunWithoutAwaitingCompletion()
    {
        var output = CreateOutput("generated");
        var releaseWorkflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspace = new EditorWorkspaceState();
        var runner = new CharacterBibleOperationRunner(
            workspace,
            new BlockingWorkflowClient(releaseWorkflow.Task, output));
        using var state = new CharacterBibleOperationState(
            runner,
            NullLogger<CharacterBibleOperationState>.Instance);

        await state.StartAsync(new CharacterBibleOperationRequest("Generate character bible", null));
        await WaitUntilAsync(() => state.IsRunning && workspace.IsReadOnly);

        Assert.True(state.IsRunning);
        Assert.True(workspace.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => workspace.LoadMarkdown("# Blocked"));

        releaseWorkflow.SetResult();
        await WaitUntilAsync(() => state.GetRuns().Single().Status == CharacterBibleOperationStatus.Completed);

        Assert.False(state.IsRunning);
        Assert.False(workspace.IsReadOnly);
        Assert.Contains(
            state.GetRuns().Single().Events,
            item => item.Type == CharacterBibleOperationEventType.Completed && ReferenceEquals(output, item.Output));
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
                    ["Al"],
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Al"] = "Al opened the notebook."
                    },
                    "female")
            ]);
        return new CharacterBibleWorkflowOutput(dossiers, status, 0, 1, 0, 0, 0, []);
    }

    private static CharacterProfile FullProfile()
    {
        return new CharacterProfile(
            "Thin silhouette and silver hair.",
            "Archivist with formal training.",
            "Careful under pressure.",
            "Dry, precise questions.");
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
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

    private sealed class BlockingWorkflowClient : ICharacterBibleWorkflowClient
    {
        private readonly Task releaseWorkflow;
        private readonly CharacterBibleWorkflowOutput output;

        public BlockingWorkflowClient(Task releaseWorkflow, CharacterBibleWorkflowOutput output)
        {
            this.releaseWorkflow = releaseWorkflow;
            this.output = output;
        }

        public async Task<CharacterBibleWorkflowOutput> RunAsync(
            EditorWorkspaceState workspace,
            CharacterBibleWorkflowInput request,
            IProgress<CharacterBibleWorkflowProgress>? progress,
            CancellationToken cancellationToken)
        {
            await releaseWorkflow.WaitAsync(cancellationToken);
            return output;
        }
    }
}

