using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Z3;

namespace Zzzap
{
    static class RewriteExplorer
    {
        public static Expr CreateRewriteStrategy(Context ctx, Aggregation uda, Expr comp, Func<Expr, Expr> matchState)
        {
            //var solver = ctx.MkSimpleSolver();
            var solver = ctx.TryFor(ctx.MkTactic("default"), 1000).Solver;

            var tree = Explore(ctx, solver, uda, comp, Rewriters.DefaultRewrite, matchState);
            var strategy = TreeToExpr(ctx, tree, matchState);
            Eval eval = (assumption, query) =>
            {
                try
                {
                    solver.Push();
                    solver.Assert(assumption);
                    return solver.Check() == Status.SATISFIABLE &&
                        solver.CheckTerm(!query) == Status.UNSATISFIABLE;
                }
                finally { solver.Pop(); }
            };
            var simplifiedStrategy = Rewriters.DefaultRewrite(ctx, uda, eval, strategy);

            return simplifiedStrategy;
        }

        static Expr TreeToExpr(Context ctx, NodeHead tree, Func<Expr, Expr> matchState)
        {
            switch (tree.Tail)
            {
                case Branch br:
                    return ctx.MkITE(br.Condition, TreeToExpr(ctx, br.IfTrue, matchState), TreeToExpr(ctx, br.IfFalse, matchState));
                case Assume asm:
                    return TreeToExpr(ctx, asm.Child, matchState);
                case Return ret:
                    return matchState(ret.Term);
                default:
                    throw new ZzzapException("Unhandled exploration tree node type");
            }
        }

        static NodeHead Explore(Context ctx, Solver solver, Aggregation uda, Expr comp, Rewrite rewrite, Func<Expr, Expr> matchState)
        {
            var root = new NodeHead(null);
            var current = root;
            BoolExpr path = ctx.MkTrue();

            Func<NodeHead> newChild = () => new NodeHead(current);

            Eval eval = (assumption, query) =>
            {
                QueryNodeTail queryTail;
                bool result;
                if (null != current.Tail) // Finding path to frontier
                {
                    queryTail = (QueryNodeTail)current.Tail;
                    Debug.Assert(queryTail.Assumption == assumption && queryTail.Query == query);
                    for (int i = 0; i < queryTail.NumChildren; ++i)
                    {
                        if (!queryTail.GetChild(i).Finished)
                        {
                            current = queryTail.GetChild(i);
                            result = queryTail.GetResult(i);
                            goto END;
                        }
                    }
                    throw new ZzzapException("Rewrite exploration encountered dead end");
                }
                else
                {
                    solver.Push();
                    solver.Assert(path);
                    if (solver.CheckTerm(assumption) == Status.SATISFIABLE)
                    {
                        if (solver.CheckTerm(assumption & query) == Status.UNSATISFIABLE) // Unsatisfiable for all instantiations
                        {
                            queryTail = new Assume(assumption, query, false, newChild());
                        }
                        else if (solver.CheckTerm(assumption & !query) == Status.UNSATISFIABLE) // Valid for all instantiations
                        {
                            queryTail = new Assume(assumption, query, true, newChild());
                        }
                        else
                        {
                            // Get the weakest implicant that does not depend on the batch starting state
                            var condition = ctx.MkForall(new Expr[] { uda.State }, query); ;
                            if (solver.CheckTerm(condition) == Status.SATISFIABLE) // Valid for some instantiation
                            {
                                var qeGoal = ctx.MkGoal(models: false);
                                qeGoal.Add(path & condition);
                                var qeResult = ctx.TryFor(ctx.MkTactic("qe"), 1000).Apply(qeGoal);
                                //var qeResult = ctx.MkTactic("qe").Apply(qeGoal);
                                if (qeResult.NumSubgoals == 1)
                                {
                                    // The resulting condition repeats the path, but it should be simplified away later
                                    var qeCondition = qeResult.Subgoals[0].AsBoolExpr();
                                    queryTail = new Branch(assumption, query, qeCondition, newChild(), newChild());
                                }
                                else
                                {
                                    // If quantifier elimination fails then assume the query never holds
                                    queryTail = new Assume(assumption, query, false, newChild());
                                }
                            }
                            else // Not valid for any instantiation
                            {
                                queryTail = new Assume(assumption, query, false, newChild());
                            }
                        }
                    }
                    else
                    {
                        queryTail = new Assume(assumption, query, false, newChild());
                    }
                    solver.Pop();
                    current.Tail = queryTail;
                    current = queryTail.GetChild(0);
                    result = queryTail.GetResult(0);
                }
                END: if (queryTail is Branch)
                {
                    var condition = ((Branch)queryTail).Condition;
                    path &= result ? condition : !condition;
                }
                return result;
            };

            while (!root.Finished)
            {
                var result = rewrite(ctx, uda, eval, comp);
                current.Tail = new Return(result);
                matchState(result);
                MarkFinished(current);
                current = root;
                path = ctx.MkTrue();
            }

            return root;
        }

