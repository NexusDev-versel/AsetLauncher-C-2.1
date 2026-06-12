using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AsetLauncher.Services
{
    public sealed class LauncherDeviceAuthService
    {
        private const string DefaultBackendBaseUrl = "https://asetlauncher.ru";

        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 64
        };

        public async Task<DeviceStartResult> StartDeviceAsync(string backendBaseUrl, CancellationToken ct)
        {
            var errors = new List<string>();
            foreach (var candidateBaseUrl in GetCandidateBaseUrls(backendBaseUrl))
            {
                LauncherLogService.Info("Device auth start: " + candidateBaseUrl);
                try
                {
                    return await StartDeviceCoreAsync(candidateBaseUrl, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var error = "[" + candidateBaseUrl + "] " + DescribeException(ex);
                    errors.Add(error);
                    LauncherLogService.Warn("Device auth start failed: " + error);
                }
            }

            throw new InvalidOperationException(
                "Не удалось начать авторизацию. Проверены адреса: "
                + string.Join(" | ", errors));
        }

        public async Task<DevicePollResult> PollDeviceAsync(string backendBaseUrl, string deviceCode, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(deviceCode))
            {
                throw new ArgumentException("Код авторизации пустой.", nameof(deviceCode));
            }

            var errors = new List<string>();
            foreach (var candidateBaseUrl in GetCandidateBaseUrls(backendBaseUrl))
            {
                LauncherLogService.Info("Device auth poll: " + candidateBaseUrl);
                try
                {
                    return await PollDeviceCoreAsync(candidateBaseUrl, deviceCode, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var error = "[" + candidateBaseUrl + "] " + DescribeException(ex);
                    errors.Add(error);
                    LauncherLogService.Warn("Device auth poll failed: " + error);
                }
            }

            throw new InvalidOperationException(
                "Не удалось проверить авторизацию. Проверены адреса: "
                + string.Join(" | ", errors));
        }

        private static async Task<DeviceStartResult> StartDeviceCoreAsync(string normalizedBaseUrl, CancellationToken ct)
        {
            var url = BuildApiUrl(normalizedBaseUrl, "device/start");
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw BuildRequestException("Не удалось начать авторизацию: " + url, response.StatusCode, text);
                    }

                    var map = Json.DeserializeObject(text) as Dictionary<string, object>;
                    if (map == null)
                    {
                        throw new InvalidOperationException("Backend вернул некорректный JSON для /device/start.");
                    }

                    var deviceCode = Str(map, "device_code");
                    var verificationUri = Str(map, "verification_uri");
                    if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(verificationUri))
                    {
                        throw new InvalidOperationException("Backend не вернул device_code или verification_uri.");
                    }

                    verificationUri = NormalizeVerificationUri(verificationUri);

                    return new DeviceStartResult
                    {
                        BackendBaseUrl = normalizedBaseUrl,
                        DeviceCode = deviceCode,
                        VerificationUri = verificationUri,
                        IntervalSeconds = Math.Max(1, ToInt(map, "interval", 3)),
                        ExpiresInSeconds = Math.Max(15, ToInt(map, "expires_in", 600))
                    };
                }
            }
        }

        private static async Task<DevicePollResult> PollDeviceCoreAsync(string normalizedBaseUrl, string deviceCode, CancellationToken ct)
        {
            var url = BuildApiUrl(normalizedBaseUrl, "device/poll");
            var payload = Json.Serialize(new Dictionary<string, object>
            {
                { "device_code", deviceCode }
            });

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode == 202)
                    {
                        return new DevicePollResult { IsPending = true };
                    }

                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw BuildRequestException("Ошибка проверки авторизации: " + url, response.StatusCode, text);
                    }

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return new DevicePollResult { IsPending = true };
                    }

                    var map = Json.DeserializeObject(text) as Dictionary<string, object>;
                    if (map == null)
                    {
                        throw new InvalidOperationException("Backend вернул некорректный JSON для /device/poll.");
                    }

                    var ok = ToBool(map, "ok");
                    if (!ok)
                    {
                        throw new InvalidOperationException(Str(map, "message", "Авторизация отклонена."));
                    }

                    return new DevicePollResult
                    {
                        IsPending = false,
                        Username = Str(map, "username"),
                        Uuid = NormalizeUuid(Str(map, "uuid")),
                        AccessToken = Str(map, "accessToken"),
                        ClientToken = Str(map, "clientToken")
                    };
                }
            }
        }

        private static string BuildApiUrl(string normalizedBaseUrl, string endpoint)
        {
            Uri baseUri;
            if (!Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out baseUri))
            {
                throw new InvalidOperationException("Некорректный адрес backend: " + normalizedBaseUrl);
            }

            var path = (baseUri.AbsolutePath ?? "/").TrimEnd('/');
            if (path == "/")
            {
                path = string.Empty;
            }

            var apiPath = path.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
                ? path
                : path + "/api";

            var endpointPath = endpoint.TrimStart('/');
            var builder = new UriBuilder(baseUri)
            {
                Path = apiPath.TrimEnd('/') + "/" + endpointPath,
                Query = string.Empty
            };

            return builder.Uri.AbsoluteUri;
        }

        private static Exception BuildRequestException(string prefix, HttpStatusCode code, string body)
        {
            var compactBody = CompactBody(body);
            var message = string.IsNullOrWhiteSpace(compactBody)
                ? prefix + ". Код HTTP: " + (int)code
                : prefix + ". Код HTTP: " + (int)code + ". " + compactBody;
            return new InvalidOperationException(message);
        }

        private static string NormalizeVerificationUri(string verificationUri)
        {
            if (string.IsNullOrWhiteSpace(verificationUri))
            {
                return verificationUri;
            }

            Uri uri;
            if (!Uri.TryCreate(verificationUri, UriKind.Absolute, out uri))
            {
                return verificationUri;
            }

            var builder = new UriBuilder(uri);

            // На сайте страница авторизации устройства находится по /device.html.
            if (string.Equals(builder.Path, "/device", StringComparison.OrdinalIgnoreCase))
            {
                builder.Path = "/device.html";
            }

            return builder.Uri.AbsoluteUri;
        }

        private static string NormalizeBaseUrl(string url)
        {
            var value = (url ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                value = DefaultBackendBaseUrl;
            }

            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                value = "http://" + value;
            }

            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
            {
                return value.TrimEnd('/');
            }

            var builder = new UriBuilder(uri)
            {
                Query = string.Empty,
                Fragment = string.Empty
            };

            return builder.Uri.AbsoluteUri.TrimEnd('/');
        }

        private static IEnumerable<string> GetCandidateBaseUrls(string backendBaseUrl)
        {
            var normalized = NormalizeBaseUrl(backendBaseUrl);
            var list = new List<string>();

            Uri uri;
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out uri))
            {
                return new[] { normalized }.Distinct(StringComparer.OrdinalIgnoreCase);
            }

            AddCandidate(list, uri);
            AddCandidate(list, SwapScheme(uri));

            if (IsDefaultPort(uri) && IsLocalHost(uri))
            {
                AddCandidate(list, WithPort(uri, 5500));
                AddCandidate(list, WithPort(SwapScheme(uri), 5500));
            }

            if (!string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal))
            {
                var rootUri = WithPath(uri, "/");
                AddCandidate(list, rootUri);
                AddCandidate(list, SwapScheme(rootUri));

                if (IsDefaultPort(rootUri) && IsLocalHost(rootUri))
                {
                    AddCandidate(list, WithPort(rootUri, 5500));
                    AddCandidate(list, WithPort(SwapScheme(rootUri), 5500));
                }
            }

            return list
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.TrimEnd('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddCandidate(List<string> list, Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
            {
                return;
            }

            list.Add(uri.AbsoluteUri.TrimEnd('/'));
        }

        private static Uri SwapScheme(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
            {
                return uri;
            }

            var nextScheme = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? Uri.UriSchemeHttp
                : Uri.UriSchemeHttps;

            var builder = new UriBuilder(uri)
            {
                Scheme = nextScheme
            };

            // Avoid invalid combinations like http://host:443 or https://host:80
            // when we only switch scheme for fallback.
            if (uri.IsDefaultPort || uri.Port == 80 || uri.Port == 443)
            {
                builder.Port = -1;
            }

            return builder.Uri;
        }

        private static Uri WithPort(Uri uri, int port)
        {
            if (uri == null || !uri.IsAbsoluteUri)
            {
                return uri;
            }

            return new UriBuilder(uri) { Port = port }.Uri;
        }

        private static Uri WithPath(Uri uri, string path)
        {
            if (uri == null || !uri.IsAbsoluteUri)
            {
                return uri;
            }

            return new UriBuilder(uri) { Path = path ?? "/" }.Uri;
        }

        private static bool IsDefaultPort(Uri uri)
        {
            if (uri == null)
            {
                return false;
            }

            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                return uri.Port == 80;
            }

            if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return uri.Port == 443;
            }

            return false;
        }

        private static bool IsLocalHost(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
            {
                return false;
            }

            var host = (uri.Host ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
        }

        private static string DescribeException(Exception ex)
        {
            if (ex == null)
            {
                return "unknown error";
            }

            var parts = new List<string>();
            var current = ex;
            var depth = 0;
            while (current != null && depth < 4)
            {
                var text = (current.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = current.GetType().Name;
                }

                parts.Add(current.GetType().Name + ": " + text);
                current = current.InnerException;
                depth++;
            }

            var summary = string.Join(" -> ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
            if (summary.Length > 420)
            {
                summary = summary.Substring(0, 420) + "...";
            }

            return summary;
        }

        private static string CompactBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            var compact = body
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            // Strip HTML tags from nginx/proxy error pages to keep message readable.
            compact = Regex.Replace(compact, "<[^>]+>", " ").Trim();
            compact = Regex.Replace(compact, "\\s{2,}", " ").Trim();

            if (compact.Length > 320)
            {
                compact = compact.Substring(0, 320) + "...";
            }

            return compact;
        }

        private static string NormalizeUuid(string uuid)
        {
            return string.IsNullOrWhiteSpace(uuid)
                ? string.Empty
                : uuid.Replace("-", string.Empty).Trim().ToLowerInvariant();
        }

        private static string Str(Dictionary<string, object> map, string key, string fallback = "")
        {
            object value;
            if (map != null && map.TryGetValue(key, out value))
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback;
            }

            return fallback;
        }

        private static int ToInt(Dictionary<string, object> map, string key, int fallback)
        {
            int parsed;
            return int.TryParse(Str(map, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : fallback;
        }

        private static bool ToBool(Dictionary<string, object> map, string key)
        {
            var value = Str(map, key);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class DeviceStartResult
    {
        public string BackendBaseUrl { get; set; } = "";
        public string DeviceCode { get; set; } = "";
        public string VerificationUri { get; set; } = "";
        public int IntervalSeconds { get; set; } = 3;
        public int ExpiresInSeconds { get; set; } = 600;
    }

    public sealed class DevicePollResult
    {
        public bool IsPending { get; set; } = true;
        public string Username { get; set; } = "";
        public string Uuid { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string ClientToken { get; set; } = "";
    }
}

