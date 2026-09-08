using RealtimeSubtitle.Core.Audio;

namespace RealtimeSubtitle.Tests;

public class RingBufferTests
{
    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var ring = new RingBuffer<int>(8);
        ring.TryWrite(new[] { 1, 2, 3, 4, 5 });
        var dest = new int[3];

        Assert.Equal(3, ring.TryRead(dest));
        Assert.Equal(new[] { 1, 2, 3 }, dest);

        var dest2 = new int[3];
        Assert.Equal(2, ring.TryRead(dest2));
        Assert.Equal(new[] { 4, 5, 0 }, dest2);
        Assert.Equal(0, ring.TryRead(dest2));
    }

    [Fact]
    public void Read_WrapsAround()
    {
        var ring = new RingBuffer<int>(4);
        ring.TryWrite(new[] { 1, 2, 3, 4 });
        var dest = new int[2];
        Assert.Equal(2, ring.TryRead(dest)); // consumed [1,2], head now at index 2

        ring.TryWrite(new[] { 5, 6 });       // wraps: [5,6,3,4]
        var dest2 = new int[4];
        Assert.Equal(4, ring.TryRead(dest2));
        Assert.Equal(new[] { 3, 4, 5, 6 }, dest2);
    }

    [Fact]
    public void WriteWhenFull_DropsBatch()
    {
        var ring = new RingBuffer<int>(4);
        Assert.Equal(4, ring.TryWrite(new[] { 1, 2, 3, 4 }));
        Assert.Equal(0, ring.TryWrite(new[] { 5, 6, 7, 8, 9 }));
        Assert.Equal(4, ring.Count);
    }

    [Fact]
    public void TryRead_LargerDestination_ReturnsAvailable()
    {
        var ring = new RingBuffer<int>(8);
        ring.TryWrite(new[] { 10, 20, 30 });
        var dest = new int[10];

        Assert.Equal(3, ring.TryRead(dest));
        Assert.Equal(new[] { 10, 20, 30, 0, 0, 0, 0, 0, 0, 0 }, dest);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Clear_ResetToEmpty()
    {
        var ring = new RingBuffer<int>(4);
        ring.TryWrite(new[] { 1, 2, 3 });
        ring.Clear();
        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.TryRead(new int[2]));
    }
}