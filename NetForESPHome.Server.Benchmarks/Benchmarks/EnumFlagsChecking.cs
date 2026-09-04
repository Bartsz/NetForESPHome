//      | Method                                 | Mean       | Error    | StdDev   | Median     | Ratio | RatioSD | Allocated | Alloc Ratio |
//      |--------------------------------------- |-----------:|---------:|---------:|-----------:|------:|--------:|----------:|------------:|
//      | 'Empty benchmark'                      |   238.7 ns |  4.52 ns |  9.91 ns |   234.8 ns |  0.41 |    0.02 |         - |          NA |
//      | 'Bitwise & check'                      |   580.8 ns | 10.84 ns | 10.64 ns |   580.9 ns |  1.00 |    0.03 |         - |          NA |
//      | 'Bitwise & check & deref'              |   578.8 ns |  8.60 ns |  7.63 ns |   578.1 ns |  1.00 |    0.02 |         - |          NA |
//      | 'Bitwise & check & deref & null check' |   778.0 ns | 14.21 ns | 18.97 ns |   782.7 ns |  1.34 |    0.04 |         - |          NA |
//      | 'HasFlag() check'                      | 1,680.8 ns | 33.07 ns | 29.32 ns | 1,671.4 ns |  2.89 |    0.07 |         - |          NA |

namespace NetForESPHome.Server.Benchmarks.Benchmarks;

// 1) Your Flags enum
[Flags]
public enum RequestRecipient
{
    None = 0,
    StateManager = 1 << 0,
    AutomationService = 1 << 1,
    SchedulerService = 1 << 2,
    IntegrationService = 1 << 3,
    All = 0xFFFFFFF // all bits set
}

// 2) A tiny Request type carrying the flags
public class Request
{
    public RequestRecipient Recipients { get; }
    public Request(RequestRecipient recipients) => Recipients = recipients;
}

[MemoryDiagnoser]                // track memory, too
public class EnumFlagsChecking
{
    private Request[] _messages;

    [GlobalSetup]
    public void Setup()
    {
        // get only the single‐bit flags (exclude None and All)
        var allValues = Enum.GetValues(typeof(RequestRecipient)).Cast<RequestRecipient>();
        var singleFlags = allValues
            .Where(v => v != RequestRecipient.None
                     && v != RequestRecipient.All
                     && ((int)v & (int)v - 1) == 0)
            .ToArray();

        int bitCount = singleFlags.Length;             // e.g. 4
        var rnd = new Random(42);

        // build 100 Requests with rolling flags // e.g. 0b0001, 0b0010, 0b0100, 0b1000, 0b0001, ...
        _messages = Enumerable.Range(0, 1000)
            .Select(x =>
            {
                var bitShiftLeft = x % bitCount; // 0 to 3 for 4 bits
                return new Request((RequestRecipient)(1 << bitShiftLeft));
            })
            .ToArray();
    }

    [Benchmark(Description = "Empty benchmark")]
    public int EmptyBenchmark()
    {
        int count = 0;
        foreach (var req in _messages)
        {
            // do nothing
            count++;
        }
        return count;
    }

    [Benchmark(Baseline = true, Description = "Bitwise & check")]
    public int BitwiseCheck()
    {
        int count = 0;
        foreach (var req in _messages)
        {
            // test the StateManager bit
            if ((req.Recipients & RequestRecipient.StateManager) != 0)
                count++;
            if ((req.Recipients & RequestRecipient.AutomationService) != 0)
                count++;
            if ((req.Recipients & RequestRecipient.SchedulerService) != 0)
                count++;
            if ((req.Recipients & RequestRecipient.IntegrationService) != 0)
                count++;
        }
        return count;
    }

    [Benchmark( Description = "Bitwise & check & deref")]
    public int BitwiseCheckAndDeref()
    {
        int count = 0;
        foreach (var req in _messages)
        {
            var recipients = req.Recipients; // dereference once
            // test the StateManager bit
            if ((recipients & RequestRecipient.StateManager) != 0)
                count++;
            if ((recipients & RequestRecipient.AutomationService) != 0)
                count++;
            if ((recipients & RequestRecipient.SchedulerService) != 0)
                count++;
            if ((recipients & RequestRecipient.IntegrationService) != 0)
                count++;
        }
        return count;
    }

    [Benchmark( Description = "Bitwise & check & deref & null check")]
    public int BitwiseCheckAndDerefNullCheck()
    {
        int count = 0;
        foreach (var req in _messages)
        {
            if (req == null) continue; // null check
            var recipients = req.Recipients; // dereference once
            // test the StateManager bit
            if ((recipients & RequestRecipient.StateManager) != 0)
                count++;
            if ((recipients & RequestRecipient.AutomationService) != 0)
                count++;
            if ((recipients & RequestRecipient.SchedulerService) != 0)
                count++;
            if ((recipients & RequestRecipient.IntegrationService) != 0)
                count++;
        }
        return count;
    }

    [Benchmark(Description = "HasFlag() check")]
    public int HasFlagCheck()
    {
        int count = 0;
        foreach (var req in _messages)
        {
            if (req.Recipients.HasFlag(RequestRecipient.StateManager))
                count++;
            if (req.Recipients.HasFlag(RequestRecipient.AutomationService))
                count++;
            if (req.Recipients.HasFlag(RequestRecipient.SchedulerService))
                count++;
            if (req.Recipients.HasFlag(RequestRecipient.IntegrationService))
                count++;
        }
        return count;
    }
}
