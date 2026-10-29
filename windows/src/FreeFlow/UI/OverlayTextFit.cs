namespace FreeFlow.UI;

/// <summary>
/// Fits a transcript into the overlay's text block by trimming from its START so
/// the newest words stay visible — pure tail-ellipsis would hide exactly the part
/// being dictated right now. Extracted from OverlayForm so the search direction
/// stays regression-tested: the predicate "suffix starting at mid fits" is
/// monotone (a larger mid means a shorter suffix), so the SMALLEST fitting mid is
/// the longest visible tail. Searching for the largest fitting mid instead
/// collapsed the transcript to an ellipsis plus one character.
/// </summary>
public static class OverlayTextFit
{
    public static string FitTail(string text, Rectangle rect, Font font)
    {
        if (text.Length == 0) return text;
        if (MeasuredHeight(text, rect, font) <= rect.Height) return text;
        int lo = 1, hi = text.Length - 1;
        int best = text.Length - 1; // fallback: ellipsis + the newest character
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (MeasuredHeight("…" + text[mid..], rect, font) <= rect.Height)
            {
                best = mid;
                hi = mid - 1;
            }
            else lo = mid + 1;
        }
        return "…" + text[best..];
    }

    public static int MeasuredHeight(string text, Rectangle rect, Font font) =>
        TextRenderer.MeasureText(text, font, rect.Size,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis).Height;
}
