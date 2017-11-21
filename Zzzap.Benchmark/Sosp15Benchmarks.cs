using System;
using System.Numerics;
using Microsoft.Z3;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

namespace Zzzap
{
    using VT = ValueTuple;
    class Sosp15Benchmarks
    {
        private static List<(int op, int time)> RandomGithubOps(int num = 1000000)
        {
            var rand = new Random();
            var ops = new List<(int op, int time)>();
            var time = 0;
            for (int i = 0; i < num; i++)
            {
                time += rand.Next(100);
                ops.Add((op: rand.Next(5), time: time));
            }
            return ops;
        }

        public static void ParallelizeG1()
        {
            // "Return all repositories with only push commands"
            // Push = 0
            var input = RandomGithubOps();
            var parallelResult = input.Parasail<bool, (int op, int time)>((s, i) => s ? i.op == 0 : s)(true);
            var sequentialResult = input.Aggregate(true, (s, i) => s ? i.op == 0 : s);
            Debug.Assert(parallelResult == sequentialResult);
        }

        public static void ParallelizeG2()
        {
            // "All operations on a repository directly preceding a delete operation"
            // The state aggregation remembers the previous operation
            var input = RandomGithubOps();
            var parallelResult = input.Parasail<int, (int op, int time)>((s, i) => i.op)(0);
            var sequentialResult = input.Aggregate(0, (s, i) => i.op);
            Debug.Assert(parallelResult == sequentialResult);
        }

        public static void ParallelizeG3()
        {
            // "Number of operations executed on a repository between pull open and close"
            // Open = 0
            // Close = 1
            var input = RandomGithubOps();
            var parallelResult = input.Parasail<(bool isPushing, int count), (int op, int time)>((s, i) => s.isPushing ?
                (i.op == 1 ? VT.Create(false, s.count) : VT.Create(s.isPushing, s.count + 1)) :
                (i.op == 0 ? VT.Create(true, 0) : VT.Create(s.isPushing, s.count)))((false, 0));
            var sequentialResult = input.Aggregate((isPushing: false, count: 0), (s, i) => s.isPushing ?
                (i.op == 1 ? (false, s.count) : (s.isPushing, s.count + 1)) :
                (i.op == 0 ? (true, 0) : s));
            Debug.Assert(parallelResult.Equals(sequentialResult));
        }

        public static void ParallelizeG4() // TODO: this one sometimes fails due to tuple equality not being lowered properly
        {
            // "The time between branch deletion and branch creation in a repository"
            // Delete = 0
            // Create = 1
            var input = RandomGithubOps();
            var parallelResult = input.Parasail<(bool inDelete, int time), (int op, int time)>((s, i) =>
                i.op == 0 ? VT.Create(true, i.time) :
                    i.op == 1 && s.inDelete ? VT.Create(false, 0) : VT.Create(s.inDelete, s.time))((false, 0));
            var sequentialResult = input.Aggregate((inDelete: false, time: 0), (s, i) =>
                i.op == 0 ? VT.Create(true, i.time) :
                    i.op == 1 & s.inDelete ? VT.Create(false, 0) : VT.Create(s.inDelete, s.time));
            Debug.Assert(parallelResult.Equals(sequentialResult));
        }
        
        private static List<int> RandomTimestamps(int num = 100000)
        {
            var rand = new Random();
            var ops = new List<int>();
            var time = 0;
            for (int i = 0; i < num; i++)
            {
                time += rand.Next(1000);
                ops.Add(time);
            }
            return ops;
        }

        public static void ParallelizeB1()
        {
            // "Outages: more than 2 minutes with no successful query by any user"
            // Count number of outages
            var input = RandomTimestamps();
            var parallelResult = input.Parasail<(int previous, int count), int>((s, i) =>
                i > s.previous + 120 ? VT.Create(i, s.count + 1) : VT.Create(i, s.count))((0, 0));
            var sequentialResult = input.Aggregate((previous: 0, count: 0), (s, i) =>
                i > s.previous + 120 ? (i, s.count + 1) : (i, s.count));
            Debug.Assert(parallelResult.Equals(sequentialResult));
        }
    }
}