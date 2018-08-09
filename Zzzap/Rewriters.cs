using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Microsoft.Z3;

namespace Zzzap
{
    delegate bool Eval(BoolExpr assumption, BoolExpr query);
    delegate void Assert(BoolExpr condition);
    delegate Expr Rewrite(Context ctx, Aggregation uda, Eval eval, Assert assert, Expr term);

    static class Rewriters
    {
        public static Rewrite DefaultRewrite =>
            Fix(AndThen(Z3Simplify, Project, BoolConstants, ITEs, ITEDistributivity, GroupCommutative, LowerDTEquals));

        static Rewrite Fix(Rewrite rewrite) => (ctx, uda, eval, assert, term) =>
        {
            Expr oldTerm;
            do
            {
                oldTerm = term;
                term = rewrite(ctx, uda, eval, assert, term);
            } while (term != oldTerm);
            return term;
        };

        static Rewrite AndThen(params Rewrite[] rewrite) => (ctx, uda, eval, assert, term) =>
        {
            foreach (var rule in rewrite)
            {
                term = rule(ctx, uda, eval, assert, term);
            }
            return term;
        };

        static Expr Z3Simplify(Context ctx, Aggregation uda, Eval eval, Assert assert, Expr root) => root.ZzzappSimplify(ctx);

        delegate Expr Rule(BoolExpr path, Expr term);
        static Expr TopDownWith(Context ctx, Assert assert, Expr term, Rule rule) => TopDownRewrite(ctx, assert, ctx.MkTrue(), term, rule);

        static Expr TopDownRewrite(Context ctx, Assert assert, BoolExpr path, Expr term, Rule rule)
        {
            var oldTerm = term;
            term = rule(path, term);
            assert(ctx.MkImplies(path, ctx.MkEq(oldTerm, term)));
            if (term.IsApp)
            {
                var decl = term.FuncDecl;
                switch (decl.DeclKind)
                {
                    case Z3_decl_kind.Z3_OP_ITE:
                        var condition = (BoolExpr)term.Args[0];
                        Expr ifTrue = TopDownRewrite(ctx, assert, path & condition, term.Args[1], rule);
                        Expr ifFalse = TopDownRewrite(ctx, assert, path & !condition, term.Args[2], rule);
                        term = ctx.MkITE(condition, ifTrue, ifFalse);
                        break;
                    default:
                        term = ctx.MkApp(decl, term.Args.Select(x => TopDownRewrite(ctx, assert, path, x, rule)).ToArray());
                        break;
                }
            }
            return term;
        }

        static bool AreEquivalentUnder(Context ctx, Eval eval, BoolExpr assumption, Expr left, Expr right) =>
            left.Equals(right) || eval(assumption, ctx.MkEq(left, right));

        static Expr ITEs(Context ctx, Aggregation uda, Eval eval, Assert assert, Expr root) => TopDownWith(ctx, assert, root, (path, term) =>
        {
            if (term.IsITE)
            {
                var condition = (BoolExpr)term.Args[0];
                var ifTrue = term.Args[1];
                var ifFalse = term.Args[2];
                if (eval(path, condition))
                    return ifTrue;
                else if (eval(path, !condition))
                    return ifFalse;
                else if (AreEquivalentUnder(ctx, eval, path, ifTrue, ifFalse))
                    return ifTrue.Size() <= ifFalse.Size() ? ifTrue : ifFalse;
                else if (AreEquivalentUnder(ctx, eval, path, term, ifTrue))
                    return ifTrue;
                else if (AreEquivalentUnder(ctx, eval, path, term, ifFalse))
                    return ifFalse;
            }
            return term;
        });

        static Expr ITEDistributivity(Context ctx, Aggregation uda, Eval eval, Assert assert, Expr root) => TopDownWith(ctx, assert, root, (path, term) =>
        {
            if (term.IsApp && term.NumArgs == 1)
            {
                var arg = term.Args[0];
                if (arg.IsITE)
                {
                    var decl = term.FuncDecl;
                    return ctx.MkITE((BoolExpr)arg.Args[0], decl.Apply(arg.Args[1]), decl.Apply(arg.Args[2]));
                }
            }
            return term;
        });

