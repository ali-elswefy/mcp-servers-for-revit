using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>Thrown when the submitted snippet does not compile.</summary>
    public class CodeCompilationException : Exception
    {
        public CodeCompilationException(string message) : base(message) { }
    }

    /// <summary>
    /// Compiles AI-submitted snippets into an <c>Execute(Document, object[])</c> entry point.
    /// </summary>
    public static class DynamicCodeCompiler
    {
        // Metadata references keyed by assembly path. Creating a reference reads the whole file, so
        // the per-call scan of every loaded assembly only pays that cost for assemblies loaded since
        // the previous call. Collectible assemblies are referenced but never cached because they can
        // be unloaded; dynamic and in-memory assemblies have no file and are skipped.
        private static readonly ConcurrentDictionary<string, MetadataReference> ReferenceCache =
            new ConcurrentDictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        public static MethodInfo Compile(string code, string requestId = null)
        {
            var wrappedCode = $@"
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;

namespace AIGeneratedCode
{{
    public static class CodeExecutor
    {{
        public static object Execute(Document document, object[] parameters)
        {{
            // User code entry point
            {code}
        }}
    }}
}}";

            var stopwatch = Stopwatch.StartNew();
            var syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode);
            var references = GetLoadedAssemblyReferences();
            long referencesMs = stopwatch.ElapsedMilliseconds;

            var compilation = CSharpCompilation.Create(
                "AIGeneratedCode",
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            using (var ms = new MemoryStream())
            {
                var result = compilation.Emit(ms);
                CommandLog.Info("req={4} send_code compiled in {0}ms ({1} references, {2}ms resolving references, success={3})",
                    stopwatch.ElapsedMilliseconds, references.Count, referencesMs, result.Success, requestId ?? "-");

                if (!result.Success)
                {
                    var errors = string.Join("\n", result.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => $"Line {d.Location.GetLineSpan().StartLinePosition.Line}: {d.GetMessage()}"));
                    throw new CodeCompilationException($"Code compilation errors:\n{errors}");
                }

                var assembly = Assembly.Load(ms.ToArray());
                return assembly.GetType("AIGeneratedCode.CodeExecutor").GetMethod("Execute");
            }
        }

        private static List<MetadataReference> GetLoadedAssemblyReferences()
        {
            var references = new List<MetadataReference>();
            var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                string location;
                try
                {
                    location = assembly.Location;
                }
                catch (NotSupportedException)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(location) || !seenLocations.Add(location))
                    continue;

                try
                {
#if NET
                    if (assembly.IsCollectible)
                    {
                        references.Add(MetadataReference.CreateFromFile(location));
                        continue;
                    }
#endif
                    references.Add(ReferenceCache.GetOrAdd(location, path => MetadataReference.CreateFromFile(path)));
                }
                catch (IOException)
                {
                    // The file was removed after the assembly was loaded; it cannot be referenced.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return references;
        }
    }
}
