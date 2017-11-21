using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Z3;
using System.Reflection;
using AgileObjects.ReadableExpressions;

namespace Zzzap
{
    public static partial class IEnumerableExtensions
    {
        public static Func<TState, TState> Parasail<TState, TIn>(this IEnumerable<TIn> input, Expression<Func<TState, TIn, TState>> aggregationExpression)
        {
            var ctx = new Context();
            var uda = Aggregation.FromExpression(ctx, aggregationExpression);
            var pa = new ParallelAggregation(ctx, uda);

            var inputEnum = input.GetEnumerator();
            pa.MatchState(uda.State);
            object[] currentSymState = new object[] { pa.ShapeDelcs[uda.State] };
            while (true)
            {
                var composer = CompileComposer<TIn>(uda, pa, currentSymState);
                currentSymState = composer(inputEnum);
                var currentShape = pa.Shapes[(FuncDecl)currentSymState[0]];
                if (pa.CompositionStrategies.ContainsKey(currentShape))
                {
                    break;
                }
                else
                {
                    pa.CreateCompositionStrategy(currentShape);
                }
            }

            return CompileStateTransformer<TState>(uda, pa, currentSymState);
        }

        private static Func<TState, TState> CompileStateTransformer<TState>(Aggregation uda, ParallelAggregation pa, params object[] symState)
        {
            var state = Expression.Parameter(typeof(TState), "s");
            var variables = new Dictionary<Expr, ParameterExpression>();
            variables.Add(uda.State, state);
            Func<Expr, Expression> getVar = (term) =>
            {
                Debug.Assert(term.IsConst);
                ParameterExpression variable;
                if (!variables.TryGetValue(term, out variable))
                {
                    var name = term.FuncDecl.Name;
                    variable = Expression.Parameter(SortToType(term.Sort), name.IsStringSymbol() ? name.ToString() : $"var{name}");
                    variables.Add(term, variable);
                }
                return variable;
            };

            var statements = new List<Expression>();
            var decl = (FuncDecl)symState[0];
            var parameters = pa.ShapeParameters[decl];
            for (int i = 0; i < parameters.Length; ++i)
            {
                statements.Add(Expression.Assign(ExprToExpression(parameters[i], getVar),
                    Expression.Convert(Expression.Constant(symState[i + 1]), SortToType(parameters[i].Sort))));
            }

            statements.Add(ExprToExpression(pa.Shapes[decl], getVar));

            var body = Expression.Block(typeof(TState), variables.Values.Where(x => x != state), statements.ToArray());
            var result = Expression.Lambda<Func<TState, TState>>(body, state);
            return result.Compile();
        }

