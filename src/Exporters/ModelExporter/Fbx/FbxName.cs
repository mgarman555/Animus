namespace GameAssetExplorer.Exporters.ModelExporter.Fbx;

internal static class FbxName
{
    /// <summary>
    /// Picks the first non-blank of primary/fallback (else "submesh_{index}") and strips characters
    /// that would break an FBX object name or a downstream filename.
    /// </summary>
    public static string Sanitize(string? primary, string? fallback, int index)
    {
        var n = !string.IsNullOrWhiteSpace(primary) ? primary
              : !string.IsNullOrWhiteSpace(fallback) ? fallback : $"submesh_{index}";
        return n!.Replace(' ', '_').Replace('/', '_').Replace('\\', '_').Replace(':', '_').Replace('"', '_');
    }
}
