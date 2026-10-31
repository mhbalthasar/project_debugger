// [debug-harness] 正式工程的 csproj 开着 ImplicitUsings；验证台关着（见 Directory.Build.props）。
// 这些文件是从正式工程逐字复制的，不该为了编译再加一堆 using —— 用一份全局 using 补齐
// 与 ImplicitUsings 等价的那一组，复制品本身就能保持"逐字"。
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
