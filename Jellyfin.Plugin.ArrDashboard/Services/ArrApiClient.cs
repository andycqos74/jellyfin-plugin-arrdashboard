using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDashboard.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArrDashboard.Services;

/// <summary>
/// Thrown when a Sonarr/Radarr call fails. Carries a message safe to show in the UI.
/// </summary>
public class ArrRequestException : Exception
{
    public ArrRequestException(string message)
        : base(message)
    {
    }

    public ArrRequestException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Minimal typed HTTP client for the Sonarr/Radarr v3 APIs.
/// </summary>
public class ArrApiClient
{
    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArrApiClient> _logger;

    public ArrApiClient(IHttpClientFactory httpClientFactory, ILogger<ArrApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Builds an absolute API URL from an instance base URL, honouring reverse-proxy sub-paths.
    /// </summary>
    public static string BuildUrl(string baseUrl, string path, IDictionary<string, string?>? query)
    {
        var root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        var builder = new StringBuilder(root);
        builder.Append("/api/v3/");
        builder.Append(path.TrimStart('/'));

        if (query is not null)
        {
            var first = true;
            foreach (var kvp in query)
            {
                if (kvp.Value is null)
                {
                    continue;
                }

                builder.Append(first ? '?' : '&');
                builder.Append(Uri.EscapeDataString(kvp.Key));
                builder.Append('=');
                builder.Append(Uri.EscapeDataString(kvp.Value));
                first = false;
            }
        }

        return builder.ToString();
    }

    public Task<T> GetAsync<T>(ArrInstance instance, string path, CancellationToken cancellationToken)
        => GetAsync<T>(instance, path, null, cancellationToken);

    public async Task<T> GetAsync<T>(
        ArrInstance instance,
        string path,
        IDictionary<string, string?>? query,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(instance.Url, path, query);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(instance, request, cancellationToken).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                var result = await JsonSerializer
                    .DeserializeAsync<T>(stream, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);

                if (result is null)
                {
                    throw new ArrRequestException("Empty response from " + instance.Name + ".");
                }

                return result;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "[ArrDashboard] Malformed JSON from {Name} at {Url}", instance.Name, url);
                throw new ArrRequestException(
                    instance.Name + " returned an unexpected response. Check that the base URL points at the app root.",
                    ex);
            }
        }
    }

    /// <summary>
    /// POSTs to /api/v3/command, e.g. { "name": "SeriesSearch", "seriesId": 12 }.
    /// </summary>
    public async Task PostCommandAsync(ArrInstance instance, object body, CancellationToken cancellationToken)
    {
        var url = BuildUrl(instance.Url, "command", null);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, _jsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        using var response = await SendAsync(instance, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        ArrInstance instance,
        string path,
        IDictionary<string, string?>? query,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(instance.Url, path, query);
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        using var response = await SendAsync(instance, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches a non-API asset (a MediaCover image) relative to the instance root.
    /// </summary>
    public async Task<AssetResult> GetAssetAsync(
        ArrInstance instance,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var root = instance.Url.Trim().TrimEnd('/');
        var url = root + "/" + relativePath.TrimStart('/');

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(instance, request, cancellationToken).ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        return new AssetResult(bytes, contentType);
    }

    private async Task<HttpResponseMessage> SendAsync(
        ArrInstance instance,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instance.Url))
        {
            throw new ArrRequestException("No URL configured for " + instance.Name + ".");
        }

        if (request.RequestUri is null || !request.RequestUri.IsAbsoluteUri)
        {
            throw new ArrRequestException(
                "Invalid URL configured for " + instance.Name + ". Include the scheme, e.g. http://host:8989");
        }

        request.Headers.TryAddWithoutValidation("X-Api-Key", instance.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var timeout = ArrDashboardPlugin.Instance?.Configuration.RequestTimeoutSeconds ?? 20;
        timeout = Math.Clamp(timeout, 3, 120);

        var client = _httpClientFactory.CreateClient(NamedClient.Default);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeout));

        HttpResponseMessage response;
        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ArrRequestException(
                instance.Name + " did not respond within " + timeout.ToString(CultureInfo.InvariantCulture) + "s.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[ArrDashboard] Connection to {Name} failed", instance.Name);
            throw new ArrRequestException("Could not reach " + instance.Name + ": " + ex.Message, ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = (int)response.StatusCode;
        response.Dispose();

        throw status switch
        {
            401 or 403 => new ArrRequestException(instance.Name + " rejected the API key (HTTP " + status + ")."),
            404 => new ArrRequestException(instance.Name + " returned 404. Check the base URL - do not include /api."),
            _ => new ArrRequestException(instance.Name + " returned HTTP " + status + ".")
        };
    }

    /// <summary>
    /// Bytes and content type of a proxied image.
    /// </summary>
    public sealed class AssetResult
    {
        public AssetResult(byte[] data, string contentType)
        {
            Data = data;
            ContentType = contentType;
        }

        public byte[] Data { get; }

        public string ContentType { get; }
    }
}