        static void MarkFinished(NodeHead node)
        {
            node.Finished = true;
            var tail = node.Tail;
            for (int i = 0; i < tail.NumChildren; ++i)
            {
                node.Finished &= tail.GetChild(i).Finished;
            }
            if (null != node.Parent)
            {
                MarkFinished(node.Parent);
            }
        }

        abstract class NodeTail
        {
            public abstract int NumChildren { get; }
            public abstract NodeHead GetChild(int i);
            public abstract bool GetResult(int i);
        }

        abstract class QueryNodeTail : NodeTail
        {
            public BoolExpr Query { get; }
            public BoolExpr Assumption { get; }

            public QueryNodeTail(BoolExpr assumption, BoolExpr query)
            {
                Assumption = assumption;
                Query = query;
            }
        }

        class Branch : QueryNodeTail
        {
            public BoolExpr Condition { get; }
            public NodeHead IfTrue { get; }
            public NodeHead IfFalse { get; }

            public Branch(BoolExpr assumption, BoolExpr query, BoolExpr condition, NodeHead ifTrue, NodeHead ifFalse) : base(assumption, query)
            {
                Condition = condition;
                IfTrue = ifTrue;
                IfFalse = ifFalse;
            }

            public override int NumChildren { get => 2; }

            public override NodeHead GetChild(int i)
            {
                switch (i)
                {
                    case 0: return IfTrue;
                    case 1: return IfFalse;
                    default: throw new IndexOutOfRangeException();
                }
            }

            public override bool GetResult(int i)
            {
                switch (i)
                {
                    case 0: return true;
                    case 1: return false;
                    default: throw new IndexOutOfRangeException();
                }
            }
        }

        class Assume : QueryNodeTail
        {
            public bool Result { get; }
            public NodeHead Child { get; }

            public Assume(BoolExpr assumption, BoolExpr query, bool result, NodeHead child) : base(assumption, query)
            {
                Result = result;
                Child = child;
            }

            public override int NumChildren { get => 1; }

            public override NodeHead GetChild(int i)
            {
                switch (i)
                {
                    case 0: return Child;
                    default: throw new IndexOutOfRangeException();
                }
            }

            public override bool GetResult(int i)
            {
                switch (i)
                {
                    case 0: return Result;
                    default: throw new IndexOutOfRangeException();
                }
            }
        }

        class Return : NodeTail
        {
            public Expr Term { get; }

            public Return(Expr term)
            {
                Term = term;
            }

            public override int NumChildren { get => 0; }
            public override NodeHead GetChild(int i) => throw new IndexOutOfRangeException();
            public override bool GetResult(int i) => throw new IndexOutOfRangeException();
        }

        class NodeHead
        {
            public NodeHead Parent { get; }
            public NodeTail Tail { get; set; }
            public bool Finished { get; set; } = false;

            public NodeHead(NodeHead parent)
            {
                Parent = parent;
            }
        }
    }
}