using System.Collections;
using System.Text;
using System.Text.Json;
using Python.Runtime;
using QuantConnect.Python;

namespace QuantConnect.Research.Engine.Experiments.Python
{
    /// <summary>
    /// <see cref="IPythonStrategyHost"/> implemented with pythonnet. Loads a strategy class
    /// from the script module and drives its optional hooks. The class name defaults to
    /// <c>Strategy</c> and can be overridden with experimentConfig key <c>"class"</c>
    /// (used by the base-class API, <c>quantlab.research.ResearchStrategy</c>).
    ///
    /// When the environment variable <c>QUANTLAB_PYTHON_PATH</c> is set, it is treated as a
    /// path-separated list of extra directories added to the strategy's <c>sys.path</c> (so a
    /// strategy that imports shared libraries such as quantlab finds them). Example on the cloud
    /// VM: <c>&lt;repo&gt;/Research/Python</c>.
    ///
    /// Python-to-.NET values (hook return values) cross the boundary as JSON so the interop stays
    /// version-agnostic: strategies must return plain Python values (float, int, str, bool, None,
    /// list, dict). NaN / Infinity and numpy scalars are not JSON-serializable and will raise a
    /// clear <see cref="StrategyScriptException"/> (call <c>.item()</c> for numpy scalars).
    /// </summary>
    public sealed class PythonNetStrategyHost : IPythonStrategyHost
    {
        private PyObject _strategy;
        private string _scriptPath;
        private bool _disposed;
        private static bool _stdoutRedirected;
        private int _observationCount;

        /// <summary>
        /// Strategy class name for a run: experimentConfig key <c>"class"</c>, else <c>Strategy</c>.
        /// </summary>
        public static string ResolveStrategyClassName(Dictionary<string, object> context)
        {
            if (context != null
                && context.TryGetValue("config", out var configObj)
                && configObj is IDictionary<string, object> config
                && config.TryGetValue("class", out var classObj)
                && classObj is string className
                && !string.IsNullOrWhiteSpace(className))
            {
                return className;
            }

            return "Strategy";
        }

        public void Initialize(string scriptPath, Dictionary<string, object> context)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(scriptPath))
            {
                throw new StrategyScriptException(
                    "Strategy script path is empty. Set job.StrategyScript (or experimentConfig \"script\").");
            }

            var fullPath = Path.GetFullPath(scriptPath);
            if (!File.Exists(fullPath))
            {
                throw new StrategyScriptException($"Strategy script not found: {fullPath}");
            }

            var moduleName = Path.GetFileNameWithoutExtension(fullPath);
            if (string.IsNullOrWhiteSpace(moduleName)
                || !moduleName.All(c => char.IsLetterOrDigit(c) || c == '_')
                || !(char.IsLetter(moduleName[0]) || moduleName[0] == '_'))
            {
                throw new StrategyScriptException(
                    $"Script file name '{moduleName}' is not a valid Python module name (letters, digits, underscores).");
            }

            try
            {
                PythonInitializer.Initialize();
            }
            catch (Exception ex)
            {
                throw new StrategyScriptException(
                    "Failed to initialize the Python runtime: " + ex.Message +
                    " pythonnet needs a compatible CPython on this machine; set the PYTHONNET_PYDLL " +
                    "environment variable to the python DLL if auto-detection fails.",
                    ex);
            }

            var scriptDir = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
            var pythonPaths = new List<string> { scriptDir };
            var extraPythonPaths = Environment.GetEnvironmentVariable("QUANTLAB_PYTHON_PATH");
            if (!string.IsNullOrWhiteSpace(extraPythonPaths))
            {
                foreach (var dir in extraPythonPaths.Split(
                    new[] { Path.PathSeparator, ';' },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Directory.Exists(dir))
                    {
                        pythonPaths.Add(dir);
                    }
                }
            }

            PythonInitializer.AddPythonPaths(pythonPaths.ToArray());