        private static Func<IEnumerator<TIn>, object[]> CompileComposer<TIn>(Aggregation uda, ParallelAggregation pa, params object[] initialSymState)
        {
            var returnValue = Expression.Parameter(typeof(object[]), "returnValue");
            var inputs = Expression.Parameter(typeof(IEnumerator<TIn>), "inputs");
            var input = Expression.Parameter(typeof(TIn), "i");
            var variables = new Dictionary<Expr, ParameterExpression>();
            variables.Add(uda.Input, input);
            Func<Expr, Expression> getVar = (term) =>
            {
                Debug.Assert(term.IsConst);
                ParameterExpression variable;
                if (!variables.TryGetValue(term, out variable))
                {
                    var name = term.FuncDecl.Name;
                    variable = Expression.Parameter(SortToType(term.Sort), name.IsStringSymbol() ? name.ToString() : $"var{name}");
                    variables.Add(term, variable);
                }
                return variable;
            };

            var generated = new HashSet<FuncDecl>();
            var toGenerate = new Stack<FuncDecl>();
            var shapeTargets = new Dictionary<FuncDecl, LabelTarget>();
            int nextShapeIndex = 0;
            Func<FuncDecl, LabelTarget> getShapeTarget = (decl) =>
            {
                LabelTarget target;
                if (!shapeTargets.TryGetValue(decl, out target))
                {
                    target = Expression.Label($"S{nextShapeIndex++}");
                    shapeTargets.Add(decl, target);
                }
                if (!generated.Contains(decl))
                {
                    generated.Add(decl);
                    toGenerate.Push(decl);
                }
                return target;
            };

            var statements = new List<Expression>();
            {
                var initialDecl = (FuncDecl)initialSymState[0];
                var parameters = pa.ShapeParameters[initialDecl];
                for (int i = 0; i < parameters.Length; ++i)
                {
                    statements.Add(Expression.Assign(ExprToExpression(parameters[i], getVar),
                        Expression.Convert(Expression.Constant(initialSymState[i + 1]), SortToType(parameters[i].Sort))));
                }
                statements.Add(Expression.Goto(getShapeTarget(initialDecl)));
            }

            var returnLabel = Expression.Label("return");
            while (toGenerate.Count > 0)
            {
                var decl = toGenerate.Pop();
                var shape = pa.Shapes[decl];
                var exit = Expression.Block(
                    Expression.Assign(returnValue, Expression.NewArrayInit(typeof(object),
                        new Expression[] { Expression.Constant(decl) }.Concat(
                            pa.ShapeParameters[decl].Select(x => ExprToExpression(x, getVar))
                            .Select(x => x.Type.IsValueType ? Expression.Convert(x, typeof(object)) : x)))),
                    Expression.Goto(returnLabel));

                statements.Add(Expression.Label(getShapeTarget(decl)));
                Expr strategy;
                if (pa.CompositionStrategies.TryGetValue(shape, out strategy))
                {
                    var moveNext = typeof(System.Collections.IEnumerator).GetMethod("MoveNext");
                    var readInput = Expression.IfThenElse(Expression.Call(inputs, moveNext),
                        Expression.Assign(input, Expression.Property(inputs, "Current")),
                        exit);
                    statements.Add(readInput);
                    statements.Add(StrategyToExpression(strategy, pa, getVar, getShapeTarget));
                }
                else
                {
                    statements.Add(exit);
                }
            }

            statements.Add(Expression.Label(returnLabel));
            statements.Add(returnValue);

            var allVariables = variables.Values.ToList();
            allVariables.Add(returnValue);
            var body = Expression.Block(typeof(object[]), allVariables, statements.ToArray());

            var result = Expression.Lambda<Func<IEnumerator<TIn>, object[]>>(body, inputs);
            return result.Compile();
        }

        private static Expression StrategyToExpression(Expr strat, ParallelAggregation pa, Func<Expr, Expression> getVar, Func<FuncDecl, LabelTarget> getShapeTarget)
        {
            if (strat.IsApp)
            {
                if (strat.IsITE)
                {
                    var test = ExprToExpression(strat.Args[0], getVar);
                    var ifTrue = StrategyToExpression(strat.Args[1], pa, getVar, getShapeTarget);
                    var ifFalse = StrategyToExpression(strat.Args[2], pa, getVar, getShapeTarget);
                    return Expression.IfThenElse(test, ifTrue, ifFalse);
                }
                else
                {
                    var decl = strat.FuncDecl;
                    var parameters = pa.ShapeParameters[decl];

                    var temps = new ParameterExpression[strat.NumArgs];
                    var statements = new List<Expression>();
                    for (int i = 0; i < strat.NumArgs; ++i)
                    {
                        var arg = strat.Args[i];
                        temps[i] = Expression.Parameter(SortToType(arg.Sort), $"temp{i}");
                        statements.Add(Expression.Assign(temps[i], ExprToExpression(arg, getVar)));
                    }
                    for (int i = 0; i < strat.NumArgs; ++i)
                    {
                        statements.Add(Expression.Assign(ExprToExpression(parameters[i], getVar), temps[i]));
                    }
                    statements.Add(Expression.Goto(getShapeTarget(decl)));
                    return Expression.Block(temps, statements.ToArray());
                }
            }
            else throw new ZzzapException("Malformed strategy created");
        }

        private static Type SortToType(Sort sort)
        {
            if (sort is BoolSort) return typeof(Boolean);
            if (sort is IntSort) return typeof(int);
            throw new ZzzapException($"Unsupported sort {sort}");
        }

