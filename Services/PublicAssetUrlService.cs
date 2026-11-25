namespace MyQabayel.Admin.Services;

public sealed class PublicAssetUrlService(IConfiguration configuration)
{
    public string ToAbsolute(string value, HttpRequest request)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        var configuredBase = configuration["PublicBaseUrl"]?.TrimEnd('/');
        var baseUrl = !string.IsNullOrWhiteSpace(configuredBase)
            ? configuredBase
            : $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');

        return $"{baseUrl}/{value.TrimStart('/')}";
    }
}
