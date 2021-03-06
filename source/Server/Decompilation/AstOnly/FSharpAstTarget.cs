﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AshMind.Extensions;
using JetBrains.Annotations;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Compiler;
using MirrorSharp.Advanced;
using MirrorSharp.FSharp.Advanced;
using Mono.Cecil.Cil;

namespace SharpLab.Server.Decompilation.AstOnly {
    public class FSharpAstTarget : IAstTarget {
        private delegate void SerializeChildAction<T>(T item, IFastJsonWriter writer, string parentPropertyName, ref bool childrenStarted);
        private delegate void SerializeChildrenAction(object parent, IFastJsonWriter writer, ref bool childrenStarted);
        
        private static readonly ConcurrentDictionary<Type, Lazy<SerializeChildrenAction>> ChildrenSerializers =
            new ConcurrentDictionary<Type, Lazy<SerializeChildrenAction>>();
        private static readonly Lazy<IReadOnlyDictionary<Type, Func<object, string>>> TagNameGetters =
            new Lazy<IReadOnlyDictionary<Type, Func<object, string>>>(CompileTagNameGetters, LazyThreadSafetyMode.ExecutionAndPublication);
        private static readonly Lazy<IReadOnlyDictionary<Type, Func<Ast.SynConst, string>>> ConstValueGetters =
            new Lazy<IReadOnlyDictionary<Type, Func<Ast.SynConst, string>>>(CompileConstValueGetters, LazyThreadSafetyMode.ExecutionAndPublication);

        private static class Methods {
            // ReSharper disable MemberHidesStaticFromOuterClass
            // ReSharper disable HeapView.DelegateAllocation
            public static readonly MethodInfo SerializeNode =
                ((SerializeChildAction<object>)FSharpAstTarget.SerializeNode).Method;
            public static readonly MethodInfo SerializeList =
                ((SerializeChildAction<FSharpList<object>>)FSharpAstTarget.SerializeList).Method.GetGenericMethodDefinition();
            public static readonly MethodInfo SerializeIdent =
                ((SerializeChildAction<Ast.Ident>)FSharpAstTarget.SerializeIdent).Method;
            public static readonly MethodInfo SerializeIdentList =
                ((SerializeChildAction<FSharpList<Ast.Ident>>)FSharpAstTarget.SerializeIdentList).Method;
            public static readonly MethodInfo SerializeEnum =
                ((SerializeChildAction<int>)FSharpAstTarget.SerializeEnum).Method.GetGenericMethodDefinition();
            // ReSharper restore HeapView.DelegateAllocation
            // ReSharper restore MemberHidesStaticFromOuterClass
        }

        public Task<object> GetAstAsync(IWorkSession session, CancellationToken cancellationToken) {
            var parseTree = session.FSharp().GetLastParseResults()?.ParseTree?.Value;
            return Task.FromResult((object)(parseTree as Ast.ParsedInput.ImplFile));
        }

        public void SerializeAst(object ast, IFastJsonWriter writer) {
            var root = ((Ast.ParsedInput.ImplFile)ast).Item;
            writer.WriteStartArray();
            var childrenStarted = true;
            SerializeNode(root, writer, null, ref childrenStarted);
            writer.WriteEndArray();
        }

        private static void SerializeNode(object node, IFastJsonWriter writer, [CanBeNull] string parentPropertyName, ref bool parentChildrenStarted) {
            EnsureChildrenStarted(ref parentChildrenStarted, writer);
            writer.WriteStartObject();
            writer.WriteProperty("kind", GetFullName(node.GetType()));
            if (parentPropertyName != null)
                writer.WriteProperty("property", parentPropertyName);

            if (node is Ast.SynConst @const) {
                writer.WriteProperty("type", "token");
                if (@const is Ast.SynConst.String @string) {
                    writer.WriteProperty("value", "\"" + @string.text + "\"");
                }
                else if (@const is Ast.SynConst.Char @char) {
                    writer.WriteProperty("value", "'" + @char.Item + "'");
                }
                else {
                    var getter = ConstValueGetters.Value.GetValueOrDefault(@const.GetType());
                    if (getter != null)
                        writer.WriteProperty("value", getter(@const));
                }
            }
            else {
                writer.WriteProperty("type", "node");
                var tagName = GetTagName(node);
                if (tagName != null)
                    writer.WriteProperty("value", tagName);
            }

            var childrenStarted = false;
            GetChildrenSerializer(node.GetType()).Invoke(node, writer, ref childrenStarted);
            EnsureChildrenEnded(childrenStarted, writer);
            writer.WriteEndObject();
        }

