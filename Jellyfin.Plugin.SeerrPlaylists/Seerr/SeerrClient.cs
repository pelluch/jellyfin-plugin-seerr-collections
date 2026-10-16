using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeerrPlaylists.Seerr.Models;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrPlaylists.Seerr;

public class SeerrClient
{
    private const int MaxAttempts = 3;
    private const int PageSize = 100;

    private static readonly TimeSpan _attemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SeerrClient> _logger;

    public SeerrClient(IHttpClientFactory httpClientFactory, ILogger<SeerrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<SeerrStatus> GetStatusAsync(SeerrConnection connection, CancellationToken cancellationToken)
        => GetAsync<SeerrStatus>(connection, "status", cancellationToken);

    public Task<SeerrPublicSettings> GetPublicSettingsAsync(SeerrConnection connection, CancellationToken cancellationToken)
        => GetAsync<SeerrPublicSettings>(connection, "settings/public", cancellationToken);

    public Task<SeerrJellyfinSettings> GetJellyfinSettingsAsync(SeerrConnection connection, CancellationToken cancellationToken)
        => GetAsync<SeerrJellyfinSettings>(connection, "settings/jellyfin", cancellationToken);

    public async Task<IReadOnlyList<SeerrRequest>> GetRequestsAsync(SeerrConnection connection, CancellationToken cancellationToken)
    {
        // Requests added while paging push older ones onto the next page, so the same id can arrive twice.
        var requests = new Dictionary<int, SeerrRequest>();
        for (var skip = 0; ; skip += PageSize)
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"request?filter=all&take={PageSize}&skip={skip}");
            var page = await GetAsync<SeerrRequestPage>(connection, path, cancellationToken).ConfigureAwait(false);
            foreach (var request in page.Results)
            {
                requests.TryAdd(request.Id, request);
            }

            if (page.Results.Count < PageSize || skip + PageSize >= page.PageInfo.Results)
            {
                return requests.Values.ToList();
            }
        }
    }

    private static SeerrException StatusError(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new SeerrException("Seerr rejected the API key."),
            HttpStatusCode.NotFound => new SeerrException("Seerr does not have this API endpoint. Check the URL."),
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError
                => new SeerrException($"Seerr answered {code} {response.ReasonPhrase}.", isTransient: true),
            _ => new SeerrException($"Seerr answered {code} {response.ReasonPhrase}."),
        };
    }

    private async Task<T> GetAsync<T>(SeerrConnection connection, string pathAndQuery, CancellationToken cancellationToken)
    {
        var uri = connection.ApiUri(pathAndQuery);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                var result = await GetOnceAsync<T>(uri, connection.ApiKey, cancellationToken).ConfigureAwait(false);
                if (Plugin.VerboseLogging)
                {
                    _logger.LogInformation("Seerr request {PathAndQuery} took {ElapsedMs} ms", uri.PathAndQuery, stopwatch.ElapsedMilliseconds);
                }

                return result;
            }
            catch (SeerrException e) when (e.IsTransient && attempt < MaxAttempts)
            {
                var delay = _retryDelay * attempt;
                _logger.LogWarning(
                    "Seerr request {Path} failed on attempt {Attempt} of {MaxAttempts}: {Error} Retrying in {Delay}.",
                    uri.AbsolutePath,
                    attempt,
                    MaxAttempts,
                    e.Message,
                    delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<T> GetOnceAsync<T>(Uri uri, string apiKey, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_attemptTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("X-Api-Key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        try
        {
            using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw StatusError(response);
            }

            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase))
            {
                throw new SeerrException($"{uri} did not answer like Seerr. Check the URL.");
            }

            return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, timeout.Token).ConfigureAwait(false)
                ?? throw new SeerrException("Seerr sent an empty response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SeerrException($"Seerr did not answer within {_attemptTimeout.TotalSeconds} seconds.", isTransient: true);
        }
        catch (HttpRequestException e)
        {
            throw new SeerrException($"Could not connect to Seerr: {e.Message}", isTransient: true, e);
        }
        catch (IOException e)
        {
            throw new SeerrException($"The connection to Seerr broke: {e.Message}", isTransient: true, e);
        }
        catch (JsonException e)
        {
            throw new SeerrException("Seerr sent a response this plugin cannot read.", innerException: e);
        }
    }
}
