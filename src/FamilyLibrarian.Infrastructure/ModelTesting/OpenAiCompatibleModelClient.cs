using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FamilyLibrarian.Application.ModelTesting;

namespace FamilyLibrarian.Infrastructure.ModelTesting;

/// <summary>
/// <see cref="IModelClient"/> over the OpenAI-compatible <c>/v1/chat/completions</c> API that LiteLLM (and many local servers)
/// expose. One request per call: no retries, no streaming, no redirects, no fallback model. Bodies are size-bounded, a
/// provider failure becomes a classified <see cref="ModelResponse"/> rather than an exception, and the API key is never
/// written to a failure message.
/// </summary>
public sealed class OpenAiCompatibleModelClient(IHttpClientFactory httpClientFactory) : IModelClient
{
    public const string HttpClientName = "model-test";
    public const int MaxResponseBytes = 256 * 1024;
    private const int MaxDetailLength = 200;

    public async Task<ModelResponse> CompleteAsync(ModelConnection connection, ModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connection.Timeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(connection.Endpoint))
            {
                Content = new StringContent(BuildBody(connection, request), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(connection.ApiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
            }

            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await ReadBoundedAsync(response, timeout.Token);
            if (body is null)
            {
                return ModelResponse.Failed(ModelFailureKind.InvalidResponse, "The reply was larger than the allowed size.", stopwatch.ElapsedMilliseconds);
            }

            return response.IsSuccessStatusCode
                ? ParseSuccess(body, stopwatch.ElapsedMilliseconds)
                : ClassifyHttpFailure(response.StatusCode, body, connection.ApiKey, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ModelResponse.Failed(ModelFailureKind.Timeout, $"No answer within {connection.Timeout.TotalSeconds:0} seconds.", stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException exception)
        {
            return ModelResponse.Failed(ModelFailureKind.Unreachable, Redact(exception.Message, connection.ApiKey), stopwatch.ElapsedMilliseconds);
        }
        catch (JsonException)
        {
            return ModelResponse.Failed(ModelFailureKind.InvalidResponse, "The reply was not valid JSON.", stopwatch.ElapsedMilliseconds);
        }
    }

    public static Uri BuildUri(Uri endpoint)
    {
        var text = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri(text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? $"{text}/chat/completions" : $"{text}/v1/chat/completions");
    }

    private static string BuildBody(ModelConnection connection, ModelRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = connection.Model,
            ["messages"] = new JsonArray([.. request.Messages.Select(message => (JsonNode)new JsonObject
            {
                ["role"] = message.Role,
                ["content"] = message.Content
            })]),
            ["max_tokens"] = request.MaxOutputTokens,
            ["temperature"] = 0,
            ["stream"] = false
        };
        if (request.JsonObjectOutput)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        if (request.Tools is { Count: > 0 })
        {
            body["tools"] = new JsonArray([.. request.Tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.ParametersJsonSchema)
                }
            })]);
            body["tool_choice"] = "auto";
        }

        return body.ToJsonString();
    }

    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static ModelResponse ParseSuccess(string body, long latency)
    {
        var root = JsonNode.Parse(body) as JsonObject;
        var choice = (root?["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var message = choice?["message"] as JsonObject;
        if (root is null || choice is null || message is null)
        {
            return ModelResponse.Failed(ModelFailureKind.InvalidResponse, "The reply had no choices[0].message.", latency);
        }

        var content = message["content"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var toolCalls = (message["tool_calls"] as JsonArray)?
            .Select(call => (call as JsonObject)?["function"] as JsonObject)
            .Select(function => function?["name"] is JsonValue name && name.TryGetValue<string>(out var toolName) ? toolName : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new ModelToolCall(name!.Length <= 64 ? name : name[..64]))
            .ToArray() ?? [];
        var usageNode = root["usage"] as JsonObject;
        var usage = usageNode is null
            ? null
            : new ModelUsage(ReadInt(usageNode, "prompt_tokens"), ReadInt(usageNode, "completion_tokens"), ReadInt(usageNode, "total_tokens"));
        return new ModelResponse(
            true, ModelFailureKind.None, null, content, toolCalls,
            choice["finish_reason"] is JsonValue reason && reason.TryGetValue<string>(out var finish) ? finish : null,
            root["model"] is JsonValue model && model.TryGetValue<string>(out var reported) ? reported : null,
            usage, latency);
    }

    private static int? ReadInt(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<int>(out var number) && number >= 0 ? number : null;

    private static ModelResponse ClassifyHttpFailure(HttpStatusCode status, string body, string? apiKey, long latency)
    {
        var message = ErrorMessage(body);
        var detail = Redact($"HTTP {(int)status}" + (message is null ? string.Empty : $": {message}"), apiKey);
        var mentionsModel = message is not null && message.Contains("model", StringComparison.OrdinalIgnoreCase) &&
                            (message.Contains("not found", StringComparison.OrdinalIgnoreCase) || message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                             message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) || message.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
                             message.Contains("no healthy", StringComparison.OrdinalIgnoreCase) || message.Contains("no deployments", StringComparison.OrdinalIgnoreCase));
        var kind = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ModelFailureKind.Unauthorized,
            HttpStatusCode.NotFound => ModelFailureKind.ModelUnavailable,
            HttpStatusCode.TooManyRequests => ModelFailureKind.RateLimited,
            _ when mentionsModel => ModelFailureKind.ModelUnavailable,
            _ when (int)status >= 500 => ModelFailureKind.ServerError,
            _ => ModelFailureKind.BadRequest
        };
        return ModelResponse.Failed(kind, detail, latency);
    }

    private static string? ErrorMessage(string body)
    {
        try
        {
            var root = JsonNode.Parse(body) as JsonObject;
            return (root?["error"] as JsonObject)?["message"] is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : root?["detail"] is JsonValue detail && detail.TryGetValue<string>(out var detailText) ? detailText : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Redact(string value, string? apiKey)
    {
        var cleaned = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        if (!string.IsNullOrEmpty(apiKey))
        {
            cleaned = cleaned.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        }

        return cleaned.Length <= MaxDetailLength ? cleaned : cleaned[..MaxDetailLength] + "…";
    }
}
