using System;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Microsoft.Z3;
using System.Collections.Generic;

namespace Zzzap
{
    public class Aggregation
    {
        private static string[] ValueTupleFieldNames = new string[] { "Item1", "Item2", "Item3", "Item4", "Item5", "Item6", "Item7", "Rest" };

        public Expr State { get; }
        public Expr Input { get; }
        public Expr Body { get; }

        public Aggregation(Expr state, Expr input, Expr body)
        {
            Require.NotNull(state, nameof(state));
            Require.NotNull(input, nameof(input));
            Require.NotNull(body, nameof(body));
            Require.Holds(body.Sort == state.Sort, "body must have the same sort as state");

            State = state;
            Input = input;
            Body = body;
        }

        public static Aggregation FromExpression<TState, TIn>(Context ctx, Expression<Func<TState, TIn, TState>> aggregation)
        {
            Console.WriteLine(aggregation);
            Debug.Assert(aggregation.Parameters.Count == 2);
            var stateVar = ExpressionToExpr(ctx, aggregation.Parameters[0]);
            var inputVar = ExpressionToExpr(ctx, aggregation.Parameters[1]);
            var body = ExpressionToExpr(ctx, aggregation.Body);
            return new Aggregation(stateVar, inputVar, body);
        }

        public static Expr ExpressionToExpr(Context ctx, Expression expression)
        {
            switch (expression)
            {
                case ParameterExpression parameter:
                    var parameterSort = TypeToSort(ctx, parameter.Type);
                    return ctx.MkConst(parameter.Name, parameterSort);
                case ConstantExpression constant:
                    switch (constant.Value)
                    {
                        case int intConst: return ctx.MkInt(intConst);
                        case bool boolConst: return ctx.MkBool(boolConst);
                    }
                    throw new ArgumentException($"Unsupported constant type {constant.Type.Name}");
                case ConditionalExpression conditional:
                    var condition = (BoolExpr)ExpressionToExpr(ctx, conditional.Test);
                    var ifTrue = ExpressionToExpr(ctx, conditional.IfTrue);
                    var IfFalse = ExpressionToExpr(ctx, conditional.IfFalse);
                    return ctx.MkITE(condition, ifTrue, IfFalse);
                case BinaryExpression binary:
                    var left = ExpressionToExpr(ctx, binary.Left);
                    var right = ExpressionToExpr(ctx, binary.Right);
                    Expr binaryExpr = BinaryToExpr(ctx, binary.NodeType, left, right);
                    if (null != binaryExpr) return binaryExpr;
                    else throw new ArgumentException($"Unsupported binary operation in {binary}");
                case UnaryExpression unary:
                    var unaryArg = ExpressionToExpr(ctx, unary.Operand);
                    Expr unaryExpr = UnaryToExpr(ctx, unary.NodeType, unaryArg);
                    if (null != unaryExpr) return unaryExpr;
                    else throw new ArgumentException($"Unsupported unary operation in {unary}");
                case MemberExpression member:
                    var memberArg = ExpressionToExpr(ctx, member.Expression);
                    if (IsValueTupleInstance(member.Member.DeclaringType))
                    {
                        var fieldIndex = ValueTupleFieldNames.Select((x, i) => (x, i)).FirstOrDefault(x => x.Item1 == member.Member.Name).Item2;
                        var tupleSort = (DatatypeSort)memberArg.Sort;
                        return tupleSort.Accessors[0][fieldIndex].Apply(memberArg);
                    }
                    else throw new ArgumentException($"Unsupported member access in {member}");
                case MethodCallExpression methodCall:
                    if (methodCall.Object == null &&
                        methodCall.Method.DeclaringType == typeof(ValueTuple) &&
                        methodCall.Method.Name == "Create")
                    {
                        var argExprs = methodCall.Arguments.Select(x => ExpressionToExpr(ctx, x)).ToArray();
                        var tupleSort = (DatatypeSort)TypeToSort(ctx, methodCall.Type);
                        return tupleSort.Constructors[0].Apply(argExprs);
                    }
                    else throw new ArgumentException($"Unsupported method call in {methodCall}");
                default:
                    throw new ArgumentOutOfRangeException($"Unsupported expression type {expression.NodeType}");
            }
        }

