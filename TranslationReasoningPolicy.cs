namespace LightTranslate;

internal static class TranslationReasoningPolicy
{
    public static string GetEffort(TranslationAction action)
    {
        return action == TranslationAction.Translate ? "high" : "max";
    }

    public static string GetDisplayEffort(TranslationAction action)
    {
        return GetEffort(action).ToUpperInvariant();
    }
}
