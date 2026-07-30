namespace CodeExploder.Llm;

/// <summary>A chat call failed (transport error, non-2xx status, or malformed response).</summary>
public sealed class LlmException : Exception
{
    public LlmException()
    {
    }

    public LlmException(string message)
        : base(message)
    {
    }

    public LlmException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The model consumed the whole output budget without producing content
/// (finish_reason "length" with empty content — a reasoning model burning tokens).
/// Retryable; treating it as empty output would read as "no findings".
/// </summary>
public sealed class LlmStarvedException : Exception
{
    public LlmStarvedException()
    {
    }

    public LlmStarvedException(string message)
        : base(message)
    {
    }

    public LlmStarvedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
