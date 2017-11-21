using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Z3;

namespace Zzzap
{
    public class ParallelAggregation
    {
        private Context ctx;
        private Aggregation uda;
        private HashSet<Sort> allowedSorts = new HashSet<Sort>();

        internal Dictionary<FuncDecl, Expr> Shapes { get; } = new Dictionary<FuncDecl, Expr>();
        internal Dictionary<Expr, FuncDecl> ShapeDelcs { get; } = new Dictionary<Expr, FuncDecl>();
        internal Dictionary<FuncDecl, Expr[]> ShapeParameters { get; } = new Dictionary<FuncDecl, Expr[]>();
        internal Dictionary<Expr, Expr> CompositionStrategies { get; } = new Dictionary<Expr, Expr>();

        public ParallelAggregation(Context ctx, Aggregation uda)
        {
            this.ctx = ctx;
            this.uda = uda;
            allowedSorts.Add(ctx.BoolSort);
            allowedSorts.Add(ctx.IntSort);
        }

        internal void CreateCompositionStrategy(Expr shape)
        {
            Console.WriteLine("\nShape " + ShapeDelcs[shape]);
            Console.WriteLine(shape);
            var composition = Compose(shape);
            var compositionStrategy = RewriteExplorer.CreateRewriteStrategy(ctx, uda, composition, MatchState);
            CompositionStrategies.Add(shape, compositionStrategy);
        }

        internal Expr MatchState(Expr body)
        {
            var arguments = new List<Expr>();
            var parameters = new List<Expr>();
            var shape = BodyToShape(body, arguments, parameters);
            if (null == shape)
            {
                shape = ctx.MkConst("g" + 0, body.Sort);
                arguments.Add(body);
                parameters.Add(shape);
            }
            FuncDecl shapeDecl;
            if (!ShapeDelcs.TryGetValue(shape, out shapeDecl))
            {
                //Console.WriteLine("\n" + body);
                shapeDecl = AddState(shape, arguments, parameters);
            }
            return ctx.MkApp(shapeDecl, arguments);
        }

        private FuncDecl AddState(Expr shape, IEnumerable<Expr> arguments, IEnumerable<Expr> parameters)
        {
            var decl = ctx.MkFreshFuncDecl("Z", arguments.Select(x => x.Sort).ToArray(), uda.State.Sort);
            ShapeDelcs.Add(shape, decl);
            Shapes.Add(decl, shape);
            ShapeParameters.Add(decl, parameters.ToArray());
            return decl;
        }

        private Expr BodyToShape(Expr body, List<Expr> arguments, List<Expr> parameters)
        {
            uint nextIndex = 0;
            var substitutions = new Dictionary<Expr, Expr>();
            var blocks = new HashSet<Expr>();
            blocks.Add(uda.State);

            var blockedBody = body;
            while (true)
            {
                var candidates = new HashSet<Expr>();
                FindSubstitutionCandidates(blockedBody, blocks, (candidate) =>
                {
                    candidates.Add(candidate);
                });
                if (candidates.Count == 0)
                {
                    break;
                }
                var subst = candidates.First();
                foreach (var cand in candidates)
                {
                    if (cand.Size() > subst.Size()) subst = cand;
                }

                var param = ctx.MkConst("g" + nextIndex++, subst.Sort);
                arguments.Add(subst);
                parameters.Add(param);
                body = body.Substitute(subst, param);

                var block = ctx.MkFreshConst("BLOCK", subst.Sort);
                blocks.Add(block);
                blockedBody = blockedBody.Substitute(subst, block);
            }

            return body;
        }

        private bool FindSubstitutionCandidates(Expr body, HashSet<Expr> blocks, Action<Expr> markCandidate)
        {
            if (blocks.Contains(body))
            {
                return true;
            }
            else if (body.IsApp && body.NumArgs > 0)
            {
                bool anyBlocked = false;
                foreach (var arg in body.Args)
                {
                    anyBlocked |= FindSubstitutionCandidates(arg, blocks, markCandidate);
                }
                if (!anyBlocked && allowedSorts.Contains(body.Sort))
                    markCandidate(body);
                return anyBlocked;
            }
            else
            {
                markCandidate(body);
                return false;
            }
        }

        private Expr Compose(Expr shape)
        {
            if (shape.IsApp && shape.FuncDecl.DeclKind == Z3_decl_kind.Z3_OP_ITE)
                return ctx.MkITE((BoolExpr)shape.Args[0], Compose(shape.Args[1]), Compose(shape.Args[2]));
            else
                return uda.Body.Substitute(uda.State, shape);
        }
    }
}