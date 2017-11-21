using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Z3;

namespace Zzzap
{

    public partial class Zzzap
    {
        //public static ParallelAggregation Parallelize(Context ctx, Aggregation uda) =>
        //    new AotParallelizer(ctx, uda).ExploreParallelization();
    }

    class AotParallelizer {
        private const uint FirstArgumentIndex = 2;

        private Context ctx;
        private Aggregation uda;
        private HashSet<Sort> allowedSorts = new HashSet<Sort>();
        private Dictionary<FuncDecl, Expr> shapes = new Dictionary<FuncDecl, Expr>();
        private Dictionary<Expr, FuncDecl> shapeDelcs = new Dictionary<Expr, FuncDecl>();
        private Dictionary<Expr, Expr> shapeCompositionStrategies = new Dictionary<Expr, Expr>();

        public AotParallelizer(Context ctx, Aggregation uda)
        {
            this.ctx = ctx;
            this.uda = uda;
            allowedSorts.Add(ctx.BoolSort);
            allowedSorts.Add(ctx.IntSort);
        }

        /*
        public ParallelAggregation ExploreParallelization()
        {
            var toExpand = new HashSet<Expr>();
            var initialShape = AddState(uda.State, Enumerable.Empty<Expr>());
            toExpand.Add(uda.State);

            while (toExpand.Count > 0)
            {
                var shape = toExpand.First();

                Console.WriteLine("\nShape " + shapeDelcs[shape]);
                Console.WriteLine(shape);

                var composition = Compose(shape).Simplify();
                var compositionStrategy = RewriteExplorer.CreateRewriteStrategy(ctx, uda, composition, MatchState).Simplify();

                shapeCompositionStrategies.Add(shape, compositionStrategy);
                AddShapesToExpand(compositionStrategy, toExpand);
                toExpand.Remove(shape);
            }

            return new ParallelAggregation(shapes, initialShape);
        }
        */

        private void AddShapesToExpand(Expr term, ISet<Expr> toExpand)
        {
            if (term.IsApp)
            {
                var decl = term.FuncDecl;
                Expr shape;
                if (shapes.TryGetValue(decl, out shape))
                {
                    if (!shapeCompositionStrategies.ContainsKey(shape))
                    {
                        toExpand.Add(shape);
                    }
                }
                else
                {
                    foreach (var arg in term.Args)
                        AddShapesToExpand(arg, toExpand);
                }
            }
        }

        private Expr MatchState(Expr body)
        {
            //uint nextIndex = 0;
            var arguments = new List<Expr>();
            var shape = BodyToShape3(body, arguments);
            if (null == shape)
            {
                shape = ctx.MkConst("g" + 0, body.Sort);
                arguments.Add(body);
            }
            FuncDecl shapeDecl;
            if (!shapeDelcs.TryGetValue(shape, out shapeDecl))
            {
                Console.WriteLine("\n" + body);
                shapeDecl = AddState(shape, arguments);
            }
            return ctx.MkApp(shapeDecl, arguments);
        }

        private FuncDecl AddState(Expr shape, IEnumerable<Expr> arguments)
        {
            var decl = ctx.MkFreshFuncDecl("Z", arguments.Select(x => x.Sort).ToArray(), uda.State.Sort);
            shapeDelcs.Add(shape, decl);
            shapes.Add(decl, shape);
            return decl;
        }

        private Expr BodyToShape(Expr body, ref uint nextIndex, List<Expr> shapeArguments)
        {
            if (body == uda.State)
            {
                return body;
            }
            else if (body.IsApp && body.NumArgs > 0)
            {
                var subShapes = new Expr[body.NumArgs];
                for (int i = 0; i < body.NumArgs; ++i)
                {
                    subShapes[i] = BodyToShape(body.Args[i], ref nextIndex, shapeArguments);
                }
                if (subShapes.All(x => x == null))
                {
                    return null;
                }
                else
                {
                    for (int i = 0; i < body.NumArgs; ++i)
                    {
                        if (null == subShapes[i])
                        {
                            var newArg = body.Args[i];
                            shapeArguments.Add(newArg);
                            subShapes[i] = ctx.MkConst("g" + nextIndex++, newArg.Sort);
                        }
                    }
                    return ctx.MkApp(body.FuncDecl, subShapes);
                }
            }
            else return null;
        }

        private Expr BodyToShape2(Expr body, List<Expr> shapeArguments)
        {
            uint nextIndex = 0;
            var substitutions = new Dictionary<Expr, Expr>();
            var blocks = new HashSet<Expr>();
            blocks.Add(uda.State);

            var blockedBody = body;
            while (true)
            {
                var candidates = new Dictionary<Expr, int>();
                FindSubstitutionCandidates(blockedBody, blocks, (candidate) =>
                {
                    int count;
                    if (!candidates.TryGetValue(candidate, out count))
                    {
                        count = 0;
                    }
                    candidates[candidate] = count + 1;
                });
                if (candidates.Count == 0)
                {
                    break;
                }
                candidates.ToList().Sort((a, b) =>
                {
                    var countOrder = a.Value.CompareTo(b.Value);
                    return countOrder != 0 ? countOrder : TermSize(a.Key).CompareTo(TermSize(b.Key));
                });

                var subst = candidates.Last().Key;
                shapeArguments.Add(subst);
                body = body.Substitute(subst, ctx.MkConst("g" + nextIndex++, subst.Sort));

                var block = ctx.MkFreshConst("BLOCK", subst.Sort);
                blocks.Add(block);
                blockedBody = blockedBody.Substitute(subst, block); // TODO: 
            }

            return body;
        }

        private Expr BodyToShape3(Expr body, List<Expr> shapeArguments)
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
                    if (TermSize(cand) > TermSize(subst)) subst = cand;
                }
                
                shapeArguments.Add(subst);
                body = body.Substitute(subst, ctx.MkConst("g" + nextIndex++, subst.Sort));

                var block = ctx.MkFreshConst("BLOCK", subst.Sort);
                blocks.Add(block);
                blockedBody = blockedBody.Substitute(subst, block); // TODO: 
            }

            return body;
        }

        private int TermSize(Expr term)
        {
            if (term.IsApp && term.NumArgs > 0)
            {
                return 1 + term.Args.Sum(x => TermSize(x));
            }
            else
            {
                return 1;
            }
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

        //private Expr Compose(Expr shape) => uda.Body.Substitute(uda.State, shape);
        private Expr Compose(Expr shape)
        {
            if (shape.IsApp && shape.FuncDecl.DeclKind == Z3_decl_kind.Z3_OP_ITE)
                return ctx.MkITE((BoolExpr)shape.Args[0], Compose(shape.Args[1]), Compose(shape.Args[2]));
            else
                return uda.Body.Substitute(uda.State, shape);
        }
    }
}