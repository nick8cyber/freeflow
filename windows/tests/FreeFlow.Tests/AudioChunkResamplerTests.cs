using FreeFlow.Services;
using Xunit;

namespace FreeFlow.Tests;

public class AudioChunkResamplerTests
{
    [Fact]
    public void SixteenKiloherzMonoIsPassthrough()
    {
        var resampler = new AudioChunkResampler(16000, 1);
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6 };
        Assert.Same(pcm, resampler.Convert(pcm));
    }

    [Fact]
    public void EmptyChunkReturnsNull()
    {
        var resampler = new AudioChunkResampler(48000, 2);
        Assert.Null(resampler.Convert([]));
    }

    [Fact]
    public void FortyEightKiloherzStereoConstantKeepsValueAndLengthRatio()
    {
        var resampler = new AudioChunkResampler(48000, 2);
        var inFrames = 4800; // 100 ms at 48 kHz
        var pcm = new byte[inFrames * 4];
        for (var f = 0; f < inFrames; f++)
        {
            BitConverter.GetBytes((short)1000).CopyTo(pcm, f * 4);
            BitConverter.GetBytes((short)1000).CopyTo(pcm, f * 4 + 2);
        }
        var outPcm = resampler.Convert(pcm);
        Assert.NotNull(outPcm);
        var outFrames = outPcm!.Length / 2;
        // 48000 → 16000: exactly a third of the input frames.
        Assert.Equal(inFrames / 3, outFrames);
        for (var i = 0; i < outFrames; i++)
            Assert.Equal(1000, BitConverter.ToInt16(outPcm, i * 2));
    }

    [Fact]
    public void FortyFourKiloherzStereoConstantKeepsMean()
    {
        var resampler = new AudioChunkResampler(44100, 2);
        var inFrames = 4410;
        var pcm = new byte[inFrames * 4];
        for (var f = 0; f < inFrames; f++)
        {
            BitConverter.GetBytes((short)-2000).CopyTo(pcm, f * 4);
            BitConverter.GetBytes((short)-2000).CopyTo(pcm, f * 4 + 2);
        }
        var outPcm = resampler.Convert(pcm);
        Assert.NotNull(outPcm);
        var outFrames = outPcm!.Length / 2;
        Assert.True(outFrames > inFrames * 16 / 44.1 * 0.9);
        foreach (var i in Enumerable.Range(0, outFrames))
            Assert.Equal(-2000, BitConverter.ToInt16(outPcm, i * 2));
    }

    [Fact]
    public void RampAcrossChunksStaysMonotonic()
    {
        var resampler = new AudioChunkResampler(48000, 2);
        short value = 0;
        var outputs = new List<short>();
        for (var chunk = 0; chunk < 4; chunk++)
        {
            var inFrames = 480;
            var pcm = new byte[inFrames * 4];
            for (var f = 0; f < inFrames; f++)
            {
                BitConverter.GetBytes(value).CopyTo(pcm, f * 4);
                BitConverter.GetBytes(value).CopyTo(pcm, f * 4 + 2);
                value++;
            }
            var outPcm = resampler.Convert(pcm)!;
            for (var i = 0; i < outPcm.Length; i += 2)
                outputs.Add(BitConverter.ToInt16(outPcm, i));
        }
        for (var i = 1; i < outputs.Count; i++)
            Assert.True(outputs[i] >= outputs[i - 1],
                $"output not monotonic at {i}: {outputs[i - 1]} -> {outputs[i]}");
    }
}
