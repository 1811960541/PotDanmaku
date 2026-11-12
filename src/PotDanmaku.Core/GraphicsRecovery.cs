namespace PotDanmaku.Core;

/// <summary>Only known transient graphics failures may be retried automatically.</summary>
public static class GraphicsRecovery
{
    public const int MaxAttempts = 5;
    public static bool IsRecoverable(int errorCode) => unchecked((uint)errorCode) is
        0x887A0005 or 0x887A0006 or 0x887A0007 or 0x887A0020 or 0x8899000C;
    public static int RetryDelayMs(int attempt) => 500 * (1 << Math.Clamp(attempt - 1, 0, 4));
}
