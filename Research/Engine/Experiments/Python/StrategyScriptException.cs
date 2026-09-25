namespace QuantConnect.Research.Engine.Experiments.Python
{
    /// <summary>
    /// Raised when a Python strategy script fails to load, construct, or raises inside a hook.
    /// The message carries script context and the Python traceback when available.
    /// </summary>
    public sealed class StrategyScriptException : Exception
    {
        public StrategyScriptException(string message)
            : base(message)
        {
        }

        public StrategyScriptException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}