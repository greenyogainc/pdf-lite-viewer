using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.IO;
using System.Text;
using System.Text.Json;
using GreenYoga.Support;

namespace HangProbe;

/// <summary>
/// Guards the native-app support protocol in PdfLiteViewer.dll: the request validator, the
/// draft's idempotency key, and every outcome of <see cref="AppSupportClient.SendAsync"/>.
/// tools/SupportTransportChecks compiles its own copy of the client, so its cases never
/// reached the shipped assembly; they are ported here and extended to every branch. Nothing
/// touches the network - a fake handler answers - and a post that must never happen is
/// counted rather than trusted.
/// </summary>
internal static class SupportClientChecks
{
    private const string Endpoint = "https://api.greenyogainc.com/app-support";

    public static async Task<List<Check>> RunAsync()
    {
        var checks = new List<Check>
        {
            CheckKit.Run("support client: draft keeps its request id until edited", DraftIds),
            CheckKit.Run("support client: validation table", ValidationTable),
            CheckKit.Run("support client: email rules", EmailRules),
        };
        checks.Add(await CheckKit.RunAsync("support client: invalid requests never post", InvalidNeverPostsAsync));
        checks.Add(await CheckKit.RunAsync("support client: request is a JSON POST to the endpoint", PostShapeAsync));
        checks.Add(await CheckKit.RunAsync("support client: only a matching 200 acknowledges", AcknowledgmentAsync));
        checks.Add(await CheckKit.RunAsync("support client: status and error map to one outcome each", StatusTableAsync));
        checks.Add(await CheckKit.RunAsync("support client: unusable bodies fail", UnusableBodiesAsync));
        checks.Add(await CheckKit.RunAsync("support client: body limit is 8192 bytes across chunks", BodyLimitAsync));
        checks.Add(await CheckKit.RunAsync("support client: transport failures fail without retry", TransportFailuresAsync));
        checks.Add(await CheckKit.RunAsync("support client: caller cancellation propagates", CallerCancellationAsync));
        return checks;
    }

    private static AppSupportRequest Valid(string? id = null) => new(
        id ?? Guid.NewGuid().ToString(), "pdf-lite-viewer", "1.0.18", "Windows 11", "en",
        "Ada", "ada@example.com", "Question", "Please help.");

    // ---------- AppSupportDraft ----------

    private static Check DraftIds()
    {
        var wrong = new List<string>();
        var draft = new AppSupportDraft();
        AppSupportRequest Make(string message = "Help") =>
            draft.GetRequest("pdf-lite-viewer", "1.0.18", "Windows 11", "en", " Ada ", " ada@example.com ", " Question ", message);

        var first = Make();
        if (!Guid.TryParse(first.RequestId, out _)) wrong.Add($"first id '{first.RequestId}' is not a GUID");
        if (Make().RequestId != first.RequestId) wrong.Add("an unchanged draft got a new id");
        if (Make("  Help  ").RequestId != first.RequestId) wrong.Add("whitespace around the same text changed the id");
        if (first.Name != "Ada" || first.Email != "ada@example.com" || first.Subject != "Question" || first.Message != "Help")
            wrong.Add($"fields were not trimmed: '{first.Name}' '{first.Email}' '{first.Subject}' '{first.Message}'");

        var changed = Make("Changed");
        if (changed.RequestId == first.RequestId) wrong.Add("an edited message kept the old id");
        if (Make("Changed").RequestId != changed.RequestId) wrong.Add("the edited draft did not keep its own new id");
        if (Make("Help").RequestId == first.RequestId) wrong.Add("going back to the old text reused the old id");

        // Every field is part of the identity, not just the message.
        var edits = new (string Field, Func<AppSupportDraft, AppSupportRequest> Edit)[]
        {
            ("appId", d => d.GetRequest("epub-lite-reader", "1.0.18", "Windows 11", "en", "Ada", "a@b.c", "S", "M")),
            ("appVersion", d => d.GetRequest("pdf-lite-viewer", "1.0.19", "Windows 11", "en", "Ada", "a@b.c", "S", "M")),
            ("osVersion", d => d.GetRequest("pdf-lite-viewer", "1.0.18", "Windows 12", "en", "Ada", "a@b.c", "S", "M")),
            ("locale", d => d.GetRequest("pdf-lite-viewer", "1.0.18", "Windows 11", "de", "Ada", "a@b.c", "S", "M")),
            ("name", d => d.GetRequest("pdf-lite-viewer", "1.0.18", "Windows 11", "en", "Bob", "a@b.c", "S", "M")),
            ("email", d => d.GetRequest("pdf-lite-viewer", "1.0.18", "Windows 11", "en", "Ada", "x@b.c", "S", "M")),
            ("subject", d => d.GetRequest("pdf-lite-viewer", "1.0.18", "Windows 11", "en", "Ada", "a@b.c", "T", "M")),
        };
        foreach (var (field, edit) in edits)
        {
            var d = new AppSupportDraft();
            var baseline = d.GetRequest("pdf-lite-viewer", "1.0.18", "Windows 11", "en", "Ada", "a@b.c", "S", "M");
            if (edit(d).RequestId == baseline.RequestId) wrong.Add($"editing {field} kept the old id");
        }
        return CheckKit.Tally("support client: draft keeps its request id until edited", 10 + edits.Length, wrong);
    }

