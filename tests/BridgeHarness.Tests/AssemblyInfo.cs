using Xunit;

// 进程级测试会起真桥、抢同一组临时文件与同一根管道名；串行跑，别互相踩。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
