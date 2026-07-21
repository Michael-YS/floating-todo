using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace FloatingTodo;

public sealed class CalDavClient
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace CalendarServer = "http://calendarserver.org/ns/";

    public async Task<IReadOnlyList<DiscoveredCalendar>> DiscoverAsync(
        AccountRecord account,
        string password,
        CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateBaseUri(account.BaseUrl);
        using var client = CreateClient(account.Username, password);
        var principalDocument = await PropFindAsync(client, baseUri, 0, """
            <d:propfind xmlns:d="DAV:">
              <d:prop><d:current-user-principal /></d:prop>
            </d:propfind>
            """, cancellationToken);
        var principalHref = principalDocument.Descendants(Dav + "current-user-principal").Elements(Dav + "href").FirstOrDefault()?.Value
                            ?? throw new CalDavException("服务器未返回 current-user-principal");
        var principalUri = Resolve(baseUri, principalHref);

        var homeDocument = await PropFindAsync(client, principalUri, 0, """
            <d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><c:calendar-home-set /></d:prop>
            </d:propfind>
            """, cancellationToken);
        var homeHref = homeDocument.Descendants(CalDav + "calendar-home-set").Elements(Dav + "href").FirstOrDefault()?.Value
                       ?? throw new CalDavException("服务器未返回 calendar-home-set");
        var homeUri = Resolve(baseUri, homeHref);

        var calendarsDocument = await PropFindAsync(client, homeUri, 1, """
            <d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:cs="http://calendarserver.org/ns/">
              <d:prop>
                <d:displayname />
                <d:resourcetype />
                <c:supported-calendar-component-set />
                <cs:getctag />
                <d:sync-token />
              </d:prop>
            </d:propfind>
            """, cancellationToken);

        var result = new List<DiscoveredCalendar>();
        foreach (var response in calendarsDocument.Descendants(Dav + "response"))
        {
            var resourceType = response.Descendants(Dav + "resourcetype").FirstOrDefault();
            if (resourceType?.Element(CalDav + "calendar") is null)
            {
                continue;
            }

            var components = response.Descendants(CalDav + "comp")
                .Select(element => element.Attribute("name")?.Value)
                .Where(value => value is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (components.Count > 0 && !components.Contains("VTODO"))
            {
                continue;
            }

            var href = response.Element(Dav + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            result.Add(new DiscoveredCalendar(
                Resolve(baseUri, href).AbsoluteUri,
                response.Descendants(Dav + "displayname").FirstOrDefault()?.Value ?? "Tasks",
                response.Descendants(CalendarServer + "getctag").FirstOrDefault()?.Value,
                response.Descendants(Dav + "sync-token").FirstOrDefault()?.Value));
        }

        return result;
    }

    public async Task<CalendarSnapshot> DownloadCalendarAsync(
        AccountRecord account,
        string password,
        CalendarRecord calendar,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(account.Username, password);
        var uri = new Uri(calendar.RemoteUrl, UriKind.Absolute);
        using var request = new HttpRequestMessage(new HttpMethod("REPORT"), uri)
        {
            Content = XmlContent("""
                <c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <d:prop><d:getetag /><c:calendar-data /></d:prop>
                  <c:filter>
                    <c:comp-filter name="VCALENDAR">
                      <c:comp-filter name="VTODO" />
                    </c:comp-filter>
                  </c:filter>
                </c:calendar-query>
                """)
        };
        request.Headers.Add("Depth", "1");
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccess(response, "读取任务清单", cancellationToken);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var resources = new List<RemoteTaskResource>();
        foreach (var item in document.Descendants(Dav + "response"))
        {
            var href = item.Element(Dav + "href")?.Value;
            var data = item.Descendants(CalDav + "calendar-data").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(data))
            {
                continue;
            }
            resources.Add(new RemoteTaskResource(
                Resolve(uri, href).AbsoluteUri,
                NormalizeEtag(item.Descendants(Dav + "getetag").FirstOrDefault()?.Value),
                data));
        }

        var syncState = await ReadCalendarStateAsync(client, uri, cancellationToken);
        return new CalendarSnapshot(resources, syncState.CTag, syncState.SyncToken);
    }

    public async Task<CalendarState> GetCalendarStateAsync(
        AccountRecord account,
        string password,
        CalendarRecord calendar,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(account.Username, password);
        var state = await ReadCalendarStateAsync(client, new Uri(calendar.RemoteUrl, UriKind.Absolute), cancellationToken);
        return new CalendarState(state.CTag, state.SyncToken);
    }

    public async Task<UploadResult> PutAsync(
        AccountRecord account,
        string password,
        string targetUrl,
        string rawICalendar,
        string? etag,
        bool create,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(account.Username, password);
        using var request = new HttpRequestMessage(HttpMethod.Put, targetUrl)
        {
            Content = new StringContent(rawICalendar, Encoding.UTF8, "text/calendar")
        };
        if (create)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }
        else if (!string.IsNullOrWhiteSpace(etag))
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return new UploadResult(false, true, null);
        }
        await EnsureSuccess(response, "上传任务", cancellationToken);
        return new UploadResult(true, false, NormalizeEtag(response.Headers.ETag?.Tag));
    }

    public async Task DeleteAsync(
        AccountRecord account,
        string password,
        string targetUrl,
        string? etag,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(account.Username, password);
        using var request = new HttpRequestMessage(HttpMethod.Delete, targetUrl);
        if (!string.IsNullOrWhiteSpace(etag))
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccess(response, "删除任务", cancellationToken);
        }
    }

    public async Task<RemoteTaskResource> GetRawAsync(
        AccountRecord account,
        string password,
        string targetUrl,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(account.Username, password);
        using var response = await client.GetAsync(targetUrl, cancellationToken);
        await EnsureSuccess(response, "读取冲突任务", cancellationToken);
        return new RemoteTaskResource(
            targetUrl,
            NormalizeEtag(response.Headers.ETag?.Tag),
            await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static async Task<(string? CTag, string? SyncToken)> ReadCalendarStateAsync(
        HttpClient client,
        Uri calendarUri,
        CancellationToken cancellationToken)
    {
        var document = await PropFindAsync(client, calendarUri, 0, """
            <d:propfind xmlns:d="DAV:" xmlns:cs="http://calendarserver.org/ns/">
              <d:prop><cs:getctag /><d:sync-token /></d:prop>
            </d:propfind>
            """, cancellationToken);
        return (
            document.Descendants(CalendarServer + "getctag").FirstOrDefault()?.Value,
            document.Descendants(Dav + "sync-token").FirstOrDefault()?.Value);
    }

    private static async Task<XDocument> PropFindAsync(
        HttpClient client,
        Uri uri,
        int depth,
        string body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri) { Content = XmlContent(body) };
        request.Headers.Add("Depth", depth.ToString());
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccess(response, "发现 CalDAV 资源", cancellationToken);
        return XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static HttpClient CreateClient(string username, string password)
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FloatingTodo/0.1");
        return client;
    }

    private static StringContent XmlContent(string body) => new(body, Encoding.UTF8, "application/xml");

    private static Uri ValidateBaseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new CalDavException("CalDAV 地址必须是有效的 HTTPS URL");
        }
        return uri;
    }

    private static Uri Resolve(Uri baseUri, string href) => Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute : new Uri(baseUri, href);
    private static string? NormalizeEtag(string? etag) => string.IsNullOrWhiteSpace(etag) ? null : etag.Trim();

    private static async Task EnsureSuccess(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode || (int)response.StatusCode == 207)
        {
            return;
        }
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        if (detail.Length > 500)
        {
            detail = detail[..500];
        }
        throw new CalDavException($"{operation}失败：{(int)response.StatusCode} {response.ReasonPhrase} {detail}".Trim());
    }
}

public sealed record DiscoveredCalendar(string RemoteUrl, string DisplayName, string? CTag, string? SyncToken);
public sealed record RemoteTaskResource(string RemoteHref, string? ETag, string RawICalendar);
public sealed record CalendarSnapshot(IReadOnlyList<RemoteTaskResource> Resources, string? CTag, string? SyncToken);
public sealed record CalendarState(string? CTag, string? SyncToken);
public sealed record UploadResult(bool Success, bool Conflict, string? ETag);

public sealed class CalDavException(string message) : Exception(message);
