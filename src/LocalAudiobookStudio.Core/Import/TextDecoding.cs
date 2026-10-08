using System.Globalization;
using System.Text;

namespace LocalAudiobookStudio.Core.Import;

/// <summary>Reads text files as UTF-8 (with or without BOM), falling back to the system code page.</summary>
public static class TextDecoding
{
    static TextDecoding()
    {
        // Makes legacy Windows code pages (1252 etc.) available on .NET.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string ReadFile(string path) => Decode(File.ReadAllBytes(path));

    public static string Decode(byte[] bytes)
    {
        // Byte order marks.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return SystemCodePage().GetString(bytes);
        }
    }

    private static Encoding SystemCodePage()
    {
        try
        {
            var cp = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            if (cp > 0 && cp != 65001) return Encoding.GetEncoding(cp);
        }
        catch
        {
            // Fall through to Windows-1252, the most common code page for older text files.
        }
        try { return Encoding.GetEncoding(1252); } catch { return Encoding.Latin1; }
    }
}