        private static void SerializeList<T>(FSharpList<T> list, IFastJsonWriter writer, [CanBeNull] string parentPropertyName, ref bool parentChildrenStarted) {
            foreach (var item in list) {
                SerializeNode(item, writer, null /* UI does not support list property names at the moment */, ref parentChildrenStarted);
            }
        }

        private static void SerializeIdent(Ast.Ident ident, IFastJsonWriter writer, [CanBeNull] string parentPropertyName, ref bool parentChildrenStarted) {
            EnsureChildrenStarted(ref parentChildrenStarted, writer);
            writer.WriteStartObject();
            writer.WriteProperty("type", "token");
            writer.WriteProperty("kind", "Ast.Ident");
            if (parentPropertyName != null)
                writer.WriteProperty("property", parentPropertyName);
            writer.WriteProperty("value", ident.idText);
            writer.WriteEndObject();
        }

        private static void SerializeIdentList(FSharpList<Ast.Ident> list, IFastJsonWriter writer, [CanBeNull]  string parentPropertyName, ref bool parentChildrenStarted) {
            foreach (var ident in list) {
                SerializeIdent(ident, writer, parentPropertyName, ref parentChildrenStarted);
            }
        }

        private static void SerializeEnum<TEnum>(TEnum value, IFastJsonWriter writer, [CanBeNull] string parentPropertyName, ref bool parentChildrenStarted)
            where TEnum: IFormattable
        {
            EnsureChildrenStarted(ref parentChildrenStarted, writer);
            if (parentPropertyName != null) {
                writer.WriteStartObject();
                writer.WriteProperty("type", "value");
                writer.WriteProperty("property", parentPropertyName);
                writer.WriteProperty("value", value.ToString("G", null));
                writer.WriteEndObject();
            }
            else {
                writer.WriteValue(value.ToString("G", null));
            }
        }

        private static void EnsureChildrenStarted(ref bool childrenStarted, IFastJsonWriter writer) {
            if (childrenStarted)
                return;
            writer.WritePropertyStartArray("children");
            childrenStarted = true;
        }

        private static void EnsureChildrenEnded(bool childrenStarted, IFastJsonWriter writer) {
            if (!childrenStarted)
                return;
            writer.WriteEndArray();
        }

        private static SerializeChildrenAction GetChildrenSerializer(Type type) {
            return ChildrenSerializers.GetOrAdd(
                type,
                t => new Lazy<SerializeChildrenAction>(() => CompileChildrenSerializer(t), LazyThreadSafetyMode.ExecutionAndPublication)
            ).Value;
        }

        private static SerializeChildrenAction CompileChildrenSerializer(Type type) {
            var nodeAsObject = Expression.Parameter(typeof(object));
            var writer = Expression.Parameter(typeof(IFastJsonWriter));
            var refChildrenStarted = Expression.Parameter(typeof(bool).MakeByRefType());

            var node = Expression.Variable(type);
            var body = new List<Expression> {
                Expression.Assign(node, Expression.Convert(nodeAsObject, type))
            };

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)) {
                if (ShouldSkipNodeProperty(type, property))
                    continue;
                var propertyType = property.PropertyType;
                var method = GetMethodToSerialize(propertyType);
                if (method == null)
                    continue;

                var propertyName = property.Name;
                if (Regex.IsMatch(propertyName, @"^Item\d*$"))
                    propertyName = null;
                body.Add(Expression.Call(method, Expression.Property(node, property), writer, Expression.Constant(propertyName, typeof(string)), refChildrenStarted));
            }

