using BenchmarkDotNet.Order;
using Google.Protobuf;
using NetForESPHome.DomainModel.Native;

namespace NetForESPHome.Server.Benchmarks.Benchmarks;

//      | Method                        | CurrentMessage      | Mean        | Error       | StdDev      | Median      | Gen0    | Allocated |
//      |------------------------------ |-------------------- |------------:|------------:|------------:|------------:|--------:|----------:|
//      | CalculateSize                 | { }                 |    341.9 ns |     6.80 ns |     9.54 ns |    340.4 ns |       - |         - |
//      | WriteToPreAllocatedHeapBuffer | { }                 |  3,000.5 ns |    18.17 ns |    16.11 ns |  3,001.6 ns |       - |         - |
//      | WriteToStackAllocSpan         | { }                 |  3,313.4 ns |    15.82 ns |    13.21 ns |  3,311.5 ns |       - |         - |
//      | WriteToArray                  | { }                 | 14,407.2 ns |   143.43 ns |   134.17 ns | 14,382.3 ns |  7.0038 |   88000 B |
//      | ToByteArray                   | { }                 | 14,777.9 ns |   293.56 ns |   599.67 ns | 14,452.4 ns |  7.0038 |   88000 B |
//      | CalculateSize                 | { "o(...)}" } [136] | 13,656.2 ns |   183.37 ns |   171.52 ns | 13,636.8 ns |       - |         - |
//      | WriteToPreAllocatedHeapBuffer | { "o(...)}" } [136] | 46,451.3 ns |   816.39 ns |   723.71 ns | 46,233.4 ns |       - |         - |
//      | WriteToStackAllocSpan         | { "o(...)}" } [136] | 48,080.5 ns |   850.00 ns |   795.09 ns | 47,926.7 ns |       - |         - |
//      | ToByteArray                   | { "o(...)}" } [136] | 60,456.8 ns |   950.72 ns |   842.79 ns | 60,542.2 ns | 13.9771 |  176000 B |
//      | WriteToArray                  | { "o(...)}" } [136] | 60,559.3 ns | 1,191.59 ns | 1,889.99 ns | 60,256.1 ns | 13.9160 |  176000 B |


[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class ProtobufSerialization
{
    private const int LoopCount = 1000;
    private static PingResponse _shortMessage = new() { };
    private static ListEntitiesBinarySensorResponse _longMessage = new()
    {
        ObjectId = "binarySensor.ObjectId",
        Key = 664645,
        Name = "BinarySensor Name",
    };


    IEnumerable<IMessage> Messages { get; set; } = [_shortMessage, _longMessage];  // This property will provide the values for the [ParamsSource] 

    [ParamsSource(nameof(Messages))]
    public static IMessage CurrentMessage { get; set; } // The parameter that will receive the message

    private byte[] _preAllocatedBuffer = new byte[1024]; // Pre-allocated buffer for WriteTo(byte[])


    /// <summary>
    /// Benchmarks the IMessage.CalculateSize() method.
    /// This should be very fast and allocate minimal memory.
    /// </summary>
    [Benchmark]
    public void CalculateSize()
    {
        var counter = 0;
        for (int i = 0; i < LoopCount; i++)
        {
            counter += CalculateSizeFunc();
        }
    }

    /// <summary> To include overhead of calling a function </summary>
    private static int CalculateSizeFunc()
    {
        return CurrentMessage.CalculateSize();
    }

    /// <summary>
    /// Benchmarks the IMessage.ToByteArray() method.
    /// This involves heap allocation for each byte array created.
    /// </summary>
    [Benchmark]
    public void ToByteArray()
    {
        var counter = 0;

        for (int i = 0; i < LoopCount; i++)
        {
            counter += ToByteArrayFunc().Length;
        }
    }

    /// <summary> To include overhead of calling a function </summary>
    private static byte[] ToByteArrayFunc()
    {
        return CurrentMessage.ToByteArray(); // Allocates a new byte array each time
    }

    /// <summary>
    /// Benchmarks writing to a stack-allocated Span<byte> using IMessage.WriteTo(Span<byte>).
    /// This should avoid heap allocations for the message data itself,
    /// but requires knowing the size beforehand.
    /// </summary>
    [Benchmark]
    public void WriteToStackAllocSpan()
    {
        var counter = 0;

        for (int i = 0; i < LoopCount; i++)
        {
            counter += WriteToStackAllocSpanFunc();
        }
    }

    /// <summary> To include overhead of calling a function </summary>
    private static int WriteToStackAllocSpanFunc()
    {
        int writtenBytes = 0;
        int size = CurrentMessage.CalculateSize();
        Span<byte> buffer = stackalloc byte[size];

        CurrentMessage.WriteTo(buffer);

        return size;
    }

    [Benchmark]
    public void WriteToArray()
    {
        var counter = 0;

        for (int i = 0; i < LoopCount; i++)
        {
            counter += WriteToArrayFunc().Length;
        }
    }

    /// <summary> To include overhead of calling a function </summary>
    private static byte[] WriteToArrayFunc()
    {
        return CurrentMessage.ToByteArray();
    }


    /// Benchmarks writing to a pre-allocated heap array using IMessage.WriteTo(byte[] buffer).
    /// This avoids per-call allocations but requires a reusable buffer.
    /// </summary>
    [Benchmark]
    public void WriteToPreAllocatedHeapBuffer()
    {
        var counter = 0;

        for (int i = 0; i < LoopCount; i++)
        {
            counter += WriteToPreAllocatedHeapBufferFunc();
        }
    }

    /// <summary> To include overhead of calling a function </summary>
    private int WriteToPreAllocatedHeapBufferFunc()
    {
        var sizedSpan = _preAllocatedBuffer.AsSpan(0, CurrentMessage.CalculateSize());
        CurrentMessage.WriteTo(sizedSpan);
        return sizedSpan.Length;
    }
}