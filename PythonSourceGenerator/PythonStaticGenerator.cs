﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Python.Runtime;
using PythonSourceGenerator.Reflection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PythonSourceGenerator
{
    [Generator(LanguageNames.CSharp)]
    public class PythonStaticGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            //System.Diagnostics.Debugger.Launch();
            var pythonFilesPipeline = context.AdditionalTextsProvider
                .Where(static text => Path.GetExtension(text.Path) == ".py")
                .Collect();

            var optionsPipeline = context.AnalyzerConfigOptionsProvider.Select(static (options, ct) =>
            {
                var globalOptions = options.GlobalOptions;
                if (!globalOptions.TryGetValue("build_property.PythonVersion", out string pythonVersion))
                {
                    pythonVersion = "3.12.4";
                }

                if (!globalOptions.TryGetValue("build_property.PythonLocation", out string pythonLocation))
                {
                    pythonLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python", pythonVersion);
                }

                if (!globalOptions.TryGetValue("build_property.PythonVirtualEnvironment", out string pythonVirtualEnvironment))
                {
                    pythonVirtualEnvironment = null;
                }

                return (pythonVersion, pythonLocation, pythonVirtualEnvironment);
            });

            context.RegisterSourceOutput(pythonFilesPipeline.Combine(optionsPipeline), static (sourceContext, pair) =>
            {
                var pyFiles = pair.Left;
                var pythonLocation = pair.Right.pythonLocation;
                var pythonVersion = pair.Right.pythonVersion;
                var pythonVirtualEnvironment = pair.Right.pythonVirtualEnvironment;

                var builder = new PythonEnvironment(
                        pythonLocation,
                        pythonVersion);

                if (!string.IsNullOrEmpty(pythonVirtualEnvironment))
                {
                    builder.WithVirtualEnvironment(pythonVirtualEnvironment);
                    sourceContext.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor("PSG001", "PythonStaticGenerator", $"Using virtual environment {pythonVirtualEnvironment}", "PythonStaticGenerator", DiagnosticSeverity.Warning, true), Location.None));
                }
                foreach (var file in pyFiles)
                {
                    using var env = builder.Build(Path.GetDirectoryName(file.Path));

                    // Add environment path
                    var @namespace = "Python.Generated"; // TODO : Infer from project

                    var fileName = Path.GetFileNameWithoutExtension(file.Path);

                    // Convert snakecase to pascal case
                    var pascalFileName = string.Join("", fileName.Split('_').Select(s => char.ToUpperInvariant(s[0]) + s.Substring(1)));

                    List<MethodDeclarationSyntax> methods;
                    using (Py.GIL())
                    {
                        // create a Python scope
                        using PyModule scope = Py.CreateScope();
                        var pythonModule = scope.Import(fileName);
                        methods = ModuleReflection.MethodsFromModule(pythonModule, scope);
                    }

                    string source = FormatClassFromMethods(@namespace, pascalFileName, methods);
                    sourceContext.AddSource($"{pascalFileName}.py.cs", source);
                    sourceContext.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor("PSG002", "PythonStaticGenerator", $"Generated {pascalFileName}.py.cs", "PythonStaticGenerator", DiagnosticSeverity.Warning, true), Location.None));
                }
            });
        }

        public static string FormatClassFromMethods(string @namespace, string pascalFileName, List<MethodDeclarationSyntax> methods)
        {
            return $$"""
                // <auto-generated/>
                using Python.Runtime;
                using PythonEnvironments;

                using System.Collections.Generic;

                namespace {{@namespace}}
                {
                    public static class {{pascalFileName}}Extensions
                    {
                        public static I{{pascalFileName}} {{pascalFileName}}(this IPythonEnvironment env)
                        {
                            return new {{pascalFileName}}Internal();
                        }

                        private class {{pascalFileName}}Internal : I{{pascalFileName}}
                        {
                            {{methods.Compile()}}
                        }
                    }
                    public interface I{{pascalFileName}}
                    {
                        {{string.Join(Environment.NewLine, methods.Select(m => $"{m.ReturnType.NormalizeWhitespace()} {m.Identifier.Text}{m.ParameterList.NormalizeWhitespace()};"))}}
                    }
                }
                """;
        }

    }
}