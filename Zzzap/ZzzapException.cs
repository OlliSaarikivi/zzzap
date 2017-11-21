using System;
using System.Runtime.Serialization;

namespace Zzzap {
    [Serializable]
    public class ZzzapException : Exception
    {
        public ZzzapException() { }
        public ZzzapException(string message) : base(message) { }
        public ZzzapException(string message, Exception inner) : base(message, inner) { }
        protected ZzzapException(
            SerializationInfo info,
            StreamingContext context) : base(info, context) { }
    }
}