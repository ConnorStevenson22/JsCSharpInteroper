namespace Interoper;

/// <summary>Helpers for the generated registry's import map (design §3.5).</summary>
public static class JsImportMap
{
    /// <summary>
    /// <paramref name="root"/> with a trailing slash. Browsers drop an import map address that isn't a URL or doesn't start with
    /// <c>/</c>, <c>./</c> or <c>../</c>, without an error, so a root like <c>_content/Lib</c> throws here instead.
    /// </summary>
    public static string Root(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!root.StartsWith('/') && !root.StartsWith("./", StringComparison.Ordinal) && !root.StartsWith("../", StringComparison.Ordinal) &&
            !Uri.TryCreate(root, UriKind.Absolute, out _))
        {
            throw new ArgumentException($"The import map root `{root}` must start with /, ./ or ../, or be a URL; browsers ignore any other address.", nameof(root));
        }
        return root.EndsWith('/') ? root : root + "/";
    }
}