        private static Expression ExprToExpression(Expr expr, Func<Expr, Expression> getVar)
        {
            if (expr.IsTrue) return Expression.Constant(true);
            if (expr.IsFalse) return Expression.Constant(false);
            if (expr is IntNum intNum) return Expression.Constant(intNum.Int);
            if (expr.IsConst) return getVar(expr);
            if (expr.IsApp)
            {
                if (expr.IsITE)
                {
                    var test = ExprToExpression(expr.Args[0], getVar);
                    var ifTrue = ExprToExpression(expr.Args[1], getVar);
                    var ifFalse = ExprToExpression(expr.Args[2], getVar);
                    return Expression.Condition(test, ifTrue, ifFalse);
                }
                else if (expr.NumArgs > 0)
                {
                    return AppToExpression(expr.FuncDecl, expr.Args.Select(x => ExprToExpression(x, getVar)).ToList());
                }
            }
            throw new ZzzapException($"Unsupported term {expr}");
        }

        private static Expression AppToExpression(FuncDecl funcDecl, List<Expression> args)
        {
            switch (funcDecl.DeclKind)
            {
                case Z3_decl_kind.Z3_OP_EQ: return Expression.Equal(args[0], args[1]);
                case Z3_decl_kind.Z3_OP_IFF: return Expression.Equal(args[0], args[1]);
                case Z3_decl_kind.Z3_OP_AND: return args.Aggregate(Expression.And);
                case Z3_decl_kind.Z3_OP_OR: return args.Aggregate(Expression.Or);
                case Z3_decl_kind.Z3_OP_XOR: return args.Aggregate(Expression.ExclusiveOr);
                case Z3_decl_kind.Z3_OP_GT: return Expression.GreaterThan(args[0], args[1]);
                case Z3_decl_kind.Z3_OP_LT: return Expression.LessThan(args[0], args[1]);
                case Z3_decl_kind.Z3_OP_GE: return Expression.GreaterThanOrEqual(args[0], args[1]);
                case Z3_decl_kind.Z3_OP_LE: return Expression.LessThanOrEqual(args[0], args[1]);
                case Z3_decl_kind.Z3_OP_ADD: return args.Aggregate(Expression.Add);
                case Z3_decl_kind.Z3_OP_SUB: return Expression.Subtract(args[0], args[1]);
                case Z3_decl_kind.Z3_OP_MUL: return args.Aggregate(Expression.Multiply);
                case Z3_decl_kind.Z3_OP_DIV: return Expression.Divide(args[0], args[1]);
                case Z3_decl_kind.Z3_OP_MOD: return Expression.Modulo(args[0], args[1]);
                case Z3_decl_kind.Z3_OP_NOT: return Expression.Not(args[0]);
                case Z3_decl_kind.Z3_OP_UMINUS: return Expression.Negate(args[0]);
                case Z3_decl_kind.Z3_OP_DT_CONSTRUCTOR:
                    {
                        if (funcDecl.Range is DatatypeSort dtSort && dtSort.NumConstructors == 1 && dtSort.Name.ToString() == "Tuple")
                        {
                            var generic = typeof(ValueTuple).GetMethods().Where(x => x.Name == "Create" && x.GetGenericArguments().Length == args.Count).First();
                            var create = generic.MakeGenericMethod(args.Select(x => x.Type).ToArray());
                            return Expression.Call(create, args);
                        }
                        break;
                    }
                case Z3_decl_kind.Z3_OP_DT_ACCESSOR:
                    {
                        if (funcDecl.Domain[0] is DatatypeSort dtSort && dtSort.NumConstructors == 1 && dtSort.Name.ToString() == "Tuple")
                        {
                            Type generic;
                            switch (dtSort.Constructors[0].Arity)
                            {
                                case 1: generic = typeof(ValueTuple<>); break;
                                case 2: generic = typeof(ValueTuple<,>); break;
                                case 3: generic = typeof(ValueTuple<,,>); break;
                                case 4: generic = typeof(ValueTuple<,,,>); break;
                                case 5: generic = typeof(ValueTuple<,,,,>); break;
                                case 6: generic = typeof(ValueTuple<,,,,,>); break;
                                case 7: generic = typeof(ValueTuple<,,,,,,>); break;
                                default: throw new ZzzapException($"Unsupported tuple sort {dtSort}");
                            }
                            var valueTuple = generic.MakeGenericType(dtSort.Constructors[0].Domain.Select(x => SortToType(x)).ToArray());
                            return Expression.MakeMemberAccess(args[0], valueTuple.GetField(funcDecl.Name.ToString()));
                        }
                        break;
                    }

            }
            throw new ZzzapException($"Unsupported Z3 function {funcDecl}");
        }
    }
}