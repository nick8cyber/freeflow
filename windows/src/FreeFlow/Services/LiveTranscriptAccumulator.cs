namespace FreeFlow.Services;

/// <summary>
/// Accumulates realtime transcripts across voice-activity segments. The server's
/// interimInputTranscription text REPLACES the current segment's text (not a
/// delta), and a pause starts a new segment — without accumulation every new
/// segment would overwrite the transcript, losing the beginning of the dictation
/// (observed live, 2026-10-27). Mirrors the macOS accumulator semantics
/// (partialText per segment, committed segments joined with spaces).
/// </summary>
public sealed class LiveTranscriptAccumulator
{
    private readonly List<string> _committedSegments = [];
    private string _currentSegment = "";

    /// <summary>Server's interim text for the open segment (full replacement).</summary>
    public void FeedInterim(string text) => _currentSegment = text ?? "";

    /// <summary>Server's final text for the open segment, when it emits one.</summary>
    public void FeedFinal(string text) => _currentSegment = text ?? "";

    /// <summary>Closes the open segment into the committed text (idempotent when empty).</summary>
    public void EndSegment()
    {
        var trimmed = _currentSegment.Trim();
        if (trimmed.Length > 0)
            _committedSegments.Add(trimmed);
        _currentSegment = "";
    }

    /// <summary>Closes any open segment before a new one begins.</summary>
    public void StartSegment() => EndSegment();

    /// <summary>Everything transcribed so far: committed segments + the open one.</summary>
    public string Current()
    {
        var parts = new List<string>(_committedSegments);
        var current = _currentSegment.Trim();
        if (current.Length > 0) parts.Add(current);
        return string.Join(" ", parts);
    }

    /// <summary>Final transcript for the whole session.</summary>
    public string Final() => Current();
}
