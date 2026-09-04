namespace NetForESPHome.Server.Benchmarks.Benchmarks;

public enum MyTypeEnum
{
    TypeA,
    TypeB,
    TypeC,
    TypeD,
    TypeE,
    TypeF,
    TypeG,
}
public interface IMyType { MyTypeEnum TypeEnum { get; } }

public class MyTypeA : IMyType { public MyTypeEnum TypeEnum => MyTypeEnum.TypeA; }
public class MyTypeB : IMyType { public MyTypeEnum TypeEnum => MyTypeEnum.TypeB; }
public class MyTypeC : IMyType { public MyTypeEnum TypeEnum => MyTypeEnum.TypeC; }
public class MyTypeD : IMyType { public MyTypeEnum TypeEnum => MyTypeEnum.TypeD; }
public class MyTypeE : IMyType { public MyTypeEnum TypeEnum => MyTypeEnum.TypeE; }
public class MyTypeF : IMyType { public MyTypeEnum TypeEnum => MyTypeEnum.TypeF; }
public class MyTypeG : IMyType { public MyTypeEnum TypeEnum => MyTypeEnum.TypeG; }


[MemoryDiagnoser]
public class TypeDetection
{
    //          BenchmarkDotNet v0.15.2 
    //          12th Gen Intel Core i7-1280P 2.00GHz, 1 CPU, 20 logical and 14 physical cores
    //          .NET SDK 9.0.302
    //      | Method                | Mean      | Error     | StdDev    | Allocated |
    //      |---------------------- |----------:|----------:|----------:|----------:|
    //      | SwitchStatementOnEnum | 14.301 ns | 0.1999 ns | 0.1870 ns |         - |
    //      | IfElseOnEnum          | 43.122 ns | 0.8717 ns | 1.0038 ns |         - |
    //      | PatternMatching       |  2.428 ns | 0.0677 ns | 0.0633 ns |         - |
    //      | SwitchStatementOnType |  2.528 ns | 0.0752 ns | 0.1192 ns |         - |

    List<IMyType> myObjects = new List<IMyType> { new MyTypeA(), new MyTypeB(), new MyTypeC(), new MyTypeD(), new MyTypeE(), new MyTypeF(), new MyTypeG() };

    [Benchmark]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0059:Unnecessary assignment of a value", Justification = "<Pending>")]
    public void SwitchStatementOnEnum()
    {
        foreach (var myObject in myObjects)
        {
            switch (myObject.TypeEnum)
            {
                case MyTypeEnum.TypeA:
                    var A = myObject as MyTypeA;
                    break;
                case MyTypeEnum.TypeB:
                    var B_ = myObject as MyTypeB;
                    break;
                case MyTypeEnum.TypeC:
                    var C = myObject as MyTypeC;
                    break;
                case MyTypeEnum.TypeD:
                    var D = myObject as MyTypeD;
                    break;
                case MyTypeEnum.TypeE:
                    var E = myObject as MyTypeE;
                    break;
                case MyTypeEnum.TypeF:
                    var F = myObject as MyTypeF;
                    break;
                case MyTypeEnum.TypeG:
                    var G = myObject as MyTypeG;
                    break;
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0059:Unnecessary assignment of a value", Justification = "<Pending>")]
    [Benchmark]
    public void IfElseOnEnum()
    {
        foreach (var myObject in myObjects)
        {
            if (myObject.TypeEnum == MyTypeEnum.TypeA) { var A = myObject as MyTypeA; }
            else if (myObject.TypeEnum == MyTypeEnum.TypeB) { var B_ = myObject as MyTypeB; }
            else if (myObject.TypeEnum == MyTypeEnum.TypeC) { var C = myObject as MyTypeC; }
            else if (myObject.TypeEnum == MyTypeEnum.TypeD) { var D = myObject as MyTypeD; }
            else if (myObject.TypeEnum == MyTypeEnum.TypeE) { var E = myObject as MyTypeE; }
            else if (myObject.TypeEnum == MyTypeEnum.TypeF) { var F = myObject as MyTypeF; }
            else if (myObject.TypeEnum == MyTypeEnum.TypeG) { var G = myObject as MyTypeG; }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0059:Unnecessary assignment of a value", Justification = "<Pending>")]
    [Benchmark]
    public void PatternMatching()
    {
        foreach (var myObject in myObjects)
        {
            if (myObject is MyTypeA a) { var A = a; }
            else if (myObject is MyTypeB b) { var B_ = b; }
            else if (myObject is MyTypeC c) { var C = c; }
            else if (myObject is MyTypeD d) { var D = d; }
            else if (myObject is MyTypeE e) { var E = e; }
            else if (myObject is MyTypeF f) { var F = f; }
            else if (myObject is MyTypeG g) { var G = g; }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0059:Unnecessary assignment of a value", Justification = "<Pending>")]
    [Benchmark]
    public void SwitchStatementOnType()
    {
        foreach (var myObject in myObjects)
        {
            switch (myObject)
            {
                case MyTypeA a:
                    var A = a;
                    break;
                case MyTypeB b:
                    var B_ = b;
                    break;
                case MyTypeC c:
                    var C = c;
                    break;
                case MyTypeD d:
                    var D = d;
                    break;
                case MyTypeE e:
                    var E = e;
                    break;
                case MyTypeF f:
                    var F = f;
                    break;
                case MyTypeG g:
                    var G = g;
                    break;
            }
        }
    }
};