            using (Py.GIL())
            {
                try
                {
                    RedirectPythonOutput(context);
                    using var module = Py.Import(moduleName);
                    var className = ResolveStrategyClassName(context);
                    if (!module.HasAttr(className))
                    {
                        throw new StrategyScriptException(
                            $"Script {fullPath} must define a class named '{className}'.");
                    }

                    using var strategyType = module.GetAttr(className);
                    _strategy = strategyType.Invoke();
                    _scriptPath = fullPath;
                    if (_strategy == null || _strategy.IsNone())
                    {
                        throw new StrategyScriptException($"Constructing Strategy from {fullPath} returned None.");
                    }

                    var hooks = new[] { "initialize", "on_observation", "on_outcome", "finalize" }
                        .Where(_strategy.HasAttr);
                    LogLine("run hooks: " + string.Join(" ", hooks));
                }
                catch (StrategyScriptException)
                {
                    throw;
                }
                catch (PythonException ex)
                {
                    throw new StrategyScriptException($"Failed to load strategy '{fullPath}': {FormatPythonError(ex)}", ex);
                }
            }

            try
            {
                using var gil = Py.GIL();
                InvokeOptional("initialize", context == null ? Array.Empty<PyObject>() : new[] { ToPyDictionary(context) });
            }
            catch (StrategyScriptException)
            {
                throw;
            }
            catch (PythonException ex)
            {
                WriteLogError("Strategy.initialize", _scriptPath, ex);
                throw new StrategyScriptException($"Strategy.initialize failed for '{_scriptPath}': {FormatPythonError(ex)}", ex);
            }
        }

        /// <summary>
        /// Reads a string from the run's experimentConfig ("config") block, if any.
        /// </summary>
        private static string ResolveConfigValue(Dictionary<string, object> context, string key)
        {
            if (context != null
                && context.TryGetValue("config", out var configObj)
                && configObj is IDictionary<string, object> config
                && config.TryGetValue(key, out var valueObj)
                && valueObj is string value
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return null;
        }

        /// <summary>
        /// Redirects the strategy's Python stdout/stderr to a log file so hook print()s
        /// and tracebacks reach disk instead of the (unwired) embedded console.
        /// The path comes from experimentConfig key "strategy_log"; skipped when absent.
        /// </summary>
        private static void RedirectPythonOutput(Dictionary<string, object> context)
        {
            var logPath = ResolveConfigValue(context, "strategy_log");
            if (string.IsNullOrWhiteSpace(logPath))
            {
                return;
            }

            if (_stdoutRedirected)
            {
                return;
            }

            try
            {
                var parent = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                var quoted = System.Text.Json.JsonSerializer.Serialize(logPath);
                PythonEngine.Exec(
                    "import sys as _sys\n" +
                    "_ql_f = open(" + quoted + ", 'w', encoding='utf-8')\n" +
                    "_ql_f.write('# quantlab strategy log (python ' + _sys.version.split()[0] + ')\\n')\n" +
                    "_ql_f.flush()\n" +
                    "def _ql_write(s):\n" +
                    "    try:\n" +
                    "        _ql_f.write(s); _ql_f.flush()\n" +
                    "    except Exception:\n" +
                    "        pass\n" +
                    "class _QLStream:\n" +
                    "    def write(self, s): _ql_write(s)\n" +
                    "    def writelines(self, lines):\n" +
                    "        for ln in lines: _ql_write(ln)\n" +
                    "    def flush(self):\n" +
                    "        try: _ql_f.flush()\n" +
                    "        except Exception: pass\n" +
                    "    def isatty(self): return False\n" +
                    "    def fileno(self):\n" +
                    "        try: return _ql_f.fileno()\n" +
                    "        except Exception: return 0\n" +
                    "_sys.stdout = _QLStream()\n" +
                    "_sys.stderr = _sys.stdout\n");
                _stdoutRedirected = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[python] could not redirect strategy output to {logPath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Logs an engine-level line to the strategy log ('[ql]' prefix so it never
        /// collides with user prints).
        /// </summary>
        private static void LogLine(string text)
        {
            if (!_stdoutRedirected)
            {
                return;
            }

            try
            {
                var quoted = System.Text.Json.JsonSerializer.Serialize($"[ql] {text}\n");
                using (Py.GIL())
                {
                    PythonEngine.Exec("import sys; sys.stdout.write(" + quoted + ")");
                }
            }
            catch
            {
                // Best effort; the strategy log is a diagnostic aid.
            }
        }

        /// <summary>
        /// Writes a structured error section to the strategy log so a hook failure is
        /// traceable from the log itself (which hook, which observation, where in the
        /// strategy script, and the full Python traceback).
        /// </summary>
        private static void WriteLogError(string hook, string context, Exception ex)
        {
            if (!_stdoutRedirected)
            {
                return;
            }

            try
            {
                var pythonFrames = new List<string>();
                var stackTrace = (ex as PythonException)?.StackTrace ?? string.Empty;
                var sawFrame = false;
                foreach (var raw in stackTrace.Split('\n'))
                {
                    var line = raw.TrimEnd('\r').Trim();
                    if (line.Length == 0)
                    {
                        if (sawFrame)
                        {
                            break;
                        }

                        continue;
                    }

                    if (line.StartsWith("at ", StringComparison.Ordinal))
                    {
                        if (sawFrame)
                        {
                            break;
                        }

                        continue;
                    }

                    pythonFrames.Add(line);
                    sawFrame = true;
                }

                var sb = new StringBuilder();
                sb.Append('\n').Append("<-- ").Append(hook).Append(" raised -->\n");
                sb.Append("  cause  ").Append(ex.Message).Append('\n');
                if (!string.IsNullOrWhiteSpace(context))
                {
                    sb.Append("  while  ").Append(context).Append('\n');
                }

                sb.Append("  stack\n");
                foreach (var frame in pythonFrames.DefaultIfEmpty("(no python frames captured)"))
                {
                    sb.Append("    ").Append(frame).Append('\n');
                }

                sb.Append("<-- end -->\n");
                var quoted = System.Text.Json.JsonSerializer.Serialize(sb.ToString());
                using (Py.GIL())
                {
                    PythonEngine.Exec("import sys; sys.stderr.write(" + quoted + ")");
                }
            }
            catch (Exception logEx)
            {
                Console.WriteLine($"[python] could not write error block to strategy log: {logEx.Message}");
            }
        }

        public Dictionary<string, object> OnObservation(Dictionary<string, object> observation, Dictionary<string, object> features)
        {
            ThrowIfDisposed();
            _observationCount++;

            object result;
            try
            {
                using var gil = Py.GIL();
                result = InvokeReturning("on_observation", ToPyDictionary(observation), ToPyDictionary(features));
            }
            catch (StrategyScriptException)
            {
                throw;
            }
            catch (PythonException ex)
            {
                var ts = observation != null && observation.TryGetValue("timestamp", out var tsObj)
                    ? tsObj?.ToString() : string.Empty;
                WriteLogError("Strategy.on_observation",
                    "observation #" + _observationCount + (string.IsNullOrWhiteSpace(ts) ? "" : " t=" + ts), ex);
                throw new StrategyScriptException(
                    $"Strategy.on_observation failed for '{_scriptPath}': {FormatPythonError(ex)}", ex);
            }

            if (result == null)
            {
                return null;
            }

            if (result is Dictionary<string, object> row)
            {
                return row;
            }

            throw new StrategyScriptException(
                $"Strategy.on_observation must return a dict (result row) or None; got {result.GetType().Name}. " +
                "Return plain Python values only.");
        }

        public void OnOutcome(Dictionary<string, object> outcome)
        {
            ThrowIfDisposed();
            try
            {
                using var gil = Py.GIL();
                InvokeOptional("on_outcome", outcome == null ? Array.Empty<PyObject>() : new[] { ToPyDictionary(outcome) });
            }
            catch (StrategyScriptException)
            {
                throw;
            }
            catch (PythonException ex)
            {
                WriteLogError("Strategy.on_outcome", string.Empty, ex);
                throw new StrategyScriptException($"Strategy.on_outcome failed for '{_scriptPath}': {FormatPythonError(ex)}", ex);
            }
        }

        public StrategyFinalizeResult Finalize()
        {
            ThrowIfDisposed();

            object raw;
            try
            {
                using var gil = Py.GIL();
                raw = InvokeReturning("finalize");
            }
            catch (StrategyScriptException)
            {
                throw;
            }
            catch (PythonException ex)
            {
                WriteLogError("Strategy.finalize", string.Empty, ex);
                throw new StrategyScriptException($"Strategy.finalize failed for '{_scriptPath}': {FormatPythonError(ex)}", ex);
            }

            var result = new StrategyFinalizeResult();
            if (raw == null)
            {
                return result;
            }

            if (raw is not Dictionary<string, object> finalized)
            {
                throw new StrategyScriptException(
                    "Strategy.finalize must return a dict with optional keys 'rows', 'metrics', " +
                    "'metadata' (or None); got " + raw.GetType().Name + ".");
            }

            if (finalized.TryGetValue("rows", out var rowsObj) && rowsObj is List<object> rows)
            {
                foreach (var row in rows)
                {
                    if (row is Dictionary<string, object> rowDict)
                    {
                        result.Rows.Add(rowDict);
                    }
                    else
                    {
                        throw new StrategyScriptException("Strategy.finalize['rows'] must be a list of dicts.");
                    }
                }
            }

            if (finalized.TryGetValue("metrics", out var metricsObj) && metricsObj != null)
            {
                if (metricsObj is not Dictionary<string, object> metrics)
                {
                    throw new StrategyScriptException("Strategy.finalize['metrics'] must be a dict.");
                }

                foreach (var kvp in metrics)
                {
                    result.Metrics[kvp.Key] = kvp.Value;
                }
            }

            if (finalized.TryGetValue("metadata", out var metaObj) && metaObj is Dictionary<string, object> meta)
            {
                foreach (var kvp in meta)
                {
                    result.Metadata[kvp.Key] = kvp.Value?.ToString() ?? string.Empty;
                }
            }

            return result;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                using (Py.GIL())
                {
                    PythonEngine.Exec("sys.stdout.flush(); sys.stderr.flush()");
                }
            }
            catch
            {
                // Best-effort; the runtime may be gone.
            }

            try
            {
                using (Py.GIL())
                {
                    _strategy?.Dispose();
                }
            }
            catch
            {
                // Runtime may already be gone; the PyObject wrapper will be finalized by the GC.
            }

            _strategy = null;
        }

        /// <summary>
        /// Invokes an optional hook with converted arguments. Returns null when the hook is absent.
        /// </summary>
        private object InvokeReturning(string methodName, params PyObject[] args)
        {
            if (_strategy == null)
            {
                throw new StrategyScriptException("Strategy is not loaded.");
            }

            try
            {
                if (!_strategy.HasAttr(methodName))
                {
                    return null;
                }

                using var method = _strategy.GetMethod(methodName);
                using var result = method.Invoke(args);
                return FromPython(result);
            }
            finally
            {
                foreach (var arg in args)
                {
                    arg?.Dispose();
                }
            }
        }

        private void InvokeOptional(string methodName, params PyObject[] args)
        {
            if (_strategy == null)
            {
                throw new StrategyScriptException("Strategy is not loaded.");
            }

            try
            {
                if (!_strategy.HasAttr(methodName))
                {
                    return;
                }

                using var method = _strategy.GetMethod(methodName);
                using var result = method.Invoke(args);
            }
            finally
            {
                foreach (var arg in args)
                {
                    arg?.Dispose();
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PythonNetStrategyHost));
            }
        }

        private static string FormatPythonError(PythonException ex)
        {
            var stackTrace = ex.StackTrace;
            return string.IsNullOrEmpty(stackTrace) ? ex.Message : ex.Message + Environment.NewLine + stackTrace;
        }

        /// <summary>
        /// Converts a returned PyObject into JSON-friendly CLR values (null, bool, string, long,
        /// double, List&lt;object&gt;, Dictionary&lt;string, object&gt;).
        /// </summary>
        private static object FromPython(PyObject value)
        {
            if (value == null || value.IsNone())
            {
                return null;
            }

            using var json = Py.Import("json");
            using var dumps = json.GetAttr("dumps");
            PyObject encoded;
            try
            {
                encoded = dumps.Invoke(value);
            }
            catch (PythonException ex)
            {
                throw new StrategyScriptException(
                    "Strategy returned a value that cannot be JSON-encoded. Return plain Python values " +
                    "only (float, int, str, bool, None, list, dict); for numpy scalars call .item(): " +
                    FormatPythonError(ex),
                    ex);
            }

            using (encoded)
            {
                var text = encoded.As<string>();

                // json.dumps emits bare NaN/Infinity/-Infinity for non-finite floats, which is valid
                // Python but not valid JSON, so JsonDocument.Parse would fail with an opaque
                // "Expected a value" message. Detect it here and name the offending keys.
                var nonFinite = FindNonFiniteTokens(text);
                if (nonFinite != null)
                {
                    throw new StrategyScriptException(
                        "Strategy returned a non-finite float (NaN or Infinity), which cannot be written to the " +
                        $"experiment output. Offending value(s): {nonFinite}. Guard your arithmetic, e.g. " +
                        "return None instead of float('nan'), or check the divisor before dividing.");
                }

                using var doc = JsonDocument.Parse(text);
                return JsonValueToObject(doc.RootElement);
            }
        }

        /// <summary>
        /// Scans JSON text for the bare NaN/Infinity/-Infinity tokens json.dumps produces, and returns a
        /// short description of where they appear, or null when the payload is clean. Bounded scan: it
        /// stops after the first few occurrences so a pathological row cannot be walked in full.
        /// Internal rather than private so the token rules can be tested without a Python runtime.
        /// </summary>
        internal static string FindNonFiniteTokens(string json)
        {
            const int maxReported = 3;
            var found = new List<string>();

            foreach (var token in new[] { "NaN", "Infinity", "-Infinity" })
            {
                var index = 0;
                while (found.Count < maxReported
                       && (index = json.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
                {
                    // A quoted occurrence is the literal string "NaN" inside data, not a non-finite float.
                    var isQuoted = index > 0 && json[index - 1] == '"'
                                   && index + token.Length < json.Length
                                   && json[index + token.Length] == '"';
                    if (!isQuoted)
                    {
                        found.Add(token);
                    }

                    index += token.Length;
                }

                if (found.Count >= maxReported)
                {
                    break;
                }
            }

            return found.Count == 0 ? null : string.Join(", ", found);
        }

        private static object JsonValueToObject(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number =>
                    element.TryGetInt64(out var integral) ? integral : element.GetDouble(),
                JsonValueKind.Array => element
                    .EnumerateArray()
                    .Select(JsonValueToObject)
                    .ToList(),
                JsonValueKind.Object => element
                    .EnumerateObject()
                    .ToDictionary(prop => prop.Name, prop => JsonValueToObject(prop.Value)),
                _ => element.GetRawText()
            };
        }

        private static PyObject ToPyDictionary(IDictionary<string, object> source)
        {
            var pyDict = new PyDict();
            if (source != null)
            {
                foreach (var kvp in source)
                {
                    if (string.IsNullOrEmpty(kvp.Key) || kvp.Value == null)
                    {
                        continue;
                    }

                    pyDict.SetItem(kvp.Key.ToPython(), ToPyValue(kvp.Value));
                }
            }

            return pyDict;
        }

        private static PyObject ToPyDictionaryFromNonGeneric(IDictionary source)
        {
            var pyDict = new PyDict();
            if (source != null)
            {
                foreach (var key in source.Keys)
                {
                    var value = source[key];
                    if (key == null || value == null)
                    {
                        continue;
                    }

                    pyDict.SetItem(key.ToString().ToPython(), ToPyValue(value));
                }
            }

            return pyDict;
        }

        private static PyObject ToPyList(IEnumerable<object> values)
        {
            return new PyList(values.Where(v => v != null).Select(ToPyValue).ToArray());
        }

        private static PyObject ToPyValue(object value)
        {
            return value switch
            {
                bool b => b.ToPython(),
                int i => i.ToPython(),
                long l => l.ToPython(),
                double d => d.ToPython(),
                float f => f.ToPython(),
                decimal m => ((double)m).ToPython(),
                string s => s.ToPython(),
                DateTime t => t.ToString("O").ToPython(),
                DateTimeOffset o => o.ToString("O").ToPython(),
                IDictionary<string, object> dict => ToPyDictionary(dict),
                IDictionary nonGenericDict => ToPyDictionaryFromNonGeneric(nonGenericDict),
                IEnumerable<object> list => ToPyList(list),
                _ => (value?.ToString() ?? string.Empty).ToPython()
            };
        }
    }
}