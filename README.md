# ZZZAP

The Z3 Aggregation Parallelizer (ZZZAP) automatically parallelizes stateful aggregations over sequential data. In its current state it parallelizes some interesting aggregations, but is not general enough to be truly useful. However, I find the approach ZZZAP uses interesting and will give an overview of it below.

For some background, I started working on this problem while interning at Microsoft Research, as a continuation of the work in this paper: [Parallelizing user-defined aggregations using symbolic execution](https://doi.org/10.1145/2815400.2815418). In that paper's approach users build simple symbolic executors for their aggregations such that the loop-carried state is unknown. The idea is that *if the state representation of that symbolic execution is compact*, then it can be used for parallelizing evaluation. In that paper the representations are compact by construction. We started building a more general approach based on runtime rewriting of the state representation, but somewhat predictably that turned out to be too slow. The internship pivoted to lower hanging fruit, but I continued working on the problem later.

The breakthrough idea is that parallelizable aggregations seem to have symbolic state representations that rewrite into a small number of "shapes". For example, if the aggregation is for the maximum element, then without knowing the maximum `x` so far, there are only two shapes the symbolic computation has to consider:
1. Just return the symbolic state `x`. *This happens when no further input has been seen.*
1. Take the larger of a concrete value `c` and the symbolic state `x`.

Lets look at how the latter shape behaves when expanded with a further input value. So given a new input `i`, the naive state representation you'd get is:
* Take the larger of a concrete value `c` and the symbolic state `x`. Then take the larger of that and a concrete value `i`.

However, this is equivalent to directly evaluating the max of `c` and `i` and instantiating shape 2 with the result.

ZZZAP accomplishes this kind of reasoning through a system of rewrite rules (see [Zzzap/Rewriters.cs](Zzzap/Rewriters.cs)) combined with a system for exploring the different ways those rewrite rules may apply (see [Zzzap/RewriteExplorer.cs](Zzzap/RewriteExplorer.cs)). Because ZZZAP is solving the different "shapes" of the symbolic execution's state representation statically, at this rewrite-time there are actually three kinds of symbolic values present:
* The loop-carried state (`x` above).
* Shape parameters (`c` above).
* The next input (`i` above).

Given a known shape ZZZAP needs to first expand it with one more step of aggregation with a new `i` and then find good ways to rewrite the resulting symbolic state representation into new compact shapes such that the set of reachable shapes is finite. These rewrites can depend on predicates that depend on `c` and `i`, but not on `x`. To accomplish this, the rewrites in [Zzzap/Rewriters.cs](Zzzap/Rewriters.cs) have access to a function `eval(pred)`, that returns true when the given predicate `pred` can be assumed to be true. The way [Zzzap/RewriteExplorer.cs](Zzzap/RewriteExplorer.cs) achieves this is by *symbolically executing the rewrite rules* to discover the different ways the rewrite rules themselves may branch. Essentially, if the rewrite explorer can eliminate `x` from `pred`, then the true branch in the rewrite will be explored. ZZZAP ultimately relies on Z3's quantifier elimination tactics for this, which works sometimes but not always.

To summarize: to discover an efficient symbolic executor at compile time ZZZAP is **symbolically executing a rewrite system** for simplifying symbolic state representations.

If anybody wants to push this idea further, I'd be more than happy to help you get started/provide mentorship.
