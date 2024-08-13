﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using PythonSourceGenerator.Parser;
using PythonSourceGenerator.Parser.Types;
using PythonSourceGenerator.Reflection;

namespace PythonSourceGenerator;

[Generator(LanguageNames.CSharp)]
public class PythonStaticGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        //System.Diagnostics.Debugger.Launch();
        var pythonFilesPipeline = context.AdditionalTextsProvider
            .Where(static text => Path.GetExtension(text.Path) == ".py")
            .Collect();

        context.RegisterSourceOutput(pythonFilesPipeline, static (sourceContext, inputFiles) =>
        {
            foreach (var file in inputFiles)
            {
                // Add environment path
                var @namespace = "CSnakes.Runtime"; // TODO: (track) Infer namespace from project

                var fileName = Path.GetFileNameWithoutExtension(file.Path);

                // Convert snakecase to pascal case
                var pascalFileName = string.Join("", fileName.Split('_').Select(s => char.ToUpperInvariant(s[0]) + s.Substring(1)));
                // Read the file
                var code = file.GetText(sourceContext.CancellationToken);

                if (code == null) continue;

                // Parse the Python file
                var result = PythonParser.TryParseFunctionDefinitions(code, out PythonFunctionDefinition[] functions, out GeneratorError[]? errors);

                foreach (var error in errors)
                {
                    // Update text span
                    Location errorLocation = Location.Create(file.Path, TextSpan.FromBounds(0, 1), new LinePositionSpan(new LinePosition(error.StartLine, error.StartColumn), new LinePosition(error.EndLine, error.EndColumn)));
                    sourceContext.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor("PSG004", "PythonStaticGenerator", error.Message, "PythonStaticGenerator", DiagnosticSeverity.Error, true), errorLocation));
                }

                if (result) {
                    IEnumerable<MethodDefinition> methods = ModuleReflection.MethodsFromFunctionDefinitions(functions, fileName);
                    string source = FormatClassFromMethods(@namespace, pascalFileName, methods, fileName);
                    sourceContext.AddSource($"{pascalFileName}.py.cs", source);
                    sourceContext.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor("PSG002", "PythonStaticGenerator", $"Generated {pascalFileName}.py.cs", "PythonStaticGenerator", DiagnosticSeverity.Info, true), Location.None));
                }
            }
        });
    }

    public static string FormatClassFromMethods(string @namespace, string pascalFileName, IEnumerable<MethodDefinition> methods, string fileName)
    {
        var paramGenericArgs = methods
            .Select(m => m.ParameterGenericArgs)
            .Where(l => l is not null && l.Any());

        return $$"""
            // <auto-generated/>
            using CSnakes.Runtime;
            using CSnakes.Runtime.Python;

            using System;
            using System.Collections.Generic;
            using System.ComponentModel;
            using System.Diagnostics;

            using Microsoft.Extensions.Logging;

            namespace {{@namespace}};
            public static class {{pascalFileName}}Extensions
            {
                private static I{{pascalFileName}}? instance;

                public static I{{pascalFileName}} {{pascalFileName}}(this IPythonEnvironment env)
                {
                    if (instance is null)
                    {
                        instance = new {{pascalFileName}}Internal(env.Logger);
                    }
                    Debug.Assert(!env.IsDisposed());
                    return instance;
                }

                private class {{pascalFileName}}Internal : I{{pascalFileName}}
                {
                    private readonly TypeConverter td = TypeDescriptor.GetConverter(typeof(PyObject));

                    private readonly PyObject module;

                    private readonly ILogger<IPythonEnvironment> logger;

                    internal {{pascalFileName}}Internal(ILogger<IPythonEnvironment> logger)
                    {
                        this.logger = logger;
                        using (GIL.Acquire())
                        {
                            logger.LogInformation("Importing module {ModuleName}", "{{fileName}}");
                            module = Import.ImportModule("{{fileName}}");
                        }
                    }

                    public void Dispose()
                    {
                        logger.LogInformation("Disposing module {ModuleName}", "{{fileName}}");
                        module.Dispose();
                    }

                    {{methods.Select(m => m.Syntax).Compile()}}
                }
            }
            public interface I{{pascalFileName}}
            {
                {{string.Join(Environment.NewLine, methods.Select(m => m.Syntax).Select(m => $"{m.ReturnType.NormalizeWhitespace()} {m.Identifier.Text}{m.ParameterList.NormalizeWhitespace()};"))}}
            }
            """;
    }
}