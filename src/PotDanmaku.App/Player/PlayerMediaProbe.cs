using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using PotDanmaku.App.Interop;
using PotDanmaku.Core;

namespace PotDanmaku.App.Player;

internal sealed record MediaIdentity(string? Path, long Sequence, double ObservedSeconds);

// PotPlayer's string API replies with a bounded UTF-8 WM_COPYDATA payload.
internal sealed class PlayerMediaProbe : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData { public nuint Command; public int Length; public nint Data; }
    private readonly HwndSource receiver;
    private readonly PlayerTarget target;
    private readonly nint receiverHandle;
    private MediaIdentity? latest;
    private long sequence;
    public MediaIdentity? Latest => Volatile.Read(ref latest);
    public PlayerMediaProbe(PlayerTarget target)
    {
        this.target = target;
        receiver = new HwndSource(new HwndSourceParameters("PotDanmaku media identity") { ParentWindow = new nint(-3), WindowStyle = 0 });
        receiver.AddHook(Receive); receiverHandle = receiver.Handle;
    }
    public void Request()
    {
        if (!Native.IsWindow(target.Handle)) return;
        Native.GetWindowThreadProcessId(target.Handle, out uint pid);
        if (pid == target.ProcessId)
            Native.SendMessageTimeout(target.Handle, 0x400, 0x6020, receiverHandle, 0x22, 30, out _);
    }
    private nint Receive(nint hwnd, int message, nint sender, nint data, ref bool handled)
    {
        if (message != 0x4A || data == 0 || (sender != 0 && sender != target.Handle)) return 0;
        var payload = Marshal.PtrToStructure<CopyData>(data);
        if (payload.Command != 0x6020 || payload.Length < 0 || payload.Length > 65536 || (payload.Length > 0 && payload.Data == 0)) return 0;
        handled = true;
        try
        {
            byte[] bytes = new byte[payload.Length];
            if (bytes.Length > 0) Marshal.Copy(payload.Data, bytes, 0, bytes.Length);
            string raw = new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0');
            Volatile.Write(ref latest, new(VideoFiles.Normalize(raw), ++sequence, PotPlayerClient.Now));
        }
        catch (DecoderFallbackException) { Volatile.Write(ref latest, new(null, ++sequence, PotPlayerClient.Now)); }
        return 1;
    }
    public void Dispose() { receiver.RemoveHook(Receive); receiver.Dispose(); }
}
