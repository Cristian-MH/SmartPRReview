namespace SmartPRReview.Application.AI;

// Reservations include repeated continuation input and maximum reasoning/output tokens.
// Never release a reservation after an uncertain provider failure.
public sealed class ReviewBudget(ReviewLimits limits)
{
    private readonly object gate = new();
    private int calls, input, output;
    private bool retried;
    public void Reserve(int inputTokens, int outputTokens, int stageLimit, int contextLimit)
    {
        lock (gate)
        {
            if (inputTokens < 0 || outputTokens <= 0 || inputTokens > stageLimit ||
                (long)inputTokens + outputTokens > contextLimit || calls >= limits.MaxCalls ||
                (long)input + inputTokens > limits.MaxInput || (long)output + outputTokens > limits.MaxOutput)
                throw new AiRequestException("Presupuesto de tokens insuficiente; revisión incompleta.");
            calls++;
            input += inputTokens;
            output += outputTokens;
        }
    }
    public bool TryRetry()
    {
        lock (gate)
        {
            if (retried) return false;
            retried = true;
            return true;
        }
    }
}
