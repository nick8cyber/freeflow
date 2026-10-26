using System.IO;

namespace FreeFlow.Services;

/// <summary>
/// Stateful per-chunk converter from whatever format the capture ladder opened
/// (typically 48 kHz or 44.1 kHz stereo) to the 16 kHz mono PCM16 that realtime
/// streaming declares. Linear interpolation with a carried fractional position
/// and last-frame memory keeps chunk boundaries seamless. The capture ladder
/// usually opens at 16 kHz anyway — then Convert is a passthrough.
/// </summary>
public sealed class AudioChunkResampler
{
    private readonly int _inRate;
    private readonly int _inChannels;
    private readonly double _step;
    private readonly double[] _lastFrame; // per-channel previous input frame
    private long _consumed;               // absolute input frames consumed so far
    private double _nextOutPos;           // absolute input-frame coordinate of the next output frame

    public AudioChunkResampler(int inRate, int inChannels)
    {
        if (inRate <= 0 || inChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(inRate), "Capture format must be positive");
        _inRate = inRate;
        _inChannels = inChannels;
        _step = (double)inRate / 16000;
        _lastFrame = new double[inChannels];
    }

    /// <summary>16 kHz mono PCM16 for this input chunk; null for empty input.</summary>
    public byte[]? Convert(byte[] pcm16)
    {
        var bytesPerFrame = 2 * _inChannels;
        var inFrames = pcm16.Length / bytesPerFrame;
        if (inFrames == 0) return null;
        if (_inRate == 16000 && _inChannels == 1) return pcm16;

        var output = new MemoryStream((int)(inFrames / _step * 2) + 2);
        // Emit while both interpolation points p and p+1 exist: p+1 must land on an
        // already-captured absolute frame (the carried frame covers p == _consumed-1).
        var lastAvailableAbs = _consumed + inFrames - 1;
        while (_nextOutPos + 1 <= lastAvailableAbs)
        {
            var p = (long)Math.Floor(_nextOutPos);
            var frac = _nextOutPos - p;
            double sum = 0;
            for (var ch = 0; ch < _inChannels; ch++)
            {
                var a = p < _consumed ? _lastFrame[ch] : ReadFrame(pcm16, inFrames, (int)(p - _consumed), ch);
                var b = ReadFrame(pcm16, inFrames, (int)(p + 1 - _consumed), ch);
                sum += (a + (b - a) * frac) / _inChannels;
            }
            var clamped = Math.Clamp((int)Math.Round(sum), short.MinValue, short.MaxValue);
            output.Write(BitConverter.GetBytes((short)clamped));
            _nextOutPos += _step;
        }
        for (var ch = 0; ch < _inChannels; ch++)
            _lastFrame[ch] = ReadFrame(pcm16, inFrames, inFrames - 1, ch);
        _consumed += inFrames;
        return output.ToArray();
    }

    private double ReadFrame(byte[] pcm16, int inFrames, int frame, int channel)
    {
        if (frame < 0 || frame >= inFrames) return _lastFrame[channel];
        var offset = frame * 2 * _inChannels + channel * 2;
        return BitConverter.ToInt16(pcm16, offset);
    }
}
