using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BenchmarkCS
{
    static class Program
    {
        private const int BufferSize = 128;

        private static async Task WriteFile(string path)
        {
            var junk = new byte[BufferSize];
            using (var file = File.Create(path))
            {
                for (var i = 1; i <= 10000; i++)
                {
                    await file.WriteAsync(junk, 0, junk.Length);
                }
            }
        }

        private static async Task ReadFile(string path)
        {
            var buffer = new byte[BufferSize];
            using (var file = File.OpenRead(path))
            {
                var reading = true;
                while (reading)
                {
                    var countRead = await file.ReadAsync(buffer, 0, buffer.Length);
                    reading = countRead > 0;
                }
            }
        }

        private static async Task Bench()
        {
            const string tmp = "tmp";
            var sw = new Stopwatch();
            sw.Start();
            for (var i = 1; i <= 10; i++)
            {
                await WriteFile(tmp);
                await ReadFile(tmp);
            }
            sw.Stop();
            Console.WriteLine($"C# methods completed in {sw.ElapsedMilliseconds} ms");
            File.Delete(tmp);
        }

        static void Main(string[] args)
            => Bench().Wait();
    }
}
