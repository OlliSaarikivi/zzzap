using System;
using System.Collections.Generic;
using Microsoft.Z3;
using System.Linq;

namespace Zzzap
{
    class BasicBenchmarks
    {
        public static void ParallelizeMax()
        {
            var rand = new Random();
            var input = new List<int>();
            for (int i = 0; i < 1000000; i++)
            {
                input.Add(rand.Next());
            }
            var parallelResult = input.Parasail<int, int>((s, i) => i > s ? i : s)(int.MinValue);
            var sequentialResult = input.Aggregate(int.MinValue, (s, i) => i > s ? i : s);
            Console.WriteLine($"{parallelResult} {sequentialResult}");
        }
    }
}
