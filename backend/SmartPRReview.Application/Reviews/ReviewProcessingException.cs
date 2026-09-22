namespace SmartPRReview.Application.Reviews;

public sealed class ReviewProcessingException(string message, Exception? innerException = null)
    : Exception(message, innerException);