    // ---------- Validate ----------

    private sealed record Case(string Label, AppSupportRequest Request, AppSupportStatus? Expected, bool NotInvalidRequest = false);

    private static Check ValidationTable()
    {
        var ok = Valid();
        var cases = new List<Case>
        {
            new("valid request", ok, null),
            new("epub-lite-reader is accepted", ok with { AppId = "epub-lite-reader" }, null),
            new("unknown app id", ok with { AppId = "other-app" }, AppSupportStatus.InvalidRequest),
            new("app id is case sensitive", ok with { AppId = "PDF-Lite-Viewer" }, AppSupportStatus.InvalidRequest),
            new("null app id", ok with { AppId = null! }, AppSupportStatus.InvalidRequest),
            new("request id is not a GUID", ok with { RequestId = "not-a-guid" }, AppSupportStatus.InvalidRequest),
            new("null request id", ok with { RequestId = null! }, AppSupportStatus.InvalidRequest),
            new("empty request id", ok with { RequestId = "" }, AppSupportStatus.InvalidRequest),
        };

        var fields = new (string Field, int Max, Func<AppSupportRequest, string?, AppSupportRequest> Set)[]
        {
            ("AppVersion", 64, (r, v) => r with { AppVersion = v! }),
            ("OsVersion", 128, (r, v) => r with { OsVersion = v! }),
            ("Locale", 35, (r, v) => r with { Locale = v! }),
            ("Name", 200, (r, v) => r with { Name = v! }),
            ("Email", 320, (r, v) => r with { Email = v! }),
            ("Subject", 200, (r, v) => r with { Subject = v! }),
            ("Message", 5000, (r, v) => r with { Message = v! }),
        };
        foreach (var (field, max, set) in fields)
        {
            cases.Add(new($"{field} null", set(ok, null), AppSupportStatus.InvalidRequest));
            cases.Add(new($"{field} empty", set(ok, ""), AppSupportStatus.InvalidRequest));
            cases.Add(new($"{field} blank", set(ok, "   "), AppSupportStatus.InvalidRequest));
            cases.Add(new($"{field} leading space", set(ok, " x"), AppSupportStatus.InvalidRequest));
            cases.Add(new($"{field} trailing space", set(ok, "x "), AppSupportStatus.InvalidRequest));
            cases.Add(new($"{field} trailing newline", set(ok, "x\n"), AppSupportStatus.InvalidRequest));
            cases.Add(new($"{field} over {max}", set(ok, new string('x', max + 1)), AppSupportStatus.InvalidRequest));
            if (field == "Email")
            {
                // A 320-character address is long, not necessarily one MailAddress accepts: the
                // length gate must let it through to the address rules.
                var longEmail = new string('a', max - "@example.com".Length) + "@example.com";
                cases.Add(new($"{field} at {max}", set(ok, longEmail), null, NotInvalidRequest: true));
            }
            else
            {
                cases.Add(new($"{field} at {max}", set(ok, new string('x', max)), null));
            }
        }

        var wrong = new List<string>();
        foreach (var c in cases)
        {
            var got = AppSupportClient.Validate(c.Request);
            bool good = c.NotInvalidRequest ? got != AppSupportStatus.InvalidRequest : got == c.Expected;
            if (!good) wrong.Add($"{c.Label}: got {Show(got)}, expected {(c.NotInvalidRequest ? "anything but InvalidRequest" : Show(c.Expected))}");
        }
        return CheckKit.Tally("support client: validation table", cases.Count, wrong);
    }

    private static string Show(AppSupportStatus? status) => status?.ToString() ?? "valid";

