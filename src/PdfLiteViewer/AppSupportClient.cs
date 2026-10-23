using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace GreenYoga.Support;

/// <summary>Shared native-app support protocol. No documents or credentials are included.</summary>
public enum AppSupportStatus { Success, InvalidRequest, InvalidEmail, InProgress, Conflict, RateLimited, Failed }
public sealed record AppSupportRequest(string RequestId, string AppId, string AppVersion, string OsVersion, string Locale, string Name, string Email, string Subject, string Message);
public sealed record AppSupportResult(AppSupportStatus Status, string RequestId);

/// <summary>Retains an idempotency key for an unchanged draft; editing creates a new request.</summary>
public sealed class AppSupportDraft
{
    private AppSupportRequest? _last;
    public AppSupportRequest GetRequest(string appId, string appVersion, string osVersion, string locale, string name, string email, string subject, string message)
    {
        var candidate = new AppSupportRequest(_last?.RequestId ?? Guid.NewGuid().ToString(), appId, appVersion, osVersion, locale,
            name.Trim(), email.Trim(), subject.Trim(), message.Trim());
        if (_last is not null && candidate != _last)
            candidate = candidate with { RequestId = Guid.NewGuid().ToString() };
        return _last = candidate;
    }
}

public sealed class AppSupportClient(HttpClient http)
{
    public const string Endpoint = "https://api.greenyogainc.com/app-support";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static AppSupportStatus? Validate(AppSupportRequest request)
    {
        static bool Fits(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && value == value.Trim();
        if (!Guid.TryParse(request.RequestId, out _) ||
            request.AppId is not ("pdf-lite-viewer" or "epub-lite-reader") ||
            !Fits(request.AppVersion, 64) || !Fits(request.OsVersion, 128) || !Fits(request.Locale, 35) ||
            !Fits(request.Name, 200) || !Fits(request.Email, 320) ||
            !Fits(request.Subject, 200) || !Fits(request.Message, 5000))
            return AppSupportStatus.InvalidRequest;
        if (!MailAddress.TryCreate(request.Email, out var email) || email.Address != request.Email ||
            request.Email.Contains((char)13) || request.Email.Contains((char)10) ||
            !request.Email[(request.Email.LastIndexOf((char)64) + 1)..].Contains((char)46))
            return AppSupportStatus.InvalidEmail;
        return null;
    }

    public async Task<AppSupportResult> SendAsync(AppSupportRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Validate(request) is { } invalid)
            return new(invalid, request.RequestId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var body = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json");
            using var outgoing = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = body };
            using var response = await http.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            // Bound the acknowledgment; support never returns documents or unbounded content.
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > 8192) return new(AppSupportStatus.Failed, request.RequestId);
                buffer.Write(chunk, 0, read);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(AppSupportStatus.Failed, request.RequestId);
            bool acknowledged = response.StatusCode == HttpStatusCode.OK &&
                root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String &&
                id.GetString() == request.RequestId;
            if (acknowledged) return new(AppSupportStatus.Success, request.RequestId);
            string? error = root.TryGetProperty("error", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
            var status = ((int)response.StatusCode, error) switch
            {
                (400, "invalid-email") => AppSupportStatus.InvalidEmail,
                (400, "invalid-request") => AppSupportStatus.InvalidRequest,
                (409, "request-in-progress") => AppSupportStatus.InProgress,
                (409, "request-conflict") => AppSupportStatus.Conflict,
                (429, "rate-limited") => AppSupportStatus.RateLimited,
                _ => AppSupportStatus.Failed
            };
            return new(status, request.RequestId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(AppSupportStatus.Failed, request.RequestId); }
        catch (HttpRequestException) { return new(AppSupportStatus.Failed, request.RequestId); }
        catch (JsonException) { return new(AppSupportStatus.Failed, request.RequestId); }
        catch (IOException) { return new(AppSupportStatus.Failed, request.RequestId); }
    }
}
