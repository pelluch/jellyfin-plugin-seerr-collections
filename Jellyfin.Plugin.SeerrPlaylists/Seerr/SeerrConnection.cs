using System;

namespace Jellyfin.Plugin.SeerrPlaylists.Seerr;

public sealed class SeerrConnection
{
    private SeerrConnection(Uri baseUrl, string apiKey)
    {
        BaseUrl = baseUrl;
        ApiKey = apiKey;
    }

    public Uri BaseUrl { get; }

    public string ApiKey { get; }

    public static SeerrConnection Create(string? url, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new SeerrException("The Seerr URL is not set.");
        }

        // The trailing slash keeps a URL base such as /seerr when resolving the API paths against it.
        if (!Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new SeerrException($"\"{url}\" is not an http or https URL.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SeerrException("The Seerr API key is not set.");
        }

        return new SeerrConnection(baseUrl, apiKey.Trim());
    }

    public Uri ApiUri(string pathAndQuery) => new(BaseUrl, "api/v1/" + pathAndQuery);
}
