using System.Globalization;
using System.Text;

namespace Highbyte.Wrighty.LocalMarkdown;

public static class PortableFilenameSlugger
{
    public static string Slugify(string title, int maximumLength = 80)
    {
        var normalized = title.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        var separatorPending = false;
        foreach (var character in normalized)
        {
            separatorPending = AppendCharacter(builder, character, separatorPending);

            if (builder.Length >= maximumLength)
            {
                break;
            }
        }

        var result = builder.ToString().Trim('-');
        return result.Length == 0 ? "item" : result;
    }

    private static bool AppendCharacter(
        StringBuilder builder,
        char character,
        bool separatorPending)
    {
        if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
        {
            return separatorPending;
        }

        var lower = char.ToLowerInvariant(character);
        if (lower is not (>= 'a' and <= 'z' or >= '0' and <= '9'))
        {
            return true;
        }

        if (separatorPending && builder.Length > 0)
        {
            builder.Append('-');
        }

        builder.Append(lower);
        return false;
    }

    public static string FileName(int id, string title) =>
        $"{id.ToString("D3", CultureInfo.InvariantCulture)}-{Slugify(title)}.md";
}
