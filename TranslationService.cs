using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LightTranslate;

public enum TranslationAction
{
    Translate,
    Explain,
    Polish
}

public sealed class TranslationService
{
    private static readonly TimeSpan StreamInactivityTimeout = ResolveStreamInactivityTimeout();
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    private static TimeSpan ResolveStreamInactivityTimeout()
    {
        return int.TryParse(
                   Environment.GetEnvironmentVariable("LIGHTTRANSLATE_STREAM_TIMEOUT_SECONDS"),
                   out var seconds) && seconds is >= 1 and <= 600
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(60);
    }

    public async Task<string> TranslateAsync(
        string sourceText,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        return await TranslateStreamingAsync(
            sourceText,
            targetLanguage,
            TranslationAction.Translate,
            existingTranslation: null,
            onDelta: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> TranslateStreamingAsync(
        string sourceText,
        string targetLanguage,
        TranslationAction action,
        string? existingTranslation,
        IProgress<string>? onDelta,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
            throw new ArgumentException("没有需要处理的文字", nameof(sourceText));

        var settings = SettingsStore.Load();
        var apiKey = SecretStore.LoadApiKey();

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            throw new InvalidOperationException("尚未设置 API Base URL");
        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new InvalidOperationException("尚未设置模型名称");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("尚未设置 API Key");

        var terminology = TerminologyStore.GetRelevant(sourceText);
        var systemPrompt = BuildSystemPrompt(targetLanguage, action, terminology);
        var userContent = BuildUserContent(sourceText, existingTranslation, action);
        var effort = TranslationReasoningPolicy.GetEffort(action);
        var protocol = TranslationApiProtocolPolicy.Resolve(settings);

        return protocol == TranslationApiProtocol.Responses
            ? await SendResponsesStreamingAsync(
                    settings,
                    apiKey,
                    systemPrompt,
                    userContent,
                    effort,
                    onDelta,
                    cancellationToken)
                .ConfigureAwait(false)
            : await SendChatCompletionsStreamingAsync(
                    settings,
                    apiKey,
                    systemPrompt,
                    userContent,
                    effort,
                    onDelta,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task<string> SendChatCompletionsStreamingAsync(
        AppSettings settings,
        string apiKey,
        string systemPrompt,
        string userContent,
        string effort,
        IProgress<string>? onDelta,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildChatCompletionsEndpoint(settings.BaseUrl);
        var payload = new
        {
            model = settings.Model,
            reasoning_effort = effort,
            thinking = new { type = "enabled" },
            stream = true,
            max_tokens = 4096,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            }
        };

        using var request = CreateStreamingRequest(endpoint, apiKey, payload);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);
        var result = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await ReadLineWithInactivityTimeoutAsync(reader, cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line[5..].Trim();
            if (data.Length == 0)
                continue;
            if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                break;

            var piece = ExtractChatStreamContent(data);
            if (string.IsNullOrEmpty(piece))
                continue;

            result.Append(piece);
            onDelta?.Report(piece);
        }

        return FinalizeContent(result);
    }

    private static async Task<string> SendResponsesStreamingAsync(
        AppSettings settings,
        string apiKey,
        string systemPrompt,
        string userContent,
        string effort,
        IProgress<string>? onDelta,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildResponsesEndpoint(settings.BaseUrl);
        var payload = new
        {
            model = settings.Model,
            instructions = systemPrompt,
            input = userContent,
            reasoning = new { effort },
            stream = true,
            max_output_tokens = 8192
        };

        using var request = CreateStreamingRequest(endpoint, apiKey, payload);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);
        var result = new StringBuilder();
        var completed = false;
        string? eventName = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await ReadLineWithInactivityTimeoutAsync(reader, cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line[6..].Trim();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line[5..].Trim();
            if (data.Length == 0)
                continue;
            if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                eventName = null;
                continue;
            }

            if (ProcessResponsesStreamEvent(data, eventName, result, onDelta))
            {
                completed = true;
                break;
            }

            eventName = null;
        }

        if (!completed)
            throw new InvalidOperationException("Responses 流在完成事件前中断，请重试");

        return FinalizeContent(result);
    }

    private static HttpRequestMessage CreateStreamingRequest(Uri endpoint, string apiKey, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("LightTranslate/0.5.7");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new TranslationApiException((int)response.StatusCode, ExtractApiError(errorBody));
    }

