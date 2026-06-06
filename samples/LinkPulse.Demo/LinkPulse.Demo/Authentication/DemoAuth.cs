namespace LinkPulseDemo.Authentication;

/// <summary>Well-known authorization names shared across the Program wiring, the login page, and the dashboard page.</summary>
internal static class DemoAuth
{
    /// <summary>The authorization policy the LinkPulse dashboard is gated on.</summary>
    public const string ViewerPolicy = "LinkPulseViewer";

    /// <summary>The role the demo grants on sign-in, required by <see cref="ViewerPolicy"/>.</summary>
    public const string OperatorRole = "LinkPulseOperator";
}
