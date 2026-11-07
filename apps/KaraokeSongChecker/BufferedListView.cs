namespace KaraokeSongChecker;

/// <summary>
/// A ListView that paints off-screen first, so adding, removing or sorting items doesn't flicker.
/// </summary>
internal sealed class BufferedListView : ListView
{
    public BufferedListView()
    {
        // DoubleBuffered on a ListView turns on the native LVS_EX_DOUBLEBUFFER style.
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
}
