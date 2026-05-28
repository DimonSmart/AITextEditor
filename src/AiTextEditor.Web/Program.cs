using AiTextEditor.Web.Components;
using AiTextEditor.Agent.CharacterBible.VectorSearch;
using AiTextEditor.Core.Infrastructure;
using AiTextEditor.Web.Services;
using MatBlazor;
using Microsoft.AspNetCore.SignalR;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);
const long BookEditorReceiveMessageLimit = 4 * 1024 * 1024;

// Application logs live beside %LOCALAPPDATA%\AITextEditor\settings.json:
// %LOCALAPPDATA%\AITextEditor\logs\aitexteditor.log
builder.Logging.AddProvider(new SimpleFileLoggerProvider(
    Path.Combine(AppLogPaths.GetLogRoot(), "aitexteditor.log"),
    minimumLevel: LogLevel.Information));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.Configure<HubOptions>(options =>
{
    options.MaximumReceiveMessageSize = BookEditorReceiveMessageLimit;
});
builder.Services.AddMatToaster();
builder.Services.AddMudServices();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IProgramSettingsStore, FileProgramSettingsStore>();
builder.Services.AddScoped<ICharacterBibleFileStore, CharacterBibleFileStore>();
builder.Services.AddScoped<EditorWorkspaceState>();
builder.Services.AddScoped<ICharacterBibleMarkdownRenderer, CharacterBibleMarkdownRenderer>();
builder.Services.AddScoped<ICharacterBibleOperationRunner, CharacterBibleOperationRunner>();
builder.Services.AddScoped<CharacterBibleOperationState>();
builder.Services.AddScoped<ICharacterBibleWorkflowClient, CharacterBibleWorkflowClient>();
builder.Services.AddScoped<ICharacterVectorSearchTool, ConfiguredCharacterVectorSearchTool>();
builder.Services.AddSingleton<CharacterBibleCommandParser>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