    private static Check EmailRules()
    {
        var ok = Valid();
        var accepted = new[] { "ada@example.com", "ada@sub.example.co.uk", "a.b+tag@example.com", "\u00e9@example.com", "\"a b\"@example.com" };
        var rejected = new (string Email, string Why)[]
        {
            ("ada.example.com", "no @"),
            ("@example.com", "no local part"),
            ("ada@", "no domain"),
            ("Ada <ada@example.com>", "display-name form, address differs from the text"),
            ("ada@localhost", "no dot in the domain"),
            ("ada@example", "no dot in the domain"),
            ("a@b@example.com", "two @"),
            ("a b@example.com", "unquoted space"),
            ("\"a\r\nb\"@example.com", "CR LF inside a quoted local part"),
            ("\"a\nb\"@example.com", "LF inside a quoted local part"),
            ("\"a\rb\"@example.com", "CR inside a quoted local part"),
            ("\"a\r\n b\"@example.com", "folded quoted local part"),
            ("a(\r\n)@example.com", "CR LF inside a comment"),
            ("a@example.com\r\nBcc: x@example.org", "header injection after the address"),
            ("\"a@b.c\"@localhost", "the dot in the quoted local part is not in the domain"),
        };

        var wrong = new List<string>();
        foreach (var email in accepted)
            if (AppSupportClient.Validate(ok with { Email = email }) is { } status)
                wrong.Add($"'{Printable(email)}' was refused as {status}");
        int reachable = 0, crlfVariants = 0;
        foreach (var (email, why) in rejected)
        {
            var got = AppSupportClient.Validate(ok with { Email = email });
            if (got != AppSupportStatus.InvalidEmail) wrong.Add($"'{Printable(email)}' ({why}): got {Show(got)}");
            if (email.Contains('\r') || email.Contains('\n'))
            {
                crlfVariants++;
                if (MailAddress.TryCreate(email, out _)) reachable++;
            }
        }
        return CheckKit.Tally("support client: email rules", accepted.Length + rejected.Length, wrong,
            $"CR/LF reachable through MailAddress: {(reachable == 0 ? "no" : "YES")} " +
            $"({reachable} of {crlfVariants} CR/LF spellings passed MailAddress.TryCreate, so the explicit CR/LF clauses are defence in depth)");
    }

