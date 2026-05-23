namespace RockBot.UserProxy.WorkIqAuth;

/// <summary>
/// Thrown when the UI-tier WorkIQ device-code flow cannot complete.
/// The <see cref="Code"/> property carries a stable identifier UI code
/// can switch on without parsing message text.
/// </summary>
public sealed class WorkIqAuthFlowException : Exception
{
    public string Code { get; }

    public WorkIqAuthFlowException(string code, string message) : base(message)
    {
        Code = code;
    }

    public WorkIqAuthFlowException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public static class Codes
    {
        /// <summary>User cancelled before completing sign-in.</summary>
        public const string UserCancelled = "user_cancelled";

        /// <summary>Device code expired before the user signed in.</summary>
        public const string ChallengeExpired = "challenge_expired";

        /// <summary>Settings are missing TenantId or ClientId.</summary>
        public const string NotConfigured = "not_configured";

        /// <summary>MSAL returned an error during token acquisition.</summary>
        public const string MsalError = "msal_error";

        /// <summary>Publishing the cache to the bus failed.</summary>
        public const string PublishFailed = "publish_failed";

        /// <summary>Catch-all for unexpected failures.</summary>
        public const string Unknown = "unknown";
    }
}
