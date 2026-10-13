namespace Aurum.App.SharedKernel.Constants;

public static class ErrorMessages
{
    public const string NoPriceMessage =
        "No price has been received since the app started and none is stored yet. " +
        "The poller fills this on its next successful poll.";

    public const string UnexpectedErrorMessage = "An unexpected error occurred.";
}
