namespace DiskCleaner.Models;

public static class Format
{
    public static string Bytes(long value)
    {
        const long kb = 1024, mb = kb * 1024, gb = mb * 1024;
        if (value >= gb) return $"{value / (double)gb:F2} GB";
        if (value >= mb) return $"{value / (double)mb:F1} MB";
        if (value >= kb) return $"{value / (double)kb:F0} KB";
        return $"{value} B";
    }
}