        static Expr BoolConstants(Context ctx, Aggregation uda, Eval eval, Assert assert, Expr root) => TopDownWith(ctx, assert, root, (path, term) =>
        {
            if (term is BoolExpr boolTerm && term.Contains(uda.State))
            {
                if (eval(path, boolTerm))
                    return ctx.MkTrue();
                else if (eval(path, !boolTerm))
                    return ctx.MkFalse();
                else
                    return term;
            }
            return term;
        });

        static Expr Project(Context ctx, Aggregation uda, Eval eval, Assert assert, Expr root) => TopDownWith(ctx, assert, root, (path, term) =>
        {
            if (term.IsApp &&
                term.FuncDecl.DeclKind == Z3_decl_kind.Z3_OP_DT_ACCESSOR &&
                term.Args[0].IsApp &&
                term.Args[0].FuncDecl.DeclKind == Z3_decl_kind.Z3_OP_DT_CONSTRUCTOR)
            {
                var accessor = term.FuncDecl;
                var constructor = term.Args[0].FuncDecl;

                var dtSort = (DatatypeSort)term.Args[0].Sort;
                for (int i = 0; i < dtSort.NumConstructors; ++i)
                {
                    if (dtSort.Constructors[i] == constructor)
                    {
                        for (int j = 0; j < constructor.NumParameters; ++j)
                        {
                            if (dtSort.Accessors[i][j] == accessor)
                            {
                                return term.Args[0].Args[j];
                            }
                        }
                        break;
                    }
                }
            }
            return term;
        });

        static Expr LowerDTEquals(Context ctx, Aggregation uda, Eval eval, Assert assert, Expr root) => TopDownWith(ctx, assert, root, (path, term) =>
        {
            if (term.IsEq)
            {
                var left = term.Args[0];
                var right = term.Args[1];
                if (left.Sort is DatatypeSort dtSort && dtSort.NumConstructors == 1)
                {
                    return ctx.MkAnd(dtSort.Accessors[0].Select(a => ctx.MkEq(a.Apply(left), a.Apply(right))));
                }
            }
            return term;
        });

        public static Expr GroupCommutative(Context ctx, Aggregation uda, Eval eval, Assert assert, Expr root) => TopDownWith(ctx, assert, root, (path, term) =>
        {
            if (term.IsApp && term.Contains(uda.State))
            {
                Func<IEnumerable<Expr>, IEnumerable<Expr>, Expr> op;
                switch (term.FuncDecl.DeclKind)
                {
                    default:
                        return term;
                    case Z3_decl_kind.Z3_OP_ADD:
                        op = AdaptToExpr<ArithExpr>(ctx.MkAdd);
                        break;
                    case Z3_decl_kind.Z3_OP_AND:
                        op = AdaptToExpr<BoolExpr>(ctx.MkAnd);
                        break;
                }
                var newArgs = new List<Expr>();
                var groupedArgs = new List<Expr>();
                foreach (Expr arg in term.Args)
                {
                    if (arg.Contains(uda.State))
                        newArgs.Add(arg);
                    else
                        groupedArgs.Add(arg);
                }
                if (groupedArgs.Count > 1)
                    return op(newArgs, groupedArgs);
            }
            return term;
        });

        private static Func<IEnumerable<Expr>, IEnumerable<Expr>, Expr> AdaptToExpr<T>(Func<IEnumerable<T>, T> f)
            where T : Expr => (n, g) => f(n.Cast<T>().Concat(new T[] { f(g.Cast<T>()) }));

        static Expr Constants(Context ctx, Aggregation uda, Eval eval, Assert assert, Expr root) => TopDownWith(ctx, assert, root, (path, term) =>
        {
            if (term.IsApp && term.NumArgs > 0)
            {
                // TODO: extend eval to support finding the constraints under with a term is constant also for non-boolean expressions
            }
            throw new NotImplementedException();
        });
    }
}