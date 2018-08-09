/*using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Z3;

namespace Zzzap
{
    struct RuleResult
    {
        Expr Rewrite { get; }
        Expr Guard { get; }

        public RuleResult(Expr rewrite, Expr guard = null)
        {
            Rewrite = rewrite;
            Guard = guard;
        }

        public static implicit operator RuleResult((Expr rewrite, Expr guard) guardedRewrite) =>
            new RuleResult(guardedRewrite.rewrite, guardedRewrite.guard);

        public static implicit operator RuleResult(Expr rewrite) => new RuleResult(rewrite);
    }

    delegate RuleResult Rule(Context ctx, Aggregation uda, Expr term);
    delegate Expr Rule(Context ctx, Eval eval, Expr term);

    struct WorkItem
    {
        public AST TermOrDecl { get; }
        public BoolExpr Path { get; }

        public WorkItem(Expr term, BoolExpr path)
        {
            TermOrDecl = term;
            Path = path;
        }

        public WorkItem(FuncDecl decl, BoolExpr path)
        {
            TermOrDecl = decl;
            Path = path;
        }
    }

    class RewriteState
    {
        Stack<WorkItem> WorkStack { get; } = new Stack<WorkItem>();
        Stack<Expr> Arguments { get; } = new Stack<Expr>();

        bool ProcessNext(Context ctx, Rewrite topDownRule, Rewrite bottomUpRule)
        {
            var item = WorkStack.Pop();
            if (item.TermOrDecl is Expr term)
            {
                if (term.IsApp)
                {
                    WorkStack.Push(new WorkItem(term.FuncDecl, item.Path));
                    if (term.FuncDecl.DeclKind == Z3_decl_kind.Z3_OP_ITE)
                    {
                        WorkStack.Push(new WorkItem(term.Args[2], ctx.MkAnd(item.Path, ctx.MkNot((BoolExpr)term.Args[0]))));
                        WorkStack.Push(new WorkItem(term.Args[1], ctx.MkAnd(item.Path, (BoolExpr)term.Args[0])));
                        WorkStack.Push(new WorkItem(term.Args[0], item.Path));
                    }
                    else
                    {
                        for (int i = term.Args.Length - 1; i >= 0; --i)
                        {
                            WorkStack.Push(new WorkItem(term.Args[i], item.Path));
                        }
                    }
                }
                else
                {
                    Arguments.Push(term);
                }
            }
            else if (item.TermOrDecl is FuncDecl decl)
            {
                if (Arguments.Count < decl.Arity)
                    throw new ZzzapException("Ran out of arguments");

                var appArguments = new Expr[decl.Arity];
                for (int i = 0; i < decl.Arity; ++i)
                {
                    appArguments[decl.Arity - 1 - i] = Arguments.Pop();
                }
                var app = ctx.MkApp(decl, appArguments);
                Arguments.Push(app);
            }
            else Debug.Assert(false);
        }
    }
}*/