    private static string Printable(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n");

    // ---------- SendAsync ----------

    private static async Task<Check> InvalidNeverPostsAsync()
    {
        const string name = "support client: invalid requests never post";
        var fake = new Fake((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var wrong = new List<string>();
        var invalid = new (string Label, AppSupportRequest Request, AppSupportStatus Expected)[]
        {
            ("blank name", Valid() with { Name = "" }, AppSupportStatus.InvalidRequest),
            ("message over the limit", Valid() with { Message = new string('x', 5001) }, AppSupportStatus.InvalidRequest),
            ("bad request id", Valid("nope"), AppSupportStatus.InvalidRequest),
            ("bad email", Valid() with { Email = "bad" }, AppSupportStatus.InvalidEmail),
        };
        foreach (var (label, request, expected) in invalid)
        {
            var result = await fake.Client.SendAsync(request);
            if (result.Status != expected) wrong.Add($"{label}: {result.Status}, expected {expected}");
            if (result.RequestId != request.RequestId) wrong.Add($"{label}: reply carries id '{result.RequestId}'");
        }
        if (fake.Posts != 0) wrong.Add($"{fake.Posts} post(s) reached the handler");
        return CheckKit.Tally(name, invalid.Length, wrong);
    }

    private static async Task<Check> PostShapeAsync()
    {
        const string name = "support client: request is a JSON POST to the endpoint";
        var request = Valid();
        var fake = new Fake((_, _) => Task.FromResult(Ok(request.RequestId)));
        var result = await fake.Client.SendAsync(request);

        var wrong = new List<string>();
        if (result.Status != AppSupportStatus.Success) wrong.Add($"status {result.Status}");
        if (fake.Posts != 1) wrong.Add($"{fake.Posts} posts");
        if (fake.Method != "POST") wrong.Add($"method {fake.Method}");
        if (fake.Uri != Endpoint) wrong.Add($"uri {fake.Uri}");
        if (fake.ContentType != "application/json") wrong.Add($"content type {fake.ContentType}");

        using var json = JsonDocument.Parse(fake.Body ?? "{}");
        var expected = new Dictionary<string, string>
        {
            ["requestId"] = request.RequestId, ["appId"] = request.AppId, ["appVersion"] = request.AppVersion,
            ["osVersion"] = request.OsVersion, ["locale"] = request.Locale, ["name"] = request.Name,
            ["email"] = request.Email, ["subject"] = request.Subject, ["message"] = request.Message,
        };
        foreach (var (key, value) in expected)
        {
            if (!json.RootElement.TryGetProperty(key, out var actual) || actual.GetString() != value)
                wrong.Add($"body field {key} is not '{value}'");
        }
        int extra = json.RootElement.EnumerateObject().Count() - expected.Count;
        if (extra != 0) wrong.Add($"body carries {extra} field(s) beyond the nine in the protocol");
        return CheckKit.Tally(name, 5 + expected.Count + 1, wrong);
    }

    private static async Task<Check> AcknowledgmentAsync()
    {
        const string name = "support client: only a matching 200 acknowledges";
        var request = Valid();
        var wrong = new List<string>();

        var good = new Fake((_, _) => Task.FromResult(Ok(request.RequestId)));
        var result = await good.Client.SendAsync(request);
        if (result.Status != AppSupportStatus.Success || result.RequestId != request.RequestId)
            wrong.Add($"matching acknowledgment gave {result.Status} / '{result.RequestId}'");

        // The same body under any other status is not an acknowledgment.
        foreach (var status in new[] { HttpStatusCode.Accepted, HttpStatusCode.Created, HttpStatusCode.InternalServerError })
        {
            var fake = new Fake((_, _) => Task.FromResult(Reply(status, $"{{\"success\":true,\"requestId\":\"{request.RequestId}\"}}")));
            var got = await fake.Client.SendAsync(request);
            if (got.Status != AppSupportStatus.Failed) wrong.Add($"{(int)status} with a matching body gave {got.Status}");
        }
        return CheckKit.Tally(name, 4, wrong);
    }

    private static async Task<Check> StatusTableAsync()
    {
        const string name = "support client: status and error map to one outcome each";
        var request = Valid();
        var table = new (int Status, string? Error, AppSupportStatus Expected)[]
        {
            (400, "invalid-email", AppSupportStatus.InvalidEmail),
            (400, "invalid-request", AppSupportStatus.InvalidRequest),
            (409, "request-in-progress", AppSupportStatus.InProgress),
            (409, "request-conflict", AppSupportStatus.Conflict),
            (429, "rate-limited", AppSupportStatus.RateLimited),
            (503, "delivery-failed", AppSupportStatus.Failed),
            (400, "unheard-of", AppSupportStatus.Failed),
            (500, "invalid-email", AppSupportStatus.Failed),       // right error, wrong status
            (409, "invalid-request", AppSupportStatus.Failed),
            (400, "rate-limited", AppSupportStatus.Failed),
            (429, "request-conflict", AppSupportStatus.Failed),
            (400, null, AppSupportStatus.Failed),                   // no error property at all
            (200, "invalid-email", AppSupportStatus.Failed),        // an error does not become a success
        };
        var wrong = new List<string>();
        foreach (var (status, error, expected) in table)
        {
            var fake = new Fake((_, _) => Task.FromResult(Reply((HttpStatusCode)status,
                error is null ? "{}" : JsonSerializer.Serialize(new { error }))));
            var got = await fake.Client.SendAsync(request);
            if (got.Status != expected) wrong.Add($"({status}, {error ?? "none"}) -> {got.Status}, expected {expected}");
            if (fake.Posts != 1) wrong.Add($"({status}, {error ?? "none"}) posted {fake.Posts} times");
            if (got.RequestId != request.RequestId) wrong.Add($"({status}, {error ?? "none"}) lost the request id");
        }
        return CheckKit.Tally(name, table.Length, wrong, "each posted exactly once, no automatic retry");
    }

    private static async Task<Check> UnusableBodiesAsync()
    {
        const string name = "support client: unusable bodies fail";
        var request = Valid();
        string id = request.RequestId;
        var bodies = new (string Label, HttpResponseMessage Response)[]
        {
            ("[] (not an object)", Reply(HttpStatusCode.OK, "[]")),
            ("null", Reply(HttpStatusCode.OK, "null")),
            ("a bare string", Reply(HttpStatusCode.OK, "\"ok\"")),
            ("{}", Reply(HttpStatusCode.OK, "{}")),
            ("success without requestId", Reply(HttpStatusCode.OK, "{\"success\":true}")),
            ("another request's id", Reply(HttpStatusCode.OK, $"{{\"success\":true,\"requestId\":\"{Guid.NewGuid()}\"}}")),
            ("requestId is a number", Reply(HttpStatusCode.OK, "{\"success\":true,\"requestId\":7}")),
            ("requestId is null", Reply(HttpStatusCode.OK, "{\"success\":true,\"requestId\":null}")),
            ("success is false", Reply(HttpStatusCode.OK, $"{{\"success\":false,\"requestId\":\"{id}\"}}")),
            ("success is the string true", Reply(HttpStatusCode.OK, $"{{\"success\":\"true\",\"requestId\":\"{id}\"}}")),
            ("error is a number", Reply(HttpStatusCode.BadRequest, "{\"error\":5}")),
            ("error is an object", Reply(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"rate-limited\"}}")),
            ("not JSON", Reply(HttpStatusCode.OK, "not json")),
            ("truncated JSON", Reply(HttpStatusCode.OK, "{\"success\":tr")),
            ("empty body", Reply(HttpStatusCode.OK, "")),
            ("no content at all", new HttpResponseMessage(HttpStatusCode.OK)),
        };
        var wrong = new List<string>();
        foreach (var (label, response) in bodies)
        {
            var fake = new Fake((_, _) => Task.FromResult(response));
            var got = await fake.Client.SendAsync(request);
            if (got.Status != AppSupportStatus.Failed) wrong.Add($"{label}: {got.Status}");
            if (fake.Posts != 1) wrong.Add($"{label}: posted {fake.Posts} times");
        }
        return CheckKit.Tally(name, bodies.Length, wrong);
    }

    private static async Task<Check> BodyLimitAsync()
    {
        const string name = "support client: body limit is 8192 bytes across chunks";
        var request = Valid();
        var wrong = new List<string>();
        var cases = new (string Label, int Bytes, int Chunk, AppSupportStatus Expected)[]
        {
            ("3000 bytes in 100-byte chunks", 3000, 100, AppSupportStatus.Success),
            ("8192 bytes in 700-byte chunks", 8192, 700, AppSupportStatus.Success),
            ("8192 bytes in one read", 8192, 8192, AppSupportStatus.Success),
            ("8193 bytes in 700-byte chunks", 8193, 700, AppSupportStatus.Failed),
            ("8193 bytes in one read", 8193, 8193, AppSupportStatus.Failed),
            ("20000 bytes in 1000-byte chunks", 20_000, 1000, AppSupportStatus.Failed),
        };
        foreach (var (label, bytes, chunk, expected) in cases)
        {
            var body = Encoding.UTF8.GetBytes(PaddedAcknowledgment(request.RequestId, bytes));
            var fake = new Fake((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ScriptedContent(Chunked(body, chunk)),
            }));
            var got = await fake.Client.SendAsync(request);
            if (body.Length != bytes) wrong.Add($"{label}: fixture is {body.Length} bytes");
            if (got.Status != expected) wrong.Add($"{label}: {got.Status}, expected {expected}");
        }
        return CheckKit.Tally(name, cases.Length, wrong);
    }

    /// <summary>A valid acknowledgment padded with an unused property to exactly <paramref name="bytes"/> bytes.</summary>
    private static string PaddedAcknowledgment(string id, int bytes)
    {
        string head = $"{{\"success\":true,\"requestId\":\"{id}\",\"pad\":\"";
        const string tail = "\"}";
        return head + new string('x', bytes - head.Length - tail.Length) + tail;
    }

    private static async Task<Check> TransportFailuresAsync()
    {
        const string name = "support client: transport failures fail without retry";
        var request = Valid();
        var ack = Encoding.UTF8.GetBytes($"{{\"success\":true,\"requestId\":\"{request.RequestId}\"}}");
        var failures = new (string Label, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply)[]
        {
            ("HttpRequestException from the handler", (_, _) => throw new HttpRequestException("offline")),
            ("TaskCanceledException while the caller has not cancelled",
                (_, _) => throw new TaskCanceledException("simulated 20 s timeout", new TimeoutException())),
            ("OperationCanceledException while the caller has not cancelled",
                (_, _) => throw new OperationCanceledException("simulated timeout")),
            ("IOException on the first read", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ScriptedContent(() => new ReadScript((_, _) => throw new IOException("connection reset"))),
            })),
            ("IOException after a partial body", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ScriptedContent(() =>
                {
                    int reads = 0;
                    return new ReadScript((buffer, _) =>
                    {
                        if (reads++ > 0) throw new IOException("connection reset mid-body");
                        int n = Math.Min(10, buffer.Length);
                        ack.AsMemory(0, n).CopyTo(buffer);
                        return new ValueTask<int>(n);
                    });
                }),
            })),
        };
        var wrong = new List<string>();
        foreach (var (label, reply) in failures)
        {
            var fake = new Fake(reply);
            var got = await fake.Client.SendAsync(request);
            if (got.Status != AppSupportStatus.Failed) wrong.Add($"{label}: {got.Status}");
            if (got.RequestId != request.RequestId) wrong.Add($"{label}: lost the request id");
            if (fake.Posts != 1) wrong.Add($"{label}: posted {fake.Posts} times");
        }
        return CheckKit.Tally(name, failures.Length, wrong);
    }

    private static async Task<Check> CallerCancellationAsync()
    {
        const string name = "support client: caller cancellation propagates";
        var request = Valid();
        var wrong = new List<string>();

        // Cancelled before the call: nothing is validated or posted, even for an invalid request.
        foreach (var (label, subject) in new[] { ("valid request", request), ("invalid request", request with { Name = "" }) })
        {
            var fake = new Fake((_, _) => Task.FromResult(Ok(request.RequestId)));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            if (!await ThrowsCancellationAsync(() => fake.Client.SendAsync(subject, cts.Token)))
                wrong.Add($"{label}: a cancelled token did not throw OperationCanceledException");
            if (fake.Posts != 0) wrong.Add($"{label}: posted {fake.Posts} times after cancellation");
        }

        // Cancelled while the handler is waiting.
        {
            var entered = new TaskCompletionSource();
            var fake = new Fake(async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var cts = new CancellationTokenSource();
            var send = fake.Client.SendAsync(request, cts.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            if (!await ThrowsCancellationAsync(() => send))
                wrong.Add("cancelling while the handler waited did not throw OperationCanceledException");
        }

        // Cancelled while the body is being read.
        {
            var reading = new TaskCompletionSource();
            var fake = new Fake((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ScriptedContent(() => new ReadScript(async (_, ct) =>
                {
                    reading.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return 0;
                })),
            }));
            using var cts = new CancellationTokenSource();
            var send = fake.Client.SendAsync(request, cts.Token);
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            if (!await ThrowsCancellationAsync(() => send))
                wrong.Add("cancelling while the body was read did not throw OperationCanceledException");
        }
        return CheckKit.Tally(name, 4, wrong);
    }

    private static async Task<bool> ThrowsCancellationAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (OperationCanceledException) { return true; }
    }

    // ---------- fakes ----------

    private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json) };

    private static HttpResponseMessage Ok(string requestId) =>
        Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { success = true, requestId }));

    /// <summary>A client over a fake handler that counts and records what was posted.</summary>
    private sealed class Fake
    {
        public int Posts { get; private set; }
        public string? Method { get; private set; }
        public string? Uri { get; private set; }
        public string? ContentType { get; private set; }
        public string? Body { get; private set; }
        public AppSupportClient Client { get; }

        public Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply)
        {
            Client = new AppSupportClient(new HttpClient(new AboutChecks.SupportReply(async (request, ct) =>
            {
                Posts++;
                Method = request.Method.Method;
                Uri = request.RequestUri?.AbsoluteUri;
                ContentType = request.Content?.Headers.ContentType?.MediaType;
                if (request.Content is not null && !ct.IsCancellationRequested)
                    Body = await request.Content.ReadAsStringAsync(CancellationToken.None);
                return await reply(request, ct);
            })));
        }
    }

    /// <summary>A response body whose read stream the check scripts: chunking, failure, or ignoring cancellation.</summary>
    internal sealed class ScriptedContent(Func<Stream> open) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(open());
    }

    /// <summary>The bytes of <paramref name="data"/>, at most <paramref name="chunk"/> per read.</summary>
    internal static Func<Stream> Chunked(byte[] data, int chunk, bool honorCancellation = true) => () =>
    {
        int position = 0;
        return new ReadScript((buffer, ct) =>
        {
            if (honorCancellation) ct.ThrowIfCancellationRequested();
            int n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - position);
            data.AsMemory(position, n).CopyTo(buffer);
            position += n;
            return new ValueTask<int>(n);
        });
    };

    internal sealed class ReadScript(Func<Memory<byte>, CancellationToken, ValueTask<int>> read) : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            read(buffer, cancellationToken);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        // The scripts here are synchronous, so a StreamReader (the license check) can use them too.
        public override int Read(byte[] buffer, int offset, int count) =>
            read(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
