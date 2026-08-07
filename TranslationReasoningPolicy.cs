namespace LightTranslate;

internal static class TranslationReasoningPolicy
{
    public static string GetEffort(TranslationAction action)
    {
        var settings = SettingsStore.Load();
        return action switch
        {
            TranslationAction.Explain => settings.ExplainEffort,
            TranslationAction.Polish => settings.PolishEffort,
            _ => settings.TranslateEffort
        };
    }

    public static string GetDisplayEffort(TranslationAction action)
    {
        return GetEffort(action).ToUpperInvariant();
    }
}
