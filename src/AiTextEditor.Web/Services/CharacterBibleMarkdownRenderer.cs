using System.Text;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Web.Services;

public interface ICharacterBibleMarkdownRenderer
{
    string Render(CharacterDossiers dossiers);
}

public sealed class CharacterBibleMarkdownRenderer : ICharacterBibleMarkdownRenderer
{
    public string Render(CharacterDossiers dossiers)
    {
        ArgumentNullException.ThrowIfNull(dossiers);

        var builder = new StringBuilder();
        builder.AppendLine("# Character Bible");
        builder.AppendLine();

        foreach (var dossier in dossiers.Characters.OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase))
        {
            AppendDossier(builder, dossier);
        }

        return builder.ToString();
    }

    private static void AppendDossier(StringBuilder builder, CharacterDossier dossier)
    {
        builder.Append("## ");
        builder.AppendLine(NormalizeMarkdownLine(dossier.Name));
        builder.AppendLine();
        builder.Append("**Gender:** ");
        builder.AppendLine(string.IsNullOrWhiteSpace(dossier.Gender) ? "unknown" : dossier.Gender.Trim());
        builder.AppendLine();
        builder.AppendLine("### Description");
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(dossier.Description) ? "No description." : dossier.Description.Trim());
        builder.AppendLine();
        var profile = CharacterProfile.Normalize(dossier.Profile);

        AppendTextSection(builder, "Appearance", profile.Appearance);
        AppendTextSection(builder, "Background, status and education", profile.BackgroundStatusEducation);
        AppendTextSection(builder, "Psychological profile", profile.PsychologicalProfile);
        AppendTextSection(builder, "Speech and communication", profile.SpeechAndCommunication);

        builder.AppendLine("### Key role bonds");
        builder.AppendLine();

        if (profile.KeyRoleBonds is not { Count: > 0 })
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var bond in profile.KeyRoleBonds)
            {
                builder.Append("- ");
                builder.Append(NormalizeMarkdownLine(bond.CharacterName));
                builder.Append(" — ");
                builder.Append(NormalizeMarkdownLine(bond.Role));
                builder.Append(": ");
                builder.AppendLine(NormalizeMarkdownLine(bond.Description));
            }
        }

        builder.AppendLine();
        builder.AppendLine("### Aliases");
        builder.AppendLine();

        if (dossier.AliasExamples.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var alias in dossier.AliasExamples.OrderBy(alias => alias.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("- ");
                builder.Append(NormalizeMarkdownLine(alias.Key));
                builder.Append(": ");
                builder.AppendLine(NormalizeMarkdownLine(alias.Value));
            }
        }

        builder.AppendLine();
        builder.AppendLine("### Additional facts");
        builder.AppendLine();

        if (dossier.Facts.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var fact in dossier.Facts.OrderBy(fact => fact.Key, StringComparer.OrdinalIgnoreCase).ThenBy(fact => fact.Value, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("- ");
                builder.Append(NormalizeMarkdownLine(fact.Key));
                builder.Append(": ");
                builder.AppendLine(NormalizeMarkdownLine(fact.Value));
                builder.Append("  Evidence: ");
                builder.AppendLine(NormalizeMarkdownLine(fact.Example));
            }
        }

        builder.AppendLine();
    }

    private static void AppendTextSection(StringBuilder builder, string title, string? value)
    {
        builder.Append("### ");
        builder.AppendLine(title);
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(value) ? "Not specified." : value.Trim());
        builder.AppendLine();
    }

    private static string NormalizeMarkdownLine(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.ReplaceLineEndings(" ").Trim();
    }
}
