namespace CodeExploder.Llm;

/// <summary>
/// Cheap token estimate used by the context packers (docs/06-llm-strategy.md):
/// budgets are ceilings, not precise counts, so chars/4 is good enough.
/// </summary>
public static class TokenEstimator
{
    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return Math.Max(1, text.Length / 4);
    }
}