        private static Sort TypeToSort(Context ctx, Type type)
        {
            if (type == typeof(int)) return ctx.IntSort;
            if (type == typeof(bool)) return ctx.BoolSort;
            if (IsValueTupleInstance(type))
            {
                var fieldTypes = type.GetGenericArguments().Select(x => TypeToSort(ctx, x)).ToArray();
                return ctx.MkDatatypeSort("Tuple", new Constructor[] { ctx.MkConstructor("MkTuple", "IsTuple",
                    ValueTupleFieldNames.Take(fieldTypes.Length).ToArray(),
                    fieldTypes) });
            }
            return null;
        }

        private static bool IsValueTupleInstance(Type type)
        {
            if (type.IsGenericType)
            {
                var generic = type.GetGenericTypeDefinition();
                return
                    generic == typeof(ValueTuple<>) ||
                    generic == typeof(ValueTuple<,>) ||
                    generic == typeof(ValueTuple<,,>) ||
                    generic == typeof(ValueTuple<,,,>) ||
                    generic == typeof(ValueTuple<,,,,>) ||
                    generic == typeof(ValueTuple<,,,,,>) ||
                    generic == typeof(ValueTuple<,,,,,,>) ||
                    generic == typeof(ValueTuple<,,,,,,,>);
            }
            return false;
        }

        private static Expr BinaryToExpr(Context ctx, ExpressionType type, Expr left, Expr right)
        {
            switch (type)
            {
                case ExpressionType.Equal: return ctx.MkEq(left, right);
                case ExpressionType.NotEqual: return ctx.MkNot(ctx.MkEq(left, right));
                case ExpressionType.AndAlso:
                case ExpressionType.And: return MakeIfIsA<BoolExpr>(ctx.MkAnd, left, right);
                case ExpressionType.OrElse:
                case ExpressionType.Or: return MakeIfIsA<BoolExpr>(ctx.MkOr, left, right);
                case ExpressionType.ExclusiveOr: return MakeIfIs2<BoolExpr>(ctx.MkXor, left, right);
                case ExpressionType.GreaterThan: return MakeIfIs2<ArithExpr>(ctx.MkGt, left, right);
                case ExpressionType.LessThan: return MakeIfIs2<ArithExpr>(ctx.MkLt, left, right);
                case ExpressionType.GreaterThanOrEqual: return MakeIfIs2<ArithExpr>(ctx.MkGe, left, right);
                case ExpressionType.LessThanOrEqual: return MakeIfIs2<ArithExpr>(ctx.MkLe, left, right);
                case ExpressionType.Add: return MakeIfIsA<ArithExpr>(ctx.MkAdd, left, right);
                case ExpressionType.Subtract: return MakeIfIsA<ArithExpr>(ctx.MkSub, left, right);
                case ExpressionType.Multiply: return MakeIfIsA<ArithExpr>(ctx.MkMul, left, right);
                case ExpressionType.Divide: return MakeIfIs2<ArithExpr>(ctx.MkDiv, left, right);
                case ExpressionType.Modulo: return MakeIfIs2<IntExpr>(ctx.MkMod, left, right);
            }
            return null;
        }

        private static Expr UnaryToExpr(Context ctx, ExpressionType type, Expr arg)
        {
            switch (type)
            {
                case ExpressionType.Not: return MakeIfIs1<BoolExpr>(ctx.MkNot, arg);
                case ExpressionType.Negate: return MakeIfIs1<ArithExpr>(ctx.MkUnaryMinus, arg);
                case ExpressionType.UnaryPlus: return arg;
            }
            return null;
        }

        private static Expr MakeIfIsA<T>(Func<T[], Expr> mk, params Expr[] args) where T : Expr
        {
            if (args.All(x => x is T)) return mk(args.Cast<T>().ToArray());
            else return null;
        }

        private static Expr MakeIfIs1<T>(Func<T, Expr> mk, Expr arg) where T : Expr
        {
            if (arg is T a) return mk(a);
            else return null;
        }

        private static Expr MakeIfIs2<T>(Func<T, T, Expr> mk, Expr left, Expr right) where T : Expr
        {
            if (left is T l && right is T r) return mk(l, r);
            else return null;
        }
    }
}