    private static string FinalizeContent(StringBuilder result)
    {
        var content = result.ToString().Trim();
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("模型返回了空结果");
        return content;
    }

    private static async Task<string?> ReadLineWithInactivityTimeoutAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        using var inactivityCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inactivityCancellation.CancelAfter(StreamInactivityTimeout);
        try
        {
            return await reader.ReadLineAsync(inactivityCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"AI 服务连续 {StreamInactivityTimeout.TotalSeconds:0} 秒没有返回数据，请重试");
        }
    }

    private static string? ExtractChatStreamContent(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.TryGetProperty("error", out var streamError))
            {
                var streamErrorText = streamError.ValueKind == JsonValueKind.String
                    ? streamError.GetString()
                    : streamError.TryGetProperty("message", out var errorMessage)
                        ? errorMessage.GetString()
                        : null;
                throw new InvalidOperationException(streamErrorText ?? "AI 流式响应返回错误");
            }

            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return null;

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var deltaContent) &&
                deltaContent.ValueKind == JsonValueKind.String)
                return deltaContent.GetString();

            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var messageContent) &&
                messageContent.ValueKind == JsonValueKind.String)
                return messageContent.GetString();
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static bool ProcessResponsesStreamEvent(
        string data,
        string? eventName,
        StringBuilder result,
        IProgress<string>? onDelta)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Responses 流返回了无法解析的数据", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var directError) && directError.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException(ExtractErrorMessage(directError) ?? "Responses API 返回错误");

            var type = root.TryGetProperty("type", out var typeElement) &&
                       typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : eventName;
            if (string.IsNullOrWhiteSpace(type))
                return false;
            switch (type)
            {
                case "response.output_text.delta":
                    if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                        AppendStreamPiece(result, onDelta, delta.GetString());
                    return false;

                case "response.output_text.done":
                    if (result.Length == 0 &&
                        root.TryGetProperty("text", out var doneText) &&
                        doneText.ValueKind == JsonValueKind.String)
                        AppendStreamPiece(result, onDelta, doneText.GetString());
                    return false;

                case "response.completed":
                    if (result.Length == 0 && root.TryGetProperty("response", out var completedResponse))
                        AppendStreamPiece(result, onDelta, ExtractResponsesOutputText(completedResponse));
                    return true;

                case "response.incomplete":
                    var reason = ExtractIncompleteReason(root);
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(reason)
                            ? "模型输出不完整，请缩短原文或重试"
                            : $"模型输出不完整：{reason}");

                case "response.failed":
                case "error":
                    throw new InvalidOperationException(
                        ExtractResponsesEventError(root) ?? "Responses API 生成失败");

                default:
                    return false;
            }
        }
    }

    private static void AppendStreamPiece(
        StringBuilder result,
        IProgress<string>? onDelta,
        string? piece)
    {
        if (string.IsNullOrEmpty(piece))
            return;

        result.Append(piece);
        onDelta?.Report(piece);
    }

    private static string? ExtractResponsesOutputText(JsonElement response)
    {
        if (response.TryGetProperty("output_text", out var directText) &&
            directText.ValueKind == JsonValueKind.String)
            return directText.GetString();

        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return null;

        var text = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var partType) &&
                    partType.ValueKind == JsonValueKind.String &&
                    !partType.GetString()!.Equals("output_text", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (part.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String)
                    text.Append(partText.GetString());
            }
        }

        return text.Length == 0 ? null : text.ToString();
    }

    private static string? ExtractResponsesEventError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            return ExtractErrorMessage(error);

        return root.TryGetProperty("response", out var response) &&
               response.TryGetProperty("error", out var responseError) &&
               responseError.ValueKind != JsonValueKind.Null
            ? ExtractErrorMessage(responseError)
            : null;
    }

    private static string? ExtractIncompleteReason(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("incomplete_details", out var details) ||
            details.ValueKind != JsonValueKind.Object ||
            !details.TryGetProperty("reason", out var reason) ||
            reason.ValueKind != JsonValueKind.String)
            return null;

        return reason.GetString() switch
        {
            "max_output_tokens" => "已达到输出 token 上限",
            "content_filter" => "内容过滤器提前终止了输出",
            var value => value
        };
    }

    private static string? ExtractErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
            return error.GetString();
        if (error.ValueKind != JsonValueKind.Object)
            return null;
        if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString();
        if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
            return code.GetString();
        return null;
    }

    private static Uri BuildChatCompletionsEndpoint(string baseUrl)
    {
        return BuildApiEndpoint(baseUrl, "chat/completions");
    }

    private static Uri BuildResponsesEndpoint(string baseUrl)
    {
        return BuildApiEndpoint(baseUrl, "responses");
    }

    private static Uri BuildApiEndpoint(string baseUrl, string endpointPath)
    {
        var normalized = baseUrl.Trim().TrimEnd('/');
        foreach (var knownSuffix in new[] { "/chat/completions", "/responses" })
        {
            if (normalized.EndsWith(knownSuffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^knownSuffix.Length].TrimEnd('/');
                break;
            }
        }

        return new Uri($"{normalized}/{endpointPath}", UriKind.Absolute);
    }

    private static string BuildUserContent(string sourceText, string? existingTranslation, TranslationAction action)
    {
        var source = sourceText.Trim();
        if (action == TranslationAction.Translate)
            return source;

        if (action == TranslationAction.Explain)
        {
            return string.IsNullOrWhiteSpace(existingTranslation)
                ? $"Source text:\n{source}"
                : $"Source text:\n{source}\n\nCurrent translation:\n{existingTranslation.Trim()}";
        }

        return string.IsNullOrWhiteSpace(existingTranslation)
            ? $"Source text:\n{source}"
            : $"Source text:\n{source}\n\nDraft translation to polish:\n{existingTranslation.Trim()}";
    }

    private static string BuildSystemPrompt(
        string targetLanguage,
        TranslationAction action,
        IReadOnlyList<TerminologyEntry> terminology)
    {
        var glossary = BuildGlossaryPrompt(terminology);
        if (action == TranslationAction.Explain)
        {
            return $"""
You help a Chinese-speaking reader fully understand foreign-language text. Explain the source in Simplified Chinese.

Rules:
1. Start with the plain-language meaning in one short paragraph.
2. Add at most four short bullet points, and only for wording, tone, ambiguity, cultural context, or technical terms that genuinely need explanation.
3. For straightforward text, stop after one to three sentences.
4. Mention OCR only when a character actually looks suspicious; never add a generic "OCR is correct" note.
5. Preserve model IDs, API fields, commands, paths, URLs, shortcuts, numbers, and units exactly.
6. Keep the response compact and practical. Do not reveal hidden reasoning.
{glossary}
""";
        }

        if (action == TranslationAction.Polish)
        {
            return $"""
You are a meticulous bilingual editor. Produce a polished {targetLanguage} translation from the source and optional draft.

Rules:
1. Correct mistranslations, omissions, awkward wording, OCR line-break artifacts, and inconsistent terminology.
2. Preserve names, model IDs, API fields, commands, paths, URLs, shortcuts, placeholders, numbers, units, and code.
3. Preserve paragraph structure and meaning.
4. Return only the polished translation. Do not add notes or reveal reasoning.
{glossary}
""";
        }

        return $"""
You are a precise professional translator. Translate the user's text into {targetLanguage}.

Rules:
1. Preserve names, model IDs, API fields, commands, file paths, URLs, keyboard shortcuts, placeholders, numbers, units, and code when they should remain unchanged.
2. Preserve paragraph structure. Repair only obvious OCR line-break, spacing, and hyphenation artifacts.
3. Do not add facts, commentary, headings, quotation marks, or explanations.
4. Prefer natural, concise wording while remaining faithful to the source.
5. If the source is a single ambiguous word, give the most likely translation first and then up to three short common alternatives separated by "；".
6. Return only the translation. Do not reveal reasoning.
{glossary}
""";
    }

    private static string BuildGlossaryPrompt(IReadOnlyList<TerminologyEntry> terminology)
    {
        if (terminology.Count == 0)
            return string.Empty;

        var lines = terminology.Select(entry => $"- {entry.Source} => {entry.Target}");
        return "\nUse these personal terminology mappings when applicable:\n" + string.Join("\n", lines);
    }

    private static string ExtractApiError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? "API 请求失败";
                if (error.TryGetProperty("message", out var message))
                    return message.GetString() ?? "API 请求失败";
            }
        }
        catch
        {
        }

        return string.IsNullOrWhiteSpace(body)
            ? "API 请求失败"
            : body.Length > 300 ? body[..300] : body;
    }
}

public sealed class TranslationApiException : Exception
{
    public int StatusCode { get; }

    public TranslationApiException(int statusCode, string message)
        : base($"API {statusCode}: {message}")
    {
        StatusCode = statusCode;
    }
}
