using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
        var endpoint = BuildChatCompletionsEndpoint(settings.BaseUrl);
        var payload = new
        {
            model = settings.Model,
            reasoning_effort = TranslationReasoningPolicy.GetEffort(action),
            thinking = new { type = "enabled" },
            stream = true,
            max_tokens = 4096,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = BuildSystemPrompt(targetLanguage, action, terminology)
                },
                new
                {
                    role = "user",
                    content = BuildUserContent(sourceText, existingTranslation, action)
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("LightTranslate/0.5.6");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = JsonContent.Create(payload);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new TranslationApiException((int)response.StatusCode, ExtractApiError(errorBody));
        }

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

            var piece = ExtractStreamContent(data);
            if (string.IsNullOrEmpty(piece))
                continue;

            result.Append(piece);
            onDelta?.Report(piece);
        }

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

    private static string? ExtractStreamContent(string data)
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

    private static Uri BuildChatCompletionsEndpoint(string baseUrl)
    {
        var normalized = baseUrl.Trim().TrimEnd('/');
        if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return new Uri(normalized, UriKind.Absolute);

        return new Uri(normalized + "/chat/completions", UriKind.Absolute);
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
