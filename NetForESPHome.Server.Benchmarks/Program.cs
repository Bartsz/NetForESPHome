using BenchmarkDotNet.Running;
using NetForESPHome.Server.Benchmarks.Benchmarks;

namespace NetForESPHome.Server.Benchmarks;

internal sealed class Program
{
    static void Main(string[] args)
    {
        //MessageTypeConversion benchmark = new MessageTypeConversion();

        //benchmark.UsingDictionary();
        //benchmark.UsingIfs();
        //benchmark.UsingList();
        //benchmark.UsingProtobufReflections();

        BenchmarkRunner.Run<MessageTypeConversion>();
    }
}