            return Expression.Lambda<SerializeChildrenAction>(
                Expression.Block(new[] {node}, body),
                nodeAsObject, writer, refChildrenStarted
            ).Compile();
        }

        private static MethodInfo GetMethodToSerialize(Type propertyType) {
            if (propertyType == typeof(Ast.Ident))
                return Methods.SerializeIdent;

            if (propertyType == typeof(FSharpList<Ast.Ident>))
                return Methods.SerializeIdentList;

            if (propertyType.IsGenericTypeDefinedAs(typeof(FSharpList<>))) {
                var elementType = propertyType.GetGenericArguments()[0];
                if (!IsNodeType(elementType))
                    return null;
                return Methods.SerializeList.MakeGenericMethod(elementType);
            }

            if (!IsNodeType(propertyType))
                return null;

            if (propertyType.IsEnum)
                return Methods.SerializeEnum.MakeGenericMethod(propertyType);

            return Methods.SerializeNode;
        }

        private static bool ShouldSkipNodeProperty(Type type, PropertyInfo property) {
            return (type == typeof(Ast.LongIdentWithDots) && property.Name == nameof(Ast.LongIdentWithDots.id));
        }

        private static bool IsNodeType(Type type) {
            return type.DeclaringType == typeof(Ast)
                && type != typeof(Ast.QualifiedNameOfFile)
                && type != typeof(Ast.XmlDocCollector)
                && type != typeof(Ast.PreXmlDoc)
                && !(type.Name.StartsWith("SequencePoint"));
        }

        private static string GetFullName(Type astType) {
            if (astType == typeof(Ast))
                return "Ast";
            return GetFullName(astType.DeclaringType) + "." + astType.Name;
        }

        private static string GetTagName(object node) {
            var getter = TagNameGetters.Value.GetValueOrDefault(node.GetType());
            return getter?.Invoke(node);
        }

        private static IReadOnlyDictionary<Type, Func<object, string>> CompileTagNameGetters() {
            var getters = new Dictionary<Type, Func<object, string>>();
            CompileAndCollectTagNameGettersRecursive(getters, typeof(Ast));
            return getters;
        }

        private static void CompileAndCollectTagNameGettersRecursive(IDictionary<Type, Func<object, string>> getters, Type astType) {
            foreach (var nested in astType.GetNestedTypes()) {
                if (nested.Name == "Tags") {
                    getters.Add(astType, CompileTagNameGetter(astType, nested));
                    continue;
                }
                CompileAndCollectTagNameGettersRecursive(getters, nested);
            }
        }

        private static Func<object, string> CompileTagNameGetter(Type astType, Type tagsType) {
            var tagMap = tagsType
                .GetFields()
                .OrderBy(f => (int)f.GetValue(null))
                .Select(f => f.Name)
                .ToArray();
            var nodeUntyped = Expression.Parameter(typeof(object));
            var tagGetter = Expression.Lambda<Func<object, int>>(
                Expression.Property(Expression.Convert(nodeUntyped, astType), "Tag"),
                nodeUntyped
            ).Compile();
            return instance => tagMap[tagGetter(instance)];
        }

        private static IReadOnlyDictionary<Type, Func<Ast.SynConst, string>> CompileConstValueGetters() {
            var getters = new Dictionary<Type, Func<Ast.SynConst, string>>();
            foreach (var type in typeof(Ast.SynConst).GetNestedTypes()) {
                if (type.BaseType != typeof(Ast.SynConst))
                    continue;

                var valueProperty = type.GetProperty("Item");
                if (valueProperty == null)
                    continue;

                var toString = valueProperty.PropertyType.GetMethod("ToString", Type.EmptyTypes);
                var constUntyped = Expression.Parameter(typeof(Ast.SynConst));
                getters.Add(type, Expression.Lambda<Func<Ast.SynConst, string>>(
                    Expression.Call(Expression.Property(Expression.Convert(constUntyped, type), valueProperty), toString),
                    constUntyped
                ).Compile());
            }
            return getters;
        }

        public IReadOnlyCollection<string> SupportedLanguageNames { get; } = new[] {"F#"};
    }
}
