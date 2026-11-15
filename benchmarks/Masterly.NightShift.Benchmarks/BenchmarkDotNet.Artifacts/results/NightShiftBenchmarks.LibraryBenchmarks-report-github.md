```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9818)
Intel Core Ultra 7 255H 2.00GHz, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.404
  [Host]    : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  .NET 10.0 : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Job=.NET 10.0  Runtime=.NET 10.0  Categories=Run a job  

```
| Method             | Mean     | Error    | StdDev   | Median   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |---------:|---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Hangfire_RunSync   | 700.0 ns | 19.76 ns | 57.02 ns | 692.2 ns |  1.01 |    0.11 | 0.1316 |    1656 B |        1.00 |
| NightShift_RunSync | 461.0 ns | 15.84 ns | 46.71 ns | 466.8 ns |  0.66 |    0.08 | 0.0534 |     672 B |        0.41 |
| Quartz_Run         | 508.0 ns | 23.93 ns | 69.03 ns | 484.7 ns |  0.73 |    0.11 | 0.0572 |     720 B |        0.43 |
