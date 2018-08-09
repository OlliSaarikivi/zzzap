using Microsoft.Z3;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Zzzap
{
    static class Z3Extensions
    {
        public static Status CheckTerm(this Solver solver, BoolExpr term)
        {
            solver.Push();
            solver.Assert(term);
            var result = solver.Check();
            solver.Pop();
            return result;
        }

        public static bool Contains(this Expr term, Expr subTerm)
        {
            if (term == subTerm)
            {
                return true;
            }
            else if (term.IsApp && term.NumArgs > 0)
            {
                return term.Args.Any(x => x.Contains(subTerm));
            }
            else return false;
        }

        public static Expr ZzzappSimplify(this Expr term, Context ctx)
        {
            var numerals = new HashSet<Expr>();
            FindNumerals(term, numerals);
            var numeralsArr = numerals.ToArray();
            var blockers = numeralsArr.Select(x => ctx.MkFreshConst("BLOCK", x.Sort)).ToArray();
            var simplified = term.Substitute(numeralsArr, blockers).Simplify();
            return simplified.Substitute(blockers, numeralsArr);
        }

        private static void FindNumerals(Expr term, HashSet<Expr> numerals)
        {
            if (term.IsApp && term.NumArgs > 0)
            {
                foreach (var arg in term.Args)
                    FindNumerals(arg, numerals);
            }
            else if (term.IsNumeral)
            {
                numerals.Add(term);
            }
        }

        public static int Size(this Expr term)
        {
            if (term.IsApp && term.NumArgs > 0)
            {
                return 1 + term.Args.Sum(x => x.Size());
            }
            else
            {
                return 1;
            }
        }

        class SolverFrame : IDisposable
        {
            Solver solver;

            public SolverFrame(Solver solver)
            {
                this.solver = solver;
            }

            public void Dispose()
            {
                solver.Pop();
            }
        }

        public static IDisposable CreateFrame(this Solver solver)
        {
            solver.Push();
            return new SolverFrame(solver);
        }
    }
}