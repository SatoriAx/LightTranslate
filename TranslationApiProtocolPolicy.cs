namespace LightTranslate;

public enum TranslationApiProtocol
{
    Responses,
    ChatCompletions
}

public static class TranslationApiProtocolPolicy
{
    public const string AutoSetting = "auto";
    public const string ResponsesSetting = "responses";
    public const string ChatCompletionsSetting = "chat-completions";

    public static string NormalizeSetting(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            ResponsesSetting => ResponsesSetting,
            ChatCompletionsSetting => ChatCompletionsSetting,
            "chat" => ChatCompletionsSetting,
            "chatcompletions" => ChatCompletionsSetting,
            _ => AutoSetting
        };
    }

    public static TranslationApiProtocol Resolve(AppSettings settings)
    {
        var configured = NormalizeSetting(settings.ApiProtocol);
        if (configured == ResponsesSetting)
            return TranslationApiProtocol.Responses;
        if (configured == ChatCompletionsSetting)
            return TranslationApiProtocol.ChatCompletions;

        var baseUrl = (settings.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            return TranslationApiProtocol.Responses;
        if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return TranslationApiProtocol.ChatCompletions;

        return IsOfficialDeepSeekFlash(settings)
            ? TranslationApiProtocol.Responses
            : TranslationApiProtocol.ChatCompletions;
    }

    public static string GetDisplayName(TranslationApiProtocol protocol)
    {
        return protocol == TranslationApiProtocol.Responses
            ? "Responses API"
            : "Chat Completions";
    }

    public static string GetResolvedDisplayName(AppSettings settings)
    {
        return GetDisplayName(Resolve(settings));
    }

    private static bool IsOfficialDeepSeekFlash(AppSettings settings)
    {
        if (!(settings.Model ?? string.Empty).Trim().Equals("deepseek-v4-flash", StringComparison.OrdinalIgnoreCase))
            return false;

        return Uri.TryCreate((settings.BaseUrl ?? string.Empty).Trim(), UriKind.Absolute, out var uri) &&
               uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase);
    }
}
