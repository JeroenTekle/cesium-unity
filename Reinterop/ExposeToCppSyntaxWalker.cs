﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;

namespace Reinterop
{
    /// <summary>
    /// Walks the methods in the Reinterop-tagged class to discover which types, methods,
    /// and properties should be exposed to C++.
    /// </summary>
    internal class ExposeToCppSyntaxWalker : CSharpSyntaxWalker
    {
        public readonly Dictionary<ITypeSymbol, TypeToGenerate> GenerationItems = new Dictionary<ITypeSymbol, TypeToGenerate>(SymbolEqualityComparer.Default);

        public ExposeToCppSyntaxWalker(SemanticModel semanticModel)
        {
            this._semanticModel = semanticModel;
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            base.VisitInvocationExpression(node);
            ISymbol? symbol = this._semanticModel.GetSymbolInfo(node.Expression).Symbol;

            IMethodSymbol? methodSymbol = symbol as IMethodSymbol;
            if (methodSymbol != null)
            {
                this.AddMethod(methodSymbol);
            }

            IPropertySymbol? propertySymbol = symbol as IPropertySymbol;
            if (propertySymbol != null)
            {
                this.AddProperty(propertySymbol);
            }
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            base.VisitMemberAccessExpression(node);

            ISymbol? symbol = this._semanticModel.GetSymbolInfo(node.Name).Symbol;

            IPropertySymbol? propertySymbol = symbol as IPropertySymbol;
            if (propertySymbol != null)
            {
                this.AddProperty(propertySymbol);
            }

            IEventSymbol? eventSymbol = symbol as IEventSymbol;
            if (eventSymbol != null)
            {
                this.AddEvent(eventSymbol);
            }
        }

        public override void VisitGenericName(GenericNameSyntax node)
        {
            base.VisitGenericName(node);

            ITypeSymbol? type = this._semanticModel.GetTypeInfo(node).Type;
            if (type == null)
                return;

            this.AddType(type);
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            base.VisitIdentifierName(node);

            ITypeSymbol? type = this._semanticModel.GetTypeInfo(node).Type;
            if (type == null)
                return;

            this.AddType(type);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            base.VisitObjectCreationExpression(node);

            ISymbol? symbol = this._semanticModel.GetSymbolInfo(node).Symbol;
            IMethodSymbol? methodSymbol = symbol as IMethodSymbol;
            if (methodSymbol == null)
                return;

            this.AddConstructor(methodSymbol);
        }

        private TypeToGenerate AddType(ITypeSymbol type)
        {
            // Drop the nullability ("?") from the type if present.
            if (type.NullableAnnotation == NullableAnnotation.Annotated && type.OriginalDefinition != null)
            {
                type = type.OriginalDefinition;
            }

            // Don't add error types.
            if (type.TypeKind == TypeKind.Error)
                return new TypeToGenerate(type);

            // Don't add generic type parameters
            if (type.Kind == SymbolKind.TypeParameter)
                return new TypeToGenerate(type);

            // Don't add "void"
            if (type.SpecialType == SpecialType.System_Void)
                return new TypeToGenerate(type);

            // Don't add types without a name.
            if (type.Name == "")
                return new TypeToGenerate(type);

            TypeToGenerate generationItem;
            if (!this.GenerationItems.TryGetValue(type, out generationItem))
            {
                generationItem = new TypeToGenerate(type);
                this.GenerationItems.Add(type, generationItem);

                // If this is a delegate, be sure to add the Invoke method.
                if (type.TypeKind == TypeKind.Delegate)
                {
                    IMethodSymbol? invokeMethod = type.GetMembers("Invoke").FirstOrDefault() as IMethodSymbol;
                    if (invokeMethod != null)
                    {
                        this.AddMethod(invokeMethod);
                    }
                }
            }

            // If this type is an enumeration, add all of the enum values if we haven't already.
            if (generationItem.EnumValues.Count == 0 &&
                SymbolEqualityComparer.Default.Equals(type.BaseType, _semanticModel.Compilation.GetSpecialType(SpecialType.System_Enum)))
            {
                ImmutableArray<ISymbol> members = type.GetMembers();
                foreach (ISymbol symbol in members)
                {
                    IFieldSymbol? field = symbol as IFieldSymbol;
                    if (field == null || !field.IsConst)
                        continue;

                    generationItem.EnumValues.Add(field);
                }
            }

            // If this is an instantiated generic, we also need the types it's instantiated with.
            INamedTypeSymbol? named = type as INamedTypeSymbol;
            if (named != null && named.IsGenericType)
            {
                foreach (ITypeSymbol arg in named.TypeArguments)
                {
                    this.AddType(arg);
                }
            }

            return generationItem;
        }

        private TypeToGenerate AddMethod(IMethodSymbol symbol)
        {
            TypeToGenerate item = this.AddType(symbol.ContainingType);
            item.Methods.Add(symbol);

            // We also need to generate the parameter and return value types
            this.AddType(symbol.ReturnType);

            foreach (IParameterSymbol parameter in symbol.Parameters)
            {
                this.AddType(parameter.Type);
            }

            return item;
        }

        private TypeToGenerate AddProperty(IPropertySymbol symbol)
        {
            TypeToGenerate item = this.AddType(symbol.ContainingType);
            item.Properties.Add(symbol);

            // We also need to generate the property type.
            this.AddType(symbol.Type);

            return item;
        }

        private void AddEvent(IEventSymbol symbol)
        {
            TypeToGenerate item = this.AddType(symbol.ContainingType);
            item.Events.Add(symbol);

            // We also need to generate the event type.
            this.AddType(symbol.Type);
        }

        private TypeToGenerate AddConstructor(IMethodSymbol symbol)
        {
            TypeToGenerate item = this.AddType(symbol.ContainingType);
            item.Constructors.Add(symbol);

            // We also need to generate the parameter types
            foreach (IParameterSymbol parameter in symbol.Parameters)
            {
                this.AddType(parameter.Type);
            }

            return item;
        }

        private SemanticModel _semanticModel;
    }
}