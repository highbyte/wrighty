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
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(character);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (separatorPending && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(lower);
                separatorPending = false;
            }
            else
            {
                separatorPending = true;
            }

            if (builder.Length >= maximumLength)
            {
                break;
            }
        }

        var result = builder.ToString().Trim('-');
        return result.Length == 0 ? "item" : result;
    }

    public static string FileName(int id, string title) =>
        $"{id.ToString("D3", CultureInfo.InvariantCulture)}-{Slugify(title)}.md";
}
