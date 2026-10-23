using System.Net;
using System.Text;
using System.Text.Json;
using GreenYoga.Support;

var draft = new AppSupportDraft();
AppSupportRequest Make(string message = "Help please") => draft.GetRequest("pdf-lite-viewer", "1.0.17", "Windows 11", "en", " Ada ", "ada@example.com", "Question", message);
var request = Make();
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
Check(request.RequestId == Make().RequestId, "unchanged draft retains request id");
Check(request.RequestId != Make("Changed").RequestId, "changed draft replaces request id");
Check(request.Name == "Ada", "trimmed fields");
Check(AppSupportClient.Validate(request) is null, "valid request");
Check(AppSupportClient.Validate(request with { Email = "invalid" }) == AppSupportStatus.InvalidEmail, "invalid email");
Check(AppSupportClient.Validate(request with { Message = new string( (char)120, 5001) }) == AppSupportStatus.InvalidRequest, "message limit");
Check(AppSupportClient.Validate(request with { Name = "" }) == AppSupportStatus.InvalidRequest, "name required");
int calls = 0;
var handler = new ReplyHandler(async (outgoing, ct) => {
    calls++;
    Check(outgoing.RequestUri!.AbsoluteUri == "https://api.greenyogainc.com/app-support", "dedicated endpoint");
    var json = JsonDocument.Parse(await outgoing.Content!.ReadAsStringAsync(ct));
    Check(json.RootElement.GetProperty("requestId").GetString() == request.RequestId, "request id serialized");
    Check(json.RootElement.GetProperty("appId").GetString() == "pdf-lite-viewer", "app id serialized");
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { success = true, requestId = request.RequestId })) };
});
var client = new AppSupportClient(new HttpClient(handler));
Check((await client.SendAsync(request)).Status == AppSupportStatus.Success, "matching response acknowledged");
Check((await client.SendAsync(request with { Email = "bad" })).Status == AppSupportStatus.InvalidEmail && calls == 1, "invalid form never posts");
foreach (var body in new[] { "{}", "{\"success\":true}", "{\"success\":true,\"requestId\":\"wrong\"}", "not json" })
{
    var malformed = new AppSupportClient(new HttpClient(new ReplyHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }))));
    Check((await malformed.SendAsync(request)).Status == AppSupportStatus.Failed, "malformed/unmatched acknowledgment rejected");
}
foreach (var pair in new[] { (400, "invalid-email", AppSupportStatus.InvalidEmail), (409, "request-in-progress", AppSupportStatus.InProgress), (409, "request-conflict", AppSupportStatus.Conflict), (429, "rate-limited", AppSupportStatus.RateLimited), (503, "delivery-failed", AppSupportStatus.Failed) })
{
    int attempts = 0;
    var failing = new AppSupportClient(new HttpClient(new ReplyHandler((_, _) => { attempts++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)pair.Item1) { Content = new StringContent(JsonSerializer.Serialize(new { error = pair.Item2 })) }); })));
    Check((await failing.SendAsync(request)).Status == pair.Item3 && attempts == 1, pair.Item2 + " no automatic retry");
}
var oversized = new AppSupportClient(new HttpClient(new ReplyHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string((char)120, 9000)) }))));
Check((await oversized.SendAsync(request)).Status == AppSupportStatus.Failed, "oversized acknowledgment rejected");
var offline = new AppSupportClient(new HttpClient(new ReplyHandler((_, _) => throw new HttpRequestException("offline"))));
Check((await offline.SendAsync(request)).Status == AppSupportStatus.Failed, "network failure");
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { await client.SendAsync(request, cancelled.Token); throw new Exception("cancellation ignored"); } catch (OperationCanceledException) { passed++; }
Console.WriteLine($"{passed} transport checks passed.");
sealed class ReplyHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return reply(request, cancellationToken); }
}
