using System;
using System.Diagnostics;

namespace Zzzap
{
    static class Require
    {
        [DebuggerStepThrough]
        public static void NotNull(object arg, string name)
        {
            if (null == arg) throw new ArgumentNullException($"{name} must not be null");
        }

        [DebuggerStepThrough]
        public static void Holds(bool arg, string message)
        {
            if (!arg) throw new ArgumentException(message);
        }
    }
}